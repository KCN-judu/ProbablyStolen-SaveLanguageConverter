using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SaveLanguageConverter
{
    // Decides which strings in a save are display text, and rewrites them with a Translator.
    // A value is only translated when it is an exact (or composed) table entry, and then only where the game
    // itself writes localized text: non-ASCII text anywhere outside id fields (ids are ASCII), ASCII text only
    // in the fields listed below. Fields such as "subtitle" hold English table text even in a Chinese save,
    // so they are internal names and stay as they are.
    // Pure file (see code_standard.md): no game or Unity types, checked headless against real tables and a real save by
    // tests/EngineTest.cs. SaveWalker.cs asks it about every string; Translator.Pure.cs does the lookup it hands over to.
    sealed class TextPolicy
    {
        readonly Translator translator;
        // Counters for the log line written after a conversion.
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

        // Substrings of field names that never hold display text (ids, paths, the names the player typed, and so on).
        static readonly string[] Never = { "guid", "uuid", "hash", "version", "path", "file", "locale", "scenario",
            "difficulty", "storename", "playername", "customname", "seed", "sprite", "sound", "prefab", "icon" };

        // Asked for every string in the save (well over 100,000), but a save has only a few hundred field names, so
        // each answer is worked out once.
        static readonly Dictionary<string, bool> skipByMember = new Dictionary<string, bool>(StringComparer.Ordinal);

        // True for fields whose name says id, key or one of Never. Unnamed values (member == null) are not skipped.
        static bool SkipMember(string member)
        {
            if (member == null) return false;
            bool skip;
            if (skipByMember.TryGetValue(member, out skip)) return skip;
            string m = member.ToLowerInvariant();
            skip = m.EndsWith("id", StringComparison.Ordinal) || m.EndsWith("ids", StringComparison.Ordinal) || m == "key"
                || m.EndsWith("keys", StringComparison.Ordinal);
            for (int i = 0; !skip && i < Never.Length; i++) skip = m.IndexOf(Never[i], StringComparison.Ordinal) >= 0;
            skipByMember[member] = skip;
            return skip;
        }

        // True for an internal identifier: ASCII only, no spaces, and all lower case, all upper case or containing an underscore.
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

        // Word tokens of an id-like value, such as item_water_pure -> item, water, pure. Collected from the strings of an object
        // and used by Translator.Choose to prefer the entry whose key shares words with them.
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
}
