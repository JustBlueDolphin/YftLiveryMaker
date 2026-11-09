using System;
using System.Collections.Generic;
using System.Linq;
using LiveryKit.Meta;

namespace LiveryKit
{
    /// <summary>In-process checks for the text patchers (run with "yftlivery selftest").</summary>
    public static class SelfTest
    {
        public static List<(bool Ok, string Name)> Run()
        {
            var results = new List<(bool, string)>();
            void Check(string name, Func<bool> test)
            {
                bool ok;
                try { ok = test(); } catch (Exception ex) { ok = false; name += " (threw " + ex.Message + ")"; }
                results.Add((ok, name));
            }

            const string kitCrlf =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<CVehicleModelInfoVarGlobal>\r\n  <Kits>\r\n    <Item>\r\n      <kitName>1_car_modkit</kitName>\r\n      <id value=\"1\" />\r\n" +
                "      <visibleMods>\r\n        <Item>\r\n          <modelName>car_liv1</modelName>\r\n          <modShopLabel>CAR_LIV1</modShopLabel>\r\n          <type>VMT_LIVERY_MOD</type>\r\n        </Item>\r\n" +
                "        <!-- <Item><modelName>car_liv9</modelName><type>VMT_LIVERY_MOD</type></Item> -->\r\n" +
                "        <Item>\r\n          <modelName>car_spoiler</modelName>\r\n          <modShopLabel>SPOIL</modShopLabel>\r\n          <type>VMT_SPOILER</type>\r\n        </Item>\r\n" +
                "      </visibleMods>\r\n    </Item>\r\n  </Kits>\r\n</CVehicleModelInfoVarGlobal>\r\n";

            Check("carcols: adds after the last livery entry, keeps CRLF and comments", () =>
            {
                var r = CarcolsPatcher.AddLiveries(kitCrlf, "1_car_modkit", "car_liv1", new[] { new NewLiveryEntry("car_liv2", "CAR_LIV2") });
                var kits = CarcolsReader.ReadKits(r.Text, "x");
                int lf = r.Text.Count(c => c == '\n'), crlf = r.Text.Split(new[] { "\r\n" }, StringSplitOptions.None).Length - 1;
                return r.Added.Count == 1 && kits[0].Liveries.Select(l => l.ModelName).SequenceEqual(new[] { "car_liv1", "car_liv2" })
                       && lf == crlf && r.Text.Contains("<!-- <Item><modelName>car_liv9") && r.Text.IndexOf("car_liv2") < r.Text.IndexOf("car_spoiler");
            });

            Check("carcols: running twice doesn't duplicate", () =>
            {
                var once = CarcolsPatcher.AddLiveries(kitCrlf, "1_car_modkit", "car_liv1", new[] { new NewLiveryEntry("car_liv2", "CAR_LIV2") }).Text;
                var twice = CarcolsPatcher.AddLiveries(once, "1_car_modkit", "car_liv1", new[] { new NewLiveryEntry("car_liv2", "CAR_LIV2") });
                return twice.Added.Count == 0 && twice.AlreadyThere.SequenceEqual(new[] { "car_liv2" }) && twice.Text == once;
            });

            Check("carcols: escapes XML in labels", () =>
            {
                var r = CarcolsPatcher.AddLiveries(kitCrlf, "1_car_modkit", "car_liv1", new[] { new NewLiveryEntry("car_liv2", "Fish & Chips <3") });
                return CarcolsReader.ReadKits(r.Text, "x")[0].Liveries[1].Label == "Fish & Chips <3" && r.Text.Contains("Fish &amp; Chips &lt;3");
            });

            const string emptyKit = "<CVehicleModelInfoVarGlobal>\n  <Kits>\n    <Item>\n      <kitName>2_x_modkit</kitName>\n      <visibleMods />\n    </Item>\n  </Kits>\n</CVehicleModelInfoVarGlobal>\n";
            Check("carcols: kit with no mods yet gets standard entries", () =>
            {
                var r = CarcolsPatcher.AddLiveries(emptyKit, "2_x_modkit", "x_liv1", new[] { new NewLiveryEntry("x_liv1", "X_LIV1"), new NewLiveryEntry("x_liv2", "X_LIV2") });
                var kit = CarcolsReader.ReadKits(r.Text, "x")[0];
                return kit.Liveries.Count == 2 && r.Text.Contains("<bone>chassis</bone>") && !r.Text.Contains("\r\n");
            });

            Check("carcols: new kit file parses back", () =>
            {
                var text = CarcolsPatcher.NewKitFile("4321_car_liveries_modkit", "4321", new[] { new NewLiveryEntry("a_liv1", "A_LIV1") });
                var k = CarcolsReader.ReadKits(text, "x").Single();
                return k.KitName == "4321_car_liveries_modkit" && k.KitId == "4321" && k.Liveries.Single().ModelName == "a_liv1";
            });

            Check("carcols: unknown kit gives a clear error", () =>
            {
                try { CarcolsPatcher.AddLiveries(kitCrlf, "nope", "car_liv1", new[] { new NewLiveryEntry("a", "b") }); return false; }
                catch (LiveryException) { return true; }
            });

            const string luaThread = "Citizen.CreateThread(function()\n\tAddTextEntry(\"GB811\", \"811\")\n\tAddTextEntry(\"811s2_LIV1\", \"Black\")\n\tAddTextEntry(\"811s2_LIV2\", \"Blue\")\n\tAddTextEntry(\"811S2_SPOIL\", \"Spoiler\")\nend)\n";
            Check("names: inserts after the last livery line, same style, inside the thread", () =>
            {
                var r = NamesLuaPatcher.AddEntries(luaThread, "811s2_LIV1", new[] { ("811s2_LIV3", "Red \"Fast\"") });
                var lines = r.Text.Split('\n');
                return r.Added.Count == 1 && lines[4] == "\tAddTextEntry(\"811s2_LIV3\", \"Red \\\"Fast\\\"\")" && lines[5].Contains("SPOIL") && lines.Last() == "" && lines[lines.Length - 2] == "end)";
            });

            const string luaSingle = "AddTextEntry('car_liv1', 'One')\r\n";
            Check("names: keeps single quotes and CRLF", () =>
            {
                var r = NamesLuaPatcher.AddEntries(luaSingle, "car_liv1", new[] { ("car_liv2", "It's two") });
                return r.Text == "AddTextEntry('car_liv1', 'One')\r\nAddTextEntry('car_liv2', 'It\\'s two')\r\n";
            });

            Check("names: skips keys that already exist", () =>
            {
                var r = NamesLuaPatcher.AddEntries(luaThread, "811s2_LIV1", new[] { ("811S2_liv2", "dupe"), ("811s2_LIV9", "new") });
                return r.Added.SequenceEqual(new[] { "811s2_LIV9" }) && r.AlreadyThere.Count == 1;
            });

            Check("names: new file round-trips through the reader", () =>
            {
                var text = NamesLuaPatcher.NewFile(new[] { ("A_LIV1", "Tom's Livery"), ("A_LIV2", "Back\\slash") });
                var e = TextEntries.Read(text, null);
                return e.Count == 2 && e[0].Value == "Tom's Livery" && e[1].Value == "Back\\slash";
            });

            Check("fxmanifest: adds client_script once", () =>
            {
                var once = FxManifestPatcher.EnsureClientScript("fx_version 'cerulean'\ngame 'gta5'\n", "livery_names.lua");
                var twice = FxManifestPatcher.EnsureClientScript(once, "livery_names.lua");
                return once.EndsWith("client_script 'livery_names.lua'\n") && twice == once;
            });

            Check("carcols: relabel an existing entry in place", () =>
            {
                var r = CarcolsPatcher.SetLabels(kitCrlf, "1_car_modkit", new[] { ("car_liv1", "My <new> name") });
                var kit = CarcolsReader.ReadKits(r.Text, "x")[0];
                return r.Added.SequenceEqual(new[] { "car_liv1" }) && kit.Liveries.Single().Label == "My <new> name"
                       && r.Text.Replace("My &lt;new&gt; name", "CAR_LIV1") == kitCrlf;
            });

            Check("names: rename existing entries, leave the rest alone", () =>
            {
                var r = NamesLuaPatcher.SetValues(luaThread, new[] { ("811S2_liv2", "Ocean \"Blue\""), ("811s2_LIV1", "Black"), ("NOPE", "x") });
                return r.Added.SequenceEqual(new[] { "811s2_LIV2" }) && r.AlreadyThere.SequenceEqual(new[] { "811s2_LIV1" })
                       && r.Text.Contains("AddTextEntry(\"811s2_LIV2\", \"Ocean \\\"Blue\\\"\")")
                       && r.Text.Replace("Ocean \\\"Blue\\\"", "Blue") == luaThread;
            });

            Check("xml spans: tolerate a stray close tag and unclosed element", () =>
            {
                var root = XmlSpans.Parse("<a><b>1</b></c><d>2</a>");
                var a = root.Child("a");
                return a != null && a.ChildText("<a><b>1</b></c><d>2</a>", "b") == "1" && a.Child("d") != null;
            });

            return results;
        }
    }
}
