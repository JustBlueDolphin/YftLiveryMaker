using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LiveryKit.Imaging;
using LiveryKit.Textures;
using LiveryKit;
using LiveryKit.Resources;
using LiveryKit.Yft;

namespace LiveryCli
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            // Must be hooked before anything touches LiveryKit/CodeWalker types.
            YftLiveryMaker.Boot.EmbeddedAssemblies.Install();
            return Run(args);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Run(string[] args)
        {
            if (args.Length == 0) return Usage();
            try
            {
                switch (args[0])
                {
                    case "inspect": return Inspect(args);
                    case "dds": return MakeDds(args);
                    case "extract": return Extract(args);
                    case "decode": return Decode(args);
                    case "build": return Build(args);
                    case "scan": return Scan(args);
                    case "togen9":
                        {
                            // Developer aid: re-save a legacy .yft as Enhanced (gen9) to test that path.
                            var y = YftIO.Load(File.ReadAllBytes(args[1]));
                            File.WriteAllBytes(args[2], YftIO.Save(y, YftIO.Gen9Version));
                            Console.WriteLine("wrote gen9 copy " + args[2]);
                            return 0;
                        }
                    case "make": return Make(args);
                    case "selftest":
                        {
                            var results = SelfTest.Run();
                            foreach (var (ok, name) in results) Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
                            int failed = results.FindAll(r => !r.Ok).Count;
                            Console.WriteLine(failed == 0 ? $"all {results.Count} self-tests passed" : $"{failed} self-test(s) FAILED");
                            return failed == 0 ? 0 : 3;
                        }
                    default: return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 2;
            }
        }

        static int Usage()
        {
            Console.WriteLine("usage:");
            Console.WriteLine("  yftlivery inspect <file.yft> [more.yft...]");
            Console.WriteLine("  yftlivery dds <image> <out.dds> [--format DXT5|DXT1|BC7|A8R8G8B8] [--mips 10] [--size WxH] [--no-edge-fix]");
            Console.WriteLine("  yftlivery extract <file.yft> <out folder>      (embedded textures -> .dds)");
            Console.WriteLine("  yftlivery decode <image or .dds> <out.png>");
            Console.WriteLine("  yftlivery scan <car folder | livery.yft>");
            Console.WriteLine("  yftlivery make <car folder | livery.yft> <image> [image...] --out <folder> [--install] [--car text]");
            Console.WriteLine("                 [--replace mycar_livery_3=art.png]   (replace one of the car's liveries; repeatable)");
            Console.WriteLine("                 [--template model] [--format DXT5] [--mips 10] [--size match|keep|1024|2048|4096] [--no-dds]");
            return 1;
        }

        static int Scan(string[] args)
        {
            if (args.Length < 2) return Usage();
            var scan = CarScanner.Scan(args[1]);
            Console.WriteLine($"scanned {scan.ScannedFolder}: {scan.Cars.Count} car(s) with YFT liveries");
            foreach (var car in scan.Cars)
            {
                Console.WriteLine((ReferenceEquals(car, scan.Preferred) ? "* " : "  ") + car);
                var t = car.DefaultTemplate;
                if (t == null) { Console.WriteLine("      no usable livery .yft (missing or escrow-locked)"); continue; }
                var naming = LiveryKit.Resources.Naming.For(car, t, scan.AllYftNames);
                Console.WriteLine($"      template {t.ModelName}  next: {naming.ModelName(0)}  label: {(naming.LiteralLabels ? "(display name)" : naming.Label(0, ""))}");
                if (car.Kit != null) Console.WriteLine($"      carcols: {CarScanner.MakeRelative(scan.ScannedFolder, car.Kit.FilePath)}");
                if (car.NamesLuaPath != null) Console.WriteLine($"      names:   {CarScanner.MakeRelative(scan.ScannedFolder, car.NamesLuaPath)}");
            }
            foreach (var w in scan.Warnings) Console.WriteLine("warning: " + w);
            return 0;
        }

        static int Make(string[] args)
        {
            if (args.Length < 3) return Usage();
            var images = new List<string>();
            var replacements = new List<(string Model, string Image)>();
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--replace")
                {
                    var pair = args[++i];
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) throw new Exception("--replace needs model=image, e.g. --replace mycar_livery_3=art.png");
                    replacements.Add((pair.Substring(0, eq), pair.Substring(eq + 1)));
                }
                else if (args[i].StartsWith("--"))
                {
                    if (args[i] == "--install" || args[i] == "--no-dds") opts[args[i]] = "";
                    else opts[args[i]] = args[++i];
                }
                else images.Add(args[i]);
            }

            var scan = CarScanner.Scan(args[1]);
            CarInfo car = scan.Preferred;
            if (opts.TryGetValue("--car", out var carText))
                car = scan.Cars.Find(c => c.DisplayName.IndexOf(carText, StringComparison.OrdinalIgnoreCase) >= 0);
            if (car == null) throw new Exception("pick a car with --car (see 'scan')");

            var tfile = scan.PreferredLivery != null && ReferenceEquals(car, scan.Preferred) ? scan.PreferredLivery : car.DefaultTemplate;
            if (opts.TryGetValue("--template", out var tname)) tfile = car.Liveries.Find(l => string.Equals(l.ModelName, tname, StringComparison.OrdinalIgnoreCase));
            if (tfile == null || !tfile.Usable) throw new Exception("no usable template livery for that car");

            var template = LiveryTemplate.Load(tfile.YftPath);
            var naming = LiveryKit.Resources.Naming.For(car, tfile, scan.AllYftNames);
            var job = new MakeJob
            {
                Car = car,
                Template = template,
                TextureHash = template.Suggested.NameHash,
                OutputFolder = opts.TryGetValue("--out", out var o) ? o : throw new Exception("--out is required"),
                Mode = opts.ContainsKey("--install") ? OutputMode.InstallIntoResource : OutputMode.ExportToFolder,
                SaveDds = !opts.ContainsKey("--no-dds"),
                LiteralLabels = naming.LiteralLabels,
                TemplateLabel = naming.TemplateLabel,
                TakenYftNames = scan.AllYftNames,
            };
            if (opts.TryGetValue("--format", out var f)) job.Texture.Format = (LiveryTextureFormat)Enum.Parse(typeof(LiveryTextureFormat), f, true);
            if (opts.TryGetValue("--mips", out var m)) job.Texture.MipLevels = int.Parse(m);
            if (opts.TryGetValue("--size", out var size))
            {
                switch (size)
                {
                    case "keep": job.SizeMode = TextureSizeMode.KeepImage; break;
                    case "1024": job.SizeMode = TextureSizeMode.Longest1024; break;
                    case "2048": job.SizeMode = TextureSizeMode.Longest2048; break;
                    case "4096": job.SizeMode = TextureSizeMode.Longest4096; break;
                    default: job.SizeMode = TextureSizeMode.MatchOriginal; break;
                }
            }

            foreach (var (model, image) in replacements)
            {
                var old = car.Liveries.Find(l => string.Equals(l.ModelName, model, StringComparison.OrdinalIgnoreCase))
                          ?? throw new Exception($"{model} isn't one of {car.DisplayName}'s liveries (see 'scan')");
                string display = PrettyName(image);
                job.Liveries.Add(new LiveryRequest
                {
                    ImagePath = image,
                    ModelName = old.ModelName,
                    DisplayName = display,
                    Label = naming.LiteralLabels ? display : old.Label ?? old.ModelName.ToUpperInvariant(),
                    TemplatePath = old.Usable ? old.YftPath : null,
                });
            }

            for (int i = 0; i < images.Count; i++)
            {
                string display = PrettyName(images[i]);
                job.Liveries.Add(new LiveryRequest
                {
                    ImagePath = images[i],
                    ModelName = naming.ModelName(i),
                    DisplayName = display,
                    Label = naming.Label(i, display),
                });
            }

            Console.WriteLine($"car: {car}\ntemplate: {template.ModelName} texture {template.Suggested}");
            var sw = Stopwatch.StartNew();
            string last = null;
            var result = LiveryMaker.Run(job, (p, s) => { if (s != last) { Console.WriteLine($"  {p,4:P0} {s}"); last = s; } });
            Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1}s -> {result.OutputFolder}");
            foreach (var w in result.Written) Console.WriteLine("  wrote " + w);
            foreach (var b in result.Backups) Console.WriteLine("  backup " + b);
            foreach (var n in result.Notes) Console.WriteLine("  note: " + n);
            foreach (var w in result.Warnings) Console.WriteLine("  WARNING: " + w);
            Console.WriteLine("  instructions: " + result.InstructionsPath);
            return 0;
        }

        static string PrettyName(string path)
        {
            var words = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++) words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }

        static int Build(string[] args)
        {
            if (args.Length < 4) return Usage();
            var opts = Options(args, 4);
            var template = LiveryTemplate.Load(args[1]);
            var target = template.Suggested ?? throw new Exception("template has no embedded texture");
            Console.WriteLine($"template {template.ModelName}: livery texture {target} used by {string.Join(", ", target.UsedBy)}");

            var img = ImageLoader.Load(args[2]);
            if (img.Width != target.Width || img.Height != target.Height)
            {
                img = Resizer.Resize(img, target.Width, target.Height);
                Console.WriteLine($"resized artwork to {img.Width}x{img.Height}");
            }
            var settings = new TextureSettings();
            if (opts.TryGetValue("--format", out var f)) settings.Format = (LiveryTextureFormat)Enum.Parse(typeof(LiveryTextureFormat), f, true);
            if (opts.TryGetValue("--mips", out var m)) settings.MipLevels = int.Parse(m);
            var tex = TextureBuilder.Encode(img, settings);

            string model = Path.GetFileNameWithoutExtension(args[3]);
            var result = LiveryYftBuilder.Build(template, target.NameHash, tex.Dds, model);
            File.WriteAllBytes(args[3], result.YftBytes);
            Console.WriteLine($"wrote {args[3]} ({result.YftBytes.Length:N0} bytes)");
            foreach (var c in result.Checks) Console.WriteLine("  verified: " + c);
            return 0;
        }

        static int Extract(string[] args)
        {
            if (args.Length < 3) return Usage();
            var data = File.ReadAllBytes(args[1]);
            var yft = YftIO.Load(data, Path.GetFileName(args[1]));
            Directory.CreateDirectory(args[2]);
            foreach (var slot in YftWalker.AllDrawables(yft.Fragment))
            {
                var texs = slot.Drawable.ShaderGroup?.TextureDictionary?.Textures?.data_items;
                if (texs == null) continue;
                foreach (var t in texs)
                {
                    var path = Path.Combine(args[2], t.Name + ".dds");
                    File.WriteAllBytes(path, CodeWalker.Utils.DDSIO.GetDDSFile(t));
                    Console.WriteLine($"{path}  {t.Width}x{t.Height} {t.Format} mips:{t.Levels}");
                }
            }
            return 0;
        }

        static int Decode(string[] args)
        {
            if (args.Length < 3) return Usage();
            var img = ImageLoader.Load(args[1]);
            ImageSaver.SavePng(img, args[2]);
            Console.WriteLine($"{args[2]}  {img.Width}x{img.Height} alpha:{img.GetAlphaKind()}");
            return 0;
        }

        static int Inspect(string[] args)
        {
            int failures = 0;
            for (int i = 1; i < args.Length; i++)
            {
                try
                {
                    Console.WriteLine(args[i]);
                    Console.WriteLine(YftReport.Describe(args[i]));
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine("  ERROR: " + ex.Message);
                }
            }
            return failures == 0 ? 0 : 2;
        }

        static int MakeDds(string[] args)
        {
            if (args.Length < 3) return Usage();
            var opts = Options(args, 3);
            var settings = new TextureSettings();
            if (opts.TryGetValue("--format", out var f)) settings.Format = (LiveryTextureFormat)Enum.Parse(typeof(LiveryTextureFormat), f, true);
            if (opts.TryGetValue("--mips", out var m)) settings.MipLevels = int.Parse(m);
            if (opts.ContainsKey("--no-edge-fix")) settings.FixTransparentEdges = false;

            var sw = Stopwatch.StartNew();
            var img = ImageLoader.Load(args[1]);
            Console.WriteLine($"loaded {img.Width}x{img.Height} alpha:{img.GetAlphaKind()} in {sw.ElapsedMilliseconds} ms");

            if (opts.TryGetValue("--size", out var size))
            {
                var parts = size.ToLowerInvariant().Split('x');
                sw.Restart();
                img = Resizer.Resize(img, int.Parse(parts[0]), int.Parse(parts[1]));
                Console.WriteLine($"resized to {img.Width}x{img.Height} in {sw.ElapsedMilliseconds} ms");
            }

            sw.Restart();
            var tex = TextureBuilder.Encode(img, settings);
            File.WriteAllBytes(args[2], tex.Dds);
            Console.WriteLine($"wrote {args[2]}: {tex.Width}x{tex.Height} {tex.Format} mips:{tex.MipLevels} data:{tex.PixelDataSize:N0} bytes in {sw.ElapsedMilliseconds} ms");
            foreach (var n in tex.Notes) Console.WriteLine("note: " + n);
            return 0;
        }

        static Dictionary<string, string> Options(string[] args, int start)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { d[args[i]] = args[i + 1]; i++; }
                else d[args[i]] = "";
            }
            return d;
        }
    }
}
