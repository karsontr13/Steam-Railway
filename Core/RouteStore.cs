using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MelonLoader.Utils;
using UnityEngine;

namespace SteamRailway
{
    /// One railway line inside one scene: an ordered list of world-space control points.
    internal sealed class RouteData
    {
        public string Id { get; set; } = "route";
        public List<float[]> Points { get; set; } = new List<float[]>();

        public Vector3 GetPoint(int i) => new Vector3(Points[i][0], Points[i][1], Points[i][2]);
        public void AddPoint(Vector3 p) => Points.Add(new[] { p.x, p.y, p.z });
    }

    internal sealed class RouteFile
    {
        /// Scene name (e.g. "LakeRegion") -> routes in that scene.
        public Dictionary<string, List<RouteData>> Scenes { get; set; } = new Dictionary<string, List<RouteData>>();
    }

    /// Routes live in Mods\SteamRailway\routes.json so they can be authored in game with the route editor
    /// and shipped with the mod.
    internal static class RouteStore
    {
        static RouteFile s_File = new RouteFile();
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        public static string FolderPath => Path.Combine(MelonEnvironment.ModsDirectory, "SteamRailway");
        public static string FilePath => Path.Combine(FolderPath, "routes.json");

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    s_File = JsonSerializer.Deserialize<RouteFile>(File.ReadAllText(FilePath), s_Json) ?? new RouteFile();
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"routes.json could not be read: {e.Message}");
                s_File = new RouteFile();
            }
        }

        public static void Save()
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s_File, s_Json));
            SteamRailwayMod.Log.Msg($"Routes saved to {FilePath}");
        }

        public static List<RouteData> GetRoutes(string scene)
        {
            return s_File.Scenes.TryGetValue(scene, out var list) ? list : null;
        }

        public static List<RouteData> GetOrCreateRoutes(string scene)
        {
            if (!s_File.Scenes.TryGetValue(scene, out var list))
            {
                list = new List<RouteData>();
                s_File.Scenes[scene] = list;
            }
            return list;
        }
    }
}
