using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SaveLanguageConverter
{
    // Decides which strings in a save are display text, and rewrites them with a Translator.
    // A value is only translated when it is an exact (or composed) table entry, and then only where the game
    // itself writes localized text: non-ASCII text anywhere outside id fields (ids are ASCII), ASCII text only
    // in the fields listed below. Fields such as "subtitle" hold English table text even in a Chinese save,
    // so they are internal names and stay as they are.
    sealed class TextPolicy
    {
        readonly Translator translator;
        public int Changed;
        public readonly Dictionary<string, int> Untranslated = new Dictionary<string, int>(StringComparer.Ordinal);

        public TextPolicy(Translator translator) { this.translator = translator; }

        // Fields that hold text in the player's language, found by comparing a Chinese save with the string tables.
        static readonly HashSet<string> Localized = new HashSet<string>(StringComparer.Ordinal)
        {
            "display", "publicDisplay", "actualDisplay", "identifier", "itemName", "clientName", "name",
            "description", "shortDescription", "flavorText", "title", "reason", "requesterName", "factionDisplay",
            "newsName", "newsDescription", "nightLogs", "dialogHistory", "bestFindItem", "bestFlipItem",
            "biggestSaleClient", "biggestSaleItem", "itemDisplayNameCache", "displayName", "internalValueString",
        };

        // Localized, but the same fields also keep some names in English in a Chinese save (phone contacts,
        // brand names). English text there is only replaced when it is exactly one entry outside the ClientName table
        // (night services, events); non-ASCII text is converted as usual.
        static readonly HashSet<string> Mixed = new HashSet<string>(StringComparer.Ordinal)
        {
            "displayName", "internalValueString",
        };

        static bool SkipMember(string member)
        {
            if (member == null) return false;
            string m = member.ToLowerInvariant();
            if (m.EndsWith("id") || m.EndsWith("ids") || m == "key" || m.EndsWith("keys")) return true;
            string[] never = { "guid", "uuid", "hash", "version", "path", "file", "locale", "scenario", "difficulty",
                               "storename", "playername", "customname", "seed", "sprite", "sound", "prefab", "icon" };
            return never.Any(n => m.Contains(n));
        }

        public static bool IsIdLike(string s)
        {
            if (s.Length == 0) return false;
            bool lower = true, upper = true;
            foreach (char c in s)
            {
                if (c > 127 || char.IsWhiteSpace(c)) return false;
                if (char.IsUpper(c)) lower = false;
                if (char.IsLower(c)) upper = false;
            }
            return s.IndexOf('_') >= 0 || lower || upper;
        }

        public static HashSet<string> ContextOf(string value)
        {
            var set = new HashSet<string>();
            if (!string.IsNullOrEmpty(value) && IsIdLike(value)) Translator.AddContextTokens(value, set);
            return set;
        }

        // Returns the new value, or null to keep the old one.
        public string Rewrite(string member, string value, HashSet<string> context)
        {
            if (string.IsNullOrEmpty(value) || SkipMember(member)) return null;
            if (JsonText.LooksLikeJson(value))
                return JsonText.Rewrite(value, Rewrite, ContextOf);

            bool nonAscii = false;
            foreach (char c in value) if (c > 127) { nonAscii = true; break; }
            if (!nonAscii && (IsIdLike(value) || member == null || !Localized.Contains(member))) return null;

            // In a mixed field, text in a Latin script is only replaced when it is exactly one game entry, and never a
            // person's name: the game keeps contacts in English, but names services and events in the player's language.
            // A person is recognised by the entry it was made from, a locID such as name_odin, next to the text.
            bool mixedLatin = !nonAscii && Mixed.Contains(member);
            if (mixedLatin && context != null && context.Contains("name")) return null;
            string t = mixedLatin ? translator.TranslateExact(value, context, "ClientName") : translator.Translate(value, context);
            if (t == null)
            {
                if (translator.LooksUntranslated(value))
                {
                    int n;
                    Untranslated.TryGetValue(value, out n);
                    Untranslated[value] = n + 1;
                }
                return null;
            }
            if (t != value) Changed++;
            return t;
        }
    }

    // Walks the loaded save (PlayerStore, StoreStation and everything they hold) through the fields listed in KnownTypes.
    sealed class SaveWalker
    {
        sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        readonly TextPolicy policy;
        readonly Dictionary<Type, FieldInfo[]> known;
        readonly Dictionary<Type, List<FieldInfo>> fieldCache = new Dictionary<Type, List<FieldInfo>>();
        readonly HashSet<object> visited = new HashSet<object>(new RefEq());
        public readonly List<string> Errors = new List<string>();

        public SaveWalker(TextPolicy policy, Dictionary<Type, FieldInfo[]> known)
        {
            this.policy = policy;
            this.known = known;
        }

        List<FieldInfo> FieldsOf(Type t)
        {
            List<FieldInfo> list;
            if (fieldCache.TryGetValue(t, out list)) return list;
            list = new List<FieldInfo>();
            for (Type c = t; c != null && c != typeof(object); c = c.BaseType)
            {
                FieldInfo[] fs;
                if (known.TryGetValue(c, out fs)) list.AddRange(fs.Where(f => !f.IsLiteral));
            }
            fieldCache[t] = list;
            return list;
        }

        public void Walk(object o, string member, HashSet<string> context)
        {
            if (o == null || o is string || o is Delegate || o is UnityEngine.Object) return;
            Type t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || t.IsValueType) return;
            if (!visited.Add(o)) return;

            var dict = o as IDictionary;
            if (dict != null) { WalkDictionary(dict, member, context); return; }
            var list = o as IList;
            if (list != null) { WalkList(list, member, context); return; }

            var fields = FieldsOf(t);
            if (fields.Count == 0) return;
            var ctx = new HashSet<string>();
            foreach (var f in fields)
            {
                if (f.FieldType != typeof(string)) continue;
                var v = f.GetValue(o) as string;
                if (v != null) foreach (var tok in TextPolicy.ContextOf(v)) ctx.Add(tok);
            }
            foreach (var f in fields)
            {
                try { WalkField(o, f, ctx); }
                catch (Exception e) { Errors.Add(t.Name + "." + f.Name + ": " + e.Message); }
            }
        }

        void WalkField(object owner, FieldInfo f, HashSet<string> ctx)
        {
            object v = f.GetValue(owner);
            if (v == null) return;
            var s = v as string;
            if (s != null)
            {
                string r = policy.Rewrite(f.Name, s, ctx);
                if (r != null && r != s) f.SetValue(owner, r);
                return;
            }
            // Collections of strings that are neither lists nor dictionaries are rebuilt in place.
            var queue = v as Queue<string>;
            if (queue != null)
            {
                var items = queue.Select(x => policy.Rewrite(f.Name, x, ctx) ?? x).ToList();
                queue.Clear();
                foreach (var x in items) queue.Enqueue(x);
                return;
            }
            var set = v as HashSet<string>;
            if (set != null)
            {
                var items = set.Select(x => policy.Rewrite(f.Name, x, ctx) ?? x).ToList();
                set.Clear();
                foreach (var x in items) set.Add(x);
                return;
            }
            Walk(v, f.Name, ctx);
        }

        void WalkList(IList list, string member, HashSet<string> context)
        {
            if (list.IsReadOnly && !(list is Array)) return;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var s = item as string;
                if (s != null)
                {
                    string r = policy.Rewrite(member, s, context);
                    if (r != null && r != s) list[i] = r;
                }
                else Walk(item, member, context);
            }
        }

        void WalkDictionary(IDictionary dict, string member, HashSet<string> context)
        {
            var entries = new List<DictionaryEntry>();
            foreach (DictionaryEntry e in dict) entries.Add(e);
            var renamed = new List<KeyValuePair<object, object>>();
            foreach (var e in entries)
            {
                var sv = e.Value as string;
                if (sv != null)
                {
                    string r = policy.Rewrite(member, sv, context);
                    if (r != null && r != sv) dict[e.Key] = r;
                }
                else Walk(e.Value, member, context);

                // Keys are only renamed when they are display text (e.g. a client name used as a portrait key).
                var sk = e.Key as string;
                if (sk != null && !TextPolicy.IsIdLike(sk))
                {
                    string rk = policy.Rewrite(member, sk, context);
                    if (rk != null && rk != sk) renamed.Add(new KeyValuePair<object, object>(sk, rk));
                }
            }
            foreach (var kv in renamed)
            {
                if (dict.Contains(kv.Value)) continue;
                object val = dict[kv.Key];
                dict.Remove(kv.Key);
                dict[kv.Value] = val;
            }
        }
    }
}
