using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// One end of a rail transition made with the track editor.
    internal sealed class PortalEnd
    {
        public string Scene { get; set; }
        public float[] Pos { get; set; }      // on the line
        public float[] Out { get; set; }      // the way a train leaves the region through it
        public string Spawn { get; set; }     // the region's spawn point the game puts an arriving player at
    }

    internal sealed class PortalLink
    {
        public string Id { get; set; }
        public PortalEnd A { get; set; }
        public PortalEnd B { get; set; }      // null while it is being made (the player is on the way there)
    }

    internal sealed class PortalFile
    {
        public int Version { get; set; } = 1;
        public List<PortalLink> Links { get; set; } = new List<PortalLink>();
    }

    /// Rail transitions between regions made by hand (for lines that run into a tunnel with no transition):
    /// both ends are points on lines, kept in Mods\SteamRailway\Portals.json. A train passing an end in its
    /// "out" direction goes to the other end's region and comes out there.
    internal static class CustomPortals
    {
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
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Portals.json could not be read: {e.Message}"); }
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
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing Portals.json failed: {e.Message}"); }
        }

        /// The transition ends in this region whose other end is set: (end here, the other end).
        public static List<(PortalEnd here, PortalEnd there)> Ends(string scene)
        {
            var res = new List<(PortalEnd, PortalEnd)>();
            foreach (var l in File_().Links)
            {
                if (l.A == null || l.B == null) continue;
                if (l.A.Scene == scene) res.Add((l.A, l.B));
                if (l.B.Scene == scene) res.Add((l.B, l.A));
            }
            return res;
        }

        /// The spawn point of this region nearest to p (the game puts an arriving player there; we then board them).
        static string NearestSpawn(Vector3 p)
        {
            string best = ""; float bd = float.MaxValue;
            foreach (var s in Object.FindObjectsOfType<PlayerSpawn>())
            {
                if (s == null) continue;
                float d = (s.transform.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = s.gameObject.name; }
            }
            return best;
        }

        static PortalLink Pending()
        {
            foreach (var l in File_().Links) if (l.A != null && l.B == null) return l;
            return null;
        }

        /// P in the track editor, at a point on a line. `away`: along the line, away from the player.
        public static string Mark(string scene, Vector3 p, Vector3 away)
        {
            var f = File_();
            var pending = Pending();
            if (pending == null || pending.A.Scene == scene)
            {
                // the start: trains leave here going away from you (into the tunnel)
                if (pending == null) { pending = new PortalLink { Id = "portal" + (f.Links.Count + 1) }; f.Links.Add(pending); }
                pending.A = new PortalEnd { Scene = scene, Pos = CustomLines.F(p), Out = CustomLines.F(away), Spawn = NearestSpawn(p) };
                Save();
                return L.T("Geçişin başlangıcı kaydedildi. Hedef bölgeye git, trenin çıkacağı yerde raya bakıp (trenin gideceği yöne dönük) P'ye bas.",
                           "Start of the transition saved. Go to the other region, look at the line where the train should come out (facing the way it should run) and press P.");
            }
            // the other end: trains come out here running away from you; they leave back the other way
            pending.B = new PortalEnd { Scene = scene, Pos = CustomLines.F(p), Out = CustomLines.F(-away), Spawn = NearestSpawn(p) };
            Save();
            return L.T($"Geçiş tamamlandı: {pending.A.Scene} ↔ {scene}. Raylar yeniden kurulunca çalışır (F9 ya da bölgeye yeniden gir).",
                       $"Transition done: {pending.A.Scene} ↔ {scene}. It works once the rails are laid again (F9 or enter the region again).");
        }

        /// Shift+P: removes the transition with an end nearest to p in this region (or the one being made).
        public static string RemoveNear(string scene, Vector3 p)
        {
            var f = File_();
            PortalLink best = null; float bd = 15f * 15f;
            foreach (var l in f.Links)
                foreach (var e in new[] { l.A, l.B })
                {
                    if (e == null || e.Scene != scene) continue;
                    float d = (CustomLines.V(e.Pos) - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = l; }
                }
            if (best == null) return L.T("Yakında elle yapılmış bir geçiş yok.", "No hand-made transition near here.");
            f.Links.Remove(best);
            Save();
            return L.T("Geçiş silindi.", "Transition deleted.");
        }

        /// A transition object of the game's kind for the scene load (copied from one the region has, so the game
        /// saves and loads the regions its own way), aimed at the other region.
        public static LoadScene MakeLoadScene(string toScene, string spawn)
        {
            LoadScene src = null;
            foreach (var ls in Object.FindObjectsOfType<LoadScene>()) if (ls != null) { src = ls; break; }
            if (src == null) return null;
            var go = Object.Instantiate(src.gameObject);
            go.name = "SR_PortalLoad_" + toScene;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            var l = go.GetComponent<LoadScene>();
            l.m_SceneToLoad = toScene;
            l.m_ExitPointName = spawn ?? "";
            l.m_GUID = System.Guid.NewGuid().ToString();
            return l;
        }
    }
}
