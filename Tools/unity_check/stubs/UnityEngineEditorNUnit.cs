// Minimal stand-ins for the UnityEngine / UnityEditor / NUnit APIs used by FireGraph.
// Rendering and GPU types only need to compile; ExactFireGpu reports Supported = false.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Collections;

namespace UnityEngine
{
    public class Object { public string name; }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class Mesh : Object { }
    public class Shader : Object { public static Shader Find(string n) => new Shader(); }
    public class Material : Object
    {
        public Material(Shader s) { }
        public void SetBuffer(string n, GraphicsBuffer b) { }
        public void SetFloat(string n, float v) { }
    }
    public class ComputeShader : Object
    {
        public int FindKernel(string n) => 0;
        public void SetBuffer(int k, string n, GraphicsBuffer b) { }
        public void SetInt(string n, int v) { }
        public void SetFloat(string n, float v) { }
        public void Dispatch(int k, int x, int y, int z) { }
    }
    public class GraphicsBuffer : IDisposable
    {
        public enum Target { Structured }
        public GraphicsBuffer(Target t, int count, int stride) { }
        public void SetData<T>(NativeArray<T> d) where T : struct { }
        public void SetData(Array d) { }
        public void GetData(Array d) { }
        public void Release() { }
        public void Dispose() { }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 positiveInfinity => new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        public static Vector3 negativeInfinity => new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 Min(Vector3 a, Vector3 b) => new Vector3(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
    }
    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }
    public struct Bounds { public Bounds(Vector3 c, Vector3 s) { } }
    public struct Rect { public Rect(float x, float y, float w, float h) { } }
    public struct RenderParams { public RenderParams(Material m) { worldBounds = default(Bounds); } public Bounds worldBounds; }
    public static class Graphics
    {
        public static void RenderMeshPrimitives(RenderParams rp, Mesh m, int sub, int count) { }
        public static void ExecuteCommandBuffer(Rendering.CommandBuffer c) { }
    }
    public static class GUI { public static void Label(Rect r, string s) { } }
    public static class Time { public static float deltaTime => 0.016f; }
    public static class Mathf
    {
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Sqrt(float a) => (float)Math.Sqrt(a);
    }
    public static class Debug
    {
        public static void Log(object o) => Console.WriteLine(o);
        public static void LogException(Exception e) => Console.WriteLine(e);
    }
    public static class SystemInfo
    {
        public static bool supportsComputeShaders => false;
        public static string graphicsDeviceName => "none";
        public static string processorType => "mono";
    }
    public static class Resources
    {
        public static T Load<T>(string p) where T : Object => null;
        public static T GetBuiltinResource<T>(string p) where T : Object => null;
    }
    public static class Application
    {
        public static string dataPath => Environment.GetEnvironmentVariable("UNITY_DATA_PATH") ?? "Assets";
        public static string streamingAssetsPath => System.IO.Path.Combine(dataPath, "StreamingAssets");
        public static bool isBatchMode => true;
    }
    [AttributeUsage(AttributeTargets.Field)] public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string s) { } }
    [AttributeUsage(AttributeTargets.Field)] public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string s) { } }

    /// <summary>Reflection-based subset of JsonUtility: public fields, numbers, strings, arrays, nested classes.</summary>
    public static class JsonUtility
    {
        public static T FromJson<T>(string json) => (T)Bind(new Parser(json).Value(), typeof(T));

        static object Bind(object v, Type t)
        {
            if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t == typeof(string)) return (string)v;
            if (t == typeof(int)) return Convert.ToInt32((double)v);
            if (t == typeof(long)) return Convert.ToInt64((double)v);
            if (t == typeof(float)) return (float)(double)v;
            if (t == typeof(double)) return (double)v;
            if (t == typeof(bool)) return (bool)v;
            if (t.IsArray)
            {
                var list = (List<object>)v;
                var arr = Array.CreateInstance(t.GetElementType(), list.Count);
                for (int i = 0; i < list.Count; i++) arr.SetValue(Bind(list[i], t.GetElementType()), i);
                return arr;
            }
            var obj = Activator.CreateInstance(t);
            var dict = (Dictionary<string, object>)v;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (dict.TryGetValue(f.Name, out var fv) && fv != null) f.SetValue(obj, Bind(fv, f.FieldType));
            return obj;
        }

        sealed class Parser
        {
            readonly string s; int p;
            public Parser(string s) { this.s = s; }
            void Ws() { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }
            public object Value()
            {
                Ws();
                char c = s[p];
                if (c == '{') { p++; var d = new Dictionary<string, object>(); Ws(); if (s[p] == '}') { p++; return d; }
                    while (true) { Ws(); string k = Str(); Ws(); p++; d[k] = Value(); Ws(); if (s[p++] == '}') return d; } }
                if (c == '[') { p++; var l = new List<object>(); Ws(); if (s[p] == ']') { p++; return l; }
                    while (true) { l.Add(Value()); Ws(); if (s[p++] == ']') return l; } }
                if (c == '"') return Str();
                if (s.Substring(p).StartsWith("true")) { p += 4; return true; }
                if (s.Substring(p).StartsWith("false")) { p += 5; return false; }
                if (s.Substring(p).StartsWith("null")) { p += 4; return null; }
                if (s.Substring(p).StartsWith("Infinity")) { p += 8; return double.PositiveInfinity; }
                if (s.Substring(p).StartsWith("NaN")) { p += 3; return double.NaN; }
                int st = p;
                while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
                return double.Parse(s.Substring(st, p - st), CultureInfo.InvariantCulture);
            }
            string Str()
            {
                var sb = new StringBuilder(); p++;
                while (s[p] != '"') { if (s[p] == '\\') { p++; sb.Append(s[p] == 'n' ? '\n' : s[p]); } else sb.Append(s[p]); p++; }
                p++; return sb.ToString();
            }
        }
    }
}

namespace UnityEngine.Rendering
{
    public class CommandBuffer
    {
        public string name;
        public void Clear() { }
        public void SetComputeIntParam(ComputeShader cs, string n, int v) { }
        public void DispatchCompute(ComputeShader cs, int k, int x, int y, int z) { }
        public void Release() { }
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class MenuItemAttribute : Attribute { public MenuItemAttribute(string p) { } }
    public static class EditorApplication { public static void Exit(int code) => Environment.Exit(code); }
    public static class EditorUtility { public static string OpenFilePanel(string a, string b, string c) => ""; }
}

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class TestAttribute : Attribute { }
    public sealed class IgnoreException : Exception { public IgnoreException(string m) : base(m) { } }
    public sealed class AssertionException : Exception { public AssertionException(string m) : base(m) { } }
    public static class Assert
    {
        static void Fail(string m) => throw new AssertionException(m);
        public static void Ignore(string m) => throw new IgnoreException(m);
        public static void IsTrue(bool c, string m = "") { if (!c) Fail("expected true " + m); }
        public static void AreEqual(object a, object b, string m = "") { if (!Equals(a, b)) Fail($"expected {a} got {b} {m}"); }
        public static void AreEqual(int a, int b, string m = "") { if (a != b) Fail($"expected {a} got {b} {m}"); }
        public static void AreEqual(float a, float b, float tol, string m = "") { if (!(Math.Abs(a - b) <= tol)) Fail($"expected {a} got {b} (tol {tol}) {m}"); }
        public static void AreEqual(double a, double b, double tol, string m = "") { if (!(Math.Abs(a - b) <= tol)) Fail($"expected {a} got {b} (tol {tol}) {m}"); }
        public static void Greater(int a, int b, string m = "") { if (!(a > b)) Fail($"{a} not > {b} {m}"); }
        public static void LessOrEqual(int a, int b, string m = "") { if (!(a <= b)) Fail($"{a} not <= {b} {m}"); }
    }
}
