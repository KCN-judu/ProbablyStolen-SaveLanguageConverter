# Which save fields does the game localize? A field is "display text" when it holds Chinese in a zh save;
# it is "mixed" when the same zh save also has English table text in it (internal names that are never localized).
import json, re, sys, collections
CJK = re.compile(r"[㐀-鿿]")
s = open(sys.argv[1], encoding="utf-8").read()
s = s.replace("\\“", "“").replace("\\”", "”")
s = re.sub(r'([{,])(-?\d+):', r'\1"\2":', s)
root = json.loads(s)
en_values = set()
for line in open(sys.argv[2], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    en_values.add(p[4].replace("\\n", "\n").replace("\\t", "\t").replace("\\\\", "\\"))
cjk = collections.Counter(); eng = collections.Counter(); eng_ex = collections.defaultdict(set)

def walk(node, member):
    if isinstance(node, dict):
        for k, v in node.items():
            walk(v, k if not re.fullmatch(r"-?\d+", k) else member)
    elif isinstance(node, list):
        for v in node: walk(v, member)
    elif isinstance(node, str):
        t = node.lstrip()
        if t[:1] in "{[":
            try: walk(json.loads(node), member); return
            except Exception: pass
        if CJK.search(node): cjk[member] += 1
        elif node in en_values and not re.fullmatch(r"[a-z0-9_]+|[A-Z0-9_]+", node):
            eng[member] += 1; eng_ex[member].add(node)

walk(root, None)
print("SAFE (localized, never English table text):")
print("  " + ", ".join(sorted(m for m in cjk if m not in eng)))
print("MIXED (localized sometimes):")
for m in sorted(m for m in cjk if m in eng): print("  %s: cjk %d, english %d e.g. %s" % (m, cjk[m], eng[m], sorted(eng_ex[m])[:4]))
print("NEVER localized but holds English table text:")
for m in sorted(m for m in eng if m not in cjk): print("  %s: %d e.g. %s" % (m, eng[m], sorted(eng_ex[m])[:4]))
