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
