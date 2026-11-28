using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

static partial class Program
{
    static MemoryStream LocaleStream(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    static string LocaleFixture(string catalog, string locale, string cardId, string english, string translation) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            meta = catalog,
            locale,
            text = new Dictionary<string, string[]> { [cardId] = new[] { LocalizedCardText.Key(english), translation } }
        });

    static void LocalizedTextTests(string temp)
    {
        const string spring = "orchard-spring";
        const string summer = "orchard-summer";
        const string card = "Test_OrchardLantern";
        const string englishSpring = "Spark a lantern near the gate.";
        const string englishSummer = "Spark two lanterns near the gate.";
        const string russianSpring = "[x]Зажгите фонарь у ворот.";
        const string russianSummer = "Зажгите два фонаря у ворот.";
        const string germanSpring = "Entzünde eine Laterne am Tor.";
        string[] locales = GameLanguage.Locales.Where(locale => locale != "enUS").ToArray();

        Check(locales.Length == 13, "13 non-English client locales");
        Check(LocalizedCardText.ResourceName(spring, "ruRU") == "SoloStone.Facts.orchard-spring.ruRU.json",
            "locale resource name includes the catalog");
        foreach (string none in new[] { null, "", "enUS", "enGB", "xxYY", "../x", "ruru", "de-DE" })
            Check(LocalizedCardText.Normalize(none) == null, "languages that use engine English " + none);
        foreach (string locale in locales)
            Check(LocalizedCardText.Normalize(locale) == locale, "a client locale is kept " + locale);

        Check(LocalizedCardText.Resolve("ruRU", null, out bool settled) == "ruRU" && settled, "the running client locale settles");
        Check(LocalizedCardText.Resolve("ruRU", "deDE", out settled) == "ruRU" && settled, "the actual client locale wins");
        Check(LocalizedCardText.Resolve("enUS", "ruRU", out settled) == null && !settled, "startup English answer is temporary");
        Check(LocalizedCardText.Resolve("enGB", "enUS", out settled) == null && settled, "English variants settle together");
        Check(LocalizedCardText.Resolve(null, "ruRU", out settled) == "ruRU" && !settled, "Options gives a temporary locale");
        Check(LocalizedCardText.Resolve(null, null, out settled) == null && !settled, "uninitialized locale is temporary");

        Check(LocalizedCardText.Key("") == "811c9dc5" && LocalizedCardText.Key("a") == "e40c292c" &&
            LocalizedCardText.Key(" \ta\n ") == LocalizedCardText.Key("a"), "the FNV key trims both ends");
        Check(LocalizedCardText.Key("Lift $2 lanterns. @spelldmg") == LocalizedCardText.Key("Lift $2 lanterns.") &&
            LocalizedCardText.Key("a b") != LocalizedCardText.Key("a  b") && LocalizedCardText.Key("ü") != LocalizedCardText.Key("u"),
            "the key uses UTF-8 and preserves inner spaces");
        string resolved = "Lift a\u00a0lantern near the gate.";
        string key = LocalizedCardText.Key(resolved);
        Check(LocalizedCardText.SameEnglish(key, "[x]Lift a_lantern&\nnear the gate.") &&
            LocalizedCardText.SameEnglish(key, resolved), "raw layout marks preserve equivalence");
        Check(!LocalizedCardText.SameEnglish(key, "Lift two lanterns near the gate."), "word changes make a translation stale");

        const string parserFixture = """
            {
                "schemaVersion": 2,
                "meta": "orchard-spring",
                "locale": "ruRU",
                "provenance": ["author", [1, {"missing": null}]],
                "unknownNumber": -3.5e2,
                "text": {
                    "A": ["00000000", "a\n\"q\" \u00fc \/ \\ \t"],
                    "B": ["00000001", "\u00fc\u65e5"]
                }
            }
            """;
        var entries = LocalizedCardText.Read(LocaleStream(parserFixture), spring, "ruRU");
        Check(entries.Count == 2 && entries["A"].Text == "a\n\"q\" ü / \\ \t" &&
            entries["A"].Key == "00000000" && entries["B"].Text == "ü日", "reader preserves JSON escapes and declared keys");
        foreach (string malformed in new[] {
            parserFixture.Replace("orchard-spring", summer), parserFixture.Replace("ruRU", "deDE"),
            parserFixture.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"),
            parserFixture.Replace("\"text\"", "\"other\""), parserFixture + "x",
            parserFixture.Substring(0, parserFixture.Length - 1), parserFixture.Replace("\\u00fc", "\\u00f?"),
            parserFixture.Replace("\\t", "\\x")
        })
        {
            bool refused = false;
            try { LocalizedCardText.Read(LocaleStream(malformed), spring, "ruRU"); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "malformed locale data or a mismatched catalog or language is refused");
        }

        string springRu = LocalizedCardText.ResourceName(spring, "ruRU");
        string summerRu = LocalizedCardText.ResourceName(summer, "ruRU");
        string springDe = LocalizedCardText.ResourceName(spring, "deDE");
        var opened = new List<string>();
        var originalOpen = LocalizedCardText.Open;
        try
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [springRu] = LocaleFixture(spring, "ruRU", card, englishSpring, russianSpring),
                [summerRu] = LocaleFixture(summer, "ruRU", card, englishSummer, russianSummer),
                [springDe] = LocaleFixture(spring, "deDE", card, englishSpring, germanSpring),
                [LocalizedCardText.ResourceName("empty-orchard", "ruRU")] = LocaleFixture(spring, "ruRU", card, englishSpring, russianSpring),
                [LocalizedCardText.ResourceName("broken-orchard", "ruRU")] = "{\"schemaVersion\":2"
            };
            LocalizedCardText.Open = name =>
            {
                opened.Add(name);
                if (name == LocalizedCardText.ResourceName(summer, "deDE")) throw new IOException("Synthetic read failure");
                return files.TryGetValue(name, out string json) ? LocaleStream(json) : null;
            };
            LocalizedCardText.Reset();

            Check(LocalizedCardText.Lookup(spring, "ruRU", card, englishSpring, out string shown) ==
                LocalizedCardText.Status.Localized && shown == russianSpring, "spring uses its translation and keeps raw layout");
            Check(LocalizedCardText.Lookup(summer, "ruRU", card, englishSummer, out shown) ==
                LocalizedCardText.Status.Localized && shown == russianSummer, "summer uses its translation for the same card ID");
            Check(LocalizedCardText.Lookup(spring, "ruRU", card, englishSummer, out shown) ==
                LocalizedCardText.Status.Stale && shown == null, "summer English falls back in the spring catalog");
            Check(LocalizedCardText.Lookup(spring, "deDE", card, englishSpring + " @spelldmg", out shown) ==
                LocalizedCardText.Status.Localized && shown == germanSpring, "language and catalog both participate in cache identity");
            Check(LocalizedCardText.Lookup(spring, "ruRU", "Test_Unknown", englishSpring, out shown) ==
                LocalizedCardText.Status.NoEntry && shown == null, "a missing card falls back");
            Check(LocalizedCardText.Lookup(spring, "ruRU", null, englishSpring, out _) ==
                LocalizedCardText.Status.NoEntry, "an empty card key falls back");
            Check(LocalizedCardText.Lookup(spring, "enUS", card, englishSpring, out shown) ==
                LocalizedCardText.Status.English && shown == null, "English uses the current catalog's text");
            Check(LocalizedCardText.Lookup("../invalid", "ruRU", card, englishSpring, out _) ==
                LocalizedCardText.Status.NoFile
                && LocalizedCardText.Lookup(null, "ruRU", card, englishSpring, out _) == LocalizedCardText.Status.NoFile,
                "invalid or absent catalog identifiers use English");
            Check(opened.SequenceEqual(new[] { springRu, summerRu, springDe }), "only a requested, valid catalog and locale is opened once");

            foreach (string missingCatalog in new[] { "missing-orchard", "empty-orchard", "broken-orchard" })
            {
                Check(LocalizedCardText.Lookup(missingCatalog, "ruRU", card, englishSpring, out shown) ==
                    LocalizedCardText.Status.NoFile && shown == null, "missing, mislabelled or malformed file falls back");
                Check(LocalizedCardText.Lookup(missingCatalog, "ruRU", card, englishSpring, out _) ==
                    LocalizedCardText.Status.NoFile
                    && opened.Count(name => name == LocalizedCardText.ResourceName(missingCatalog, "ruRU")) == 1,
                    "fallback is cached for the failed catalog and locale");
            }
            Check(LocalizedCardText.Lookup(summer, "deDE", card, englishSummer, out _) == LocalizedCardText.Status.NoFile
                && LocalizedCardText.Lookup(summer, "deDE", card, englishSummer, out _) == LocalizedCardText.Status.NoFile
                && opened.Count(name => name == LocalizedCardText.ResourceName(summer, "deDE")) == 1,
                "an I/O failure is cached for the failed catalog and locale");
            Check(LocalizedCardText.Lookup(spring, "deDE", card, englishSpring, out shown) ==
                LocalizedCardText.Status.Localized && shown == germanSpring, "an I/O failure does not invalidate another catalog's locale");
        }
        finally
        {
            LocalizedCardText.Open = originalOpen;
            LocalizedCardText.Reset();
        }

        string project = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "launcher-source", "OfflineProbe.csproj"));
        Check(project.Contains("../facts/*.*.json") && project.Contains("SoloStone.Facts.%(Filename)%(Extension)"),
            "bundled locale names follow the catalog file naming convention");

        string pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugin-source");
        string bridge = File.ReadAllText(Path.Combine(pluginDirectory, "ClientBridge.cs"));
        Check(bridge.Contains("LocalNativeText.TryGetOverride(") && bridge.Contains("RawCardTextProbe")
            && bridge.Contains("status == LocalizedCardText.Status.Localized"),
            "the client text hook and read-only probe use the localization resolver");
        string control = File.ReadAllText(Path.Combine(pluginDirectory, "NativeTestControl.cs"));
        string protocol = File.ReadAllText(Path.Combine(pluginDirectory, "TestControlProtocol.cs"));
        int mutation = protocol.IndexOf("internal bool Mutation", StringComparison.Ordinal);
        Check(control.Contains("case \"card-raw-text\"")
            && protocol.Contains("case \"card-raw-text\": extras = \"cardId\"; Text(\"cardId\"); break;")
            && mutation >= 0 && !protocol.Substring(mutation, 600).Contains("card-raw-text"),
            "the card text probe remains a read-only query");
    }
}
