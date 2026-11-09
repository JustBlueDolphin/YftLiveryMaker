using System;
using System.IO;
using CodeWalker.GameFiles;

namespace LiveryKit.Yft
{
    /// <summary>
    /// Loads and saves loose .yft files through CodeWalker.Core, keeping the file's own
    /// resource generation (legacy/FiveM = 162, Enhanced/gen9 = 171).
    /// </summary>
    public static class YftIO
    {
        public const int LegacyVersion = 162;
        public const int Gen9Version = 171;
        const uint Rsc7Magic = 0x37435352;

        // CodeWalker keeps the gen9 switch in a static, so loads/saves must not interleave.
        static readonly object Gate = new object();

        public static int ReadResourceVersion(byte[] data)
        {
            if (data == null || data.Length < 16) return -1;
            if (BitConverter.ToUInt32(data, 0) != Rsc7Magic) return -1;
            return BitConverter.ToInt32(data, 4);
        }

        public static YftFile Load(byte[] data, string name = null)
        {
            int version = ReadResourceVersion(data);
            if (version < 0)
                throw new InvalidDataException("This is not a GTA V resource file (no RSC7 header). Is it really a .yft?");
            if (version != LegacyVersion && version != Gen9Version)
                throw new InvalidDataException($"Unexpected resource version {version} (a .yft should be {LegacyVersion}, or {Gen9Version} for Enhanced edition). Is this really a .yft?");

            GameNames.EnsureLoaded();
            lock (Gate)
            {
                bool old = RpfManager.IsGen9;
                try
                {
                    RpfManager.IsGen9 = version == Gen9Version;
                    var yft = new YftFile();
                    yft.Load(data);
                    if (yft.Fragment == null) throw new InvalidDataException("The file loaded but contains no fragment.");
                    if (name != null) yft.Name = name;
                    return yft;
                }
                finally
                {
                    RpfManager.IsGen9 = old;
                }
            }
        }

        public static byte[] Save(YftFile yft, int version)
        {
            lock (Gate)
            {
                bool old = RpfManager.IsGen9;
                try
                {
                    RpfManager.IsGen9 = version == Gen9Version;
                    return yft.Save();
                }
                finally
                {
                    RpfManager.IsGen9 = old;
                }
            }
        }
    }
}
