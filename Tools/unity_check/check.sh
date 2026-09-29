#!/usr/bin/env bash
# Compile the FireGraph Unity code against lightweight stubs and run its EditMode tests on Mono.
# For cloud sessions without Unity. It checks C# syntax/types and runs the CPU solver
# (jobs execute sequentially in reverse index order); Burst, the GPU path and the real
# Unity APIs are only checked in the Unity editor (see Documentation/Research/LOCAL_ENGINE_PLAN.md).
#   apt-get install mono-mcs   (once)
#   Tools/unity_check/check.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${TMPDIR:-/tmp}/firegraph_check"
mkdir -p "$OUT"
SRC=$(find "$ROOT/Assets/Script/FireGraph" "$ROOT/Assets/Tests/FireGraph" -name '*.cs')
mcs -langversion:7.2 -nologo -debug -out:"$OUT/FireGraphCheck.exe" \
    "$ROOT"/Tools/unity_check/stubs/*.cs "$ROOT/Tools/unity_check/TestRunner.cs" $SRC
# Extra arguments are passed through, e.g.:  check.sh parity FFEData/zones/itoigawa2016/sim/base_U5_D180.ffes
UNITY_DATA_PATH="$ROOT/Assets" mono --debug "$OUT/FireGraphCheck.exe" "$@"
