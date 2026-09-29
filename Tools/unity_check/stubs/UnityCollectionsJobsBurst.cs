// Minimal stand-ins for Unity.Collections / Unity.Jobs / Unity.Burst so the FireGraph
// code compiles and runs under Mono. Jobs execute immediately on the calling thread,
// visiting indices in REVERSE order to expose any hidden dependence on update order.
using System;
using System.Collections;
using System.Collections.Generic;

namespace Unity.Collections
{
    public enum Allocator { Invalid, None, Temp, TempJob, Persistent }
    [AttributeUsage(AttributeTargets.Field)] public sealed class ReadOnlyAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class WriteOnlyAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class NativeDisableParallelForRestrictionAttribute : Attribute { }

    public struct NativeArray<T> : IDisposable, IEnumerable<T> where T : struct
    {
        T[] _a; int _start, _len;
        public NativeArray(int length, Allocator allocator) { _a = new T[length]; _start = 0; _len = length; }
        public NativeArray(T[] array, Allocator allocator) { _a = (T[])array.Clone(); _start = 0; _len = array.Length; }
        public NativeArray(NativeArray<T> other, Allocator allocator) { _a = other.ToArray(); _start = 0; _len = _a.Length; }
        public int Length => _len;
        public bool IsCreated => _a != null;
        public T this[int i]
        {
            get { if ((uint)i >= (uint)_len) throw new IndexOutOfRangeException($"{i} of {_len}"); return _a[_start + i]; }
            set { if ((uint)i >= (uint)_len) throw new IndexOutOfRangeException($"{i} of {_len}"); _a[_start + i] = value; }
        }
        public T[] ToArray() { var r = new T[_len]; Array.Copy(_a, _start, r, 0, _len); return r; }
        public NativeArray<T> GetSubArray(int start, int length) => new NativeArray<T> { _a = _a, _start = _start + start, _len = length };
        public void Dispose() { if (_a == null) throw new ObjectDisposedException("NativeArray"); _a = null; }
        public IEnumerator<T> GetEnumerator() { for (int i = 0; i < _len; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace Unity.Jobs
{
    public interface IJobParallelFor { void Execute(int index); }
    public interface IJob { void Execute(); }
    public struct JobHandle { public void Complete() { } }
    public static class IJobParallelForExtensions
    {
        public static JobHandle Schedule<T>(this T job, int arrayLength, int innerloopBatchCount, JobHandle dependsOn = default(JobHandle))
            where T : struct, IJobParallelFor
        {
            for (int i = arrayLength - 1; i >= 0; i--) job.Execute(i);
            return default(JobHandle);
        }
    }
    public static class IJobExtensions
    {
        public static JobHandle Schedule<T>(this T job, JobHandle dependsOn = default(JobHandle)) where T : struct, IJob { job.Execute(); return default(JobHandle); }
    }
}

namespace Unity.Burst
{
    public enum FloatPrecision { Standard, High, Medium, Low }
    public enum FloatMode { Default, Strict, Deterministic, Fast }
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class BurstCompileAttribute : Attribute
    {
        public BurstCompileAttribute() { }
        public BurstCompileAttribute(FloatPrecision p, FloatMode m) { }
    }
}

namespace Unity.Mathematics
{
    public struct uint2
    {
        public uint x, y;
        public uint2(uint x, uint y) { this.x = x; this.y = y; }
        public uint2(uint v) { x = v; y = v; }
        public static uint2 zero => default(uint2);
        public static uint2 operator +(uint2 a, uint2 b) => new uint2(unchecked(a.x + b.x), unchecked(a.y + b.y));
    }
    public struct uint4 : IEquatable<uint4>
    {
        public uint x, y, z, w;
        public uint4(uint x, uint y, uint z, uint w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public uint4(uint v) { x = y = z = w = v; }
        public static uint4 zero => default(uint4);
        public bool Equals(uint4 o) => x == o.x && y == o.y && z == o.z && w == o.w;
        public override bool Equals(object o) => o is uint4 u && Equals(u);
        public override int GetHashCode() => (int)(x ^ y ^ z ^ w);
        public override string ToString() => $"uint4({x:X8}, {y:X8}, {z:X8}, {w:X8})";
    }
    public static class math
    {
        public static float sqrt(float x) => (float)Math.Sqrt(x);
        public static double sqrt(double x) => Math.Sqrt(x);
        public static float min(float a, float b) => a < b ? a : (b < a ? b : (float.IsNaN(a) ? b : a));
        public static float max(float a, float b) => a > b ? a : (b > a ? b : (float.IsNaN(a) ? b : a));
        public static int max(int a, int b) => a > b ? a : b;
        public static float pow(float x, float y) => (float)Math.Pow(x, y);
        public static float ceil(float x) => (float)Math.Ceiling(x);
        public static double log(double x) => Math.Log(x);
        public static double exp(double x) => Math.Exp(x);
    }
}
