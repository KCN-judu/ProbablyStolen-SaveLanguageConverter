using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SaveLanguageConverter
{
    // Walks the loaded save (PlayerStore, StoreStation and everything they hold) through the fields listed in KnownTypes.
    // Reads and writes game save classes by reflection, through the field lists of KnownTypes.g.cs, so it names no game type
    // itself. Which strings change is decided by TextPolicy; this class only finds every string and writes back the result.
    // It edits the loaded objects in place, which is why it runs before the game builds inventories from them.
    sealed class SaveWalker
    {
        // Reference equality for the visited set: save classes may override Equals, and the walk must visit each object once
        // even when the same object is reachable by several paths or points back to its owner.
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

        // The listed fields of a type and of its base classes. KnownTypes lists DeclaredOnly fields, so the chain is followed
        // here. Cached per type because a save holds very many objects of few types.
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

        // Entry point and recursion. `member` is the name of the field (or the enclosing field, for items of a collection) that
        // holds the value; `context` is the set of id tokens next to it. Strings, delegates, Unity objects and value types are
        // leaves. Types KnownTypes does not list are not entered. It omits types without strings and the inventory classes, which a
        // save stores as JSON text inside string fields; TextPolicy rewrites those through JsonText.
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
            // Ids stored in the same object (item ids, locIDs such as name_odin) help Translator pick the right entry when
            // several share a source string, and tell TextPolicy whether a name belongs to a person.
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

        // One field: strings are rewritten and assigned back, collections of strings are rebuilt, anything else is walked.
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

        // Arrays are writable by index; other read-only lists cannot be changed and are skipped.
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

        // Values are rewritten in place. Entries are copied first and keys renamed last, because a dictionary cannot be
        // changed while it is enumerated.
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
