using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// One end of a rail transition made with the track editor.
    internal sealed class PortalEnd
    {
        public string Scene { get; set; }
        [JsonPropertyName("Pos")] public float[] Position { get; set; }      // on the line
        public float[] Out { get; set; }      // the way a train leaves the region through it
        public string Spawn { get; set; }     // the region's spawn point the game puts an arriving player at
    }

    internal sealed class PortalLink
    {
        [JsonPropertyName("Id")] public string Identifier { get; set; }
        [JsonPropertyName("A")] public PortalEnd FirstEnd { get; set; }
        [JsonPropertyName("B")] public PortalEnd SecondEnd { get; set; }      // null while it is being made (the player is on the way there)
    }

    internal sealed class PortalFile
    {
        public int Version { get; set; } = 1;
        public List<PortalLink> Links { get; set; } = new List<PortalLink>();
    }

    /// Hand-made rail transitions between regions, kept in Mods\SteamRailway\Portals.json (two ends on lines).
    /// A train passing an end in its "out" direction comes out at the other end's region.
    internal static class CustomPortals
    {
        const float NearestPortalRadiusInMeters = 15f;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        static PortalFile s_File;

        static string FilePath => Path.Combine(RouteStore.FolderPath, "Portals.json");

        static PortalFile File_()
        {
            if (s_File != null) return s_File;
            s_File = new PortalFile();
            if (File.Exists(FilePath))
            {
                try { s_File = JsonSerializer.Deserialize<PortalFile>(File.ReadAllText(FilePath), s_Json) ?? new PortalFile(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Portals.json could not be read: {exception.Message}"); }
            }
            if (s_File.Links == null) s_File.Links = new List<PortalLink>();
            return s_File;
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(RouteStore.FolderPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(File_(), s_Json));
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Writing Portals.json failed: {exception.Message}"); }
        }

        /// The transition ends in this region whose other end is set: (end here, the other end).
        public static List<(PortalEnd here, PortalEnd there)> Ends(string scene)
        {
            var results = new List<(PortalEnd, PortalEnd)>();
            foreach (var portalLink in File_().Links)
            {
                if (portalLink.FirstEnd == null || portalLink.SecondEnd == null) continue;
                if (portalLink.FirstEnd.Scene == scene) results.Add((portalLink.FirstEnd, portalLink.SecondEnd));
                if (portalLink.SecondEnd.Scene == scene) results.Add((portalLink.SecondEnd, portalLink.FirstEnd));
            }
            return results;
        }

        /// The spawn point of this region nearest to p (the game puts an arriving player there; we then board them).
        static string NearestSpawn(Vector3 point)
        {
            string best = ""; float bestDistance = float.MaxValue;
            foreach (var spawn in Object.FindObjectsOfType<PlayerSpawn>())
            {
                if (spawn == null) continue;
                float distance = (spawn.transform.position - point).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = spawn.gameObject.name; }
            }
            return best;
        }

        static PortalLink Pending()
        {
            foreach (var portalLink in File_().Links) if (portalLink.FirstEnd != null && portalLink.SecondEnd == null) return portalLink;
            return null;
        }

        /// P in the track editor, at a point on a line. `away`: along the line, away from the player.
        public static string Mark(string scene, Vector3 point, Vector3 away)
        {
            var portalFile = File_();
            var pending = Pending();
            if (pending == null || pending.FirstEnd.Scene == scene)
            {
                // the start: trains leave here going away from you (into the tunnel)
                if (pending == null) { pending = new PortalLink { Identifier = "portal" + (portalFile.Links.Count + 1) }; portalFile.Links.Add(pending); }
                pending.FirstEnd = new PortalEnd { Scene = scene, Position = CustomLines.ToFloatArray(point), Out = CustomLines.ToFloatArray(away), Spawn = NearestSpawn(point) };
                Save();
                return Localizer.Translate("Geçişin başlangıcı kaydedildi. Hedef bölgeye git, trenin çıkacağı yerde raya bakıp (trenin gideceği yöne dönük) P'ye bas.",
                           "Start of the transition saved. Go to the other region, look at the line where the train should come out (facing the way it should run) and press P.");
            }
            // the other end: trains come out here running away from you; they leave back the other way
            pending.SecondEnd = new PortalEnd { Scene = scene, Position = CustomLines.ToFloatArray(point), Out = CustomLines.ToFloatArray(-away), Spawn = NearestSpawn(point) };
            Save();
            return Localizer.Translate($"Geçiş tamamlandı: {pending.FirstEnd.Scene} ↔ {scene}. Raylar yeniden kurulunca çalışır (F9 ya da bölgeye yeniden gir).",
                       $"Transition done: {pending.FirstEnd.Scene} ↔ {scene}. It works once the rails are laid again (F9 or enter the region again).");
        }

        /// Shift+P: removes the transition with an end nearest to p in this region (or the one being made).
        public static string RemoveNear(string scene, Vector3 point)
        {
            var portalFile = File_();
            PortalLink best = null; float bestDistance = NearestPortalRadiusInMeters * NearestPortalRadiusInMeters;
            foreach (var portalLink in portalFile.Links)
                foreach (var portalEnd in new[] { portalLink.FirstEnd, portalLink.SecondEnd })
                {
                    if (portalEnd == null || portalEnd.Scene != scene) continue;
                    float distance = (CustomLines.ToVector3(portalEnd.Position) - point).sqrMagnitude;
                    if (distance < bestDistance) { bestDistance = distance; best = portalLink; }
                }
            if (best == null) return Localizer.Translate("Yakında elle yapılmış bir geçiş yok.", "No hand-made transition near here.");
            portalFile.Links.Remove(best);
            Save();
            return Localizer.Translate("Geçiş silindi.", "Transition deleted.");
        }

        const string RegionSceneSuffix = "Region";

        /// The game's own transition to that region, used as it is where there is one (the game then saves and loads as when
        /// it is walked through); else a copy of another region transition aimed there. Never a door: its load is an interior's.
        public static LoadScene TransitionTo(string toScene, string spawn)
        {
            LoadScene sourceLoadScene = null;
            foreach (var loadScene in Object.FindObjectsOfType<LoadScene>())
            {
                if (loadScene == null || !loadScene.m_TransitionOnContact || string.IsNullOrEmpty(loadScene.m_SceneToLoad)) continue;
                if (loadScene.m_SceneToLoad == toScene) return loadScene;
                if (sourceLoadScene == null && loadScene.m_SceneToLoad.EndsWith(RegionSceneSuffix, System.StringComparison.Ordinal)) sourceLoadScene = loadScene;
            }
            if (sourceLoadScene == null) return null;
            var targetObject = Object.Instantiate(sourceLoadScene.gameObject);
            targetObject.name = "SR_PortalLoad_" + toScene;
            foreach (var collider in targetObject.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            var loadSceneComponent = targetObject.GetComponent<LoadScene>();
            loadSceneComponent.m_SceneToLoad = toScene;
            loadSceneComponent.m_ExitPointName = spawn ?? "";
            loadSceneComponent.m_GUID = System.Guid.NewGuid().ToString();
            return loadSceneComponent;
        }
    }
}
