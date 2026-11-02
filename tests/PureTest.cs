// Checks the translation engine (src/*.Pure.cs) on hand-made table entries: no game, Unity or save is needed.
// tests/EngineTest.cs checks the same engine against the game's real tables and a real save.
//   csc -out:PureTest.exe tests\PureTest.cs src\Translator.Pure.cs src\JsonText.Pure.cs && PureTest.exe
using System;
using System.Collections.Generic;
using SaveLanguageConverter;

static class PureTest
{
    static int failed;

    static void Check(string what, bool ok, string got = null)
    {
        if (!ok) failed++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what + (ok || got == null ? "" : "  (got: " + got + ")"));
    }

    static void Same(string what, string want, string got) { Check(what, want == got, got ?? "null"); }

    static int Main()
    {
        // A small Chinese -> English table, in the shape of the game's own.
        var tr = new Translator("zh");
        tr.Add("Item", 0, "item_water_name", "纯净水", "Pure Water");
        tr.Add("Item", 0, "item_cassette_name", "音乐磁带", "Music Cassette");
        tr.Add("Item", 0, "item_song_stardust", "星尘", "Stardust");
        tr.Add("Mechanic", 2, "mech_durability", "耐久度：{0}/{1}", "Durability: {0}/{1}");
        tr.Add("Mechanic", 2, "mech_corrupted", "已损坏", "Corrupted");
        tr.Add("Item", 0, "plant_trait_unappetizing_name", "难吃", "Unappetizing");
        tr.Add("UI", 3, "ui_unappetizing_label", "难吃", "Gross");

        Console.WriteLine("-- matching");
        Same("an exact entry", "Pure Water", tr.Translate("纯净水", null));
        Same("a template, with its numbers kept", "Durability: 3/10", tr.Translate("耐久度：3/10", null));
        Same("a template whose argument is game text", "Durability: Pure Water/10", tr.Translate("耐久度：纯净水/10", null));
        Same("surrounding spaces are kept", " Pure Water ", tr.Translate(" 纯净水 ", null));
        Check("text that is not game text is left alone", tr.Translate("我的名字", null) == null);
        Check("an empty string is not game text", tr.Translate("", null) == null);

        Console.WriteLine("-- choosing between entries that share a source");
        var ctx = new HashSet<string>();
        Translator.AddContextTokens("category: plant_taste", ctx);
        Same("the entry whose key shares words with the ids next to the text wins", "Unappetizing", tr.Translate("难吃", ctx));
        Same("without context, the more central table wins", "Unappetizing", tr.Translate("难吃", null));

        Console.WriteLine("-- untranslated text");
        Check("Chinese text left in an English save is noticed", tr.LooksUntranslated("还有中文"));
        Check("English text is not flagged", !tr.LooksUntranslated("Pure Water"));
        Check("a translator from English never flags text", !new Translator("en").LooksUntranslated("还有中文"));

        Console.WriteLine("-- JSON rewriting");
        string json = "{\"name\":\"纯净水\",\"id\":\"item_water\",\"n\":3,\"notes\":[\"星尘\",\"x\"]}";
        string rewritten = JsonText.Rewrite(json, (member, value, context) =>
            member == "id" ? null : tr.Translate(value, context), _ => new HashSet<string>());
        Same("strings are replaced in place, everything else is kept",
            "{\"name\":\"Pure Water\",\"id\":\"item_water\",\"n\":3,\"notes\":[\"Stardust\",\"x\"]}", rewritten);
        Same("quotes and backslashes are escaped", "\"a\\\"b\\\\c\"", JsonText.Quote("a\"b\\c"));
        Check("JSON is recognised", JsonText.LooksLikeJson("{\"a\":1}") && JsonText.LooksLikeJson("[1]"));
        Check("plain text is not JSON", !JsonText.LooksLikeJson("Pure Water"));
        Check("broken JSON is refused rather than half rewritten",
            JsonText.Rewrite("{\"a\":", (m, v, c) => v, _ => new HashSet<string>()) == null);

        Console.WriteLine(failed == 0 ? "all passed" : failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
