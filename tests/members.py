# Which save fields does the game localize? A field is "display text" when it holds Chinese in a zh save;
# it is "mixed" when the same zh save also has English table text in it (internal names that are never localized).
#   python members.py <zh save> <tables_zh_en.tsv>
import json
import re
import sys
from collections import Counter, defaultdict
from collections.abc import Iterable, Iterator, Sequence
from typing import Final, Literal, NamedTuple, cast

# Characters of the CJK Unified Ideographs block (including extension A): a string with one of them is Chinese text.
CJK: Final = re.compile(r"[㐀-鿿]")
# Identifier-like text (lower or upper case, digits, underscores); the same shape TextPolicy.IsIdLike accepts.
INTERNAL_NAME: Final = re.compile(r"[a-z0-9_]+|[A-Z0-9_]+")
# Dictionary keys that are numbers carry no field name, so the enclosing field's name is kept for the values.
INDEX_KEY: Final = re.compile(r"-?\d+")
ROOT: Final = "(root)"        # the member of a string that sits outside every named field


# A string found in the save: Chinese ("cjk"), or English text that equals a table entry ("english").
class Hit(NamedTuple):
    kind: Literal["cjk", "english"]
    member: str
    text: str


# The save is an Easy Save 3 file: integer dictionary keys are unquoted and some typographic quotes are escaped, so it
# is repaired into JSON first.
def parse_save(text: str) -> object:
    fixed = text.replace("\\“", "“").replace("\\”", "”")
    quoted = re.sub(r'([{,])(-?\d+):', r'\1"\2":', fixed)
    root: object = json.loads(quoted)
    return root


# Reverses the escaping of the tsv written by fixtures.py (only the escapes it produces).
def unescape(s: str) -> str:
    return s.replace("\\n", "\n").replace("\\t", "\t").replace("\\\\", "\\")


# Column 5 of the tsv: the English text of every entry.
def english_values(lines: Iterable[str]) -> frozenset[str]:
    return frozenset(unescape(line.rstrip("\n").split("\t")[4]) for line in lines)


def nested_json(text: str) -> object | None:
    """The JSON a string field holds, if it holds any (the save stores some fields as JSON inside a string)."""
    if text.lstrip()[:1] not in "{[":
        return None
    try:
        inner: object = json.loads(text)
    except ValueError:
        return None
    return inner


# Yields every Chinese or English-table string with the name of the field that holds it. Strings that contain JSON
# are opened, since the game stores inventories that way.
def walk(node: object, member: str, en_values: frozenset[str]) -> Iterator[Hit]:
    if isinstance(node, dict):
        for k, v in cast("dict[str, object]", node).items():
            yield from walk(v, member if INDEX_KEY.fullmatch(k) else k, en_values)
    elif isinstance(node, list):
        for v in cast("list[object]", node):
            yield from walk(v, member, en_values)
    elif isinstance(node, str):
        inner = nested_json(node)
        if inner is not None:
            yield from walk(inner, member, en_values)
        elif CJK.search(node):
            yield Hit("cjk", member, node)
        elif node in en_values and not INTERNAL_NAME.fullmatch(node):
            yield Hit("english", member, node)


# The three groups TextPolicy needs: fields that only hold Chinese (Localized), fields that hold both (Mixed), and fields
# that hold English table text but are never localized (skipped). The first two lists came from this report.
def report(hits: Sequence[Hit]) -> Iterator[str]:
    cjk = Counter(h.member for h in hits if h.kind == "cjk")
    eng = Counter(h.member for h in hits if h.kind == "english")
    examples: defaultdict[str, set[str]] = defaultdict(set)
    for h in hits:
        if h.kind == "english":
            examples[h.member].add(h.text)
    yield "SAFE (localized, never English table text):"
    yield "  " + ", ".join(sorted(m for m in cjk if m not in eng))
    yield "MIXED (localized sometimes):"
    for m in sorted(m for m in cjk if m in eng):
        yield "  %s: cjk %d, english %d e.g. %s" % (m, cjk[m], eng[m], sorted(examples[m])[:4])
    yield "NEVER localized but holds English table text:"
    for m in sorted(m for m in eng if m not in cjk):
        yield "  %s: %d e.g. %s" % (m, eng[m], sorted(examples[m])[:4])


def main(argv: Sequence[str]) -> None:
    with open(argv[0], encoding="utf-8") as f:
        root = parse_save(f.read())
    with open(argv[1], encoding="utf-8") as f:
        en_values = english_values(f)
    for line in report(tuple(walk(root, ROOT, en_values))):
        print(line)


if __name__ == "__main__":
    main(sys.argv[1:])
