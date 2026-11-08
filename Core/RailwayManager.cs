using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using Il2Cpp;
using Il2CppTLD.SteamTrain;
using MelonLoader;
using ModData;
using UnityEngine;

namespace SteamRailway
{
    internal sealed class TrainState
    {
        public string Guid { get; set; }
        public string RouteId { get; set; }
        public float SplinePos { get; set; }
        public string TrainData { get; set; }   // SteamTrain.SerializeTrainCarData()
        public string FireData { get; set; }   // firebox fire as the game serialises it
        public SimState Sim { get; set; }       // boiler, tender and controls
        public float Velocity { get; set; }
        public List<CabItemState> CabItems { get; set; }   // items lying in the cab (see CabItems)
    }

    /// A train on its way into another region (it is spawned there when the region loads).
    internal sealed class PendingArrival
    {
        public string FromScene { get; set; }
        public string ToScene { get; set; }
        public TrainState Train { get; set; }
        public float Speed { get; set; }
        public bool Forward { get; set; }          // running chimney first
        public string FallbackRouteId { get; set; } // where it waits in FromScene if ToScene has no railway
        public float FallbackPos { get; set; }
    }

    internal sealed class RailwaySave
    {
        /// Scene name -> trains in that scene.
        public Dictionary<string, List<TrainState>> Scenes { get; set; } = new Dictionary<string, List<TrainState>>();
        /// Regions already visited (a free locomotive is placed only on the first visit).
        public List<string> Visited { get; set; } = new List<string>();
        /// Track id -> line runs the other way round (set when a train arrives at the far end of a line).
        public Dictionary<string, bool> Reversed { get; set; } = new Dictionary<string, bool>();
        public PendingArrival Arrival { get; set; }
        /// Switch id -> set for the branch.
        public Dictionary<string, bool> Switches { get; set; } = new Dictionary<string, bool>();
        /// The game's one locomotive has been placed (it is never placed again in this game).
        public bool TrainCreated { get; set; }
        /// The save slot this data belongs to (data of another game is never used).
        public string Slot { get; set; }
    }

    /// One railway line before it is built (kept so a line can be rebuilt reversed).
    internal sealed class TrackDef
    {
        public string Id;
        public List<Vector3> Points;
        public List<KeyValuePair<Vector3, Vector3>> Keep;   // stretches where the original piece stays (bridges)
        public RailLine Original;                           // null for hand-made routes
        public readonly List<RailLine> Originals = new List<RailLine>();   // the region's lines in it (several when joined)
        public bool HasCustom;                              // drawn with the track editor (or joined with such a line)
    }

    /// A region transition the line runs through (the game's TransitionContact plane).
    internal sealed class Portal
    {
        public LoadScene Ls;
        public BuiltTrack Track;
        public float S;        // spline metres where the line first touches the trigger, seen from inside the region
        public bool AtEnd;     // near the end of the line (else near its start)
        public float Safe;   // spline metres where a train stands clear of the trigger
        public string To;
        public bool Custom;    // made with the track editor (no game trigger: the scene is loaded through a copy)
        public string Spawn;   // Custom: the spawn point in the other region
    }

    internal sealed class LiveTrain
    {
        public SteamTrain Train;
        public string Guid;
        public string RouteId;
        public SteamSim Sim;
        public float PrevPos = float.NaN;   // spline metres last frame (junctions)
        public bool Boarding;                // BoardAfterArrival is running
        public float AwaySince = -1f;        // realtime the player was first seen outside the cab while "inside"
        public int Reboards;
    }

    /// Builds the tracks and trains of the active scene and keeps their state in the save game (via ModData).
    internal static class RailwayManager
    {
        const float DefaultSpawnMeters = 15f;
        const float CaptureInterval = 2f;

        static readonly ModDataManager s_ModData = new ModDataManager("SteamRailway", false);
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions();
        static RailwaySave s_Save = new RailwaySave();

        static string s_Scene;
        static readonly List<BuiltTrack> s_Tracks = new List<BuiltTrack>();
        static readonly List<LiveTrain> s_Trains = new List<LiveTrain>();
        static readonly HashSet<string> s_OurGuids = new HashSet<string>();
        static readonly List<TrackDef> s_Defs = new List<TrackDef>();
        static readonly List<Portal> s_Portals = new List<Portal>();
        static float s_NextCapture;
        static bool s_NeedLoad = true;      // read the save slot again after the main menu
        static bool s_Transiting;

        public static string CurrentScene => s_Scene;
        public static IReadOnlyList<BuiltTrack> Tracks => s_Tracks;

        static readonly Dictionary<string, SteamSim> s_Sims = new Dictionary<string, SteamSim>();
        static float s_NextHover;

        public static bool IsOurTrain(TrainCar car) => car != null && car.name.StartsWith(TrainBuilder.NamePrefix);
        public static bool IsOurGuid(string guid) => guid != null && s_OurGuids.Contains(guid);

        public static SteamSim GetSim(TrainCar car)
        {
            if (!IsOurTrain(car)) return null;
            return s_Sims.TryGetValue(car.name.Substring(TrainBuilder.NamePrefix.Length), out var sim) ? sim : null;
        }

        // ---------------------------------------------------------------- scene lifecycle

        public static void OnSceneInitialized(string sceneName)
        {
            // Additive layers (_SANDBOX, _WILDLIFE, _DLC01 ...) also raise this; only react to the main region scene.
            if (string.IsNullOrEmpty(GameManager.m_ActiveScene) || sceneName != GameManager.m_ActiveScene) return;
            if (sceneName.StartsWith("MainMenu") || sceneName == "Empty" || sceneName == "Boot") { s_NeedLoad = true; return; }

            s_Transiting = false;
            s_Departing = null;
            TerrainEdits.SceneLoaded();
            s_Portals.Clear();
            Switches.Clear();
            InstancedHider.Clear();
            s_Tracks.Clear();
            s_Trains.Clear();
            s_Sims.Clear();
            s_Scene = sceneName;
            MelonCoroutines.Start(BuildSceneDelayed(sceneName));
        }

        public static void OnSceneUnloaded(string sceneName)
        {
            if (sceneName != s_Scene) return;
            s_Tracks.Clear();
            s_Trains.Clear();
            s_Sims.Clear();
            s_Scene = null;
        }

        static IEnumerator BuildSceneDelayed(string sceneName)
        {
            // Let terrain, LoadScene objects and the player settle.
            yield return null;
            yield return null;
            if (sceneName != s_Scene) yield break;
            // The save slot is read once per loaded game; between regions the state in memory is the newer one
            // (a train that just left a region must not come back from a save written before it left).
            if (s_NeedLoad) { LoadSave(); s_NeedLoad = false; }
            BuildScene();
        }

        public static void BuildScene()
        {
            // the mod's object edits of this region first: the railway is laid in the changed region
            try { ObjectEdits.ApplyScene(s_Scene); }
            catch (System.Exception e) { SteamRailwayMod.Log.Error($"Applying the object edits failed: {e}"); }
            // tunnel mouths placed with the track editor
            try { Tunnels.ApplyScene(s_Scene); }
            catch (System.Exception e) { SteamRailwayMod.Log.Error($"Placing the tunnels failed: {e}"); }
            // the hand-made ground changes (once per loaded region)
            try { TerrainEdits.ApplyScene(s_Scene); }
            catch (System.Exception e) { SteamRailwayMod.Log.Error($"Applying the terrain edits failed: {e}"); }
            var mat = FindTrackMaterial();

            // The region's own railway comes first; hand-made routes (routes.json) only where a region has none.
            s_Defs.Clear();
            var lines = new List<RailLine>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // the lines saved for the region (built once from its rail pieces, then kept as saved)
                var saved = OriginalRails.LoadLines(s_Scene);
                if (saved != null) lines = saved;
                else
                {
                    lines = OriginalRails.Find();
                    if (lines.Count > 0) OriginalRails.SaveLines(s_Scene, lines);
                }
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Error($"Reading the region's rails failed: {e}"); }
            if (lines.Count > 0)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    var td = new TrackDef { Id = $"{s_Scene}_rail{i + 1}", Points = new List<Vector3>(lines[i].Points), Keep = lines[i].Keep, Original = lines[i] };
                    td.Originals.Add(lines[i]);
                    s_Defs.Add(td);
                }
                var routes0 = RouteStore.GetRoutes(s_Scene);
                if (routes0 != null && routes0.Count > 0)
                    SteamRailwayMod.Log.Msg($"{s_Scene} has its own railway; the hand-made routes in routes.json are not used here.");
            }
            else
            {
                var routes = RouteStore.GetRoutes(s_Scene);
                if (routes != null)
                    foreach (var route in routes)
                    {
                        if (route.Points.Count < 2) continue;
                        var pts = new List<Vector3>();
                        for (int i = 0; i < route.Points.Count; i++) pts.Add(route.GetPoint(i));
                        s_Defs.Add(new TrackDef { Id = route.Id, Points = pts, Keep = null });
                    }
            }
            // the lines drawn with the track editor; one meeting another line's end becomes one line with it
            try
            {
                CustomLines.AddDefs(s_Scene, s_Defs);
                CustomLines.Merge(s_Defs);
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Error($"Adding the drawn lines failed: {e}"); }
            foreach (var d in s_Defs)
            {
                // a fixed direction for each line (the pieces are found in no particular order)
                var a = d.Points[0]; var b = d.Points[d.Points.Count - 1];
                if (a.x + a.z > b.x + b.z) d.Points.Reverse();
            }
            if (Switches.Automatic)
            {
                // branches start exactly in the main line's rails
                try { Switches.SnapBranches(s_Defs); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snapping the branches failed: {e.Message}"); }
            }
            else
            {
                // no junctions for now: a line branching off another one is not built (its original pieces stay)
                try
                {
                    foreach (var id in Switches.Branches(s_Defs))
                    {
                        s_Defs.RemoveAll(x => x.Id == id);
                        SteamRailwayMod.Debug($"{id} branches off another line; left as it is (no switch).");
                    }
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Finding the branch lines failed: {e.Message}"); }
            }
            // switches: only where a drawn line was joined to the middle of a line
            var switchPoints = CustomLines.SwitchPoints(s_Scene);
            // the points lie level: a branch leaves at the main line's height
            if (switchPoints.Count > 0)
            {
                try { Switches.LevelBranches(s_Defs, switchPoints); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Levelling the branches failed: {e.Message}"); }
            }
            foreach (var d in s_Defs)
            {
                var built = BuildTrack(d);
                if (built != null) s_Tracks.Add(built);
            }
            // junctions first: their turnouts decide what is laid under the points
            if (Switches.Automatic || switchPoints.Count > 0)
            {
                try
                {
                    Switches.Find(s_Tracks, s_Save.Switches, Switches.Automatic ? null :
                        (System.Func<Vector3, bool>)(p => switchPoints.Exists(q => (q - p).sqrMagnitude < 1.5f * 1.5f)));
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Error($"Finding the junctions failed: {e}"); }
            }
            OriginalRails.BeginClear();
            foreach (var t in s_Tracks)
            {
                try { TrackBuilder.LayRails(t); }
                catch (System.Exception e) { SteamRailwayMod.Log.Error($"Laying the rails of {t.Id} failed: {e}"); }
                var def = s_Defs.Find(x => x.Id == t.Id);
                if (def != null && def.Originals.Count > 0) OriginalRails.ClearLine(t);
            }
            OriginalRails.EndClear();
            Switches.RailsLaid();
            InstancedHider.Apply();
            SteamRailwayMod.Debug($"{s_Scene}: lines and rails ready in {sw.ElapsedMilliseconds} ms.");

            var arrival = s_Save.Arrival != null && s_Save.Arrival.ToScene == s_Scene ? s_Save.Arrival : null;
            if (s_Save.Arrival != null && arrival == null)
            {
                // the player is not where the train was heading (transition cancelled?): park it before the transition
                ArrivalFailed(s_Save.Arrival);
            }
            if (s_Tracks.Count == 0)
            {
                if (arrival != null) ArrivalFailed(arrival);
                return;
            }
            // snow embankments under the drawn lines, then the snow cleared over every line
            foreach (var fp in CustomLines.FillPaths(s_Scene))
            {
                try { TerrainCarve.Fill(fp); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snow embankment failed: {e.Message}"); }
            }
            // under the points the branch was raised to the main line's height: snow under it there too
            foreach (var tsw in Switches.All)
            {
                try
                {
                    var pts = new List<Vector3>();
                    for (float u = 0f; u <= 32f; u += 2f)
                    {
                        float sb = tsw.BJoin + tsw.InB * u;
                        if (sb < 0f || sb > tsw.B.Path.Length) break;
                        tsw.B.Path.Sample(sb, out var q, out _);
                        pts.Add(q);
                    }
                    if (pts.Count >= 2) TerrainCarve.Fill(CurvePath.CatmullRom(pts));
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snow under the points failed: {e.Message}"); }
            }
            foreach (var t in s_Tracks)
            {
                try { TerrainCarve.Lower(t.Path); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Lowering the snow along {t.Id} failed: {e.Message}"); }
            }
            Switches.GroundStands();
            FindPortals();

            // an arriving train decides which way round its line runs; trains already here are turned with it
            Portal entry = null;
            if (arrival != null) entry = PrepareArrival(arrival);

            if (!s_Save.Visited.Contains(s_Scene)) s_Save.Visited.Add(s_Scene);
            if (s_Save.Scenes.TryGetValue(s_Scene, out var states) && states.Count > 0)
            {
                foreach (var st in states) { Remap(st); Spawn(st, mat); }
            }
            else if (!s_Save.TrainCreated && arrival == null)
            {
                // One locomotive per game. With a start point (Start.json) it stands there, full of water, and
                // is placed only when the player comes to that region; otherwise it is a cold one on the first railway.
                var start = StartPoint.Load();
                if (start == null || start.Scene == s_Scene)
                {
                    s_Save.TrainCreated = true;
                    var st = new TrainState { Guid = System.Guid.NewGuid().ToString(), RouteId = s_Tracks[0].Id, SplinePos = DefaultSpawnMeters };
                    if (start != null) PlaceAtStart(st, start);
                    Spawn(st, mat);
                }
            }
            s_Remap.Clear();
            if (arrival != null) Arrive(arrival, entry, mat);
            if (s_WalkToScene != null && s_WalkToScene == s_Scene)
            {
                MelonCoroutines.Start(PlaceWalker(s_WalkToPos, s_WalkToDir));
                s_WalkToScene = null;
            }
            SteamRailwayMod.Log.Msg($"{s_Scene}: {s_Tracks.Count} track(s), {s_Trains.Count} train(s), {s_Portals.Count} rail transition(s).");
        }

        /// The new game's locomotive: on the line nearest to the start point, chimney facing the start direction,
        /// boiler and tender full, everything in order.
        static void PlaceAtStart(TrainState st, StartPoint start)
        {
            var p = new Vector3(start.Pos[0], start.Pos[1], start.Pos[2]);
            BuiltTrack best = null; float bd = float.MaxValue, bs = 0f;
            foreach (var t in s_Tracks)
            {
                float sp = t.Path.NearestDistance(p, out float d);
                if (d < bd) { bd = d; best = t; bs = sp; }
            }
            if (best == null) return;
            if (bd > 30f) SteamRailwayMod.Log.Warning($"The start point is {bd:F0} m from the nearest line; the train stands on the line anyway.");
            st.RouteId = best.Id;
            st.SplinePos = Mathf.Clamp(bs * best.Spline.Length / Mathf.Max(1f, best.Path.Length), 2f, best.Spline.Length - 2f);
            best.Path.Sample(bs, out _, out var tan);
            var dir = start.Dir != null && start.Dir.Length >= 3 ? new Vector3(start.Dir[0], 0f, start.Dir[2]) : Vector3.zero;
            int facing = dir.sqrMagnitude < 0.01f || Vector3.Dot(new Vector3(tan.x, 0f, tan.z), dir) >= 0f ? 1 : -1;
            st.Sim = new SimState
            {
                Facing = facing,
                BoilerWater = SteamSim.BoilerCapacity,
                TenderWater = SteamSim.TenderCapacity,
                BoilerCondition = 100f,
                ValveCondition = 100f,
            };
            SteamRailwayMod.Log.Msg($"New game: the locomotive stands on {best.Id} at {st.SplinePos:F0} m, chimney {(facing > 0 ? "towards the end" : "towards the start")} of the line, boiler and tender full.");
        }

        static BuiltTrack BuildTrack(TrackDef d)
        {
            var pts = new List<Vector3>(d.Points);
            if (s_Save.Reversed.TryGetValue(d.Id, out bool rev) && rev) pts.Reverse();
            var built = TrackBuilder.Build(d.Id, pts, d.Keep);
            if (built != null) foreach (var o in d.Originals) OriginalRails.HideReplaced(o);
            return built;
        }

        // ---------------------------------------------------------------- region transitions

        /// The game's region transitions (TransitionContact planes) the lines run through near their ends.
        /// Hand-made transitions the player can walk through too (like the game's own).
        sealed class WalkPortal
        {
            public Vector3 Pos, Out;
            public PortalEnd There;
            public float PrevAlong = float.NaN;
        }

        static readonly List<WalkPortal> s_Walk = new List<WalkPortal>();
        static string s_WalkToScene;
        static Vector3 s_WalkToPos, s_WalkToDir;

        static void CheckWalkPortals()
        {
            if (s_Transiting || s_Walk.Count == 0 || SteamTrain.IsInsideSteamTrain) return;
            var player = GameManager.GetPlayerObject();
            if (player == null) return;
            var pp = player.transform.position;
            foreach (var w in s_Walk)
            {
                var d = pp - w.Pos;
                float along = Vector3.Dot(d, w.Out);
                float lat = Mathf.Abs(Vector3.Dot(d, Vector3.Cross(Vector3.up, w.Out)));
                if (lat > 4f || Mathf.Abs(d.y) > 5f || Mathf.Abs(along) > 10f) { w.PrevAlong = float.NaN; continue; }
                bool crossed = !float.IsNaN(w.PrevAlong) && w.PrevAlong < 0f && along >= 0f;
                w.PrevAlong = along;
                if (!crossed) continue;
                WalkTransit(w);
                return;
            }
        }

        static void WalkTransit(WalkPortal w)
        {
            s_Transiting = true;
            s_WalkToScene = w.There.Scene;
            s_WalkToPos = CustomLines.V(w.There.Pos);
            var od = CustomLines.V(w.There.Out); od.y = 0f;
            s_WalkToDir = -od.normalized;                     // into the other region, away from its portal
            SteamRailwayMod.Log.Msg($"On foot through the rail transition to {w.There.Scene}.");
            try
            {
                var ls = CustomPortals.MakeLoadScene(w.There.Scene, w.There.Spawn);
                if (ls == null) throw new System.Exception("no transition object to copy in this region");
                ls.Activate(true);
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Region transition failed: {e.Message}");
                s_Transiting = false;
                s_WalkToScene = null;
            }
        }

        /// The game puts the player at a spawn point of the region; they are moved to the transition's end.
        static IEnumerator PlaceWalker(Vector3 pos, Vector3 dir)
        {
            float start = Time.realtimeSinceStartup;
            var p0 = GameManager.GetPlayerObject();
            Vector3 last = p0 != null ? p0.transform.position : Vector3.zero;
            float still = 0f;
            while (Time.realtimeSinceStartup - start < 10f)
            {
                yield return null;
                var player = GameManager.GetPlayerObject();
                if (player == null) continue;
                var p = player.transform.position;
                bool jumped = (p - last).sqrMagnitude > 4f;
                still = (p - last).sqrMagnitude < 0.0025f ? still + Time.unscaledDeltaTime : 0f;
                last = p;
                bool fadingIn = false;
                try { fadingIn = !CameraFade.IsFadedOut || CameraFade.IsFadingIn; } catch { }
                if (jumped || (fadingIn && Time.realtimeSinceStartup - start > 0.2f) || still > 1.5f) break;
            }
            // on the ground at the line's height (not on top of a rock or a tunnel roof above it)
            var flat = pos + dir * 6f;
            float groundY = float.MinValue;
            foreach (var h in Physics.RaycastAll(new Vector3(flat.x, pos.y + 1.5f, flat.z), Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.isTrigger || h.point.y <= groundY) continue;
                bool ours = false;
                for (var x = h.collider.transform; x != null; x = x.parent) if (x.name.StartsWith("CHARACTER_FPSPlayer")) { ours = true; break; }
                if (!ours) groundY = h.point.y;
            }
            if (groundY == float.MinValue) groundY = pos.y - RailPieces.RailTop;
            var target = new Vector3(flat.x, groundY + 0.05f, flat.z);
            var rot = Quaternion.LookRotation(dir.sqrMagnitude > 0.01f ? dir : Vector3.forward, Vector3.up);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { GameManager.GetPlayerManagerComponent().TeleportPlayer(target, rot); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Placing the player failed: {e.Message}"); yield break; }
                for (int k = 0; k < 30; k++) yield return null;
                var player = GameManager.GetPlayerObject();
                if (player == null || (player.transform.position - target).magnitude < 5f) yield break;
            }
        }

        static void FindPortals()
        {
            s_Portals.Clear();
            s_Walk.Clear();
            foreach (var ls in Object.FindObjectsOfType<LoadScene>())
            {
                if (ls == null || !ls.m_TransitionOnContact || string.IsNullOrEmpty(ls.m_SceneToLoad) || ls.m_SceneToLoad == s_Scene) continue;
                if (ls.GetComponent<Collider>() == null) continue;
                var col = ls.GetComponent<Collider>();
                foreach (var t in s_Tracks)
                {
                    float sp = t.Path.NearestDistance(ls.transform.position, out float hd);
                    float len = t.Path.Length;
                    // where the line really runs through the trigger (a plane's centre may lie metres to the side)
                    bool touches = Touches(t, col, out float in0, out float in1);
                    if (!touches && hd > 18f) continue;
                    bool atEnd = touches ? (in0 + in1) * 0.5f > len * 0.5f : sp > len * 0.5f;
                    float edge = touches ? (atEnd ? in0 : in1) : sp;
                    if (atEnd ? edge < len - 300f : edge > 300f) continue;
                    // path metres -> spline metres (the two curves are nearly the same length)
                    float k = t.Spline.Length / Mathf.Max(1f, len);
                    float s = edge * k;
                    float safe = atEnd ? s - BufferFront - ClearOfTrigger : s + BufferFront + ClearOfTrigger;
                    safe = Mathf.Clamp(safe, 2f, t.Spline.Length - 2f);
                    s_Portals.Add(new Portal { Ls = ls, Track = t, S = s, AtEnd = atEnd, To = ls.m_SceneToLoad, Safe = safe });
                    SteamRailwayMod.Debug($"Rail transition to {ls.m_SceneToLoad} on {t.Id} at {s:F0} m ({(atEnd ? "end" : "start")}, {(touches ? "trigger crossed" : "trigger beside the line")}), trains stand at {safe:F0} m.");
                }
            }
            // the transitions made with the track editor: a point on a line and the way out through it
            foreach (var (here, there) in CustomPortals.Ends(s_Scene))
            {
                var p = CustomLines.V(here.Pos);
                var outDir = CustomLines.V(here.Out);
                BuiltTrack best = null; float bd = 6f, bs = 0f;
                foreach (var t in s_Tracks)
                {
                    float sp = t.Path.NearestDistance(p, out float hd);
                    if (hd < bd) { bd = hd; best = t; bs = sp; }
                }
                if (best == null) { SteamRailwayMod.Log.Warning($"Transition to {there.Scene}: no line at {p}."); continue; }
                best.Path.Sample(bs, out _, out var tan);
                bool forward = Vector3.Dot(tan, outDir) > 0f;          // leaves towards the line's end
                float k = best.Spline.Length / Mathf.Max(1f, best.Path.Length);
                float s = bs * k;
                float safe = Mathf.Clamp(forward ? s - BufferFront - ClearOfTrigger : s + BufferFront + ClearOfTrigger, 2f, best.Spline.Length - 2f);
                s_Portals.Add(new Portal { Track = best, S = s, AtEnd = forward, To = there.Scene, Safe = safe, Custom = true, Spawn = there.Spawn });
                best.Path.Sample(bs, out var onLine, out _);
                var flatOut = outDir; flatOut.y = 0f;
                s_Walk.Add(new WalkPortal { Pos = onLine, Out = flatOut.normalized, There = there });
                SteamRailwayMod.Log.Msg($"Hand-made rail transition to {there.Scene} on {best.Id} at {s:F0} m, trains stand at {safe:F0} m.");
            }
        }

        const float BufferFront = 8.3f, BufferRear = 6.5f;
        const float ClearOfTrigger = 14f;    // m between the trigger and the nearest buffer of a standing train

        /// The stretch of the line (path metres) inside the trigger, checked at the rails and at cab height.
        static bool Touches(BuiltTrack t, Collider col, out float in0, out float in1)
        {
            in0 = float.MaxValue; in1 = float.MinValue;
            if (col == null) return false;
            var path = t.Path;
            bool exact = col is BoxCollider || col is SphereCollider || col is CapsuleCollider || (col is MeshCollider mc && mc.convex);
            var bounds = col.bounds;
            bounds.Expand(0.5f);
            for (float d = 0f; d <= path.Length; d += 0.5f)
            {
                path.Sample(d, out var p, out _);
                for (int h = 0; h < 3; h++)
                {
                    var q = p + Vector3.up * (0.5f + h * 1.5f);
                    if (!bounds.Contains(q)) continue;
                    bool inside = !exact || (col.ClosestPoint(q) - q).sqrMagnitude < 0.04f;
                    if (!inside) continue;
                    in0 = Mathf.Min(in0, d); in1 = Mathf.Max(in1, d);
                    break;
                }
            }
            return in1 >= in0;
        }

        /// A train standing in (or too close to) a transition is moved clear of it, inwards.
        static float ClearOfPortals(string trackId, float pos)
        {
            foreach (var p in s_Portals)
            {
                if (p.Track.Id != trackId) continue;
                if (p.AtEnd && pos > p.Safe) pos = p.Safe;
                if (!p.AtEnd && pos < p.Safe) pos = p.Safe;
            }
            return pos;
        }

        /// The game reports a train car running into a transition.
        public static void TrainHitTransition(TrainCar car, LoadScene ls)
        {
            if (s_Transiting || car == null || ls == null) return;
            var t = s_Trains.Find(x => x.Train != null && x.Train.Pointer == car.Pointer);
            if (t == null) return;
            var p = s_Portals.Find(x => x.Ls != null && x.Ls.Pointer == ls.Pointer && x.Track.Id == t.RouteId);
            if (p == null) return;
            if (t.Train.IsInsideTrain) BeginTransit(t, p, t.Sim.LineVelocity);
            else
            {
                // nobody aboard: the train stops at the transition
                t.Sim.RestoreMotion(0f);
                t.Train.m_SplineController.Position = p.Safe;
            }
        }

        /// The player touches a transition trigger. Riding one of our trains on that line, the train goes along;
        /// the train's own colliders never set it off. Returns false to keep the game from handling it.
        public static bool AllowTransitionTrigger(LoadScene ls, Collider other)
        {
            if (ls == null) return true;
            // our own transition is under way: the game must not start a second one from the same trigger
            if (s_Transiting) return !s_Portals.Exists(x => x.Ls != null && x.Ls.Pointer == ls.Pointer);
            try
            {
                if (other != null)
                {
                    var car = other.GetComponentInParent<TrainCar>();
                    if (car != null && IsOurTrain(car)) return false;
                }
                if (!SteamTrain.IsInsideSteamTrain) return true;
                var tracked = SteamTrain.s_TrackedSteamTrain;
                if (tracked == null) return true;
                var t = s_Trains.Find(x => x.Train != null && x.Train.Pointer == tracked.Pointer);
                if (t == null) return true;
                var p = s_Portals.Find(x => x.Ls != null && x.Ls.Pointer == ls.Pointer && x.Track.Id == t.RouteId);
                if (p == null) return true;
                BeginTransit(t, p, t.Sim.LineVelocity);
                return false;
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Warning($"Transition trigger: {e.Message}");
                return true;
            }
        }

        /// The player drives the train into a transition: take the train along to the next region.
        static void CheckPortals(LiveTrain t)
        {
            var tr = t.Train;
            if (tr == null || t.Sim == null || !tr.IsInsideTrain) return;
            float v = t.Sim.LineVelocity;
            if (Mathf.Abs(v) < 0.2f) return;
            float pos = tr.m_SplineController.Position;
            float lead = v > 0f ? pos + BufferFront : pos - BufferRear;
            foreach (var p in s_Portals)
            {
                if (p.Track.Id != t.RouteId) continue;
                bool hit = p.AtEnd ? v > 0f && lead >= p.S - 1f : v < 0f && lead <= p.S + 1f;
                // a hand-made transition lies in the middle of a line: only right at it
                if (hit && p.Custom && Mathf.Abs(lead - p.S) > 25f) hit = false;
                if (!hit) continue;
                BeginTransit(t, p, v);
                return;
            }
        }

        static void BeginTransit(LiveTrain t, Portal p, float v)
        {
            s_Transiting = true;
            CaptureStates();
            TrainState state = null;
            if (s_Save.Scenes.TryGetValue(s_Scene, out var list))
            {
                state = list.Find(x => x.Guid == t.Guid);
                if (state != null) list.Remove(state);
            }
            if (state == null) { s_Transiting = false; return; }
            state.Velocity = 0f;
            s_Save.Arrival = new PendingArrival
            {
                FromScene = s_Scene,
                ToScene = p.To,
                Train = state,
                Speed = Mathf.Abs(v),
                // chimney first? (standing still: the way that leads out through this transition)
                Forward = Mathf.Abs(v) > 0.05f ? t.Sim.Velocity > 0f : (p.AtEnd ? 1 : -1) * t.Sim.FacingSign > 0,
                FallbackRouteId = t.RouteId,
                FallbackPos = p.AtEnd ? p.S - 30f : p.S + 30f,
            };
            SteamRailwayMod.Log.Msg($"Train {t.Guid} leaves {s_Scene} for {p.To}.");
            // The train is no longer this region's (its save must not keep it), but it keeps running with the
            // player aboard while the screen fades; the player leaves the cab only once it is black.
            s_Trains.Remove(t);
            s_Departing = t;
            try
            {
                var ls = p.Ls != null ? p.Ls : CustomPortals.MakeLoadScene(p.To, p.Spawn);
                if (ls == null) throw new System.Exception("no transition object to copy in this region");
                ls.Activate(true);
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Region transition failed: {e.Message}");
                s_Transiting = false;
            }
        }

        static LiveTrain s_Departing;

        /// The screen is black and the next region starts loading (LoadScene.PerformSceneLoad).
        public static void OnPerformSceneLoad()
        {
            CaptureStates();
            var t = s_Departing;
            s_Departing = null;
            if (t == null) return;
            var tr = t.Train;
            try
            {
                if (tr != null && tr.IsInsideTrain)
                {
                    tr.ExitSteamTrain(tr.transform.Find("ExitPoint"));
                    tr.FinishExitingSteamTrainAfterFadeOut();
                }
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Leaving the cab for the transition: {e.Message}"); }
            s_Sims.Remove(t.Guid);
            try { t.Sim.Audio?.StopAll(); } catch { }
            if (tr != null) Object.Destroy(tr.gameObject);
        }

        /// Picks the line end the train comes in at and turns the line so the train runs the same way as before.
        static Portal PrepareArrival(PendingArrival arr)
        {
            Portal entry = s_Portals.Find(p => p.To == arr.FromScene);
            if (entry == null)
            {
                // no transition back on the line: the line end nearest to where the game put the player
                var player = GameManager.GetPlayerObject();
                Vector3 pp = player != null ? player.transform.position : Vector3.zero;
                float best = float.MaxValue;
                foreach (var t in s_Tracks)
                {
                    float d0 = Vector3.Distance(t.Path[0], pp), d1 = Vector3.Distance(t.Path[t.Path.Count - 1], pp);
                    if (d0 < best) { best = d0; entry = new Portal { Track = t, S = 0f, AtEnd = false, To = arr.FromScene }; }
                    if (d1 < best) { best = d1; entry = new Portal { Track = t, S = t.Spline.Length, AtEnd = true, To = arr.FromScene }; }
                }
            }
            if (entry == null) return null;

            return entry;
        }

        static void Arrive(PendingArrival arr, Portal entry, Material mat)
        {
            s_Save.Arrival = null;
            if (entry == null) { ArrivalFailed(arr); return; }
            var st = arr.Train;
            float len = entry.Track.Spline.Length;
            // the exit point of the transition: clear of its trigger, so the train does not run straight back
            float pos = entry.Ls != null || entry.Custom ? entry.Safe : (entry.AtEnd ? len - 25f : 25f);
            st.RouteId = entry.Track.Id;
            st.SplinePos = Mathf.Clamp(pos, 1f, len - 1f);
            // The train moves into the region: along the line + at its start, - at its end. It keeps running the
            // way it ran before (chimney first or bunker first), so it stands on the line facing accordingly.
            int inward = entry.AtEnd ? -1 : 1;
            int run = arr.Forward ? 1 : -1;
            if (st.Sim == null) st.Sim = new SimState();
            st.Sim.Facing = inward * run;
            float velocity = run * arr.Speed;
            st.Velocity = velocity;             // it comes in running, as it left
            Spawn(st, mat);
            var live = s_Trains.Find(x => x.Guid == st.Guid);
            if (live != null) MelonCoroutines.Start(BoardAfterArrival(live, velocity));
            SteamRailwayMod.Log.Msg($"Train {st.Guid} arrived in {s_Scene} from {arr.FromScene}.");
        }

        /// The game puts the player at the region's entry point while the screen is still black, then fades in.
        /// The train runs on meanwhile; the player is put aboard the moment they have been placed (they jump
        /// there, or the fade-in starts), so the fade opens on the cab of a moving train.
        static IEnumerator BoardAfterArrival(LiveTrain t, float velocity)
        {
            t.Boarding = true;
            try
            {
                var e = BoardAfterArrivalInner(t, velocity);
                while (e.MoveNext()) yield return e.Current;
            }
            finally { t.Boarding = false; t.AwaySince = -1f; t.Reboards = 0; }
        }

        static IEnumerator BoardAfterArrivalInner(LiveTrain t, float velocity)
        {
            float start = Time.realtimeSinceStartup;
            var p0 = GameManager.GetPlayerObject();
            Vector3 last = p0 != null ? p0.transform.position : Vector3.zero;
            float still = 0f;
            while (Time.realtimeSinceStartup - start < 10f)
            {
                yield return null;
                if (t.Train == null) yield break;
                var player = GameManager.GetPlayerObject();
                if (player == null) continue;
                var p = player.transform.position;
                bool jumped = (p - last).sqrMagnitude > 4f;
                still = (p - last).sqrMagnitude < 0.0025f ? still + Time.unscaledDeltaTime : 0f;
                last = p;
                bool fadingIn = false;
                try { fadingIn = !CameraFade.IsFadedOut || CameraFade.IsFadingIn; } catch { }
                if (jumped || (fadingIn && Time.realtimeSinceStartup - start > 0.2f) || still > 1.5f) break;
            }

            for (int attempt = 0; attempt < 4 && t.Train != null; attempt++)
            {
                try
                {
                    SteamTrain.s_TrackedSteamTrain = t.Train;
                    if (!t.Train.IsInsideTrain) t.Train.EnterSteamTrain();
                    t.Train.FinishEnteringSteamTrainAfterFadeOut();
                    // EnterSteamTrain starts a fade whose end clears this; during a region load that fade never
                    // ends, and while it is set the game does not carry the player with the cab (they fall through)
                    t.Train.m_PendingEnterTrain = false;
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Boarding the arrived train failed: {e.Message}"); }
                for (int k = 0; k < 6; k++) yield return null;
                if (t.Train == null || PlayerInCab(t.Train)) break;
                SteamRailwayMod.Log.Warning($"Player not in the cab after boarding (attempt {attempt + 1}); trying again.");
            }
            if (t.Train == null) yield break;
            // the game may still move the player to the entry point a moment later: put them back aboard
            float watch = Time.realtimeSinceStartup;
            int reboards = 0;
            while (Time.realtimeSinceStartup - watch < 4f && t.Train != null && reboards < 3)
            {
                yield return null;
                if (t.Train == null || PlayerInCab(t.Train)) continue;
                reboards++;
                try
                {
                    SteamTrain.s_TrackedSteamTrain = t.Train;
                    if (!t.Train.IsInsideTrain) t.Train.EnterSteamTrain();
                    t.Train.FinishEnteringSteamTrainAfterFadeOut();
                    // clear the pending-enter flag here as well (see above)
                    t.Train.m_PendingEnterTrain = false;
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Boarding the arrived train again failed: {e.Message}"); }
                for (int k = 0; k < 6; k++) yield return null;
            }
            if (t.Train == null) yield break;
            // nobody aboard after all: the train must not run off on its own
            if (!PlayerInCab(t.Train)) t.Sim.RestoreMotion(0f);
        }

        /// The game believes the player is in the cab (train controls, no walking) but they are standing somewhere
        /// else: after a region change the game may move the player once more after the train took them aboard.
        /// Take them aboard again; if that keeps failing, let them out properly and stop the train.
        static void GuardCab(LiveTrain t)
        {
            var tr = t.Train;
            if (tr == null || t.Sim == null || t.Boarding || s_Transiting || !tr.IsInsideTrain) { t.AwaySince = -1f; return; }
            bool fading = false;
            try { fading = CameraFade.IsFadingIn || CameraFade.IsFadingOut || CameraFade.IsFadedOut; } catch { }
            // aboard but still "about to board" with no fade running: the game would never carry the player along
            if (!fading && tr.m_PendingEnterTrain)
            {
                SteamRailwayMod.Log.Warning("Boarding was left pending: finishing it.");
                tr.m_PendingEnterTrain = false;
                tr.FinishEnteringSteamTrainAfterFadeOut();
            }
            if (fading || PlayerInCab(tr)) { t.AwaySince = -1f; if (PlayerInCab(tr)) t.Reboards = 0; return; }
            float now = Time.realtimeSinceStartup;
            if (t.AwaySince < 0f) { t.AwaySince = now; return; }
            if (now - t.AwaySince < 1.2f) return;
            t.AwaySince = now;
            var player = GameManager.GetPlayerObject();
            string where = player != null ? player.transform.position.ToString("F1") : "?";
            if (t.Reboards < 3)
            {
                t.Reboards++;
                SteamRailwayMod.Log.Warning($"Player at {where} is not in the cab at {tr.m_CameraDummy.position:F1} while aboard: taking them aboard again ({t.Reboards}).");
                SteamTrain.s_TrackedSteamTrain = tr;
                tr.FinishEnteringSteamTrainAfterFadeOut();
                tr.m_PendingEnterTrain = false;
                return;
            }
            SteamRailwayMod.Log.Warning($"Player at {where} could not be put back into the cab: leaving the train and stopping it.");
            t.Sim.S.Regulator = 0f;
            t.Sim.RestoreMotion(0f);
            try
            {
                tr.ExitSteamTrain(tr.transform.Find("ExitPoint"));
                tr.FinishExitingSteamTrainAfterFadeOut();
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Leaving the train failed: {e.Message}"); }
            var pm = GameManager.GetPlayerManagerComponent();
            if (pm != null && pm.GetControlMode() == PlayerControlMode.InSteamTrain) pm.SetControlMode(PlayerControlMode.Normal);
            t.Reboards = 0;
            t.AwaySince = -1f;
        }

        static bool PlayerInCab(SteamTrain train)
        {
            var player = GameManager.GetPlayerObject();
            if (player == null || train.m_CameraDummy == null) return false;
            var d = player.transform.position - train.m_CameraDummy.position;
            return new Vector2(d.x, d.z).magnitude < 2f && Mathf.Abs(d.y) < 3f;
        }

        /// The next region has no railway: the train stays behind, stopped before the transition.
        static void ArrivalFailed(PendingArrival arr)
        {
            s_Save.Arrival = null;
            var st = arr.Train;
            st.RouteId = arr.FallbackRouteId;
            st.SplinePos = Mathf.Max(1f, arr.FallbackPos);
            st.Velocity = 0f;
            if (!s_Save.Scenes.TryGetValue(arr.FromScene, out var list)) { list = new List<TrainState>(); s_Save.Scenes[arr.FromScene] = list; }
            list.Add(st);
            CabControls.Message(L.T("Bu bölgede ray yok; tren geride, geçişin önünde kaldı.",
                                    "There is no railway in this region; the train stayed behind at the transition."));
            SteamRailwayMod.Log.Msg($"{s_Scene} has no railway; train {st.Guid} waits in {arr.FromScene}.");
        }

        /// Destroys and rebuilds everything in the current scene (route editor).
        /// Where each train stood before a rebuild (its line may have changed shape or name).
        static readonly Dictionary<string, Vector3> s_Remap = new Dictionary<string, Vector3>();

        static void Remap(TrainState st)
        {
            if (!s_Remap.TryGetValue(st.Guid, out var p) || s_Tracks.Count == 0) return;
            BuiltTrack best = null; float bd = float.MaxValue, bs = 0f;
            foreach (var t in s_Tracks)
            {
                float s = t.Path.NearestDistance(p, out float d);
                if (d < bd) { bd = d; best = t; bs = s; }
            }
            if (best == null || bd > 10f) return;
            st.RouteId = best.Id;
            st.SplinePos = bs * best.Spline.Length / Mathf.Max(1f, best.Path.Length);
        }

        /// Rebuilds the region's railway for the editors (not while the player rides a train).
        public static bool RebuildForEditor()
        {
            if (SteamTrain.IsInsideSteamTrain || s_Scene == null) return false;
            Rebuild();
            return true;
        }

        public static void Rebuild()
        {
            if (SteamTrain.IsInsideSteamTrain)
            {
                SteamRailwayMod.Log.Warning("Leave the train before rebuilding.");
                return;
            }
            CaptureStates();
            s_Remap.Clear();
            foreach (var t in s_Trains) if (t.Train != null) s_Remap[t.Guid] = t.Train.transform.position;
            foreach (var t in s_Trains) if (t.Train != null) Object.Destroy(t.Train.gameObject);
            foreach (var t in s_Tracks) if (t.Root != null) Object.Destroy(t.Root);
            s_Portals.Clear();
            Switches.Clear();
            s_Trains.Clear();
            s_Sims.Clear();
            s_Tracks.Clear();
            MelonCoroutines.Start(RebuildNextFrame());
        }

        static IEnumerator RebuildNextFrame()
        {
            yield return null;
            BuildScene();
        }

        // ---------------------------------------------------------------- trains

        static void Spawn(TrainState state, Material mat)
        {
            var track = s_Tracks.Find(t => t.Id == state.RouteId) ?? s_Tracks[0];
            // a train left standing in a transition trigger (older versions) would carry the player off again
            float clear = ClearOfPortals(track.Id, state.SplinePos);
            if (Mathf.Abs(clear - state.SplinePos) > 0.01f)
            {
                SteamRailwayMod.Log.Msg($"Train {state.Guid} stood in a region transition; moved to {clear:F0} m.");
                state.SplinePos = clear;
            }
            var train = TrainBuilder.Build(state.Guid, track.Spline, state.SplinePos, mat);
            var sim = new SteamSim(train, state.Sim);
            sim.Fire = train.m_Boiler != null ? train.m_Boiler.m_InvisibleFire : null;
            var stoveT = train.transform.Find("Firebox");
            sim.Stove = stoveT != null ? stoveT.GetComponent<WoodStove>() : null;
            sim.RestoreMotion(state.Velocity);
            sim.Animator = LocomotiveAnimator.TryCreate(train.transform);
            sim.Audio = new TrainAudio(train);
            sim.Smoke = CreateSmoke(train);
            s_Sims[state.Guid] = sim;
            CabControls.ApplyRealism(train);
            s_OurGuids.Add(state.Guid);
            s_Trains.Add(new LiveTrain { Train = train, Guid = state.Guid, RouteId = track.Id, Sim = sim });
            if (!string.IsNullOrEmpty(state.TrainData) || !string.IsNullOrEmpty(state.FireData) || (state.CabItems != null && state.CabItems.Count > 0))
                MelonCoroutines.Start(RestoreNextFrame(train, sim, state));
        }

        /// Chimney smoke from the bundle on the game's lit particle material (taken from the firebox stove's fire);
        /// the old smoke system stays as the game's emission target but no longer renders.
        static LocoSmoke CreateSmoke(SteamTrain train)
        {
            try
            {
                var old = train.transform.Find("ChimneySmoke");
                Vector3 pos = old != null ? old.localPosition : TrainConfig.ChimneyTop;
                Material lit = null;
                var stove = train.transform.Find("Firebox");
                if (stove != null)
                    foreach (var r in stove.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    {
                        var m = r.sharedMaterial;
                        if (m != null && m.name.StartsWith("FX_SmokeA") && m.shader != null && m.shader.name.Contains("VertexLit")) { lit = m; break; }
                    }
                var smoke = LocoSmoke.Create(train.transform, pos, lit);
                if (smoke != null && old != null)
                {
                    var or = old.GetComponent<ParticleSystemRenderer>();
                    if (or != null) or.enabled = false;
                }
                return smoke;
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Warning($"Chimney smoke could not be built: {e.Message}");
                return null;
            }
        }

        static IEnumerator RestoreNextFrame(SteamTrain train, SteamSim sim, TrainState state)
        {
            yield return null;
            if (train == null) yield break;
            try
            {
                if (!string.IsNullOrEmpty(state.TrainData))
                {
                    train.DeserializeTrainCarData(state.TrainData);
                    train.DeserializeTrainCarDataPostSceneLoad();
                }
                if (!string.IsNullOrEmpty(state.FireData) && sim.Fire != null)
                    sim.Fire.Deserialize(state.FireData);
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Restoring train {state.Guid} failed: {e}");
            }
            try
            {
                CabItems.Restore(train.transform, state.CabItems);
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Restoring train {state.Guid} failed: {e}");
            }
        }

        public static void Update()
        {
            if (s_Scene != null) InstancedHider.Update();
            if (s_Scene != null) { try { CheckWalkPortals(); } catch (System.Exception e) { SteamRailwayMod.Debug($"Walk transition: {e.Message}"); } }
            if (s_Scene == null || s_Trains.Count == 0) return;
            Patches.CabPlace_ControlMode.Update();
            BoilerExplosion.Update();
            for (int i = 0; i < s_Trains.Count; i++) PassJunctions(s_Trains[i]);
            for (int i = 0; i < s_Trains.Count; i++)
                if (s_Trains[i].Train != null)
                {
                    try { CabItems.Update(s_Trains[i].Train.transform); }
                    catch (System.Exception e) { SteamRailwayMod.Debug($"Cab items: {e.Message}"); }
                }
            if (!s_Transiting)
                for (int i = 0; i < s_Trains.Count; i++) CheckPortals(s_Trains[i]);
            for (int i = 0; i < s_Trains.Count; i++)
            {
                try { GuardCab(s_Trains[i]); }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Cab guard: {e.Message}"); }
            }
            if (Time.time >= s_NextHover)
            {
                s_NextHover = Time.time + 0.2f;
                foreach (var t in s_Trains)
                {
                    if (t.Train == null || t.Sim == null) continue;
                    CabControls.UpdateHoverTexts(t.Sim);
                    CabControls.UpdateEffects(t.Sim);
                }
            }
            if (Time.time < s_NextCapture) return;
            s_NextCapture = Time.time + CaptureInterval;
            CaptureStates();
            foreach (var t in s_Trains) KeepExitPointOnGround(t.Train);
        }

        static void PassJunctions(LiveTrain t)
        {
            if (t.Train == null || t.Sim == null) return;
            float pos = t.Train.m_SplineController.Position;
            float prev = t.PrevPos;
            t.PrevPos = pos;
            if (float.IsNaN(prev) || Switches.All.Count == 0) return;
            var line = s_Tracks.Find(x => x.Id == t.RouteId);
            if (line == null) return;
            var moved = Switches.Pass(t, line, prev, pos);
            if (moved != null) t.PrevPos = t.Train.m_SplineController.Position;
        }

        public static void SaveSwitch(string id, bool diverging) => s_Save.Switches[id] = diverging;

        static void KeepExitPointOnGround(SteamTrain train)
        {
            if (train == null || train.IsMoving) return;
            var exit = train.transform.Find("ExitPoint");
            if (exit == null) return;
            var local = exit.localPosition;
            exit.position = H.GroundBelow(train.transform.TransformPoint(new Vector3(local.x, 2f, local.z)));
        }

        // ---------------------------------------------------------------- save / load

        public static void CaptureStates()
        {
            if (s_Scene == null || s_Trains.Count == 0) return;
            var list = new List<TrainState>();
            foreach (var t in s_Trains)
            {
                if (t.Train == null) continue;
                try
                {
                    list.Add(new TrainState
                    {
                        Guid = t.Guid,
                        RouteId = t.RouteId,
                        SplinePos = t.Train.m_SplineController.Position,
                        TrainData = t.Train.SerializeTrainCarData(),
                        FireData = t.Sim != null && t.Sim.Fire != null ? t.Sim.Fire.Serialize() : null,
                        Sim = t.Sim != null ? t.Sim.S : null,
                        Velocity = t.Sim != null ? t.Sim.Velocity : 0f,
                        CabItems = CabItems.Capture(t.Train.transform),
                    });
                }
                catch (System.Exception e)
                {
                    SteamRailwayMod.Debug($"Capture of {t.Guid} failed: {e.Message}");
                }
            }
            if (list.Count > 0) s_Save.Scenes[s_Scene] = list;
        }

        /// Called right before the game writes the save slot (see SavePatches).
        public static void WriteSave()
        {
            CaptureStates();
            if (s_Save.Scenes.Count == 0) return;
            s_ModData.Save(JsonSerializer.Serialize(s_Save, s_Json));
            SteamRailwayMod.Debug($"Railway state written ({s_Save.Scenes.Count} scene(s)).");
        }

        static bool s_NewGame;

        /// Older versions placed a locomotive in every region the player walked into: one per game is kept (the
        /// one on its way to another region, else the one in the region visited first).
        static void KeepOneTrain()
        {
            int total = s_Save.Arrival != null ? 1 : 0;
            foreach (var kv in s_Save.Scenes) total += kv.Value != null ? kv.Value.Count : 0;
            if (total <= 1) return;
            string keepScene = null;
            if (s_Save.Arrival == null)
            {
                foreach (var v in s_Save.Visited)
                    if (s_Save.Scenes.TryGetValue(v, out var l) && l != null && l.Count > 0) { keepScene = v; break; }
                if (keepScene == null)
                    foreach (var kv in s_Save.Scenes) if (kv.Value != null && kv.Value.Count > 0) { keepScene = kv.Key; break; }
            }
            foreach (var kv in s_Save.Scenes)
            {
                if (kv.Value == null) continue;
                if (kv.Key == keepScene && kv.Value.Count > 1) kv.Value.RemoveRange(1, kv.Value.Count - 1);
                else if (kv.Key != keepScene) kv.Value.Clear();
            }
            SteamRailwayMod.Log.Msg($"{total - 1} extra locomotive(s) removed; one per game is kept" + (keepScene != null ? $" (in {keepScene})." : " (the arriving one)."));
        }

        /// A new game was started: everything of the railway starts from nothing (a brand new locomotive).
        public static void OnNewGame()
        {
            s_NewGame = true;
            s_NeedLoad = true;
        }

        static void LoadSave()
        {
            string slot = null;
            try { slot = SaveGameSystem.GetCurrentSaveName(); } catch { }
            try
            {
                string json = s_NewGame ? null : s_ModData.Load();
                s_Save = string.IsNullOrEmpty(json) ? new RailwaySave() : JsonSerializer.Deserialize<RailwaySave>(json, s_Json) ?? new RailwaySave();
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Railway save data could not be read: {e.Message}");
                s_Save = new RailwaySave();
            }
            if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(s_Save.Slot) && s_Save.Slot != slot)
            {
                SteamRailwayMod.Log.Msg($"Railway data of another game ({s_Save.Slot}) ignored; {slot} starts fresh.");
                s_Save = new RailwaySave();
            }
            if (!string.IsNullOrEmpty(slot)) s_Save.Slot = slot;
            // saves from before TrainCreated existed: a train anywhere means it was created
            foreach (var kv in s_Save.Scenes) if (kv.Value != null && kv.Value.Count > 0) s_Save.TrainCreated = true;
            if (s_Save.Arrival != null) s_Save.TrainCreated = true;
            KeepOneTrain();
            s_OurGuids.Clear();
            s_NewGame = false;
        }

        // ---------------------------------------------------------------- helpers

        static Material s_TrackMaterial;

        static Material FindTrackMaterial()
        {
            if (s_TrackMaterial != null) return s_TrackMaterial;
            Material fallback = null;
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>())
            {
                var m = mr.sharedMaterial;
                if (m == null) continue;
                if (mr.name.StartsWith("OBJ_RailTrack")) { s_TrackMaterial = m; return m; }
                if (fallback == null && m.shader != null && m.shader.name.Contains("TLD_StandardDiffuse")) fallback = m;
            }
            return fallback;
        }
    }
}
