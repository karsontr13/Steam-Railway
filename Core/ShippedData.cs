using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SteamRailway
{
    /// The shipped lines, terrain, obstacles and edits are written to Mods/SteamRailway on start. A file a new version
    /// changed replaces the old one (a hand-edited one goes to Backup first); an unchanged one keeps its hand edits.
    internal static class ShippedData
    {
        const string ResourcePrefix = "Data/";
        const string ManifestFileName = "ShippedData.txt";
        const string BackupFolderName = "Backup";
        const char ManifestSeparator = '\t';
        const string BackupTimeFormat = "yyyyMMdd-HHmmss";

        public static void Extract()
        {
            try
            {
                var assembly = typeof(ShippedData).Assembly;
                string manifestPath = Path.Combine(RouteStore.FolderPath, ManifestFileName);
                var recorded = ReadManifest(manifestPath);
                var written = new Dictionary<string, string>();
                string backupFolder = Path.Combine(RouteStore.FolderPath, BackupFolderName, System.DateTime.Now.ToString(BackupTimeFormat));
                int newCount = 0, updatedCount = 0, backedUpCount = 0;
                foreach (var name in assembly.GetManifestResourceNames())
                {
                    string resourcePath = name.Replace('\\', '/');
                    if (!resourcePath.StartsWith(ResourcePrefix)) continue;
                    string relativePath = resourcePath.Substring(ResourcePrefix.Length);
                    byte[] shipped;
                    using (var stream = assembly.GetManifestResourceStream(name))
                    using (var memory = new MemoryStream()) { stream.CopyTo(memory); shipped = memory.ToArray(); }
                    string shippedHash = HashOf(shipped);
                    written[relativePath] = shippedHash;
                    string target = Path.Combine(RouteStore.FolderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(target))
                    {
                        Write(target, shipped);
                        newCount++;
                        continue;
                    }
                    string diskHash = HashOf(File.ReadAllBytes(target));
                    if (diskHash == shippedHash) continue;
                    recorded.TryGetValue(relativePath, out string recordedHash);
                    // this version ships the same file as last time: the difference on disk is a hand edit, kept
                    if (recordedHash == shippedHash) continue;
                    // a new version of the file: a hand edit of the old one is kept aside, then the new one is written
                    if (diskHash != recordedHash)
                    {
                        Write(Path.Combine(backupFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)), File.ReadAllBytes(target));
                        backedUpCount++;
                    }
                    Write(target, shipped);
                    updatedCount++;
                }
                WriteManifest(manifestPath, written);
                if (newCount > 0 || updatedCount > 0)
                    SteamRailwayMod.Log.Msg($"Mod data in {RouteStore.FolderPath}: {newCount} file(s) written, {updatedCount} updated to this version.");
                if (backedUpCount > 0)
                    SteamRailwayMod.Log.Msg($"{backedUpCount} hand-edited data file(s) were replaced by this version; the old ones are kept in {backupFolder}.");
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"The mod's data could not be written: {exception.Message}"); }
        }

        static void Write(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, content);
        }

        static string HashOf(byte[] content)
        {
            using (var hasher = SHA256.Create()) return System.Convert.ToHexString(hasher.ComputeHash(content));
        }

        static Dictionary<string, string> ReadManifest(string path)
        {
            var recorded = new Dictionary<string, string>();
            if (!File.Exists(path)) return recorded;
            foreach (var line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf(ManifestSeparator);
                if (separator > 0) recorded[line.Substring(0, separator)] = line.Substring(separator + 1).Trim();
            }
            return recorded;
        }

        static void WriteManifest(string path, Dictionary<string, string> written)
        {
            var lines = new List<string>(written.Count);
            foreach (var pair in written) lines.Add(pair.Key + ManifestSeparator + pair.Value);
            lines.Sort(System.StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines);
        }
    }
}
