// Usage: Find.exe <dll> <out.txt> <instructionRegex>  - lists every method whose IL matches, with matching lines
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

class Find
{
    static void Main(string[] a)
    {
        var res = new DefaultAssemblyResolver();
        res.AddSearchDirectory(Path.GetDirectoryName(a[0]));
        var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters { AssemblyResolver = res });
        var re = new Regex(a[2]);
        using (var w = new StreamWriter(a[1], false, new System.Text.UTF8Encoding(false)))
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var m in t.Methods.Where(x => x.HasBody))
                {
                    var hits = m.Body.Instructions.Where(i => re.IsMatch(i.ToString())).ToList();
                    if (hits.Count == 0) continue;
                    w.WriteLine(t.FullName + "::" + m.Name);
                    foreach (var h in hits) w.WriteLine("    " + h);
                }
    }
}
