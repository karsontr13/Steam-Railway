using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamRailway
{
    /// Where the locomotive stands at the start of every new game (Mods/SteamRailway/Start.json): region, a point on its
    /// railway and the chimney direction; without the file it goes on the first railway the player comes to.
    internal sealed class StartPoint
    {
        const int Vector3ComponentCount = 3;
        public string Scene { get; set; }
        [JsonPropertyName("Pos")] public float[] Position { get; set; }        // a point on (or beside) the line, world space
        [JsonPropertyName("Dir")] public float[] Direction { get; set; }        // the way the chimney faces (horizontal, world space)

        public static string FilePath => Path.Combine(RouteStore.FolderPath, "Start.json");

        public static StartPoint Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var startPoint = JsonSerializer.Deserialize<StartPoint>(File.ReadAllText(FilePath));
                if (startPoint == null || string.IsNullOrEmpty(startPoint.Scene) || startPoint.Position == null || startPoint.Position.Length < Vector3ComponentCount) return null;
                return startPoint;
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Warning($"Start.json could not be read: {exception.Message}");
                return null;
            }
        }
    }
}
