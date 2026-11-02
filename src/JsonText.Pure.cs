using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SaveLanguageConverter
{
    // Rewrites the string values inside a JSON document (the inventories are stored as JSON inside string fields)
    // without re-serialising it: only the literals that change are replaced, every other byte stays as written.
    static class JsonText
    {
        // Called for each string value: (memberName, value, contextTokensOfEnclosingObject) -> replacement or null.
        public delegate string Rewriter(string member, string value, HashSet<string> context);

        sealed class Str { public int Start, End; public string Value; }
        sealed class Obj { public List<KeyValuePair<string, object>> Members = new List<KeyValuePair<string, object>>(); }
        sealed class Arr { public List<object> Items = new List<object>(); }
        sealed class Lit { }

        sealed class Edit { public int Start, End; public string Text; }

        public static bool LooksLikeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i < s.Length && (s[i] == '{' || s[i] == '[');
        }

        // Returns the rewritten document, or null if it is not parseable JSON.
        public static string Rewrite(string json, Rewriter rewriter, Func<string, HashSet<string>> contextOf)
        {
            object root;
            int pos = 0;
            try
            {
                root = ParseValue(json, ref pos);
                SkipWs(json, ref pos);
                if (pos != json.Length) return null;
            }
            catch (FormatException) { return null; }

            var edits = new List<Edit>();
            Walk(root, null, null, rewriter, contextOf, edits);
            if (edits.Count == 0) return json;
            edits.Sort((a, b) => b.Start.CompareTo(a.Start));
            var sb = new StringBuilder(json);
            foreach (var e in edits) sb.Remove(e.Start, e.End - e.Start).Insert(e.Start, e.Text);
            return sb.ToString();
        }

        static void Walk(object node, string member, HashSet<string> context, Rewriter rewriter, Func<string, HashSet<string>> contextOf, List<Edit> edits)
        {
            var o = node as Obj;
            if (o != null)
            {
                var ctx = new HashSet<string>();
                foreach (var m in o.Members)
                {
                    var sv = m.Value as Str;
                    if (sv != null) foreach (var t in contextOf(sv.Value)) ctx.Add(t);
                }
                foreach (var m in o.Members) Walk(m.Value, m.Key, ctx, rewriter, contextOf, edits);
                return;
            }
            var a = node as Arr;
            if (a != null)
            {
                foreach (var item in a.Items) Walk(item, member, context, rewriter, contextOf, edits);
                return;
            }
            var s = node as Str;
            if (s == null) return;
            string replacement = rewriter(member, s.Value, context ?? new HashSet<string>());
            if (replacement != null && replacement != s.Value)
                edits.Add(new Edit { Start = s.Start, End = s.End, Text = Quote(replacement) });
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{')
            {
                var o = new Obj();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipWs(s, ref i);
                    string key = ParseString(s, ref i).Value;
                    SkipWs(s, ref i);
                    Expect(s, ref i, ':');
                    o.Members.Add(new KeyValuePair<string, object>(key, ParseValue(s, ref i)));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return o;
                }
            }
            if (c == '[')
            {
                var a = new Arr();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Items.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return a;
                }
            }
            if (c == '"') return ParseString(s, ref i);
            int start = i;
            while (i < s.Length && "{}[],:\"".IndexOf(s[i]) < 0 && !char.IsWhiteSpace(s[i])) i++;
            if (i == start) throw new FormatException();
            return new Lit();
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException();
            i++;
        }

        static Str ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException();
            int start = i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException();
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException();
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException();
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            return new Str { Start = start, End = i, Value = sb.ToString() };
        }
    }
}
