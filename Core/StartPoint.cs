using System.IO;
using System.Text.Json;

namespace SteamRailway
{
    /// Where the game's one locomotive stands at the start of every new game (Mods/SteamRailway/Start.json):
    /// the region, a point on its railway and the way the chimney faces. Without the file the locomotive is placed
    /// on the first railway the player comes to.
    internal sealed class StartPoint
    {
        public string Scene { get; set; }
        public float[] Pos { get; set; }        // a point on (or beside) the line, world space
        public float[] Dir { get; set; }        // the way the chimney faces (horizontal, world space)

        public static string FilePath => Path.Combine(RouteStore.FolderPath, "Start.json");

        public static StartPoint Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var startPoint = JsonSerializer.Deserialize<StartPoint>(File.ReadAllText(FilePath));
                if (startPoint == null || string.IsNullOrEmpty(startPoint.Scene) || startPoint.Pos == null || startPoint.Pos.Length < 3) return null;
                return startPoint;
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Warning($"Start.json could not be read: {e.Message}");
                return null;
            }
        }
    }
}
