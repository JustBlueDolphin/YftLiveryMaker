using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;
using LiveryKit.Imaging;
using LiveryKit.Mcp;
using LiveryKit.Meta;
using LiveryKit.Resources;
using LiveryKit.Textures;
using LiveryKit.Yft;

namespace LiveryKit
{
    /// <summary>
    /// Runs the whole pipeline (scan, plan, make, install, MCP tool) on a car made entirely in code, so it needs
    /// no third-party files. The livery .yft is built from CodeWalker's XML form with a texture made here.
    /// </summary>
    public static class EndToEndTest
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

            string root = Path.Combine(Path.GetTempPath(), "yft-livery-maker-e2e-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string art = Path.Combine(root, "art");
                Directory.CreateDirectory(art);
                string wave = Path.Combine(art, "blue_wave.png");
                string flag = Path.Combine(art, "chequered_flag.png");
                ImageSaver.SavePng(Picture((x, y) => Math.Abs(y - 32 - 12 * Math.Sin(x / 6.0)) < 5 ? (40, 150, 240, 255) : (0, 0, 0, 0)), wave);
                ImageSaver.SavePng(Picture((x, y) => ((x / 8 + y / 8) & 1) == 0 ? (15, 15, 15, 255) : (245, 245, 245, 255)), flag);

                string car = Path.Combine(root, "testcar");
                WriteResource(car, root);
                string livery1 = Path.Combine(car, "stream", "testcar_livery_1.yft");

                Check("e2e: synthetic livery .yft loads as a template", () =>
                {
                    var t = LiveryTemplate.Load(livery1);
                    var s = t.Suggested;
                    return t.ResourceVersion == YftIO.LegacyVersion && t.Textures.Count == 1 && s != null && s.Name == "testcar_livery_1"
                           && s.InLiverySlot && s.Width == 64 && s.Height == 64 && s.MipLevels == 5 && s.FormatName == "DXT5"
                           && s.UsedBy.SequenceEqual(new[] { "vehicle_paint4 › diffusesampler2" });
                });

                Check("e2e: export a new livery and a replacement", () =>
                {
                    var wishes = new[]
                    {
                        new LiveryWish { ImagePath = wave },
                        new LiveryWish { ImagePath = flag, Replace = "testcar_livery_1", DisplayName = "Blue" },
                    };
                    var job = LiveryMaker.Plan(car, null, wishes, Path.Combine(root, "out"), install: false);
                    var r = LiveryMaker.Run(job);
                    var fresh = r.Liveries.Single(m => !m.Replaced);
                    var replaced = r.Liveries.Single(m => m.Replaced);
                    string overlay = Path.Combine(r.OutputFolder, "testcar");
                    var kit = CarcolsReader.ReadKits(File.ReadAllText(Path.Combine(overlay, "data", "carcols.meta")), "x").Single();
                    string lua = File.ReadAllText(Path.Combine(overlay, "vehicle_names.lua"));
                    return r.Liveries.Count == 2
                           && fresh.Request.ModelName == "testcar_livery_2" && fresh.Request.Label == "TESTCAR_LIV2" && fresh.Request.DisplayName == "Blue Wave"
                           && replaced.Request.ModelName == "testcar_livery_1" && replaced.Request.Label == "TESTCAR_LIV1" && replaced.Request.DisplayName == "Blue"
                           && r.Liveries.All(m => m.Yft.Width == 64 && m.Yft.Height == 64 && m.Yft.Format == "DXT5" && m.Yft.MipLevels == 5)
                           && r.Notes.Any(n => n.Contains("at most 5 mipmaps"))
                           && kit.Liveries.Select(l => l.ModelName).SequenceEqual(new[] { "testcar_livery_1", "testcar_livery_2" })
                           && lua.Contains("AddTextEntry(\"TESTCAR_LIV2\", \"Blue Wave\")") && lua.Contains("AddTextEntry(\"TESTCAR_LIV1\", \"Blue\")")
                           && LiveryTemplate.Load(Path.Combine(overlay, "stream", "testcar_livery_2.yft")).Suggested?.Name == "testcar_livery_2"
                           && File.Exists(Path.Combine(overlay, "stream", "testcar_livery_1.yft")) && File.Exists(r.InstructionsPath);
                });

                Check("e2e: install into the resource, with backups", () =>
                {
                    string car2 = Path.Combine(root, "testcar2");
                    CopyDir(car, car2);
                    string target = Path.Combine(car2, "stream", "testcar_livery_1.yft");
                    byte[] before = File.ReadAllBytes(target);
                    var wishes = new[] { new LiveryWish { ImagePath = flag, Replace = "testcar_livery_1" }, new LiveryWish { ImagePath = wave } };
                    var r = LiveryMaker.Run(LiveryMaker.Plan(car2, null, wishes, Path.Combine(root, "out2"), install: true));
                    var kit = CarcolsReader.ReadKits(File.ReadAllText(Path.Combine(car2, "data", "carcols.meta")), "x").Single();
                    return CarScanner.PathEquals(r.OutputFolder, car2)
                           && !File.ReadAllBytes(target).SequenceEqual(before)
                           && r.Backups.Any(b => b.StartsWith(Path.Combine(root, "out2", "Backups")) && b.EndsWith("testcar_livery_1.yft") && File.ReadAllBytes(b).SequenceEqual(before))
                           && File.Exists(Path.Combine(car2, "stream", "testcar_livery_2.yft"))
                           && kit.Liveries.Select(l => l.ModelName).SequenceEqual(new[] { "testcar_livery_1", "testcar_livery_2" })
                           && File.ReadAllText(Path.Combine(car2, "vehicle_names.lua")).Contains("TESTCAR_LIV2")
                           && File.Exists(r.InstructionsPath);
                });

                Check("e2e: replace a livery whose carcols label is empty", () =>
                {
                    string car4 = Path.Combine(root, "testcar4");
                    CopyDir(car, car4);
                    string meta = Path.Combine(car4, "data", "carcols.meta");
                    File.WriteAllText(meta, File.ReadAllText(meta).Replace("<modShopLabel>TESTCAR_LIV1</modShopLabel>", "<modShopLabel />"));
                    var job = LiveryMaker.Plan(car4, null, new[] { new LiveryWish { ImagePath = flag, Replace = "testcar_livery_1" } }, Path.Combine(root, "out4"), install: true);
                    var r = LiveryMaker.Run(job);
                    var kit = CarcolsReader.ReadKits(File.ReadAllText(meta), "x").Single();
                    return r.Liveries.Single().Replaced && kit.Liveries.Single().Label == "TESTCAR_LIVERY_1"
                           && Directory.GetFiles(Path.Combine(car4, "stream")).Length == 1;
                });

                Check("e2e: MCP find_cars and make_liveries", () =>
                {
                    string car3 = Path.Combine(root, "testcar3");
                    CopyDir(car, car3);
                    string found = Call("find_cars", new Dictionary<string, object> { ["folder"] = car3 }, out bool findFailed);
                    var liveries = new object[] { new Dictionary<string, object> { ["image"] = wave } };
                    string made = Call("make_liveries", new Dictionary<string, object> { ["folder"] = car3, ["liveries"] = liveries, ["output_folder"] = Path.Combine(root, "out3"), ["save_dds"] = false }, out bool makeFailed);
                    return !findFailed && found.Contains("Car: testcar") && found.Contains("testcar_livery_1  \"Stripes\"  TESTCAR_LIV1") && found.Contains("next one is testcar_livery_2 (label TESTCAR_LIV2)")
                           && !makeFailed && made.Contains("testcar_livery_2.yft \"Blue Wave\" (new, label TESTCAR_LIV2): 64x64 DXT5, 5 mipmaps")
                           && Directory.GetFiles(Path.Combine(root, "out3"), "testcar_livery_2.yft", SearchOption.AllDirectories).Length == 1;
                });
            }
            catch (Exception ex)
            {
                results.Add((false, "e2e: setting up the test car (threw " + ex.Message + ")"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            return results;
        }

        /// <summary>One MCP tools/call through the same code path the AI apps use.</summary>
        static string Call(string tool, Dictionary<string, object> arguments, out bool isError)
        {
            string request = Json.Write(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new Dictionary<string, object> { ["name"] = tool, ["arguments"] = arguments },
            });
            var reply = (Dictionary<string, object>)Json.Parse(McpServer.Handle(request));
            var result = (Dictionary<string, object>)reply["result"];
            isError = (bool)result["isError"];
            return (string)((Dictionary<string, object>)((object[])result["content"])[0])["text"];
        }

        /// <summary>A FiveM car resource with one livery: carcols.meta, carvariations.meta, a names script and the .yft.</summary>
        static void WriteResource(string car, string scratch)
        {
            Directory.CreateDirectory(Path.Combine(car, "stream"));
            Directory.CreateDirectory(Path.Combine(car, "data"));
            File.WriteAllText(Path.Combine(car, "fxmanifest.lua"),
                "fx_version 'cerulean'\ngame 'gta5'\n\nfiles { 'data/carcols.meta', 'data/carvariations.meta' }\n" +
                "data_file 'CARCOLS_FILE' 'data/carcols.meta'\ndata_file 'VEHICLE_VARIATION_FILE' 'data/carvariations.meta'\n\nclient_script 'vehicle_names.lua'\n");
            File.WriteAllText(Path.Combine(car, "data", "carcols.meta"),
                CarcolsPatcher.NewKitFile("1234_testcar_modkit", "1234", new[] { new NewLiveryEntry("testcar_livery_1", "TESTCAR_LIV1") }));
            File.WriteAllText(Path.Combine(car, "data", "carvariations.meta"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<CVehicleModelInfoVariation>\r\n  <variationData>\r\n    <Item>\r\n      <modelName>testcar</modelName>\r\n" +
                "      <kits>\r\n        <Item>1234_testcar_modkit</Item>\r\n      </kits>\r\n    </Item>\r\n  </variationData>\r\n</CVehicleModelInfoVariation>\r\n");
            File.WriteAllText(Path.Combine(car, "vehicle_names.lua"),
                "Citizen.CreateThread(function()\n\tAddTextEntry(\"TESTCAR\", \"Test Car\")\n\tAddTextEntry(\"TESTCAR_LIV1\", \"Stripes\")\nend)\n");
            File.WriteAllBytes(Path.Combine(car, "stream", "testcar_livery_1.yft"), SyntheticYft(scratch));
        }

        /// <summary>
        /// A legacy (version 162) livery .yft: one drawable, no geometry, a vehicle_paint4 shader whose DiffuseSampler2
        /// is an embedded 64x64 DXT5 texture with 5 mipmaps. Built through CodeWalker's XML import so it fills in the
        /// same internal fields it does for real files.
        /// </summary>
        static byte[] SyntheticYft(string scratch)
        {
            var stripes = Picture((x, y) => (y / 8 & 1) == 0 ? (220, 30, 40, 255) : (250, 250, 250, 255));
            var tex = TextureBuilder.Encode(stripes, new TextureSettings { MipLevels = 5 });
            File.WriteAllBytes(Path.Combine(scratch, "testcar_livery_1.dds"), tex.Dds);

            const string xml = @"<Fragment>
  <Name>testcar_livery_1</Name>
  <BoundingSphereCenter x=""0"" y=""0"" z=""0"" />
  <BoundingSphereRadius value=""3"" />
  <Drawable>
    <Name>testcar_livery_1</Name>
    <BoundingSphereCenter x=""0"" y=""0"" z=""0"" />
    <BoundingSphereRadius value=""3"" />
    <BoundingBoxMin x=""-1"" y=""-2"" z=""-1"" />
    <BoundingBoxMax x=""1"" y=""2"" z=""1"" />
    <LodDistHigh value=""200"" />
    <LodDistMed value=""300"" />
    <LodDistLow value=""500"" />
    <LodDistVlow value=""1000"" />
    <FlagsHigh value=""1"" />
    <FlagsMed value=""0"" />
    <FlagsLow value=""0"" />
    <FlagsVlow value=""0"" />
    <ShaderGroup>
      <TextureDictionary>
        <Item>
          <Name>testcar_livery_1</Name>
          <Unk32 value=""0"" />
          <Usage>DIFFUSE</Usage>
          <ExtraFlags value=""0"" />
          <Width value=""64"" />
          <Height value=""64"" />
          <MipLevels value=""5"" />
          <Format>D3DFMT_DXT5</Format>
          <FileName>testcar_livery_1.dds</FileName>
        </Item>
      </TextureDictionary>
      <Shaders>
        <Item>
          <Name>vehicle_paint4</Name>
          <FileName>vehicle_paint4.sps</FileName>
          <RenderBucket value=""0"" />
          <Parameters>
            <Item name=""DiffuseSampler"" type=""Texture"">
              <Name>vehicle_generic_smallspecmap</Name>
            </Item>
            <Item name=""DiffuseSampler2"" type=""Texture"">
              <Name>testcar_livery_1</Name>
            </Item>
            <Item name=""matDiffuseColor"" type=""Vector"" x=""1"" y=""1"" z=""1"" w=""1"" />
          </Parameters>
        </Item>
      </Shaders>
    </ShaderGroup>
  </Drawable>
</Fragment>";
            var yft = XmlYft.GetYft(xml, scratch);
            return YftIO.Save(yft, YftIO.LegacyVersion);
        }

        static RgbaImage Picture(Func<int, int, (int R, int G, int B, int A)> colour)
        {
            var img = new RgbaImage(64, 64);
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    var (r, g, b, a) = colour(x, y);
                    int o = (y * 64 + x) * 4;
                    img.Pixels[o] = (byte)r; img.Pixels[o + 1] = (byte)g; img.Pixels[o + 2] = (byte)b; img.Pixels[o + 3] = (byte)a;
                }
            }
            return img;
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
