using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LiveryKit.Meta;

namespace LiveryKit.Resources
{
    public sealed class LiveryFile
    {
        public string ModelName { get; internal set; }
        /// <summary>Full path of the .yft, or null if carcols lists it but the file wasn't found.</summary>
        public string YftPath { get; internal set; }
        public string Label { get; internal set; }
        /// <summary>The mod shop text for the label, if a names script defines it.</summary>
        public string LabelText { get; internal set; }
        public bool Escrowed { get; internal set; }
        public bool Usable => YftPath != null && !Escrowed;
        public override string ToString() => ModelName + (YftPath == null ? "  (file missing)" : Escrowed ? "  (escrow-locked)" : "");
    }

    /// <summary>A car that has YFT liveries, with everything needed to add more.</summary>
    public sealed class CarInfo
    {
        public string DisplayName { get; internal set; }
        public List<string> SpawnNames { get; } = new List<string>();
        /// <summary>Folder holding fxmanifest.lua (or the scanned folder when there is none).</summary>
        public string ResourceRoot { get; internal set; }
        public CarcolsKit Kit { get; internal set; }
        public string CarvariationsPath { get; internal set; }
        public string NamesLuaPath { get; internal set; }
        public List<LiveryFile> Liveries { get; } = new List<LiveryFile>();

        public LiveryFile DefaultTemplate =>
            Liveries.Where(l => l.Usable).OrderBy(l => Naming.TrailingNumber(l.ModelName) ?? int.MaxValue).ThenBy(l => l.ModelName).FirstOrDefault();

        public override string ToString()
        {
            int n = Liveries.Count(l => l.YftPath != null);
            string kit = Kit != null ? $"kit {Kit.KitName}" : "no carcols entry found";
            return $"{DisplayName}  —  {n} YFT {(n == 1 ? "livery" : "liveries")}  ({kit})";
        }
    }

    public sealed class ScanResult
    {
        public string ScannedFolder { get; internal set; }
        public List<CarInfo> Cars { get; } = new List<CarInfo>();
        /// <summary>The car the user pointed at (when they picked a file or a car's own folder).</summary>
        public CarInfo Preferred { get; internal set; }
        public LiveryFile PreferredLivery { get; internal set; }
        public HashSet<string> AllYftNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new List<string>();
    }

    public static class CarScanner
    {
        static readonly Regex LiveryName = new Regex(@"^(?<prefix>.*?_?liv(?:ery)?_?)(?<num>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        const int MaxFiles = 300000;

        /// <summary>Scans a resource folder (or anything above/below one) or a single .yft.</summary>
        public static ScanResult Scan(string path)
        {
            string pickedFile = File.Exists(path) ? Path.GetFullPath(path) : null;
            string startDir = pickedFile != null ? Path.GetDirectoryName(pickedFile) : Path.GetFullPath(path);
            if (!Directory.Exists(startDir)) throw new LiveryException($"Folder not found: {startDir}");

            string root = FindResourceRoot(startDir) ?? startDir;
            var result = new ScanResult { ScannedFolder = root };

            var yfts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var carcols = new List<CarcolsKit>();
            var variations = new List<VariationEntry>();
            var entries = new List<TextEntry>();

            int count = 0;
            foreach (var file in EnumerateFiles(root))
            {
                if (++count > MaxFiles) { result.Warnings.Add("Stopped scanning after 300,000 files; pick a smaller folder."); break; }
                string ext = Path.GetExtension(file).ToLowerInvariant();
                try
                {
                    if (ext == ".yft")
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        result.AllYftNames.Add(name);
                        if (!yfts.TryGetValue(name, out var list)) yfts[name] = list = new List<string>();
                        list.Add(file);
                    }
                    else if (ext == ".meta" || ext == ".xml")
                    {
                        if (new FileInfo(file).Length > 20_000_000) continue;
                        string text = File.ReadAllText(file);
                        if (CarcolsReader.IsCarcols(text)) carcols.AddRange(CarcolsReader.ReadKits(text, file));
                        else if (CarvariationsReader.IsCarvariations(text)) variations.AddRange(CarvariationsReader.Read(text, file));
                    }
                    else if (ext == ".lua")
                    {
                        if (new FileInfo(file).Length > 5_000_000) continue;
                        string text = File.ReadAllText(file);
                        if (TextEntries.HasEntries(text)) entries.AddRange(TextEntries.Read(text, file));
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Skipped {MakeRelative(root, file)}: {ex.Message}");
                }
            }

            var labelText = new Dictionary<string, TextEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) labelText[e.Key] = e;

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Cars from mod kits that contain livery mods.
            foreach (var kit in carcols.Where(k => k.Liveries.Count > 0))
            {
                var car = new CarInfo { Kit = kit, ResourceRoot = FindResourceRoot(Path.GetDirectoryName(kit.FilePath)) ?? root };
                foreach (var v in variations.Where(v => v.Kits.Any(k => string.Equals(k, kit.KitName, StringComparison.OrdinalIgnoreCase))))
                {
                    if (!car.SpawnNames.Contains(v.ModelName, StringComparer.OrdinalIgnoreCase)) car.SpawnNames.Add(v.ModelName);
                    car.CarvariationsPath = car.CarvariationsPath ?? v.FilePath;
                }

                foreach (var lv in kit.Liveries)
                {
                    var lf = new LiveryFile { ModelName = lv.ModelName, Label = lv.Label, YftPath = PickFile(yfts, lv.ModelName, car.ResourceRoot) };
                    if (labelText.TryGetValue(lv.Label ?? "", out var te)) lf.LabelText = te.Value;
                    car.Liveries.Add(lf);
                    claimed.Add(lv.ModelName);
                }

                car.NamesLuaPath = car.Liveries
                    .Select(l => labelText.TryGetValue(l.Label ?? "", out var te) ? te.FilePath : null)
                    .Where(p => p != null)
                    .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                car.DisplayName = car.SpawnNames.Count > 0 ? string.Join(" / ", car.SpawnNames) : kit.KitName;
                result.Cars.Add(car);
            }

            // Livery-looking .yft files no kit mentions: group them by name prefix.
            foreach (var group in yfts.Keys.Where(n => !claimed.Contains(n) && LiveryName.IsMatch(n))
                         .GroupBy(n => LiveryName.Match(n).Groups["prefix"].Value, StringComparer.OrdinalIgnoreCase))
            {
                var first = PickFile(yfts, group.First(), root);
                var car = new CarInfo
                {
                    DisplayName = group.Key.TrimEnd('_') + "  (not in any carcols.meta)",
                    ResourceRoot = FindResourceRoot(Path.GetDirectoryName(first)) ?? root,
                };
                foreach (var n in group) car.Liveries.Add(new LiveryFile { ModelName = n, YftPath = PickFile(yfts, n, car.ResourceRoot) });
                result.Cars.Add(car);
            }

            foreach (var car in result.Cars)
                foreach (var lf in car.Liveries.Where(l => l.YftPath != null))
                    lf.Escrowed = IsEscrowed(lf.YftPath);

            result.Cars.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            // Work out which car the user meant.
            if (pickedFile != null)
            {
                foreach (var car in result.Cars)
                {
                    var hit = car.Liveries.FirstOrDefault(l => l.YftPath != null && PathEquals(l.YftPath, pickedFile));
                    if (hit != null) { result.Preferred = car; result.PreferredLivery = hit; break; }
                }
                if (result.Preferred == null)
                {
                    // A .yft nothing references: still usable on its own.
                    var car = new CarInfo { DisplayName = Path.GetFileNameWithoutExtension(pickedFile) + "  (single file)", ResourceRoot = root };
                    var lf = new LiveryFile { ModelName = Path.GetFileNameWithoutExtension(pickedFile), YftPath = pickedFile, Escrowed = IsEscrowed(pickedFile) };
                    car.Liveries.Add(lf);
                    result.Cars.Insert(0, car);
                    result.Preferred = car;
                    result.PreferredLivery = lf;
                }
            }
            else
            {
                string picked = startDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                result.Preferred = result.Cars.FirstOrDefault(c => c.Liveries.Any(l => l.YftPath != null && l.YftPath.StartsWith(picked, StringComparison.OrdinalIgnoreCase)))
                                   ?? (result.Cars.Count == 1 ? result.Cars[0] : null);
            }

            return result;
        }

        static string PickFile(Dictionary<string, List<string>> yfts, string name, string preferRoot)
        {
            if (string.IsNullOrEmpty(name) || !yfts.TryGetValue(name, out var list)) return null;
            string prefix = preferRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return list.FirstOrDefault(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? list[0];
        }

        public static bool IsEscrowed(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var b = new byte[4];
                    return fs.Read(b, 0, 4) == 4 && b[0] == 'F' && b[1] == 'X' && b[2] == 'A' && b[3] == 'P';
                }
            }
            catch { return false; }
        }

        /// <summary>The nearest folder at or above <paramref name="dir"/> with a FiveM manifest.</summary>
        public static string FindResourceRoot(string dir)
        {
            var d = new DirectoryInfo(dir);
            for (int i = 0; d != null && i < 8; i++, d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "fxmanifest.lua")) || File.Exists(Path.Combine(d.FullName, "__resource.lua")))
                    return d.FullName;
            }
            return null;
        }

        static IEnumerable<string> EnumerateFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch { continue; }
                foreach (var f in files) yield return f;
                foreach (var d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith(".") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(d);
                }
            }
        }

        public static bool PathEquals(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        public static string MakeRelative(string root, string path)
        {
            string r = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? path.Substring(r.Length) : path;
        }
    }
}
