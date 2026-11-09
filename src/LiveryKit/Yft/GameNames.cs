using System.IO;
using System.Reflection;
using CodeWalker.GameFiles;

namespace LiveryKit.Yft
{
    /// <summary>
    /// Teaches CodeWalker's hash index the shader names used on vehicles, so shaders show up as
    /// "vehicle_paint4" instead of a bare hash. (CodeWalker reads its gen9 shader table,
    /// ShadersGen9Conversion.xml, from next to CodeWalker.Core.dll.)
    /// </summary>
    public static class GameNames
    {
        static readonly object Gate = new object();
        static bool loaded;

        static readonly string[] VehicleShaders =
        {
            "vehicle_badges", "vehicle_basic", "vehicle_blurredrotor", "vehicle_blurredrotor_emissive",
            "vehicle_cloth", "vehicle_cloth2", "vehicle_cutout", "vehicle_dash_emissive", "vehicle_dash_emissive_opaque",
            "vehicle_decal", "vehicle_decal2", "vehicle_detail", "vehicle_detail2", "vehicle_emissive_alpha",
            "vehicle_emissive_opaque", "vehicle_generic", "vehicle_interior", "vehicle_interior2",
            "vehicle_licenseplate", "vehicle_lights", "vehicle_lightsemissive", "vehicle_mesh", "vehicle_mesh_enveff",
            "vehicle_mesh2_enveff", "vehicle_nosplash", "vehicle_nowater",
            "vehicle_paint1", "vehicle_paint1_enveff", "vehicle_paint2", "vehicle_paint2_enveff",
            "vehicle_paint3", "vehicle_paint3_enveff", "vehicle_paint3_lvr", "vehicle_paint4", "vehicle_paint4_emissive",
            "vehicle_paint4_enveff", "vehicle_paint5_enveff", "vehicle_paint6", "vehicle_paint6_enveff",
            "vehicle_paint7", "vehicle_paint7_enveff", "vehicle_paint8", "vehicle_paint9",
            "vehicle_shuts", "vehicle_tire", "vehicle_tire_emissive", "vehicle_track", "vehicle_track2",
            "vehicle_track2_emissive", "vehicle_track_ammo", "vehicle_track_cutout", "vehicle_track_emissive",
            "vehicle_vehglass", "vehicle_vehglass_inner",
        };

        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (loaded) return;
                loaded = true;

                foreach (var s in VehicleShaders) AddShaderName(s);

                var strings = ReadResourceText("LiveryKit.strings.txt");
                if (strings != null)
                {
                    foreach (var raw in strings.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("//")) continue;
                        AddShaderName(line);
                    }
                }
            }
        }

        static void AddShaderName(string name)
        {
            JenkIndex.Ensure(name);
            JenkIndex.Ensure(name + ".sps");
        }

        static string ReadResourceText(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }
    }
}
