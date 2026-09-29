using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace ParallelWorld.FireGraph
{
    /// <summary>One array listed in a container manifest.</summary>
    [Serializable]
    public class FfeArrayEntry
    {
        public string name;
        public string group;
        public string dtype;
        public long count;
        public long offset;
        public long nbytes;
    }

#pragma warning disable 0649   // fields are filled by JsonUtility
    [Serializable]
    class FfeArrayList
    {
        public FfeArrayEntry[] arrays;
    }
#pragma warning restore 0649

    /// <summary>
    /// Reader for the FFEG / FFES binary container written by Tools/ffe_pipeline (ffe/ffeg.py):
    /// 4-byte magic, uint32 version, uint64 manifest length, JSON manifest, zero padding to
    /// 16 bytes, then 16-byte-aligned little-endian arrays at the offsets the manifest lists.
    /// </summary>
    public sealed class FfeContainer
    {
        public const uint SupportedVersion = 1;
        const int Align = 16;

        public readonly string Magic;
        public readonly string ManifestJson;
        readonly byte[] _raw;
        readonly long _dataStart;
        readonly Dictionary<string, FfeArrayEntry> _entries = new Dictionary<string, FfeArrayEntry>();

        FfeContainer(string magic, string manifestJson, byte[] raw, long dataStart, FfeArrayEntry[] entries)
        {
            Magic = magic;
            ManifestJson = manifestJson;
            _raw = raw;
            _dataStart = dataStart;
            foreach (var e in entries) _entries[e.name] = e;
        }

        public static FfeContainer Load(string path, string expectedMagic)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 16) throw new InvalidDataException($"{path}: file too short");
            string magic = Encoding.ASCII.GetString(raw, 0, 4);
            if (magic != expectedMagic) throw new InvalidDataException($"{path}: magic '{magic}', expected '{expectedMagic}'");
            uint version = BitConverter.ToUInt32(raw, 4);
            if (version != SupportedVersion) throw new InvalidDataException($"{path}: unsupported version {version}");
            long mlen = (long)BitConverter.ToUInt64(raw, 8);
            string json = Encoding.UTF8.GetString(raw, 16, (int)mlen);
            long headerLen = 16 + mlen;
            long dataStart = headerLen + ((Align - headerLen % Align) % Align);
            var list = JsonUtility.FromJson<FfeArrayList>(json);
            if (list?.arrays == null) throw new InvalidDataException($"{path}: manifest has no array list");
            return new FfeContainer(magic, json, raw, dataStart, list.arrays);
        }

        public bool Has(string name) => _entries.ContainsKey(name);

        public FfeArrayEntry Entry(string name)
        {
            if (!_entries.TryGetValue(name, out var e)) throw new KeyNotFoundException($"array '{name}' not in container");
            return e;
        }

        static int DtypeSize(string dtype)
        {
            switch (dtype)
            {
                case "float32": case "uint32": case "int32": return 4;
                case "int8": case "uint8": return 1;
                default: throw new InvalidDataException($"unsupported dtype {dtype}");
            }
        }

        /// <summary>
        /// Copy an array out as T[]. T must have the stored element size; uint32 data may be
        /// read as int when values are below 2^31 (node and edge indices always are).
        /// </summary>
        public T[] ReadManaged<T>(string name) where T : struct
        {
            var e = Entry(name);
            int size = DtypeSize(e.dtype);
            if (Marshal.SizeOf<T>() != size)
                throw new InvalidDataException($"array '{name}' is {e.dtype}; cannot read as {typeof(T).Name}");
            var span = new ReadOnlySpan<byte>(_raw, (int)(_dataStart + e.offset), (int)e.nbytes);
            return MemoryMarshal.Cast<byte, T>(span).ToArray();
        }

        public NativeArray<T> Read<T>(string name, Allocator allocator) where T : struct
        {
            return new NativeArray<T>(ReadManaged<T>(name), allocator);
        }
    }
}
