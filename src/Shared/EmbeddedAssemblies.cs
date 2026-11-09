using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace YftLiveryMaker.Boot
{
    /// <summary>
    /// Makes the single-file .exe work. Referenced DLLs (and CodeWalker's ShadersGen9Conversion.xml) are embedded
    /// as "embedded.*" resources. When .NET can't find a DLL next to the .exe it is unpacked to a per-build folder
    /// under %LOCALAPPDATA%\YftLiveryMaker\runtime and loaded from there, so code that looks next to its own DLL
    /// (CodeWalker does, for the gen9 shader table) keeps working.
    /// </summary>
    internal static class EmbeddedAssemblies
    {
        const string Prefix = "embedded.";
        static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        static string folder;

        public static void Install() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out var done)) return done;
                var host = typeof(EmbeddedAssemblies).Assembly;
                if (host.GetManifestResourceInfo(Prefix + name + ".dll") == null) return null;
                try
                {
                    string dir = Folder(host);
                    string path = Unpack(host, name + ".dll", dir);
                    if (string.Equals(name, "CodeWalker.Core", StringComparison.OrdinalIgnoreCase))
                        Unpack(host, "ShadersGen9Conversion.xml", dir);
                    return Loaded[name] = Assembly.LoadFrom(path);
                }
                catch
                {
                    // Can't write to the profile: load from memory instead (only Enhanced-edition files need the folder).
                    using (var s = host.GetManifestResourceStream(Prefix + name + ".dll"))
                        return Loaded[name] = Assembly.Load(ReadAll(s));
                }
            }
        }

        static string Folder(Assembly host)
        {
            if (folder != null) return folder;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YftLiveryMaker", "runtime");
            folder = Path.Combine(root, host.GetName().Name + "-" + host.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12));
            Directory.CreateDirectory(folder);
            try
            {
                // Clear out folders from older builds of this program (anything still in use is skipped).
                foreach (var d in Directory.GetDirectories(root, host.GetName().Name + "-*"))
                {
                    if (string.Equals(d, folder, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
            return folder;
        }

        static string Unpack(Assembly host, string file, string dir)
        {
            string path = Path.Combine(dir, file);
            using (var s = host.GetManifestResourceStream(Prefix + file))
            {
                if (s == null) return path;
                var bytes = ReadAll(s);
                if (File.Exists(path) && SameContent(path, bytes)) return path;
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch
                {
                    // Another copy of the program got there first; use what it wrote.
                    try { File.Delete(tmp); } catch { }
                    if (!File.Exists(path)) throw;
                }
            }
            return path;
        }

        static bool SameContent(string path, byte[] bytes)
        {
            try
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length != bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++) if (existing[i] != bytes[i]) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static byte[] ReadAll(Stream s)
        {
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
