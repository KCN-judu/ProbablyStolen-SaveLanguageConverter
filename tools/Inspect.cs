// Usage: Inspect.exe <dll> <out.txt> <typeRegex> [methodRegexForIL]
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Prints the types of a DLL whose full name matches a regex: fields, properties, events and method signatures, and the IL of
// the methods whose name matches the optional second regex. Used to read the game's Assembly-CSharp.dll when writing a patch.
// Output goes to a file because it can be very large. The game's Managed folder is added to the resolver (edit the path if
// the game is installed elsewhere). See modding_tools.md in the collection repository for how to compile it.
class Inspect
{
    static void Main(string[] a)
    {
        var res = new DefaultAssemblyResolver();
        res.AddSearchDirectory(Path.GetDirectoryName(a[0]));
        res.AddSearchDirectory(@"D:\SteamLibrary\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed");
        var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters { AssemblyResolver = res });
        var tre = new Regex(a[2]);
        var mre = a.Length > 3 ? new Regex(a[3]) : null;
        using (var w = new StreamWriter(a[1], false, new System.Text.UTF8Encoding(false)))
        {
            foreach (var t in asm.MainModule.GetTypes().Where(x => tre.IsMatch(x.FullName)))
            {
                w.WriteLine("TYPE " + t.FullName + (t.BaseType != null ? " : " + t.BaseType.FullName : "") +
                    (t.HasInterfaces ? " [" + string.Join(", ", t.Interfaces.Select(i => i.InterfaceType.FullName)) + "]" : ""));
                foreach (var f in t.Fields) w.WriteLine("  F " + (f.IsStatic ? "static " : "") + f.FieldType.FullName + " " + f.Name);
                foreach (var p in t.Properties) w.WriteLine("  P " + p.PropertyType.FullName + " " + p.Name);
                foreach (var e in t.Events) w.WriteLine("  E " + e.EventType.FullName + " " + e.Name);
                foreach (var m in t.Methods)
                {
                    w.WriteLine("  M " + (m.IsStatic ? "static " : "") + (m.IsPublic ? "public " : "") + m.ReturnType.FullName + " " + m.Name +
                        "(" + string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name)) + ")");
                    if (mre != null && mre.IsMatch(m.Name) && m.HasBody)
                        foreach (var ins in m.Body.Instructions)
                            w.WriteLine("      " + ins.ToString());
                }
            }
        }
    }
}
