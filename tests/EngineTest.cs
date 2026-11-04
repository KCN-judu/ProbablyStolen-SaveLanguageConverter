// Offline test of the translation engine against real game data (no Unity needed).
//   EngineTest.exe <tables.tsv> <expected.tsv> <original save.es3> <reference converted save.es3> <report.txt>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SaveLanguageConverter;

static class EngineTest
{
    // Undoes the escaping of the .tsv files written by tests/fixtures.py (esc).
    static string Unesc(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c == 'r' ? '\r' : c);
        }
        return sb.ToString();
    }

    // ES3 writes dictionary keys of integer type unquoted; quote them so the file parses as JSON.
    static string Es3ToJson(string s)
    {
        return Regex.Replace(s, "([{,])(-?\\d+):", "$1\"$2\":");
    }

    // Every string of a document, with the name of its field, in order; strings that hold JSON are opened. Comparing two
    // saves leaf by leaf shows where they differ.
    static List<string> Leaves(string json)
    {
        var all = new List<string>();
        JsonText.Rewriter rec = null;
        rec = (m, v, c) =>
        {
            if (JsonText.LooksLikeJson(v)) { JsonText.Rewrite(v, rec, TextPolicy.ContextOf); return null; }
            all.Add(m + "" + v);
            return null;
        };
        if (JsonText.Rewrite(json, rec, TextPolicy.ContextOf) == null) throw new Exception("unparseable");
        return all;
    }

    // Part A compares single texts with the hand-checked reference. Part B converts a whole save with TextPolicy, as the mod does
    // in the game, and compares it with the reference save. SLC_SRC sets the source language (default zh). Differences are written
    // to the report file, not treated as failures: the report is read by a person.
    static int Main(string[] a)
    {
        var tr = new Translator(Environment.GetEnvironmentVariable("SLC_SRC") ?? "zh");
        foreach (var line in File.ReadAllLines(a[0], Encoding.UTF8))
        {
            var p = line.Split('\t');
            tr.Add(p[0], int.Parse(p[1]), Unesc(p[2]), Unesc(p[3]), Unesc(p[4]));
        }
        var w = new StreamWriter(a[4], false, new UTF8Encoding(false));
        w.WriteLine("entries " + tr.EntryCount + ", templates " + tr.TemplateCount);

        // A: every distinct text the reference run translated, without context.
        int ok = 0, diff = 0, miss = 0;
        foreach (var line in File.ReadAllLines(a[1], Encoding.UTF8))
        {
            var p = line.Split('\t');
            string src = Unesc(p[0]), want = Unesc(p[1]);
            string got = tr.Translate(src, null);
            if (got == want) ok++;
            else if (got == null) { miss++; w.WriteLine("A MISS  " + Show(src)); }
            else { diff++; w.WriteLine("A DIFF  " + Show(src) + "\n   want " + Show(want) + "\n   got  " + Show(got)); }
        }
        w.WriteLine("A: " + ok + " same, " + diff + " different, " + miss + " missed");

        // B: the whole original save, through the same policy the mod applies in game.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string original = Es3ToJson(File.ReadAllText(a[2], Encoding.UTF8));
        var policy = new TextPolicy(tr);
        string converted = JsonText.Rewrite(original, policy.Rewrite, TextPolicy.ContextOf);
        w.WriteLine("B: rewrite " + (converted == null ? "FAILED" : "ok") + " in " + sw.ElapsedMilliseconds + " ms, " + policy.Changed + " strings changed");
        foreach (var kv in policy.Untranslated) w.WriteLine("B UNTRANSLATED x" + kv.Value + "  " + Show(kv.Key));

        var mine = Leaves(converted);
        var reference = Leaves(Es3ToJson(File.ReadAllText(a[3], Encoding.UTF8)));
        var before = Leaves(original);
        w.WriteLine("B: leaves mine " + mine.Count + ", reference " + reference.Count + ", original " + before.Count);
        int same = 0, differ = 0;
        var memberDiff = new Dictionary<string, int>();
        var seen = new HashSet<string>();
        for (int i = 0; i < Math.Min(mine.Count, reference.Count); i++)
        {
            if (mine[i] == reference[i]) { same++; continue; }
            string mem = mine[i].Split('')[0];
            memberDiff[mem] = (memberDiff.ContainsKey(mem) ? memberDiff[mem] : 0) + 1;
            differ++;
            if (seen.Add(before[i] + "\u0001" + mine[i]))
                w.WriteLine("B DIFF  " + Show(before[i]) + "\n   ref  " + Show(reference[i]) + "\n   mine " + Show(mine[i]));
        }
        foreach (var kv in memberDiff.OrderByDescending(x => x.Value)) w.WriteLine("B MEMBER " + kv.Key + " x" + kv.Value);
        w.WriteLine("B: " + same + " leaves identical to reference, " + differ + " differ");
        int cjk = mine.Count(s => tr.LooksUntranslated(s.Split('')[1]));
        w.WriteLine("B: leaves still containing CJK: " + cjk);
        w.Close();
        return 0;
    }

    // Shortens a text for the report.
    static string Show(string s)
    {
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
    }
}
