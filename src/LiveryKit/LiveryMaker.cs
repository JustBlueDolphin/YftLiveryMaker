using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LiveryKit.Imaging;
using LiveryKit.Meta;
using LiveryKit.Resources;
using LiveryKit.Textures;
using LiveryKit.Yft;

namespace LiveryKit
{
    public enum TextureSizeMode
    {
        /// <summary>Same size as the car's existing livery texture (the UV template it was made for).</summary>
        MatchOriginal,
        /// <summary>The artwork's own size, rounded to the nearest power of two.</summary>
        KeepImage,
        /// <summary>Longest side 1024, same shape as the original livery.</summary>
        Longest1024,
        Longest2048,
        Longest4096,
    }

    public enum OutputMode
    {
        /// <summary>Write a separate folder laid out like the resource, ready to copy over it.</summary>
        ExportToFolder,
        /// <summary>Write straight into the car's resource; changed files are backed up first.</summary>
        InstallIntoResource,
    }

    public sealed class LiveryRequest
    {
        public string ImagePath { get; set; }
        public string ModelName { get; set; }
        public string DisplayName { get; set; }
        public string Label { get; set; }
        /// <summary>
        /// Optional .yft to build this livery from instead of the job's template. When replacing one of the
        /// car's liveries this is that livery's own file, so the replacement keeps its exact model.
        /// </summary>
        public string TemplatePath { get; set; }
    }

    /// <summary>One image for <see cref="LiveryMaker.Plan"/>: a new livery, or a replacement when Replace names one of the car's.</summary>
    public sealed class LiveryWish
    {
        public string ImagePath { get; set; }
        /// <summary>Mod shop name. Default: a replaced livery keeps its name, a new one is named after the image file.</summary>
        public string DisplayName { get; set; }
        /// <summary>Model name of the car's livery to replace.</summary>
        public string Replace { get; set; }
        /// <summary>Model name for a new livery instead of the next free number (one of the car's own names replaces it).</summary>
        public string FileName { get; set; }
    }

    public sealed class MakeJob
    {
        public CarInfo Car { get; set; }
        public LiveryTemplate Template { get; set; }
        public uint TextureHash { get; set; }
        public List<LiveryRequest> Liveries { get; } = new List<LiveryRequest>();
        public TextureSettings Texture { get; set; } = new TextureSettings();
        public TextureSizeMode SizeMode { get; set; } = TextureSizeMode.MatchOriginal;
        public OutputMode Mode { get; set; } = OutputMode.ExportToFolder;
        /// <summary>Export: where the export folder is created. Install: where DDS copies and backups go.</summary>
        public string OutputFolder { get; set; }
        public bool SaveDds { get; set; } = true;
        /// <summary>Labels are the display text itself, so no names script is needed.</summary>
        public bool LiteralLabels { get; set; }
        /// <summary>The existing label new names script lines are styled on.</summary>
        public string TemplateLabel { get; set; }
        /// <summary>All .yft names in the scanned folder, to avoid clashes.</summary>
        public ICollection<string> TakenYftNames { get; set; }
    }

    public sealed class MadeLivery
    {
        public LiveryRequest Request { get; internal set; }
        public string YftPath { get; internal set; }
        public string DdsPath { get; internal set; }
        public LiveryYftResult Yft { get; internal set; }
        public EncodedTexture Texture { get; internal set; }
        /// <summary>True when this overwrote one of the car's existing liveries.</summary>
        public bool Replaced { get; internal set; }
    }

    public sealed class MakeResult
    {
        /// <summary>The export folder, or the resource folder when installing.</summary>
        public string OutputFolder { get; internal set; }
        public string InstructionsPath { get; internal set; }
        public List<MadeLivery> Liveries { get; } = new List<MadeLivery>();
        public List<string> Written { get; } = new List<string>();
        public List<string> Backups { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> NextSteps { get; } = new List<string>();
    }

    /// <summary>Runs a whole job: artwork -> DDS (with mipmaps) -> livery .yft -> carcols.meta / names script.</summary>
    public static class LiveryMaker
    {
        /// <summary>
        /// Plans a job the way the app does: finds the car (by name, or the one the path points at), its template livery
        /// and naming, then makes each image a new livery or a replacement. Texture settings stay at their defaults.
        /// </summary>
        public static MakeJob Plan(string carPath, string carName, IEnumerable<LiveryWish> wishes, string outputFolder, bool install, string templateModel = null)
        {
            var scan = CarScanner.Scan(carPath);
            var car = FindCar(scan, carName);
            var tfile = templateModel != null ? Livery(car, templateModel) : TemplateFor(scan, car);
            if (tfile == null || !tfile.Usable)
                throw new LiveryException(templateModel != null
                    ? $"{templateModel} isn't one of {car.DisplayName}'s liveries that can be opened."
                    : $"{car.DisplayName} has no livery .yft that can be opened (missing or escrow-locked).");

            var template = LiveryTemplate.Load(tfile.YftPath);
            var naming = Naming.For(car, tfile, scan.AllYftNames);
            var job = new MakeJob
            {
                Car = car,
                Template = template,
                TextureHash = (template.Suggested ?? throw new LiveryException($"{tfile.ModelName}.yft has no embedded livery texture.")).NameHash,
                OutputFolder = outputFolder,
                Mode = install ? OutputMode.InstallIntoResource : OutputMode.ExportToFolder,
                LiteralLabels = naming.LiteralLabels,
                TemplateLabel = naming.TemplateLabel,
                TakenYftNames = scan.AllYftNames,
            };

            int auto = 0, labelIndex = 0;
            foreach (var w in wishes)
            {
                string replace = !string.IsNullOrWhiteSpace(w.Replace) ? w.Replace : Livery(car, w.FileName) != null ? w.FileName : null;
                if (replace != null)
                {
                    var old = Livery(car, replace) ?? throw new LiveryException($"{replace.Trim()} isn't one of {car.DisplayName}'s liveries.");
                    string display = FirstText(w.DisplayName, old.LabelText, old.Label, old.ModelName);
                    job.Liveries.Add(new LiveryRequest
                    {
                        ImagePath = w.ImagePath,
                        ModelName = old.ModelName,
                        DisplayName = display,
                        Label = naming.LiteralLabels ? display : old.Label ?? old.ModelName.ToUpperInvariant(),
                        // Built from the livery it replaces, so it keeps that exact model.
                        TemplatePath = old.Usable ? old.YftPath : null,
                    });
                }
                else
                {
                    string display = FirstText(w.DisplayName, PrettyName(w.ImagePath));
                    job.Liveries.Add(new LiveryRequest
                    {
                        ImagePath = w.ImagePath,
                        ModelName = string.IsNullOrWhiteSpace(w.FileName) ? naming.ModelName(auto++) : w.FileName.Trim(),
                        DisplayName = display,
                        Label = naming.Label(labelIndex++, display),
                    });
                }
            }
            return job;
        }

        /// <summary>The car called <paramref name="name"/> (exact, then partial match), or the one the scan points at.</summary>
        public static CarInfo FindCar(ScanResult scan, string name)
        {
            string cars = string.Join(", ", scan.Cars.Take(40).Select(c => c.DisplayName)) + (scan.Cars.Count > 40 ? $" and {scan.Cars.Count - 40} more" : "");
            if (scan.Cars.Count == 0)
                throw new LiveryException($"No cars with YFT liveries found in {scan.ScannedFolder}. {string.Join(" ", scan.Warnings)}".TrimEnd());
            if (string.IsNullOrWhiteSpace(name))
                return scan.Preferred ?? (scan.Cars.Count == 1 ? scan.Cars[0]
                    : throw new LiveryException($"Which car? {scan.ScannedFolder} has {scan.Cars.Count} cars with YFT liveries: {cars}."));
            name = name.Trim();
            return scan.Cars.Find(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase) || c.SpawnNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                   ?? scan.Cars.Find(c => c.DisplayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? throw new LiveryException($"No car called '{name}' in {scan.ScannedFolder}. Cars with YFT liveries: {cars}.");
        }

        /// <summary>The livery new ones are built from: the one the user pointed at, else the car's first usable one.</summary>
        public static LiveryFile TemplateFor(ScanResult scan, CarInfo car) =>
            scan.PreferredLivery != null && ReferenceEquals(car, scan.Preferred) ? scan.PreferredLivery : car.DefaultTemplate;

        /// <summary>"keep", "1024", "2048", "4096", anything else matches the car's livery.</summary>
        public static TextureSizeMode SizeFrom(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "keep": return TextureSizeMode.KeepImage;
                case "1024": return TextureSizeMode.Longest1024;
                case "2048": return TextureSizeMode.Longest2048;
                case "4096": return TextureSizeMode.Longest4096;
                default: return TextureSizeMode.MatchOriginal;
            }
        }

        /// <summary>"red_stripes-v2.png" -> "Red Stripes V2".</summary>
        public static string PrettyName(string path)
        {
            var words = Path.GetFileNameWithoutExtension(path ?? "").Replace('_', ' ').Replace('-', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++) words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }

        static LiveryFile Livery(CarInfo car, string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            model = model.Trim();
            if (model.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)) model = model.Substring(0, model.Length - 4);
            return car.Liveries.Find(l => string.Equals(l.ModelName, model, StringComparison.OrdinalIgnoreCase));
        }

        static string FirstText(params string[] options) => options.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o))?.Trim();

        public static MakeResult Run(MakeJob job, Action<double, string> progress = null, CancellationToken cancel = default)
        {
            Validate(job);
            progress = progress ?? ((p, s) => { });
            job.OutputFolder = Path.GetFullPath(job.OutputFolder);

            var result = new MakeResult();
            var car = job.Car;
            var template = job.Template;
            string templateDir = Path.GetDirectoryName(template.FilePath);
            var templates = new Dictionary<string, LiveryTemplate>(StringComparer.OrdinalIgnoreCase) { [template.FilePath] = template };
            var existingLiveries = new Dictionary<string, LiveryFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in car?.Liveries ?? new List<LiveryFile>())
                if (l.YftPath != null && !existingLiveries.ContainsKey(l.ModelName)) existingLiveries[l.ModelName] = l;
            string resourceRoot = car?.ResourceRoot ?? templateDir;
            string resourceName = Path.GetFileName(resourceRoot.TrimEnd('\\', '/'));
            string carName = SafeFileName(car?.SpawnNames.FirstOrDefault() ?? BaseCarName(template.ModelName));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            bool install = job.Mode == OutputMode.InstallIntoResource;

            string exportRoot = null, overlayRoot = null, backupRoot = null, ddsRoot;
            if (install)
            {
                backupRoot = Path.Combine(job.OutputFolder, "Backups", $"{resourceName}_{stamp}");
                ddsRoot = Path.Combine(job.OutputFolder, "DDS", carName);
                result.OutputFolder = resourceRoot;
            }
            else
            {
                exportRoot = UniqueFolder(Path.Combine(job.OutputFolder, $"{carName}_liveries_{stamp}"));
                overlayRoot = Path.Combine(exportRoot, resourceName);
                ddsRoot = Path.Combine(exportRoot, "dds");
                result.OutputFolder = exportRoot;
            }

            string Dest(string original) => install
                ? original
                : Path.Combine(overlayRoot, IsInside(resourceRoot, original) ? CarScanner.MakeRelative(resourceRoot, original) : Path.GetFileName(original));

            void WriteFile(string original, byte[] bytes, string what)
            {
                string dest = Dest(original);
                if (install && File.Exists(dest)) Backup(dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                string tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                result.Written.Add($"{what}: {dest}");
            }

            void Backup(string path)
            {
                string rel = IsInside(resourceRoot, path) ? CarScanner.MakeRelative(resourceRoot, path) : Path.GetFileName(path);
                string to = Path.Combine(backupRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(path, to, true);
                result.Backups.Add(to);
            }

            // ---------------------------------------------------------------- liveries
            int n = job.Liveries.Count;
            for (int i = 0; i < n; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var req = job.Liveries[i];
                double baseP = (double)i / n, span = 1.0 / n * 0.95;
                string tag = $"[{i + 1}/{n}] {req.ModelName}";

                progress(baseP, $"{tag}: loading {Path.GetFileName(req.ImagePath)}");
                var img = ImageLoader.Load(req.ImagePath);

                existingLiveries.TryGetValue(req.ModelName, out var replaced);
                var tpl = template;
                uint texHash = job.TextureHash;
                if (!string.IsNullOrEmpty(req.TemplatePath) && !CarScanner.PathEquals(req.TemplatePath, template.FilePath))
                {
                    if (!templates.TryGetValue(Path.GetFullPath(req.TemplatePath), out tpl))
                    {
                        tpl = LiveryTemplate.Load(req.TemplatePath);
                        templates[tpl.FilePath] = tpl;
                    }
                    texHash = tpl.Suggested?.NameHash ?? throw new LiveryException($"{tpl.ModelName}.yft has no texture inside it to replace.");
                }
                var tplTex = tpl.Textures.First(t => t.NameHash == texHash);

                var (tw, th) = TargetSize(job.SizeMode, img, tplTex);
                if (Math.Abs((double)img.Width / img.Height - (double)tw / th) > 0.01)
                    result.Warnings.Add($"{req.ModelName}: your image is {img.Width}x{img.Height} but the car's livery is {tplTex.Width}x{tplTex.Height} ({Ratio(tplTex.Width, tplTex.Height)}), so it was stretched to fit. Make sure it was painted on this car's livery template.");
                if (img.Width < tw || img.Height < th)
                    result.Notes.Add($"{req.ModelName}: artwork {img.Width}x{img.Height} was enlarged to {tw}x{th}; paint at {tw}x{th} or bigger for the sharpest result.");
                if (img.Width != tw || img.Height != th)
                {
                    progress(baseP + span * 0.05, $"{tag}: resizing {img.Width}x{img.Height} -> {tw}x{th}");
                    img = Resizer.Resize(img, tw, th);
                }
                if (img.GetAlphaKind() == AlphaKind.Opaque)
                    result.Notes.Add($"{req.ModelName}: the artwork has no transparency, so the livery covers the whole car paint wherever it's mapped.");

                progress(baseP + span * 0.1, $"{tag}: making {job.Texture.Format} texture with {job.Texture.MipLevels} mipmaps");
                var tex = TextureBuilder.Encode(img, job.Texture, p => progress(baseP + span * (0.1 + 0.75 * p), $"{tag}: compressing ({p:P0})"), cancel);
                foreach (var note in tex.Notes) result.Notes.Add($"{req.ModelName}: {note}");

                progress(baseP + span * 0.88, $"{tag}: building .yft from {tpl.ModelName}.yft");
                var built = LiveryYftBuilder.Build(tpl, texHash, tex.Dds, req.ModelName);

                // A replacement goes exactly where the old file lives; new liveries sit next to the template.
                string yftOriginal = replaced?.YftPath ?? Path.Combine(templateDir, req.ModelName + ".yft");
                WriteFile(yftOriginal, built.YftBytes, replaced != null ? "replaced livery" : "livery");
                var made = new MadeLivery { Request = req, YftPath = Dest(yftOriginal), Yft = built, Texture = tex, Replaced = replaced != null };
                if (replaced != null)
                    result.Notes.Add($"{req.ModelName}.yft was replaced with your artwork{(install ? " (the old one is in the backups)" : "")}.");

                if (job.SaveDds)
                {
                    Directory.CreateDirectory(ddsRoot);
                    made.DdsPath = Path.Combine(ddsRoot, req.ModelName + ".dds");
                    File.WriteAllBytes(made.DdsPath, tex.Dds);
                    result.Written.Add($"texture: {made.DdsPath}");
                }
                result.Liveries.Add(made);
            }

            // ---------------------------------------------------------------- carcols.meta
            progress(0.96, "Updating carcols.meta");
            var entries = job.Liveries.Select(l => new NewLiveryEntry(l.ModelName, l.Label)).ToList();
            string kitHelpPath = null;
            if (car?.Kit != null)
            {
                var (text, bom) = ReadText(car.Kit.FilePath);
                var patch = CarcolsPatcher.AddLiveries(text, car.Kit.KitName, template.ModelName, entries);

                // Replaced liveries keep their entry; only a changed mod shop label is written back.
                var relabel = job.Liveries
                    .Where(l => existingLiveries.TryGetValue(l.ModelName, out var ex) && !string.Equals(ex.Label ?? "", l.Label, StringComparison.Ordinal))
                    .Select(l => (l.ModelName, l.Label))
                    .ToList();
                var labels = relabel.Count > 0 ? CarcolsPatcher.SetLabels(patch.Text, car.Kit.KitName, relabel) : null;
                string newText = labels?.Text ?? patch.Text;

                if (newText != text)
                {
                    WriteFile(car.Kit.FilePath, Encode(newText, bom), "carcols.meta");
                    if (patch.Added.Count > 0)
                        result.Notes.Add($"carcols.meta: added {string.Join(", ", patch.Added)} to mod kit {car.Kit.KitName}.");
                    if (labels != null && labels.Added.Count > 0)
                        result.Notes.Add($"carcols.meta: updated the mod shop label of {string.Join(", ", labels.Added)}.");
                }
            }
            else
            {
                // No mod kit to add to: hand over a ready kit the user wires up once.
                string kitId = new Random().Next(4000, 9000).ToString();
                string kitName = $"{kitId}_{carName}_liveries_modkit";
                var all = (car?.Liveries.Where(l => l.YftPath != null).Select(l => new NewLiveryEntry(l.ModelName, l.Label ?? l.ModelName)) ?? Enumerable.Empty<NewLiveryEntry>())
                    .Concat(entries).GroupBy(e => e.ModelName, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
                string metaDir = install ? Path.Combine(job.OutputFolder, "Meta", $"{carName}_{stamp}") : exportRoot;
                Directory.CreateDirectory(metaDir);
                kitHelpPath = Path.Combine(metaDir, "carcols_liveries.meta");
                File.WriteAllText(kitHelpPath, CarcolsPatcher.NewKitFile(kitName, kitId, all), new UTF8Encoding(false));
                result.Written.Add($"new mod kit: {kitHelpPath}");
                result.Warnings.Add($"This car's liveries aren't in any carcols.meta that was scanned, so a new mod kit ({kitName}) was written to {kitHelpPath}. See the instructions to hook it up.");
                result.NextSteps.Add($"Add the kit to the car: in its carvariations.meta, inside the car's <Item>, set <kits><Item>{kitName}</Item></kits>.");
                result.NextSteps.Add("Put carcols_liveries.meta in the resource and add to fxmanifest.lua:  data_file 'CARCOLS_FILE' 'carcols_liveries.meta'  and list it under files { }.");
            }

            // ---------------------------------------------------------------- mod shop names
            if (!job.LiteralLabels)
            {
                var names = job.Liveries.Select(l => (l.Label, string.IsNullOrWhiteSpace(l.DisplayName) ? l.ModelName : l.DisplayName.Trim())).ToList();
                if (car?.NamesLuaPath != null)
                {
                    progress(0.98, "Updating " + Path.GetFileName(car.NamesLuaPath));
                    var (text, bom) = ReadText(car.NamesLuaPath);
                    // Existing names (replaced liveries) get their new text; missing ones are added.
                    var changed = NamesLuaPatcher.SetValues(text, names);
                    var patch = NamesLuaPatcher.AddEntries(changed.Text, job.TemplateLabel, names);
                    if (patch.Text != text)
                    {
                        WriteFile(car.NamesLuaPath, Encode(patch.Text, bom), "mod shop names");
                        if (patch.Added.Count > 0)
                            result.Notes.Add($"{Path.GetFileName(car.NamesLuaPath)}: added names for {string.Join(", ", patch.Added)}.");
                        if (changed.Added.Count > 0)
                            result.Notes.Add($"{Path.GetFileName(car.NamesLuaPath)}: renamed {string.Join(", ", changed.Added)}.");
                    }
                }
                else
                {
                    string luaOriginal = Path.Combine(resourceRoot, "livery_names.lua");
                    string existing = File.Exists(luaOriginal) ? ReadText(luaOriginal).Text : null;
                    string lua = existing == null ? NamesLuaPatcher.NewFile(names) : NamesLuaPatcher.AddEntries(existing, job.TemplateLabel, names).Text;
                    WriteFile(luaOriginal, new UTF8Encoding(false).GetBytes(lua), "mod shop names");

                    string manifest = Path.Combine(resourceRoot, "fxmanifest.lua");
                    if (File.Exists(manifest))
                    {
                        var (mtext, mbom) = ReadText(manifest);
                        string patched = FxManifestPatcher.EnsureClientScript(mtext, "livery_names.lua");
                        if (patched != mtext)
                        {
                            WriteFile(manifest, Encode(patched, mbom), "fxmanifest.lua");
                            result.Notes.Add("fxmanifest.lua: added client_script 'livery_names.lua' so the mod shop shows the livery names.");
                        }
                    }
                    else
                    {
                        result.NextSteps.Add("Load livery_names.lua as a client script (client_script 'livery_names.lua' in fxmanifest.lua) so the mod shop shows the names.");
                    }
                }
            }

            // ---------------------------------------------------------------- report
            progress(0.995, "Writing instructions");
            WriteInstructions(job, result, install, resourceName, overlayRoot, exportRoot, backupRoot, kitHelpPath, ddsRoot);
            progress(1, "Done");
            return result;
        }

        static void Validate(MakeJob job)
        {
            if (job.Template == null) throw new LiveryException("Pick the car's livery .yft first.");
            if (job.Template.Textures.All(t => t.NameHash != job.TextureHash)) throw new LiveryException("Pick which texture in the template is the livery.");
            if (job.Liveries.Count == 0) throw new LiveryException("Add at least one livery image.");
            if (string.IsNullOrWhiteSpace(job.OutputFolder)) throw new LiveryException("Choose an output folder.");

            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ownNames = new HashSet<string>(job.Car?.Liveries.Select(l => l.ModelName) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var l in job.Liveries)
            {
                if (string.IsNullOrWhiteSpace(l.ImagePath) || !File.Exists(l.ImagePath)) throw new LiveryException($"Image not found: {l.ImagePath}");
                if (!string.IsNullOrEmpty(l.TemplatePath) && !File.Exists(l.TemplatePath)) throw new LiveryException($"Livery file not found: {l.TemplatePath}");
                l.ModelName = (l.ModelName ?? "").Trim();
                if (l.ModelName.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)) l.ModelName = l.ModelName.Substring(0, l.ModelName.Length - 4);
                if (!LiveryKit.Resources.Naming.IsValidModelName(l.ModelName))
                    throw new LiveryException($"'{l.ModelName}' isn't a valid file name for a livery. Use letters, numbers, _ or - (max 63).");
                if (!models.Add(l.ModelName)) throw new LiveryException($"Two liveries are both called '{l.ModelName}'.");
                if (job.TakenYftNames != null && job.TakenYftNames.Contains(l.ModelName) && !ownNames.Contains(l.ModelName))
                    throw new LiveryException($"A different model is already called '{l.ModelName}.yft' in this folder. Pick another name.");

                l.Label = (l.Label ?? "").Trim();
                if (job.LiteralLabels && l.Label.Length == 0) l.Label = string.IsNullOrWhiteSpace(l.DisplayName) ? l.ModelName : l.DisplayName.Trim();
                if (l.Label.Length == 0) throw new LiveryException($"'{l.ModelName}' needs a mod shop label.");
                if (!job.LiteralLabels && l.Label.Any(char.IsWhiteSpace)) throw new LiveryException($"The label '{l.Label}' can't contain spaces (put the readable name in Mod shop name).");
                if (!labels.Add(l.Label)) throw new LiveryException($"Two liveries share the label '{l.Label}'.");
            }
            if (job.Mode == OutputMode.InstallIntoResource && job.Car == null)
                throw new LiveryException("Installing needs the car's resource folder. Open the car's folder instead of a single file, or export to a folder.");
        }

        static (int w, int h) TargetSize(TextureSizeMode mode, RgbaImage img, TemplateTexture tpl)
        {
            switch (mode)
            {
                case TextureSizeMode.KeepImage:
                    return (Clamp(NearestPow2(img.Width)), Clamp(NearestPow2(img.Height)));
                case TextureSizeMode.Longest1024: return Shape(1024, tpl);
                case TextureSizeMode.Longest2048: return Shape(2048, tpl);
                case TextureSizeMode.Longest4096: return Shape(4096, tpl);
                default: return (tpl.Width, tpl.Height);
            }
        }

        static (int, int) Shape(int longest, TemplateTexture tpl)
        {
            if (tpl.Width >= tpl.Height) return (longest, Math.Max(4, (int)((long)longest * tpl.Height / tpl.Width)));
            return (Math.Max(4, (int)((long)longest * tpl.Width / tpl.Height)), longest);
        }

        static int NearestPow2(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            int lower = p >> 1;
            return lower > 0 && v - lower < p - v ? lower : p;
        }

        static int Clamp(int v) => Math.Max(64, Math.Min(8192, v));

        static string Ratio(int w, int h)
        {
            int a = w, b = h;
            while (b != 0) { int t = a % b; a = b; b = t; }
            return $"{w / a}:{h / a}";
        }

        static string BaseCarName(string modelName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(modelName, @"^(.*?)_?liv(ery)?_?\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : modelName;
        }

        internal static (string Text, bool Bom) ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }

        static byte[] Encode(string text, bool bom)
        {
            var body = new UTF8Encoding(false).GetBytes(text);
            if (!bom) return body;
            var all = new byte[body.Length + 3];
            all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
            Buffer.BlockCopy(body, 0, all, 3, body.Length);
            return all;
        }

        static bool IsInside(string root, string path)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "car" : s.Trim();
        }

        static string UniqueFolder(string path)
        {
            string p = path;
            for (int i = 2; Directory.Exists(p); i++) p = path + "_" + i;
            return p;
        }

        static void WriteInstructions(MakeJob job, MakeResult r, bool install, string resourceName, string overlayRoot, string exportRoot, string backupRoot, string kitHelpPath, string ddsRoot)
        {
            var sb = new StringBuilder();
            string carName = job.Car?.DisplayName ?? job.Template.ModelName;
            sb.AppendLine($"YFT Livery Maker - {carName}");
            sb.AppendLine($"Made {DateTime.Now:yyyy-MM-dd HH:mm} from template {job.Template.ModelName}.yft");
            sb.AppendLine();
            sb.AppendLine("New liveries");
            foreach (var m in r.Liveries)
            {
                sb.AppendLine($"  {m.Request.ModelName}.yft  \"{(string.IsNullOrWhiteSpace(m.Request.DisplayName) ? m.Request.Label : m.Request.DisplayName)}\"  (label {m.Request.Label}){(m.Replaced ? "  - replaces the old one" : "  - new")}");
                sb.AppendLine($"      texture {m.Yft.TextureName}: {m.Yft.Width}x{m.Yft.Height} {m.Yft.Format}, {m.Yft.MipLevels} mipmaps");
                foreach (var c in m.Yft.Checks) sb.AppendLine($"      checked: {c}");
            }
            sb.AppendLine();

            if (install)
            {
                sb.AppendLine($"Installed into {r.OutputFolder}");
                if (r.Backups.Count > 0) sb.AppendLine($"Originals of changed files were backed up to {backupRoot}");
                sb.AppendLine();
                sb.AppendLine("Next");
                sb.AppendLine("  Restart the resource (or the server). The new liveries are in the mod shop under Livery.");
            }
            else
            {
                sb.AppendLine("How to install");
                sb.AppendLine($"  1. Copy the folder \"{resourceName}\" from here onto your resource folder \"{resourceName}\"");
                sb.AppendLine("     and let Windows replace the files. It only holds new and updated files, laid out like your resource.");
                sb.AppendLine("  2. Restart the resource (or the server). The new liveries are in the mod shop under Livery.");
                sb.AppendLine();
                sb.AppendLine("What's in here");
                foreach (var w in r.Written) sb.AppendLine("  " + w.Replace(exportRoot + Path.DirectorySeparatorChar, ""));
            }

            if (r.NextSteps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Still to do by hand");
                foreach (var s in r.NextSteps) sb.AppendLine("  - " + s);
            }
            if (r.Notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Notes");
                foreach (var s in r.Notes) sb.AppendLine("  - " + s);
            }
            if (r.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings");
                foreach (var s in r.Warnings) sb.AppendLine("  - " + s);
            }
            if (job.SaveDds)
            {
                sb.AppendLine();
                sb.AppendLine($"The .dds textures are also in {ddsRoot} if you want them for anything else.");
            }

            string folder = install ? (backupRoot ?? job.OutputFolder) : exportRoot;
            Directory.CreateDirectory(folder);
            r.InstructionsPath = Path.Combine(folder, install ? "WHAT_CHANGED.txt" : "HOW_TO_INSTALL.txt");
            File.WriteAllText(r.InstructionsPath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
