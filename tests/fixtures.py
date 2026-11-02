# Exports test fixtures for the C# engine: the game's zh->en string tables (from the UnityPy dump)
# and the reference translations produced earlier by the hand-checked Python run.
import ast, json, os, re
T = os.environ["TEMP"]
OUT = os.path.join(T, "slc_test")
d = json.load(open(os.path.join(T, "loc_dump.json"), encoding="utf-8"))
ORDER = ["Item", "ClientName", "Mechanic", "UI", "Interactable", "NetworkUpgrade", "RepPerkTable", "PerkTable",
         "Dialogue", "PhoneDialogue", "Hint", "MainMenu", "Tutorial", "NewGame"]
esc = lambda s: s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")
n = 0
with open(os.path.join(OUT, "tables_zh_en.tsv"), "w", encoding="utf-8", newline="\n") as f:
    for i, tn in enumerate(ORDER):
        keys = {e["m_Id"]: e["m_Key"] for e in d["shared"][tn + " Shared Data"]["m_Entries"]}
        en = {e["m_Id"]: e["m_Localized"] for e in d["en"][tn + "_en"]["m_TableData"]}
        for e in d["zh"][tn + "_zh"]["m_TableData"]:
            if e["m_Localized"] and en.get(e["m_Id"]):
                f.write("\t".join([tn, str(i), esc(keys.get(e["m_Id"], "")), esc(e["m_Localized"]), esc(en[e["m_Id"]])]) + "\n"); n += 1
print("table rows", n)
m = 0
with open(os.path.join(T, "map_report.txt"), encoding="utf-8") as src, open(os.path.join(OUT, "expected.tsv"), "w", encoding="utf-8", newline="\n") as f:
    on = False
    for line in src:
        if line.startswith("=== DONE"): on = True; continue
        if not on or "\t=> " not in line: continue
        cnt, rest = line.rstrip("\n").split("\t", 1)
        z, e = rest.split("\t=> ", 1)
        z, e = ast.literal_eval(z), ast.literal_eval(e)
        if z.lstrip().startswith("{"): continue
        f.write(esc(z) + "\t" + esc(e) + "\n"); m += 1
print("expected rows", m)
