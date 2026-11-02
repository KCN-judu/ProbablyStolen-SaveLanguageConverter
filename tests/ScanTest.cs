// Runs the game's own mod scanner (ModScanner.Scan) on the built mod, outside the game.
//   ScanTest.exe <game Managed folder> <mod folder>
using System;
using System.IO;
using System.Linq;
using System.Reflection;

static class ScanTest
{
    static string managed;

    static int Main(string[] a)
    {
        managed = a[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        return Run(a[1]);
    }

    static int Run(string modFolder)
    {
        var dlls = new[] { Path.Combine(modFolder, "SaveLanguageConverter.dll") };
        var allowed = System.IO.Directory.GetFiles(managed, "*.dll").Select(Path.GetFileNameWithoutExtension).ToList();
        var r = ModScanner.Scan(dlls, allowed, modFolder, true);
        Console.WriteLine("methods scanned: " + r.MethodsScanned + ", refused: " + r.Refused + ", error: " + (r.Error ?? "none"));
        Console.WriteLine("capabilities: " + (r.Capabilities.Count == 0 ? "none" : string.Join(", ", r.Capabilities.ToArray())));
        foreach (var f in r.HardFlags) Console.WriteLine("HARD  " + Describe(f));
        foreach (var f in r.Findings) Console.WriteLine("FIND  " + Describe(f));
        return r.Capabilities.Count == 0 && r.HardFlags.Count == 0 && !r.Refused ? 0 : 1;
    }

    static string Describe(object finding)
    {
        return string.Join(" | ", finding.GetType().GetFields().Select(f => f.Name + "=" + f.GetValue(finding)).ToArray());
    }
}
