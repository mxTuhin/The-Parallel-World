// Runs every [Test] method in the compiled assembly and reports pass / fail / ignored.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

public static class TestRunner
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "parity")
            return ParallelWorld.FireGraph.Editor.FireGraphBatch.RunParity(args[1]) ? 0 : 1;
        int pass = 0, fail = 0, ignored = 0;
        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0)
            .Select(m => new KeyValuePair<Type, MethodInfo>(t, m)));
        foreach (var kv in tests)
        {
            Type t = kv.Key;
            MethodInfo m = kv.Value;
            try
            {
                m.Invoke(Activator.CreateInstance(t), null);
                pass++;
                Console.WriteLine($"PASS    {t.Name}.{m.Name}");
            }
            catch (TargetInvocationException e) when (e.InnerException is IgnoreException)
            {
                ignored++;
                Console.WriteLine($"IGNORED {t.Name}.{m.Name}: {e.InnerException.Message}");
            }
            catch (TargetInvocationException e)
            {
                fail++;
                Console.WriteLine($"FAIL    {t.Name}.{m.Name}: {e.InnerException}");
            }
        }
        Console.WriteLine($"\n{pass} passed, {fail} failed, {ignored} ignored");
        return fail == 0 ? 0 : 1;
    }
}
