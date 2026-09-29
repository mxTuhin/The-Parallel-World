"""HTTP helpers with a download-size guard and on-disk caching.

Works behind the cloud session's proxy (standard HTTPS_PROXY / CA env vars) and
on a plain home connection.
"""

from __future__ import annotations

import os
from pathlib import Path
from urllib.parse import urlparse

import requests

from . import config


class DownloadTooLarge(RuntimeError):
    """Raised when a download exceeds the configured size limit."""


class HostBlocked(RuntimeError):
    """Raised when the network refuses the host (e.g. a 403 CONNECT from a proxy)."""


def limit_bytes(allow_large: bool) -> float | None:
    if allow_large or config.MAX_DOWNLOAD_MB <= 0:
        return None
    return config.MAX_DOWNLOAD_MB * 1e6


def _session() -> requests.Session:
    s = requests.Session()
    s.headers["User-Agent"] = "ffe-pipeline/0.1 (research; open fire-following-earthquake model)"
    return s


def head_size(url: str, timeout: float = 30) -> int | None:
    """Content-Length from a HEAD request, or None if the server does not say."""
    try:
        r = _session().head(url, timeout=timeout, allow_redirects=True)
    except requests.exceptions.ProxyError as e:
        raise HostBlocked(f"{urlparse(url).hostname}: {e}") from e
    if r.status_code >= 400:
        return None
    n = r.headers.get("Content-Length")
    return int(n) if n and n.isdigit() else None


def download(url: str, dest: Path, *, allow_large: bool = False, params: dict | None = None,
             timeout: float = 120, overwrite: bool = False) -> Path:
    """Stream `url` to `dest`, refusing anything above the size limit.

    The limit is checked against Content-Length up front and again while
    streaming, so servers that omit the header are still bounded.
    """
    dest = Path(dest)
    if dest.exists() and not overwrite:
        return dest
    limit = limit_bytes(allow_large)
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    try:
        with _session().get(url, params=params, stream=True, timeout=timeout) as r:
            if r.status_code == 403:
                raise HostBlocked(f"{urlparse(url).hostname} answered 403")
            r.raise_for_status()
            declared = r.headers.get("Content-Length")
            if limit and declared and declared.isdigit() and int(declared) > limit:
                raise DownloadTooLarge(_too_large_msg(url, int(declared)))
            written = 0
            with open(tmp, "wb") as f:
                for chunk in r.iter_content(chunk_size=1 << 20):
                    written += len(chunk)
                    if limit and written > limit:
                        raise DownloadTooLarge(_too_large_msg(url, written, streaming=True))
                    f.write(chunk)
    except requests.exceptions.ProxyError as e:
        tmp.unlink(missing_ok=True)
        raise HostBlocked(f"{urlparse(url).hostname}: blocked by the network ({e})") from e
    except BaseException:
        tmp.unlink(missing_ok=True)
        raise
    os.replace(tmp, dest)
    return dest


def get_json(url: str, params: dict | None = None, timeout: float = 60):
    try:
        r = _session().get(url, params=params, timeout=timeout)
    except requests.exceptions.ProxyError as e:
        raise HostBlocked(f"{urlparse(url).hostname}: blocked by the network ({e})") from e
    if r.status_code == 403:
        raise HostBlocked(f"{urlparse(url).hostname} answered 403")
    r.raise_for_status()
    return r.json()


def _too_large_msg(url: str, nbytes: int, streaming: bool = False) -> str:
    how = "exceeded while streaming" if streaming else "declared size"
    return (f"{url}: {nbytes / 1e6:.1f} MB ({how}) is above the cloud-session limit of "
            f"{config.MAX_DOWNLOAD_MB:.0f} MB. Run this step on the local PC (no limit there).")


def https_proxy_options() -> dict | None:
    """pyarrow S3FileSystem proxy options derived from HTTPS_PROXY, if set."""
    proxy = os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
    if not proxy:
        return None
    p = urlparse(proxy)
    return {"scheme": p.scheme or "http", "host": p.hostname, "port": p.port or 80}
