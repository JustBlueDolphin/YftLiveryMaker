using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LiveryKit.Mcp;

namespace YftLiveryMaker
{
    /// <summary>
    /// Adds this program as an MCP server to the AI apps installed on this PC by editing their JSON settings. Only its
    /// own entry changes; everything else is kept, and the previous file is saved next to it as .yftlivery.bak.
    /// </summary>
    internal static class AiApps
    {
        internal sealed class App
        {
            public string Name, SettingsPath, ServersKey;
            public bool TypeField;
        }

        public static string ExePath => Application.ExecutablePath;

        /// <summary>Started from a temp folder (straight out of the zip): AI apps would lose it when that's cleaned up.</summary>
        public static bool RunningFromTemp => ExePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

        public static string ClaudeCodeCommand => $"claude mcp add --scope user {McpServer.Name} -- \"{ExePath}\" --mcp";

        public static string SetupText =>
            "Claude Code, run:\r\n" + ClaudeCodeCommand + "\r\n\r\nOther apps, add this to their MCP servers:\r\n" +
            Json.Write(new Dictionary<string, object> { ["mcpServers"] = new Dictionary<string, object> { [McpServer.Name] = Entry(null) } }, indented: true).Replace("\n", "\r\n");

        /// <summary>AI apps with MCP support whose settings folder exists on this PC.</summary>
        public static List<App> Installed()
        {
            // Environment variables rather than shell folders, so a test can point them somewhere else.
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string home = Environment.GetEnvironmentVariable("USERPROFILE");
            var apps = new List<App>();
            void Add(string name, string folder, string file, string key = "mcpServers", bool typeField = false)
            {
                if (Directory.Exists(folder)) apps.Add(new App { Name = name, SettingsPath = Path.Combine(folder, file), ServersKey = key, TypeField = typeField });
            }
            try
            {
                if (!string.IsNullOrEmpty(appData))
                {
                    Add("Claude Desktop", Path.Combine(appData, "Claude"), "claude_desktop_config.json");
                    Add("VS Code", Path.Combine(appData, "Code", "User"), "mcp.json", "servers", typeField: true);
                }
                // The Microsoft Store version of Claude keeps its settings inside its package folder.
                if (!string.IsNullOrEmpty(local) && Directory.Exists(Path.Combine(local, "Packages")))
                    foreach (var pkg in Directory.GetDirectories(Path.Combine(local, "Packages"), "Claude_*"))
                        Add("Claude Desktop", Path.Combine(pkg, "LocalCache", "Roaming", "Claude"), "claude_desktop_config.json");
                if (!string.IsNullOrEmpty(home))
                {
                    Add("Cursor", Path.Combine(home, ".cursor"), "mcp.json");
                    Add("Windsurf", Path.Combine(home, ".codeium", "windsurf"), "mcp_config.json");
                    Add("LM Studio", Path.Combine(home, ".lmstudio"), "mcp.json");
                }
            }
            catch (Exception) { }
            return apps;
        }

        static Dictionary<string, object> Entry(App app)
        {
            var e = new Dictionary<string, object>();
            if (app?.TypeField == true) e["type"] = "stdio";
            e["command"] = ExePath;
            e["args"] = new object[] { "--mcp" };
            return e;
        }

        /// <summary>True when at least one AI app starts this copy of the program.</summary>
        public static bool IsOn() => Installed().Any(app =>
        {
            try
            {
                var entry = File.Exists(app.SettingsPath) ? McpServer.Registered(File.ReadAllText(app.SettingsPath), app.ServersKey) : null;
                return entry != null && entry.TryGetValue("command", out var c) && string.Equals(c as string, ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        });

        /// <summary>Adds (on) or removes (off) the server in every installed AI app: the apps that have it now, and any that failed.</summary>
        public static (List<string> Done, List<string> Failed) Set(bool on)
        {
            var done = new List<string>();
            var failed = new List<string>();
            foreach (var app in Installed())
            {
                try
                {
                    string old = File.Exists(app.SettingsPath) ? File.ReadAllText(app.SettingsPath) : "";
                    string text = McpServer.Register(old, app.ServersKey, on ? Entry(app) : null);
                    if (text != null)
                    {
                        if (old.Length > 0) File.Copy(app.SettingsPath, app.SettingsPath + ".yftlivery.bak", true);
                        File.WriteAllText(app.SettingsPath, text, new UTF8Encoding(false));
                    }
                    if (on || text != null) done.Add(app.Name);
                }
                catch (Exception ex)
                {
                    failed.Add($"{app.Name}: couldn't update {app.SettingsPath} ({ex.Message})");
                }
            }
            return (done.Distinct().ToList(), failed);
        }
    }
}
