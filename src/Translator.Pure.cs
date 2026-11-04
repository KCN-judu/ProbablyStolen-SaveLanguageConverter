// Pure file (see code_standard.md): no game or Unity types, so tests/PureTest.cs can run it on hand-made entries.
// ConvertMod fills the Translator from the game's string tables (BuildTranslator); TextPolicy decides when to call it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SaveLanguageConverter
{
    // One source-language string as it appears in a game string table, and what it is in the target language.
    sealed class Candidate
    {
        // Table and Key identify the entry; Priority is the table's position in ConvertMod.Tables (lower wins).
        public string Table;
        public string Key;
        public string Target;
        public int Priority;
    }

    sealed class Template
    {
        // A source entry with {placeholders}, as a regex: the literal parts are escaped and each placeholder becomes (.+?).
        public Regex Pattern;
        public string[] Placeholders;   // "{0}", "{name}", ... in capture-group order
        public string Literal;          // longest literal run, used as a cheap pre-filter
        public List<Candidate> Candidates;
    }

    // Maps text that was written into a save in one language back to the game's string tables and
    // returns the same entries in another language. Holds no Unity types so it can be tested offline.
    sealed class Translator
    {
        // Limits of the segmentation in TranslateSegments. They bound its cost: a piece is at most 80 characters, a
        // text at most 160, and a result at most 6 entries.
        const int MaxSegmentLength = 80;
        const int MaxSegmentedLength = 160;
        const int MaxSegments = 6;

        readonly Dictionary<string, List<Candidate>> exact = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        readonly List<Template> templates = new List<Template>();
        // Results for texts looked up without context; a save repeats the same texts very often.
        readonly Dictionary<string, string> memo = new Dictionary<string, string>(StringComparer.Ordinal);
        // CJK text has no spaces between words, so word-boundary and length rules differ (see TranslateSegments and IsCjk).
        readonly bool sourceIsCjk;
        int longestValue;

        public Translator(string sourceLocale)
        {
            string s = (sourceLocale ?? "").ToLowerInvariant();
            sourceIsCjk = s.StartsWith("zh") || s.StartsWith("ja") || s.StartsWith("ko");
        }

        public int EntryCount { get { return exact.Count; } }
        public int TemplateCount { get { return templates.Count; } }

        // Registers one entry. Entries identical in both languages carry no information and are dropped. Entries with {..}
        // placeholders are also kept as templates.
        public void Add(string table, int priority, string key, string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source == target) return;
            var c = new Candidate { Table = table, Key = key ?? "", Target = target, Priority = priority };
            List<Candidate> list;
            if (!exact.TryGetValue(source, out list)) exact[source] = list = new List<Candidate>();
            list.Add(c);
            if (source.Length > longestValue) longestValue = source.Length;
            if (source.IndexOf('{') >= 0) AddTemplate(source, c);
        }

        static readonly Regex PlaceholderRx = new Regex(@"\{[^{}]*\}");

        // Templates that would match too much are refused: repeated or nested placeholders, no letters in the fixed text
        // ("{0}: {1}" would match anything), or a target text that lacks one of the placeholders.
        void AddTemplate(string source, Candidate c)
        {
            var parts = PlaceholderRx.Split(source);
            var names = PlaceholderRx.Matches(source).Cast<Match>().Select(m => m.Value).ToArray();
            if (names.Length == 0 || names.Distinct().Count() != names.Length) return;
            if (parts.Any(p => p.IndexOf('{') >= 0 || p.IndexOf('}') >= 0)) return;      // nested smart-format syntax
            string literal = parts.OrderByDescending(p => p.Length).First();
            if (!parts.Any(p => p.Any(char.IsLetter))) return;                          // "{0}: {1}" would match anything
            if (names.Any(n => c.Target.IndexOf(n, StringComparison.Ordinal) < 0)) return;
            var rx = new StringBuilder("^");
            for (int i = 0; i < parts.Length; i++)
            {
                rx.Append(Regex.Escape(parts[i]));
                if (i < names.Length) rx.Append("(.+?)");
            }
            rx.Append('$');
            var existing = templates.FirstOrDefault(t => t.Pattern.ToString() == rx.ToString());
            if (existing != null) { existing.Candidates.Add(c); return; }
            templates.Add(new Template
            {
                Pattern = new Regex(rx.ToString(), RegexOptions.Singleline),
                Placeholders = names,
                Literal = literal,
                Candidates = new List<Candidate> { c }
            });
        }

        // True when the text still contains letters of the source script, i.e. probably untranslated.
        public bool LooksUntranslated(string s)
        {
            if (!sourceIsCjk || string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s) if (IsCjk(ch)) return true;
            return false;
        }

        // Kana, CJK ideographs (including extension A) and Hangul syllables.
        static bool IsCjk(char ch)
        {
            return (ch >= '぀' && ch <= 'ヿ') || (ch >= '㐀' && ch <= '鿿') || (ch >= '가' && ch <= '힯');
        }

        // Letters that belong to a word in scripts that separate words with spaces.
        // Letters and digits of scripts that separate words with spaces; CJK characters are not word characters.
        static bool IsWordChar(char ch)
        {
            return char.IsLetterOrDigit(ch) && ch < '⺀';
        }

        // The translation of a text that is exactly one entry, from a table other than `skipTable`; null otherwise.
        // No templates, concatenations or suffixes: for fields where only a whole game entry may be replaced.
        public string TranslateExact(string s, ICollection<string> context, string skipTable)
        {
            List<Candidate> cands;
            if (string.IsNullOrEmpty(s) || !exact.TryGetValue(s, out cands)) return null;
            var allowed = cands.Where(c => c.Table != skipTable).ToList();
            return allowed.Count == 0 ? null : Choose(allowed, context);
        }

        // Returns the translation, or null when the text is not (entirely) game text.
        // context: lowercase tokens of ids found next to the text, used to pick between entries that share a source string.
        public string Translate(string s, ICollection<string> context)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string cached;
            bool cacheable = context == null || context.Count == 0;
            if (cacheable && memo.TryGetValue(s, out cached)) return cached;
            string r = Translate(s, context, 0);
            if (cacheable) memo[s] = r;
            return r;
        }

        // Tries the strategies from the most to the least specific; the first one that produces a result wins. `depth` bounds the
        // recursion, since templates, suffixes and sentences call back into Translate.
        string Translate(string s, ICollection<string> context, int depth)
        {
            if (s.Length == 0) return s;
            if (depth > 4) return null;
            List<Candidate> cands;
            if (exact.TryGetValue(s, out cands)) return Choose(cands, context);

            string trimmed = s.Trim();
            if (trimmed.Length == 0) return s;
            if (trimmed.Length != s.Length)
            {
                // Some entries keep their leading space, so the untrimmed text is tried below as well.
                string inner = Translate(trimmed, context, depth + 1);
                if (inner != null)
                {
                    int lead = s.Length - s.TrimStart().Length;
                    return s.Substring(0, lead) + inner + s.Substring(lead + trimmed.Length);
                }
            }

            string r = TranslateLines(s, context, depth);
            if (r != null) return r;
            r = TranslateTemplate(s, context, depth);
            if (r != null) return r;
            r = TranslateSuffix(s, context, depth);
            if (r != null) return r;
            r = TranslateSegments(s, context);
            if (r != null) return r;
            return TranslateSentences(s, context, depth);
        }

        // Multi-line text is often several table entries joined with newlines, some of which span lines themselves.
        string TranslateLines(string s, ICollection<string> context, int depth)
        {
            if (s.IndexOf('\n') < 0) return null;
            string[] lines = s.Split('\n');
            var output = new List<string>();
            int i = 0;
            while (i < lines.Length)
            {
                bool matched = false;
                for (int j = lines.Length; j > i; j--)
                {
                    string chunk = string.Join("\n", lines, i, j - i);
                    if (chunk.Trim().Length == 0) { output.Add(chunk); i = j; matched = true; break; }
                    List<Candidate> cands;
                    string t = null;
                    if (exact.TryGetValue(chunk, out cands)) t = Choose(cands, context);
                    else if (j == i + 1) t = Translate(chunk, context, depth + 1);
                    if (t != null) { output.Add(t); i = j; matched = true; break; }
                }
                if (!matched) return null;
            }
            return string.Join("\n", output.ToArray());
        }

        // The placeholder values are translated too when they contain letters (an item name inside a sentence); a value that
        // is not an entry, such as a number or a typed name, is copied through.
        string TranslateTemplate(string s, ICollection<string> context, int depth)
        {
            foreach (var t in templates)
            {
                if (t.Literal.Length > 0 && s.IndexOf(t.Literal, StringComparison.Ordinal) < 0) continue;
                var m = t.Pattern.Match(s);
                if (!m.Success) continue;
                string result = Choose(t.Candidates, context);
                bool ok = true;
                for (int i = 0; i < t.Placeholders.Length; i++)
                {
                    string arg = m.Groups[i + 1].Value;
                    string targ = arg;
                    if (HasLetters(arg))
                    {
                        targ = Translate(arg, context, depth + 1);
                        if (targ == null) targ = arg;   // a name or a number formatted into the text
                    }
                    result = result.Replace(t.Placeholders[i], targ);
                }
                if (ok && !LooksUntranslated(result)) return result;
            }
            return null;
        }

        // True when the text has at least one letter. Placeholder values without letters (numbers) are copied without a lookup.
        static bool HasLetters(string s)
        {
            foreach (char ch in s) if (char.IsLetter(ch)) return true;
            return false;
        }

        // "Item name (99%-99.9% Purity)": a table entry with a parenthesised suffix the game appended.
        string TranslateSuffix(string s, ICollection<string> context, int depth)
        {
            var m = Regex.Match(s, @"^(.+?)(\s*[\(（][^\(\)（）]*[\)）])$", RegexOptions.Singleline);
            if (!m.Success) return null;
            string head = Translate(m.Groups[1].Value, context, depth + 1);
            if (head == null) return null;
            string suffix = m.Groups[2].Value;
            // The suffix may itself be an entry, with or without its leading space (" (Corrupted)").
            if (exact.ContainsKey(suffix)) return head + Choose(exact[suffix], context);
            if (exact.ContainsKey(suffix.Trim()))
                return head + suffix.Substring(0, suffix.Length - suffix.TrimStart().Length) + Choose(exact[suffix.Trim()], context);
            string inner = suffix.Trim();
            inner = inner.Substring(1, inner.Length - 2);
            string tInner = exact.ContainsKey(inner) ? Choose(exact[inner], context) : null;
            if (tInner != null) suffix = suffix.Replace(inner, tInner);
            else if (LooksUntranslated(suffix)) return null;
            // Otherwise the game appended it in a fixed form, like "(99%-99.9% Purity)", which stays.
            return head + suffix;
        }

        // Text the game concatenated from several entries ("Music Cassette - " + "Stardust" + " (Corrupted)"),
        // possibly with numbers and punctuation between them. Found with a shortest segmentation.
        string TranslateSegments(string s, ICollection<string> context)
        {
            int n = s.Length;
            if (n > MaxSegmentedLength) return null;
            int maxLen = Math.Min(MaxSegmentLength, longestValue);
            var cost = new int[n + 1];
            var next = new int[n + 1];
            var isEntry = new bool[n + 1];
            for (int i = 0; i <= n; i++) cost[i] = int.MaxValue;
            cost[n] = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                for (int len = Math.Min(maxLen, n - i); len >= 1; len--)
                {
                    if (cost[i + len] == int.MaxValue) continue;
                    string piece = s.Substring(i, len);
                    if (!exact.ContainsKey(piece)) continue;
                    if (!sourceIsCjk && len < 3) continue;
                    if (sourceIsCjk && len < 2 && IsCjk(piece[0])) continue;
                    if (IsWordChar(s[i]) && i > 0 && IsWordChar(s[i - 1])) continue;
                    if (IsWordChar(s[i + len - 1]) && i + len < n && IsWordChar(s[i + len])) continue;
                    if (cost[i + len] + 1 < cost[i]) { cost[i] = cost[i + len] + 1; next[i] = i + len; isEntry[i] = true; }
                }
                if (!char.IsLetter(s[i]) && cost[i + 1] != int.MaxValue && cost[i + 1] < cost[i])
                {
                    cost[i] = cost[i + 1]; next[i] = i + 1; isEntry[i] = false;
                }
            }
            if (cost[0] == int.MaxValue || cost[0] > MaxSegments || cost[0] < 1) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < n; i = next[i])
            {
                if (isEntry[i]) sb.Append(Choose(exact[s.Substring(i, next[i] - i)], context));
                else sb.Append(s[i]);
            }
            string r = sb.ToString();
            return LooksUntranslated(r) ? null : r;
        }

        // Splits after sentence-ending punctuation, keeping the separators (the odd-numbered pieces).
        static readonly Regex SentenceRx = new Regex(@"(?<=[。！？!?])(\s*)");

        // Text made of several sentences, each an entry of its own. Gives up when any sentence is unknown, so a half-translated
        // text is never produced.
        string TranslateSentences(string s, ICollection<string> context, int depth)
        {
            string[] pieces = SentenceRx.Split(s);
            if (pieces.Length < 3) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < pieces.Length; i++)
            {
                if (i % 2 == 1 || pieces[i].Trim().Length == 0) { sb.Append(pieces[i]); continue; }
                string t = Translate(pieces[i], context, depth + 1);
                if (t == null) return null;
                // CJK sentences follow each other without a space; most target languages need one.
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[sb.Length - 1]) && t.Length > 0 && IsWordChar(t[0])) sb.Append(' ');
                sb.Append(t);
            }
            return sb.ToString();
        }

        // Separators of the words in an entry key such as plant_trait_unappetizing_name.
        static readonly char[] KeySeparators = { '_', '-', '.', ' ' };

        // Several entries can share one source string ("难以下咽" is both an undrinkable wine and an unappetizing plant).
        // Prefer the entry whose key shares words with ids next to the text, then the more central table
        // (saves hold item and mechanic text far more than dialogue), then the most common translation,
        // then the shortest (singular over plural).
        string Choose(List<Candidate> cands, ICollection<string> context)
        {
            if (cands.Count == 1) return cands[0].Target;
            var groups = cands.GroupBy(c => c.Target, StringComparer.Ordinal).ToList();
            if (groups.Count == 1) return groups[0].Key;
            return groups
                .OrderByDescending(g => g.Max(c => Overlap(c.Key, context)))
                .ThenBy(g => g.Any(c => c.Key.IndexOf("plural", StringComparison.OrdinalIgnoreCase) >= 0) ? 1 : 0)
                .ThenBy(g => g.Min(c => c.Priority))
                .ThenByDescending(g => g.Count())
                .ThenBy(g => IsShouting(g.Key) ? 1 : 0)
                .ThenBy(g => g.Key.Length)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
        }

        // All-caps text; a title-case or lower-case entry is preferred when other things are equal.
        static bool IsShouting(string s)
        {
            return s.Any(char.IsLetter) && s.ToUpperInvariant() == s && s.ToLowerInvariant() != s;
        }

        // How many words (longer than two letters) of the key also appear in the ids next to the text.
        static int Overlap(string key, ICollection<string> context)
        {
            if (context == null || context.Count == 0) return 0;
            int n = 0;
            foreach (var tok in key.ToLowerInvariant().Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries))
                if (tok.Length > 2 && context.Contains(tok)) n++;
            return n;
        }

        // Splits an id such as name_odin into words and adds them to `into`; the same rule as Overlap uses on keys.
        public static void AddContextTokens(string idLike, HashSet<string> into)
        {
            foreach (var tok in idLike.ToLowerInvariant().Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries))
                if (tok.Length > 2) into.Add(tok);
        }
    }
}
