# Exports test fixtures for the C# engine: the game's zh->en string tables (from the UnityPy dump)
# and the reference translations produced earlier by the hand-checked Python run.
import ast
import json
import os
from collections.abc import Iterator, Mapping, Sequence
from typing import Final, cast


# Strict-typing helpers: the dump and the report are untyped JSON and text, so each value is checked once here and the
# rest of the file works with exact types. A wrong shape stops the script with a message naming where.
def as_dict(value: object, where: str) -> Mapping[str, object]:
    if not isinstance(value, dict):
        raise SystemExit(f"{where}: expected an object")
    return {str(k): v for k, v in cast("dict[object, object]", value).items()}


def as_list(value: object, where: str) -> Sequence[object]:
    if not isinstance(value, list):
        raise SystemExit(f"{where}: expected a list")
    return tuple(cast("list[object]", value))


def as_str(value: object, where: str) -> str:
    if not isinstance(value, str):
        raise SystemExit(f"{where}: expected a string")
    return value


def as_int(value: object, where: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise SystemExit(f"{where}: expected an integer")
    return value


def member(obj: Mapping[str, object], key: str, where: str) -> object:
    if key not in obj:
        raise SystemExit(f"{where}: missing '{key}'")
    return obj[key]


def load_json(path: str) -> object:
    with open(path, encoding="utf-8") as f:
        data: object = json.load(f)
    return data


# The tables of ConvertMod.Tables, in the same order; the index written to the tsv is the Translator priority.
ORDER: Final[Sequence[str]] = ("Item", "ClientName", "Mechanic", "UI", "Interactable", "NetworkUpgrade", "RepPerkTable",
                               "PerkTable", "Dialogue", "PhoneDialogue", "Hint", "MainMenu", "Tutorial", "NewGame")


# One line per entry in the tsv files: tabs and line breaks inside a text are escaped; EngineTest.Unesc reverses it.
def esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


# The entries of one table of the dump. Sections: "shared" holds the keys, "en" and "zh" the texts.
def entries(dump: Mapping[str, object], section: str, table: str, listing: str) -> Sequence[Mapping[str, object]]:
    sec = as_dict(member(dump, section, "dump"), section)
    tbl = as_dict(member(sec, table, section), table)
    return [as_dict(e, table) for e in as_list(member(tbl, listing, table), listing)]


# Entries without a text in either language are left out; they cannot be translated either way.
def table_rows(dump: Mapping[str, object]) -> Iterator[str]:
    """One tab-separated line per string that exists in both Chinese and English: table, index, key, zh, en."""
    for i, tn in enumerate(ORDER):
        keys = {as_int(member(e, "m_Id", tn), "m_Id"): as_str(member(e, "m_Key", tn), "m_Key")
                for e in entries(dump, "shared", tn + " Shared Data", "m_Entries")}
        en = {as_int(member(e, "m_Id", tn), "m_Id"): as_str(member(e, "m_Localized", tn), "m_Localized")
              for e in entries(dump, "en", tn + "_en", "m_TableData")}
        for e in entries(dump, "zh", tn + "_zh", "m_TableData"):
            zh = as_str(member(e, "m_Localized", tn), "m_Localized")
            eid = as_int(member(e, "m_Id", tn), "m_Id")
            if zh and en.get(eid):
                yield "\t".join([tn, str(i), esc(keys.get(eid, "")), esc(zh), esc(en[eid])]) + "\n"


def expected_rows(report: Sequence[str]) -> Iterator[str]:
    """The reference translations after the report's '=== DONE' line, without the JSON values."""
    started = False
    for line in report:
        if line.startswith("=== DONE"):
            started = True
            continue
        if not started or "\t=> " not in line:
            continue
        _, rest = line.rstrip("\n").split("\t", 1)
        z, e = rest.split("\t=> ", 1)
        zh: object = ast.literal_eval(z)
        en: object = ast.literal_eval(e)
        zs, es = as_str(zh, "report"), as_str(en, "report")
        if zs.lstrip().startswith("{"):
            continue
        yield esc(zs) + "\t" + esc(es) + "\n"


# Writes with LF endings on every platform, so the C# test reads the same bytes everywhere.
def write_lines(path: str, lines: Iterator[str]) -> int:
    n = 0
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for line in lines:
            f.write(line)
            n += 1
    return n


# Needs %TEMP%/loc_dump.json (UnityPy dump of the game's tables) and %TEMP%/map_report.txt (the earlier reference
# run). Writes tables_zh_en.tsv and expected.tsv to %TEMP%/slc_test for tests/EngineTest.cs.
def main() -> None:
    temp = os.environ["TEMP"]
    out = os.path.join(temp, "slc_test")
    dump = as_dict(load_json(os.path.join(temp, "loc_dump.json")), "loc_dump.json")
    print("table rows", write_lines(os.path.join(out, "tables_zh_en.tsv"), table_rows(dump)))
    with open(os.path.join(temp, "map_report.txt"), encoding="utf-8") as src:
        report = src.readlines()
    print("expected rows", write_lines(os.path.join(out, "expected.tsv"), expected_rows(report)))


if __name__ == "__main__":
    main()
