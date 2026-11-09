using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LiveryKit.Resources;
using LiveryKit.Textures;

namespace LiveryKit.Mcp
{
    /// <summary>
    /// Model Context Protocol server, so AI apps (Claude Desktop, Cursor, VS Code, ...) can find cars and make liveries.
    /// It speaks newline-delimited JSON-RPC 2.0 on stdin/stdout and opens no network port.
    /// </summary>
    public static class McpServer
    {
        public const string Name = "yft-livery-maker";

        // Newest first. A client asking for one of these gets it back; anything else gets the newest.
        static readonly string[] ProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        const string Instructions =
            "YFT Livery Maker turns livery images into GTA V / FiveM livery .yft files on this PC. Call find_cars first with the " +
            "folder the user names (a car's resource, a vehicle pack or a resources folder) to see the car's liveries and names, " +
            "then make_liveries. Use full Windows paths. Images must be painted on the car's livery template. By default the " +
            "results go in a new folder to copy over the car's resource; only use install=true when the user wants them put " +
            "straight into the resource. Pass the warnings and next steps it returns on to the user.";

        const string ToolsJson = @"[
  {
    ""name"": ""find_cars"",
    ""title"": ""Find cars and their liveries"",
    ""description"": ""Lists the GTA V / FiveM cars with YFT liveries in a folder on this PC: a car's resource folder, a whole vehicle pack, a server's resources folder, or one livery .yft. For the car it shows every existing livery (file name, mod shop name, label), what the next new livery will be called, and where its carcols.meta and names file are. Other cars in the folder are listed by name. Call this before make_liveries."",
    ""inputSchema"": {
      ""type"": ""object"",
      ""properties"": {
        ""folder"": { ""type"": ""string"", ""description"": ""Full path of the car's resource folder, a vehicle pack or resources folder, or a livery .yft, e.g. D:\\server\\resources\\[cars]\\mycar"" },
        ""car"": { ""type"": ""string"", ""description"": ""Which car to show the liveries of, when the folder has more than one."" }
      },
      ""required"": [""folder""]
    },
    ""annotations"": { ""readOnlyHint"": true, ""openWorldHint"": false }
  },
  {
    ""name"": ""make_liveries"",
    ""title"": ""Make liveries"",
    ""description"": ""Turns livery images into GTA V / FiveM livery .yft files for a car. Each .yft is a copy of one of the car's own livery files with only the texture swapped (DXT5 with 10 mipmaps by default), and the carcols.meta mod kit entries and mod shop names are added too. Each image becomes a new livery, or replaces one of the car's liveries. By default everything goes in a new folder laid out like the car's resource, ready to copy over it; with install=true it is written straight into the car's resource, and every file it changes is backed up first (install=true first asks the user with a Yes/No box on their PC, so only use it when they want that). Images (PNG, PSD, TGA, JPG, BMP, TIFF or DDS) must be painted on the car's livery template."",
    ""inputSchema"": {
      ""type"": ""object"",
      ""properties"": {
        ""folder"": { ""type"": ""string"", ""description"": ""Full path of the car's resource folder, a vehicle pack or resources folder that holds it, or one of its livery .yft files."" },
        ""car"": { ""type"": ""string"", ""description"": ""Which car, when the folder has more than one (its name from find_cars)."" },
        ""liveries"": {
          ""type"": ""array"",
          ""minItems"": 1,
          ""description"": ""One entry per image."",
          ""items"": {
            ""type"": ""object"",
            ""properties"": {
              ""image"": { ""type"": ""string"", ""description"": ""Full path of the livery image."" },
              ""name"": { ""type"": ""string"", ""description"": ""Mod shop name, e.g. Red Stripes. Default: a replaced livery keeps its name, a new one is named after the image file."" },
              ""replace"": { ""type"": ""string"", ""description"": ""File name (without .yft) of the car's livery to replace, from find_cars. Leave out to add a new livery."" },
              ""file_name"": { ""type"": ""string"", ""description"": ""File name (without .yft) for a new livery. Default: the car's next free livery number."" }
            },
            ""required"": [""image""]
          }
        },
        ""install"": { ""type"": ""boolean"", ""description"": ""true: write straight into the car's resource (changed files are backed up), after the user answers a Yes/No box on their PC. Default false: write a new folder to copy over the resource."" },
        ""output_folder"": { ""type"": ""string"", ""description"": ""Where the new folder goes (with install: the backups and .dds copies). Default: Documents\\YFT Livery Maker."" },
        ""format"": { ""type"": ""string"", ""enum"": [""DXT5"", ""DXT1"", ""BC7"", ""A8R8G8B8""], ""description"": ""Texture format. Default DXT5, which every GTA livery uses."" },
        ""mipmaps"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 13, ""description"": ""Number of mipmaps. Default 10 (2048x2048 down to 4x4)."" },
        ""size"": { ""type"": ""string"", ""enum"": [""match"", ""keep"", ""1024"", ""2048"", ""4096""], ""description"": ""Texture size: match the car's livery (default), keep the image's own size, or this longest side."" },
        ""save_dds"": { ""type"": ""boolean"", ""description"": ""Also save each texture as a .dds file. Default true."" }
      },
      ""required"": [""folder"", ""liveries""]
    },
    ""annotations"": { ""readOnlyHint"": false, ""destructiveHint"": true, ""idempotentHint"": false, ""openWorldHint"": false }
  }
]";

        static readonly object Tools = Json.Parse(ToolsJson);

        /// <summary>Serves requests on stdin/stdout until the AI app closes stdin.</summary>
        public static void RunStdio()
        {
            var stdout = Console.OpenStandardOutput();
            // Anything else that prints goes to stderr (the AI app's log), never into the protocol stream.
            Console.SetOut(Console.Error);
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(stdout, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                string reply = Handle(line);
                if (reply != null) output.WriteLine(reply);
            }
        }

        /// <summary>Answers one JSON-RPC message. Null when there's nothing to send back (notifications).</summary>
        public static string Handle(string line)
        {
            Dictionary<string, object> msg;
            try { msg = Json.Parse(line) as Dictionary<string, object>; }
            catch (Exception) { return Reply(null, null, -32700, "Parse error"); }
            if (msg == null) return Reply(null, null, -32600, "Invalid request");

            msg.TryGetValue("id", out var id);
            msg.TryGetValue("method", out var method);
            // Notifications (initialized, cancelled, ...) need no answer, and this server never sends requests of its own.
            if (id == null || !(method is string)) return null;
            var p = msg.TryGetValue("params", out var pv) ? pv as Dictionary<string, object> : null;
            try
            {
                switch ((string)method)
                {
                    case "initialize": return Reply(id, Initialize(p));
                    case "ping": return Reply(id, new Dictionary<string, object>());
                    case "tools/list": return Reply(id, new Dictionary<string, object> { ["tools"] = Tools });
                    case "tools/call": return Reply(id, CallTool(p));
                    default: return Reply(id, null, -32601, "Method not found: " + method);
                }
            }
            catch (McpException e)
            {
                return Reply(id, null, e.Code, e.Message);
            }
            catch (Exception e)
            {
                Log(e.ToString());
                return Reply(id, null, -32603, "Internal error: " + e.Message);
            }
        }

        static string Reply(object id, object result, int code = 0, string error = null)
        {
            var r = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id };
            if (error == null) r["result"] = result;
            else r["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = error };
            return Json.Write(r);
        }

        static object Initialize(Dictionary<string, object> p)
        {
            string asked = Str(p, "protocolVersion");
            var v = (Assembly.GetEntryAssembly() ?? typeof(McpServer).Assembly).GetName().Version;
            return new Dictionary<string, object>
            {
                ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                ["serverInfo"] = new Dictionary<string, object> { ["name"] = Name, ["title"] = "YFT Livery Maker", ["version"] = $"{v.Major}.{v.Minor}.{v.Build}" },
                ["instructions"] = Instructions,
            };
        }

        static object CallTool(Dictionary<string, object> p)
        {
            string name = Str(p, "name");
            var args = p != null && p.TryGetValue("arguments", out var a) && a is Dictionary<string, object> d ? d : new Dictionary<string, object>();
            Func<Dictionary<string, object>, string> tool;
            switch (name)
            {
                case "find_cars": tool = FindCars; break;
                case "make_liveries": tool = MakeLiveries; break;
                default: throw new McpException(-32602, "Unknown tool: " + name);
            }

            // Tool failures go back as a result the AI can read and fix, not as a protocol error.
            bool failed = false;
            string text;
            try
            {
                text = tool(args);
            }
            catch (Exception e)
            {
                failed = true;
                text = e is LiveryException || e is IOException || e is UnauthorizedAccessException ? e.Message : e.GetType().Name + ": " + e.Message;
                Log(name + " failed: " + (e is LiveryException ? e.Message : e.ToString()));
            }
            return new Dictionary<string, object>
            {
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
                ["isError"] = failed,
            };
        }

        static string FindCars(Dictionary<string, object> a)
        {
            var scan = CarScanner.Scan(FullPath(a, "folder"));
            string name = Str(a, "car");
            var car = name != null || scan.Cars.Count == 0 ? LiveryMaker.FindCar(scan, name) : scan.Preferred ?? (scan.Cars.Count == 1 ? scan.Cars[0] : null);

            var sb = new StringBuilder();
            sb.AppendLine($"{scan.ScannedFolder}: {scan.Cars.Count} car{(scan.Cars.Count == 1 ? "" : "s")} with YFT liveries.");
            if (car != null)
            {
                sb.AppendLine().AppendLine("Car: " + car);
                sb.AppendLine("  Resource folder: " + car.ResourceRoot);
                sb.AppendLine(car.Kit != null
                    ? $"  Mod kit {car.Kit.KitName} in {car.Kit.FilePath}"
                    : "  No carcols.meta lists these liveries yet; make_liveries writes a ready-made mod kit with instructions.");
                if (car.NamesLuaPath != null) sb.AppendLine("  Mod shop names: " + car.NamesLuaPath);
                var template = LiveryMaker.TemplateFor(scan, car);
                if (template != null && template.Usable)
                {
                    var naming = Naming.For(car, template, scan.AllYftNames);
                    sb.AppendLine($"  New liveries are built from {template.ModelName}.yft; the next one is {naming.ModelName(0)}" +
                                  (naming.LiteralLabels ? "." : $" (label {naming.Label(0, "")})."));
                }
                else sb.AppendLine("  None of its livery files can be opened (missing or escrow-locked), so it can't get new liveries.");
                sb.AppendLine("  Liveries (file name, mod shop name, label):");
                foreach (var l in car.Liveries)
                {
                    string text = string.IsNullOrWhiteSpace(l.LabelText) ? "" : $"  \"{l.LabelText}\"";
                    string label = l.Label != null && l.Label != l.LabelText ? "  " + l.Label : "";
                    string state = l.YftPath == null ? "  (file missing)" : l.Escrowed ? "  (escrow-locked)" : "";
                    sb.AppendLine($"    {l.ModelName}{text}{label}{state}");
                }
            }

            var others = scan.Cars.Where(c => c != car).ToList();
            if (others.Count > 0)
            {
                sb.AppendLine().AppendLine(car == null ? "Cars (pass one as car to see its liveries):" : "Other cars here:");
                foreach (var c in others.Take(200)) sb.AppendLine("  " + c);
                if (others.Count > 200) sb.AppendLine($"  ...and {others.Count - 200} more");
            }
            foreach (var w in scan.Warnings) sb.AppendLine("Warning: " + w);
            return sb.ToString().TrimEnd();
        }

        static string MakeLiveries(Dictionary<string, object> a)
        {
            var wishes = new List<LiveryWish>();
            foreach (var item in a.TryGetValue("liveries", out var l) && l is IList list ? list : new object[0])
            {
                // A bare path is fine too.
                var d = item as Dictionary<string, object> ?? new Dictionary<string, object> { ["image"] = item };
                wishes.Add(new LiveryWish { ImagePath = FullPath(d, "image"), DisplayName = Str(d, "name"), Replace = Str(d, "replace"), FileName = Str(d, "file_name") });
            }
            if (wishes.Count == 0) throw new LiveryException("liveries needs at least one image.");

            string output = Str(a, "output_folder") != null ? FullPath(a, "output_folder")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YFT Livery Maker");
            var job = LiveryMaker.Plan(FullPath(a, "folder"), Str(a, "car"), wishes, output, Bool(a, "install", false));
            if (Str(a, "format") is string format)
                job.Texture.Format = Enum.TryParse(format, true, out LiveryTextureFormat f) ? f : throw new LiveryException($"Unknown format '{format}'. Use DXT5, DXT1, BC7 or A8R8G8B8.");
            if (Str(a, "mipmaps") is string mips)
                job.Texture.MipLevels = int.TryParse(mips, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 ? n : throw new LiveryException($"mipmaps must be a whole number from 1 up, not '{mips}'.");
            if (Str(a, "size") is string size) job.SizeMode = LiveryMaker.SizeFrom(size);
            job.SaveDds = Bool(a, "save_dds", true);

            ConfirmInstall(job);
            var sw = Stopwatch.StartNew();
            var r = LiveryMaker.Run(job);
            var sb = new StringBuilder();
            sb.AppendLine(job.Mode == OutputMode.InstallIntoResource
                ? $"Put {r.Liveries.Count} livery file(s) straight into {job.Car.DisplayName}'s resource {r.OutputFolder} in {sw.Elapsed.TotalSeconds:F1} s."
                : $"Made {r.Liveries.Count} livery file(s) for {job.Car.DisplayName} in {sw.Elapsed.TotalSeconds:F1} s, in {r.OutputFolder}");
            foreach (var m in r.Liveries)
                sb.AppendLine($"- {m.Request.ModelName}.yft \"{m.Request.DisplayName}\" ({(m.Replaced ? "replaced" : "new")}, label {m.Request.Label}): " +
                              $"{m.Yft.Width}x{m.Yft.Height} {m.Yft.Format}, {m.Yft.MipLevels} mipmaps");
            AppendList(sb, "Files written", r.Written);
            AppendList(sb, "Backed up before changing", r.Backups);
            AppendList(sb, "Notes", r.Notes);
            AppendList(sb, "Warnings (tell the user)", r.Warnings);
            AppendList(sb, "Next steps", r.NextSteps);
            if (r.InstructionsPath != null) sb.AppendLine("Full instructions: " + r.InstructionsPath);
            return sb.ToString().TrimEnd();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>Writing into a resource needs a human's Yes, so a prompt-injected AI can't change server files alone.</summary>
        static void ConfirmInstall(MakeJob job)
        {
            if (job.Mode != OutputMode.InstallIntoResource || Environment.GetEnvironmentVariable("YFTLIVERY_NO_CONFIRM") == "1") return;
            bool Replaces(LiveryRequest q) => job.Car.Liveries.Any(l => string.Equals(l.ModelName, q.ModelName, StringComparison.OrdinalIgnoreCase));
            string news = string.Join(", ", job.Liveries.Where(q => !Replaces(q)).Select(q => q.ModelName + ".yft"));
            string replaced = string.Join(", ", job.Liveries.Where(Replaces).Select(q => q.ModelName + ".yft"));
            string text = $"An AI app wants to put {job.Liveries.Count} {(job.Liveries.Count == 1 ? "livery" : "liveries")} into {job.Car.DisplayName}:\n{job.Car.ResourceRoot}\n\n" +
                (news.Length > 0 ? "New: " + news + "\n" : "") + (replaced.Length > 0 ? "Replaced: " + replaced + "\n" : "") +
                $"\nEvery file it changes is backed up first to:\n{Path.Combine(job.OutputFolder, "Backups")}\n\nAllow this?";
            // YESNO | ICONWARNING | DEFBUTTON2 (No) | SETFOREGROUND | TOPMOST
            if (MessageBoxW(IntPtr.Zero, text, "YFT Livery Maker", 0x4 | 0x30 | 0x100 | 0x10000 | 0x40000) != 6)
                throw new LiveryException("The user said no to writing into the car's resource. Ask them what they want, or make the liveries in a new folder instead (install=false).");
        }

        /// <summary>
        /// Adds this server to an AI app's JSON settings (or removes it when <paramref name="entry"/> is null), keeping
        /// everything else. Returns the new text, or null when nothing needs to change.
        /// </summary>
        public static string Register(string configJson, string serversKey, Dictionary<string, object> entry)
        {
            var root = string.IsNullOrWhiteSpace(configJson) ? new Dictionary<string, object>()
                : Json.Parse(configJson) as Dictionary<string, object> ?? throw new FormatException("the file isn't a JSON object");
            root.TryGetValue(serversKey, out var s);
            var servers = s as Dictionary<string, object>;
            if (servers == null)
            {
                if (s != null) throw new FormatException($"\"{serversKey}\" isn't a JSON object");
                if (entry == null) return null;
                root[serversKey] = servers = new Dictionary<string, object>();
            }
            if (entry == null)
            {
                if (!servers.Remove(Name)) return null;
            }
            else
            {
                if (servers.TryGetValue(Name, out var old) && Json.Write(old) == Json.Write(entry)) return null;
                servers[Name] = entry;
            }
            string text = Json.Write(root, indented: true) + "\n";
            return configJson != null && configJson.Contains("\r\n") ? text.Replace("\n", "\r\n") : text;
        }

        /// <summary>This server's entry in an AI app's JSON settings, or null.</summary>
        public static Dictionary<string, object> Registered(string configJson, string serversKey) =>
            !string.IsNullOrWhiteSpace(configJson) && Json.Parse(configJson) is Dictionary<string, object> root
            && root.TryGetValue(serversKey, out var s) && s is Dictionary<string, object> servers && servers.TryGetValue(Name, out var e)
                ? e as Dictionary<string, object>
                : null;

        static void AppendList(StringBuilder sb, string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine(title + ":");
            foreach (var i in items) sb.AppendLine("- " + i);
        }

        static string Str(Dictionary<string, object> a, string key)
        {
            if (a == null || !a.TryGetValue(key, out var v) || v == null) return null;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
            return s.Length == 0 ? null : s;
        }

        static bool Bool(Dictionary<string, object> a, string key, bool fallback)
        {
            string s = Str(a, key);
            return s == null ? fallback : s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        /// <summary>A full Windows path (quotes from "Copy as path" are fine); relative paths would depend on the AI app's folder.</summary>
        static string FullPath(Dictionary<string, object> a, string key)
        {
            string p = Str(a, key)?.Trim('"', '\'', ' ') ?? throw new LiveryException(key + " is required.");
            bool full = p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/') || p.StartsWith(@"\\");
            if (!full) throw new LiveryException($"{key} must be a full Windows path like C:\\Users\\you\\Desktop\\livery.png, not '{p}'.");
            return Path.GetFullPath(p);
        }

        static void Log(string text)
        {
            try { Console.Error.WriteLine("[yft-livery-maker] " + text); } catch { }
        }

        sealed class McpException : Exception
        {
            public int Code { get; }
            public McpException(int code, string message) : base(message) { Code = code; }
        }
    }
}
