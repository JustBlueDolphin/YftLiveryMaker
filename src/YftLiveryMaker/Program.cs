using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace YftLiveryMaker
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Must be hooked before anything touches LiveryKit/CodeWalker types.
            Boot.EmbeddedAssemblies.Install();
            Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run(string[] args)
        {
            if (args.Length > 0 && args[0] == "--mcp")
            {
                // How AI apps start it (see AiApps): no window, MCP over stdin/stdout.
                LiveryKit.Mcp.McpServer.RunStdio();
                return;
            }

            // build.ps1 renames a running copy aside when updating; tidy those up once they've exited.
            try
            {
                foreach (var old in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "YftLiveryMaker.old*.exe"))
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => MainForm.ShowError(null, e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => MainForm.ShowError(null, e.ExceptionObject as Exception);

            if (args.Length >= 2 && args[0] == "--screenshot")
                Settings.UseFile(Path.Combine(Path.GetTempPath(), "yft-livery-maker-test-settings.txt"));

            // Dark is the default; the header switch remembers the choice.
            string theme = Settings.Get("theme", "dark");
            int themeArg = Array.IndexOf(args, "--theme");
            if (themeArg >= 0 && themeArg + 1 < args.Length)
            {
                theme = args[themeArg + 1];
                var trimmed = new List<string>(args);
                trimmed.RemoveRange(themeArg, 2);
                args = trimmed.ToArray();
            }
            UI.Theme.Use(!string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase));

            var form = new MainForm();
            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                // Developer aid: render the window with a car and images loaded, save a PNG, exit.
                // It opens on the main screen so the picture has the designed size wherever the mouse is.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = Screen.PrimaryScreen.WorkingArea.Location;
                var rest = new List<string>(args).GetRange(2, args.Length - 2);
                form.Shown += async (s, e) =>
                {
                    try
                    {
                        string Take(string flag)
                        {
                            int i = rest.IndexOf(flag);
                            if (i < 0) return null;
                            string value = i + 1 < rest.Count ? rest[i + 1] : "";
                            rest.RemoveRange(i, Math.Min(2, rest.Count - i));
                            return value;
                        }
                        bool clearLog = rest.Remove("--clear-log");
                        string dialogKind = Take("--dialog");
                        string makeOut = Take("--make");
                        string replaceFirst = Take("--replace-first");
                        string menuOut = Take("--menu");
                        if (rest.Count > 0) await form.LoadCarAsync(rest[0]);
                        if (rest.Count > 1) await form.AddImagesAsync(rest.GetRange(1, rest.Count - 1));
                        if (replaceFirst != null) await form.ReplaceFirstForTestAsync(replaceFirst);
                        if (makeOut != null) await form.MakeForTestAsync(makeOut);
                        if (menuOut != null) await form.CaptureMenuAsync(menuOut);
                        if (clearLog) form.ClearLogForTest();
                        await System.Threading.Tasks.Task.Delay(400);
                        if (dialogKind != null) await form.CaptureDialogAsync(dialogKind, args[1]);
                        else form.SaveScreenshot(args[1]);
                    }
                    finally
                    {
                        form.Close();
                    }
                };
            }
            else if (args.Length > 0)
            {
                form.Shown += async (s, e) => await form.OpenPathsAsync(args);
            }
            Application.Run(form);
        }

    }
}
