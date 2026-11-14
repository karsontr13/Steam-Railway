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
            return s_Sims.TryGetValue(car.name.Substring(TrainBuilder.NamePrefix.Length), out var simulation) ? simulation : null;
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

        /// The game was changed to another save slot without the main menu being noticed: what is in memory belongs
        /// to the previous game and must not carry over.
        static bool SaveSlotChanged()
        {
            string slot = null;
            try { slot = SaveGameSystem.GetCurrentSaveName(); } catch { }
            if (string.IsNullOrEmpty(slot) || string.IsNullOrEmpty(s_Save.Slot) || slot == "autosave") return false;
            return slot != s_Save.Slot;
        }

        static IEnumerator BuildSceneDelayed(string sceneName)
        {
            // Let terrain, LoadScene objects and the player settle.
            yield return null;
            yield return null;
            // The terrain tiles may stream in later on a slower machine: the terrain edits need them.
            const int MaxFramesToWaitForTerrain = 600;
            for (int waitedFrames = 0; waitedFrames < MaxFramesToWaitForTerrain && Terrain.activeTerrains.Length == 0; waitedFrames++)
                yield return null;
            if (sceneName != s_Scene) yield break;
            // The save slot is read once per loaded game; between regions the state in memory is the newer one
            // (a train that just left a region must not come back from a save written before it left).
            if (!s_NeedLoad && SaveSlotChanged()) s_NeedLoad = true;
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
            var material = FindTrackMaterial();

            // The region's own railway comes first; hand-made routes (routes.json) only where a region has none.
            s_Defs.Clear();
            var lines = new List<RailLine>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
                for (int index = 0; index < lines.Count; index++)
                {
                    var trackDef = new TrackDef { Id = $"{s_Scene}_rail{index + 1}", Points = new List<Vector3>(lines[index].Points), Keep = lines[index].Keep, Original = lines[index] };
                    trackDef.Originals.Add(lines[index]);
                    s_Defs.Add(trackDef);
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
                        var points = new List<Vector3>();
                        for (int index = 0; index < route.Points.Count; index++) points.Add(route.GetPoint(index));
                        s_Defs.Add(new TrackDef { Id = route.Id, Points = points, Keep = null });
                    }
            }
            // the lines drawn with the track editor; one meeting another line's end becomes one line with it
            try
            {
                CustomLines.AddDefs(s_Scene, s_Defs);
                CustomLines.Merge(s_Defs);
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Error($"Adding the drawn lines failed: {e}"); }
            foreach (var trackDef in s_Defs)
            {
                // a fixed direction for each line (the pieces are found in no particular order)
                var start = trackDef.Points[0]; var end = trackDef.Points[trackDef.Points.Count - 1];
                if (start.x + start.z > end.x + end.z) trackDef.Points.Reverse();
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
                    foreach (var identifier in Switches.Branches(s_Defs))
                    {
                        s_Defs.RemoveAll(trackDef => trackDef.Id == identifier);
                        SteamRailwayMod.Debug($"{identifier} branches off another line; left as it is (no switch).");
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
            foreach (var trackDef in s_Defs)
            {
                var built = BuildTrack(trackDef);
                if (built != null) s_Tracks.Add(built);
            }
            // junctions first: their turnouts decide what is laid under the points
            if (Switches.Automatic || switchPoints.Count > 0)
            {
                try
                {
                    Switches.Find(s_Tracks, s_Save.Switches, Switches.Automatic ? null :
                        (System.Func<Vector3, bool>)(point => switchPoints.Exists(otherPoint => (otherPoint - point).sqrMagnitude < 1.5f * 1.5f)));
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Error($"Finding the junctions failed: {e}"); }
            }
            OriginalRails.BeginClear();
            foreach (var track in s_Tracks)
            {
                try { TrackBuilder.LayRails(track); }
                catch (System.Exception e) { SteamRailwayMod.Log.Error($"Laying the rails of {track.Id} failed: {e}"); }
                var trackDef = s_Defs.Find(x => x.Id == track.Id);
                if (trackDef != null && trackDef.Originals.Count > 0) OriginalRails.ClearLine(track);
            }
            OriginalRails.EndClear();
            Switches.RailsLaid();
            InstancedHider.Apply();
            SteamRailwayMod.Debug($"{s_Scene}: lines and rails ready in {stopwatch.ElapsedMilliseconds} ms.");

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
            foreach (var fillPath in CustomLines.FillPaths(s_Scene))
            {
                try { TerrainCarve.Fill(fillPath); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snow embankment failed: {e.Message}"); }
            }
            // under the points the branch was raised to the main line's height: snow under it there too
            foreach (var trackSwitch in Switches.All)
            {
                try
                {
                    var points = new List<Vector3>();
                    for (float u = 0f; u <= 32f; u += 2f)
                    {
                        float secondArc = trackSwitch.BJoin + trackSwitch.InB * u;
                        if (secondArc < 0f || secondArc > trackSwitch.B.Path.Length) break;
                        trackSwitch.B.Path.Sample(secondArc, out var otherPoint, out _);
                        points.Add(otherPoint);
                    }
                    if (points.Count >= 2) TerrainCarve.Fill(CurvePath.CatmullRom(points));
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snow under the points failed: {e.Message}"); }
            }
            foreach (var track in s_Tracks)
            {
                try { TerrainCarve.Lower(track.Path); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Lowering the snow along {track.Id} failed: {e.Message}"); }
            }
            Switches.GroundStands();
            FindPortals();

            // an arriving train decides which way round its line runs; trains already here are turned with it
            Portal entry = null;
            if (arrival != null) entry = PrepareArrival(arrival);

            if (!s_Save.Visited.Contains(s_Scene)) s_Save.Visited.Add(s_Scene);
            if (s_Save.Scenes.TryGetValue(s_Scene, out var states) && states.Count > 0)
            {
                foreach (var trainState in states) { Remap(trainState); Spawn(trainState, material); }
            }
            else if (!s_Save.TrainCreated && arrival == null)
            {
                // One locomotive per game. With a start point (Start.json) it stands there, full of water, and
                // is placed only when the player comes to that region; otherwise it is a cold one on the first railway.
                var start = StartPoint.Load();
                if (start == null || start.Scene == s_Scene)
                {
                    s_Save.TrainCreated = true;
                    var trainState = new TrainState { Guid = System.Guid.NewGuid().ToString(), RouteId = s_Tracks[0].Id, SplinePos = DefaultSpawnMeters };
                    if (start != null) PlaceAtStart(trainState, start);
                    Spawn(trainState, material);
                }
            }
            s_Remap.Clear();
            if (arrival != null) Arrive(arrival, entry, material);
            if (s_WalkToScene != null && s_WalkToScene == s_Scene)
            {
                MelonCoroutines.Start(PlaceWalker(s_WalkToPos, s_WalkToDir));
                s_WalkToScene = null;
            }
            SteamRailwayMod.Log.Msg($"{s_Scene}: {s_Tracks.Count} track(s), {s_Trains.Count} train(s), {s_Portals.Count} rail transition(s).");
        }

        /// The new game's locomotive: on the line nearest to the start point, chimney facing the start direction,
        /// boiler and tender full, everything in order.
        static void PlaceAtStart(TrainState trainState, StartPoint start)
        {
            var point = new Vector3(start.Pos[0], start.Pos[1], start.Pos[2]);
            BuiltTrack best = null; float bestDistance = float.MaxValue, bestArc = 0f;
            foreach (var track in s_Tracks)
            {
                float speed = track.Path.NearestDistance(point, out float distance);
                if (distance < bestDistance) { bestDistance = distance; best = track; bestArc = speed; }
            }
            if (best == null) return;
            if (bestDistance > 30f) SteamRailwayMod.Log.Warning($"The start point is {bestDistance:F0} m from the nearest line; the train stands on the line anyway.");
            trainState.RouteId = best.Id;
            trainState.SplinePos = Mathf.Clamp(bestArc * best.Spline.Length / Mathf.Max(1f, best.Path.Length), 2f, best.Spline.Length - 2f);
            best.Path.Sample(bestArc, out _, out var tangent);
            var direction = start.Dir != null && start.Dir.Length >= 3 ? new Vector3(start.Dir[0], 0f, start.Dir[2]) : Vector3.zero;
            int facing = direction.sqrMagnitude < 0.01f || Vector3.Dot(new Vector3(tangent.x, 0f, tangent.z), direction) >= 0f ? 1 : -1;
            trainState.Sim = new SimState
            {
                Facing = facing,
                BoilerWater = SteamSim.BoilerCapacity,
                TenderWater = SteamSim.TenderCapacity,
                BoilerCondition = 100f,
                ValveCondition = 100f,
            };
            SteamRailwayMod.Log.Msg($"New game: the locomotive stands on {best.Id} at {trainState.SplinePos:F0} m, chimney {(facing > 0 ? "towards the end" : "towards the start")} of the line, boiler and tender full.");
        }

        static BuiltTrack BuildTrack(TrackDef trackDef)
        {
            var points = new List<Vector3>(trackDef.Points);
            if (s_Save.Reversed.TryGetValue(trackDef.Id, out bool reversed) && reversed) points.Reverse();
            var built = TrackBuilder.Build(trackDef.Id, points, trackDef.Keep);
            if (built != null) foreach (var line in trackDef.Originals) OriginalRails.HideReplaced(line);
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
            foreach (var walkPortal in s_Walk)
            {
                var direction = pp - walkPortal.Pos;
                float along = Vector3.Dot(direction, walkPortal.Out);
                float lateral = Mathf.Abs(Vector3.Dot(direction, Vector3.Cross(Vector3.up, walkPortal.Out)));
                if (lateral > 4f || Mathf.Abs(direction.y) > 5f || Mathf.Abs(along) > 10f) { walkPortal.PrevAlong = float.NaN; continue; }
                bool crossed = !float.IsNaN(walkPortal.PrevAlong) && walkPortal.PrevAlong < 0f && along >= 0f;
                walkPortal.PrevAlong = along;
                if (!crossed) continue;
                WalkTransit(walkPortal);
                return;
            }
        }

        static void WalkTransit(WalkPortal walkPortal)
        {
            s_Transiting = true;
            s_WalkToScene = walkPortal.There.Scene;
            s_WalkToPos = CustomLines.V(walkPortal.There.Pos);
            var od = CustomLines.V(walkPortal.There.Out); od.y = 0f;
            s_WalkToDir = -od.normalized;                     // into the other region, away from its portal
            SteamRailwayMod.Log.Msg($"On foot through the rail transition to {walkPortal.There.Scene}.");
            try
            {
                var loadScene = CustomPortals.MakeLoadScene(walkPortal.There.Scene, walkPortal.There.Spawn);
                if (loadScene == null) throw new System.Exception("no transition object to copy in this region");
                loadScene.Activate(true);
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Region transition failed: {e.Message}");
                s_Transiting = false;
                s_WalkToScene = null;
            }
        }

        /// The game puts the player at a spawn point of the region; they are moved to the transition's end.
        static IEnumerator PlaceWalker(Vector3 pos, Vector3 direction)
        {
            float start = Time.realtimeSinceStartup;
            var sourceObject = GameManager.GetPlayerObject();
            Vector3 last = sourceObject != null ? sourceObject.transform.position : Vector3.zero;
            float still = 0f;
            while (Time.realtimeSinceStartup - start < 10f)
            {
                yield return null;
                var player = GameManager.GetPlayerObject();
                if (player == null) continue;
                var point = player.transform.position;
                bool jumped = (point - last).sqrMagnitude > 4f;
                still = (point - last).sqrMagnitude < 0.0025f ? still + Time.unscaledDeltaTime : 0f;
                last = point;
                bool fadingIn = false;
                try { fadingIn = !CameraFade.IsFadedOut || CameraFade.IsFadingIn; } catch { }
                if (jumped || (fadingIn && Time.realtimeSinceStartup - start > 0.2f) || still > 1.5f) break;
            }
            // on the ground at the line's height (not on top of a rock or a tunnel roof above it)
            var flat = pos + direction * 6f;
            float groundY = float.MinValue;
            foreach (var hit in Physics.RaycastAll(new Vector3(flat.x, pos.y + 1.5f, flat.z), Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.isTrigger || hit.point.y <= groundY) continue;
                bool ours = false;
                for (var node = hit.collider.transform; node != null; node = node.parent) if (node.name.StartsWith("CHARACTER_FPSPlayer")) { ours = true; break; }
                if (!ours) groundY = hit.point.y;
            }
            if (groundY == float.MinValue) groundY = pos.y - RailPieces.RailTop;
            var target = new Vector3(flat.x, groundY + 0.05f, flat.z);
            var rotation = Quaternion.LookRotation(direction.sqrMagnitude > 0.01f ? direction : Vector3.forward, Vector3.up);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { GameManager.GetPlayerManagerComponent().TeleportPlayer(target, rotation); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Placing the player failed: {e.Message}"); yield break; }
                for (int deepIndex = 0; deepIndex < 30; deepIndex++) yield return null;
                var player = GameManager.GetPlayerObject();
                if (player == null || (player.transform.position - target).magnitude < 5f) yield break;
            }
        }

        static void FindPortals()
        {
            s_Portals.Clear();
            s_Walk.Clear();
            foreach (var loadScene in Object.FindObjectsOfType<LoadScene>())
            {
                if (loadScene == null || !loadScene.m_TransitionOnContact || string.IsNullOrEmpty(loadScene.m_SceneToLoad) || loadScene.m_SceneToLoad == s_Scene) continue;
                if (loadScene.GetComponent<Collider>() == null) continue;
                var collider = loadScene.GetComponent<Collider>();
                foreach (var track in s_Tracks)
                {
                    float speed = track.Path.NearestDistance(loadScene.transform.position, out float hd);
                    float length = track.Path.Length;
                    // where the line really runs through the trigger (a plane's centre may lie metres to the side)
                    bool touches = Touches(track, collider, out float in0, out float in1);
                    if (!touches && hd > 18f) continue;
                    bool atEnd = touches ? (in0 + in1) * 0.5f > length * 0.5f : speed > length * 0.5f;
                    float edge = touches ? (atEnd ? in0 : in1) : speed;
                    if (atEnd ? edge < length - 300f : edge > 300f) continue;
                    // path metres -> spline metres (the two curves are nearly the same length)
                    float scale = track.Spline.Length / Mathf.Max(1f, length);
                    float arcLength = edge * scale;
                    float safe = atEnd ? arcLength - BufferFront - ClearOfTrigger : arcLength + BufferFront + ClearOfTrigger;
                    safe = Mathf.Clamp(safe, 2f, track.Spline.Length - 2f);
                    s_Portals.Add(new Portal { Ls = loadScene, Track = track, S = arcLength, AtEnd = atEnd, To = loadScene.m_SceneToLoad, Safe = safe });
                    SteamRailwayMod.Debug($"Rail transition to {loadScene.m_SceneToLoad} on {track.Id} at {arcLength:F0} m ({(atEnd ? "end" : "start")}, {(touches ? "trigger crossed" : "trigger beside the line")}), trains stand at {safe:F0} m.");
                }
            }
            // the transitions made with the track editor: a point on a line and the way out through it
            foreach (var (here, there) in CustomPortals.Ends(s_Scene))
            {
                var point = CustomLines.V(here.Pos);
                var outDir = CustomLines.V(here.Out);
                BuiltTrack best = null; float bestDistance = 6f, bestArc = 0f;
                foreach (var track in s_Tracks)
                {
                    float speed = track.Path.NearestDistance(point, out float hd);
                    if (hd < bestDistance) { bestDistance = hd; best = track; bestArc = speed; }
                }
                if (best == null) { SteamRailwayMod.Log.Warning($"Transition to {there.Scene}: no line at {point}."); continue; }
                best.Path.Sample(bestArc, out _, out var tangent);
                bool forward = Vector3.Dot(tangent, outDir) > 0f;          // leaves towards the line's end
                float scale = best.Spline.Length / Mathf.Max(1f, best.Path.Length);
                float arcLength = bestArc * scale;
                float safe = Mathf.Clamp(forward ? arcLength - BufferFront - ClearOfTrigger : arcLength + BufferFront + ClearOfTrigger, 2f, best.Spline.Length - 2f);
                s_Portals.Add(new Portal { Track = best, S = arcLength, AtEnd = forward, To = there.Scene, Safe = safe, Custom = true, Spawn = there.Spawn });
                best.Path.Sample(bestArc, out var onLine, out _);
                var flatOut = outDir; flatOut.y = 0f;
                s_Walk.Add(new WalkPortal { Pos = onLine, Out = flatOut.normalized, There = there });
                SteamRailwayMod.Log.Msg($"Hand-made rail transition to {there.Scene} on {best.Id} at {arcLength:F0} m, trains stand at {safe:F0} m.");
            }
        }

        const float BufferFront = 8.3f, BufferRear = 6.5f;
        const float ClearOfTrigger = 14f;    // m between the trigger and the nearest buffer of a standing train

        /// The stretch of the line (path metres) inside the trigger, checked at the rails and at cab height.
        static bool Touches(BuiltTrack track, Collider collider, out float in0, out float in1)
        {
            in0 = float.MaxValue; in1 = float.MinValue;
            if (collider == null) return false;
            var path = track.Path;
            bool exact = collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider || (collider is MeshCollider meshCollider && meshCollider.convex);
            var bounds = collider.bounds;
            bounds.Expand(0.5f);
            for (float distance = 0f; distance <= path.Length; distance += 0.5f)
            {
                path.Sample(distance, out var point, out _);
                for (int height = 0; height < 3; height++)
                {
                    var otherPoint = point + Vector3.up * (0.5f + height * 1.5f);
                    if (!bounds.Contains(otherPoint)) continue;
                    bool inside = !exact || (collider.ClosestPoint(otherPoint) - otherPoint).sqrMagnitude < 0.04f;
                    if (!inside) continue;
                    in0 = Mathf.Min(in0, distance); in1 = Mathf.Max(in1, distance);
                    break;
                }
            }
            return in1 >= in0;
        }

        /// A train standing in (or too close to) a transition is moved clear of it, inwards.
        static float ClearOfPortals(string trackId, float position)
        {
            foreach (var portal in s_Portals)
            {
                if (portal.Track.Id != trackId) continue;
                if (portal.AtEnd && position > portal.Safe) position = portal.Safe;
                if (!portal.AtEnd && position < portal.Safe) position = portal.Safe;
            }
            return position;
        }

        /// The game reports a train car running into a transition.
        public static void TrainHitTransition(TrainCar car, LoadScene loadScene)
        {
            if (s_Transiting || car == null || loadScene == null) return;
            var liveTrain = s_Trains.Find(x => x.Train != null && x.Train.Pointer == car.Pointer);
            if (liveTrain == null) return;
            var portal = s_Portals.Find(x => x.Ls != null && x.Ls.Pointer == loadScene.Pointer && x.Track.Id == liveTrain.RouteId);
            if (portal == null) return;
            if (liveTrain.Train.IsInsideTrain) BeginTransit(liveTrain, portal, liveTrain.Sim.LineVelocity);
            else
            {
                // nobody aboard: the train stops at the transition
                liveTrain.Sim.RestoreMotion(0f);
                liveTrain.Train.m_SplineController.Position = portal.Safe;
            }
        }

        /// The player touches a transition trigger. Riding one of our trains on that line, the train goes along;
        /// the train's own colliders never set it off. Returns false to keep the game from handling it.
        public static bool AllowTransitionTrigger(LoadScene loadScene, Collider other)
        {
            if (loadScene == null) return true;
            // our own transition is under way: the game must not start a second one from the same trigger
            if (s_Transiting) return !s_Portals.Exists(portal => portal.Ls != null && portal.Ls.Pointer == loadScene.Pointer);
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
                var liveTrain = s_Trains.Find(x => x.Train != null && x.Train.Pointer == tracked.Pointer);
                if (liveTrain == null) return true;
                var portal = s_Portals.Find(x => x.Ls != null && x.Ls.Pointer == loadScene.Pointer && x.Track.Id == liveTrain.RouteId);
                if (portal == null) return true;
                BeginTransit(liveTrain, portal, liveTrain.Sim.LineVelocity);
                return false;
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Warning($"Transition trigger: {e.Message}");
                return true;
            }
        }

        /// The player drives the train into a transition: take the train along to the next region.
        static void CheckPortals(LiveTrain liveTrain)
        {
            var train = liveTrain.Train;
            if (train == null || liveTrain.Sim == null || !train.IsInsideTrain) return;
            float value = liveTrain.Sim.LineVelocity;
            if (Mathf.Abs(value) < 0.2f) return;
            float position = train.m_SplineController.Position;
            float lead = value > 0f ? position + BufferFront : position - BufferRear;
            foreach (var portal in s_Portals)
            {
                if (portal.Track.Id != liveTrain.RouteId) continue;
                bool hit = portal.AtEnd ? value > 0f && lead >= portal.S - 1f : value < 0f && lead <= portal.S + 1f;
                // a hand-made transition lies in the middle of a line: only right at it
                if (hit && portal.Custom && Mathf.Abs(lead - portal.S) > 25f) hit = false;
                if (!hit) continue;
                BeginTransit(liveTrain, portal, value);
                return;
            }
        }

        static void BeginTransit(LiveTrain liveTrain, Portal portal, float value)
        {
            s_Transiting = true;
            CaptureStates();
            TrainState state = null;
            if (s_Save.Scenes.TryGetValue(s_Scene, out var list))
            {
                state = list.Find(trainState => trainState.Guid == liveTrain.Guid);
                if (state != null) list.Remove(state);
            }
            if (state == null) { s_Transiting = false; return; }
            state.Velocity = 0f;
            s_Save.Arrival = new PendingArrival
            {
                FromScene = s_Scene,
                ToScene = portal.To,
                Train = state,
                Speed = Mathf.Abs(value),
                // chimney first? (standing still: the way that leads out through this transition)
                Forward = Mathf.Abs(value) > 0.05f ? liveTrain.Sim.Velocity > 0f : (portal.AtEnd ? 1 : -1) * liveTrain.Sim.FacingSign > 0,
                FallbackRouteId = liveTrain.RouteId,
                FallbackPos = portal.AtEnd ? portal.S - 30f : portal.S + 30f,
            };
            SteamRailwayMod.Log.Msg($"Train {liveTrain.Guid} leaves {s_Scene} for {portal.To}.");
            // The train is no longer this region's (its save must not keep it), but it keeps running with the
            // player aboard while the screen fades; the player leaves the cab only once it is black.
            s_Trains.Remove(liveTrain);
            s_Departing = liveTrain;
            try
            {
                var loadScene = portal.Ls != null ? portal.Ls : CustomPortals.MakeLoadScene(portal.To, portal.Spawn);
                if (loadScene == null) throw new System.Exception("no transition object to copy in this region");
                loadScene.Activate(true);
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
            var liveTrain = s_Departing;
            s_Departing = null;
            if (liveTrain == null) return;
            var train = liveTrain.Train;
            try
            {
                if (train != null && train.IsInsideTrain)
                {
                    train.ExitSteamTrain(train.transform.Find("ExitPoint"));
                    train.FinishExitingSteamTrainAfterFadeOut();
                }
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Leaving the cab for the transition: {e.Message}"); }
            s_Sims.Remove(liveTrain.Guid);
            try { liveTrain.Sim.Audio?.StopAll(); } catch { }
            if (train != null) Object.Destroy(train.gameObject);
        }

        /// Picks the line end the train comes in at and turns the line so the train runs the same way as before.
        static Portal PrepareArrival(PendingArrival arrival)
        {
            Portal entry = s_Portals.Find(portal => portal.To == arrival.FromScene);
            if (entry == null)
            {
                // no transition back on the line: the line end nearest to where the game put the player
                var player = GameManager.GetPlayerObject();
                Vector3 pp = player != null ? player.transform.position : Vector3.zero;
                float best = float.MaxValue;
                foreach (var track in s_Tracks)
                {
                    float d0 = Vector3.Distance(track.Path[0], pp), d1 = Vector3.Distance(track.Path[track.Path.Count - 1], pp);
                    if (d0 < best) { best = d0; entry = new Portal { Track = track, S = 0f, AtEnd = false, To = arrival.FromScene }; }
                    if (d1 < best) { best = d1; entry = new Portal { Track = track, S = track.Spline.Length, AtEnd = true, To = arrival.FromScene }; }
                }
            }
            if (entry == null) return null;

            return entry;
        }

        static void Arrive(PendingArrival arrival, Portal entry, Material material)
        {
            s_Save.Arrival = null;
            if (entry == null) { ArrivalFailed(arrival); return; }
            var trainState = arrival.Train;
            float length = entry.Track.Spline.Length;
            // the exit point of the transition: clear of its trigger, so the train does not run straight back
            float position = entry.Ls != null || entry.Custom ? entry.Safe : (entry.AtEnd ? length - 25f : 25f);
            trainState.RouteId = entry.Track.Id;
            trainState.SplinePos = Mathf.Clamp(position, 1f, length - 1f);
            // The train moves into the region: along the line + at its start, - at its end. It keeps running the
            // way it ran before (chimney first or bunker first), so it stands on the line facing accordingly.
            int inward = entry.AtEnd ? -1 : 1;
            int run = arrival.Forward ? 1 : -1;
            if (trainState.Sim == null) trainState.Sim = new SimState();
            trainState.Sim.Facing = inward * run;
            float velocity = run * arrival.Speed;
            trainState.Velocity = velocity;             // it comes in running, as it left
            Spawn(trainState, material);
            var live = s_Trains.Find(liveTrain => liveTrain.Guid == trainState.Guid);
            if (live != null) MelonCoroutines.Start(BoardAfterArrival(live, velocity));
            SteamRailwayMod.Log.Msg($"Train {trainState.Guid} arrived in {s_Scene} from {arrival.FromScene}.");
        }

        /// The game puts the player at the region's entry point while the screen is still black, then fades in.
        /// The train runs on meanwhile; the player is put aboard the moment they have been placed (they jump
        /// there, or the fade-in starts), so the fade opens on the cab of a moving train.
        static IEnumerator BoardAfterArrival(LiveTrain liveTrain, float velocity)
        {
            liveTrain.Boarding = true;
            try
            {
                var e = BoardAfterArrivalInner(liveTrain, velocity);
                while (e.MoveNext()) yield return e.Current;
            }
            finally { liveTrain.Boarding = false; liveTrain.AwaySince = -1f; liveTrain.Reboards = 0; }
        }

        static IEnumerator BoardAfterArrivalInner(LiveTrain liveTrain, float velocity)
        {
            float start = Time.realtimeSinceStartup;
            var sourceObject = GameManager.GetPlayerObject();
            Vector3 last = sourceObject != null ? sourceObject.transform.position : Vector3.zero;
            float still = 0f;
            while (Time.realtimeSinceStartup - start < 10f)
            {
                yield return null;
                if (liveTrain.Train == null) yield break;
                var player = GameManager.GetPlayerObject();
                if (player == null) continue;
                var point = player.transform.position;
                bool jumped = (point - last).sqrMagnitude > 4f;
                still = (point - last).sqrMagnitude < 0.0025f ? still + Time.unscaledDeltaTime : 0f;
                last = point;
                bool fadingIn = false;
                try { fadingIn = !CameraFade.IsFadedOut || CameraFade.IsFadingIn; } catch { }
                if (jumped || (fadingIn && Time.realtimeSinceStartup - start > 0.2f) || still > 1.5f) break;
            }

            for (int attempt = 0; attempt < 4 && liveTrain.Train != null; attempt++)
            {
                try
                {
                    SteamTrain.s_TrackedSteamTrain = liveTrain.Train;
                    if (!liveTrain.Train.IsInsideTrain) liveTrain.Train.EnterSteamTrain();
                    liveTrain.Train.FinishEnteringSteamTrainAfterFadeOut();
                    // EnterSteamTrain starts a fade whose end clears this; during a region load that fade never
                    // ends, and while it is set the game does not carry the player with the cab (they fall through)
                    liveTrain.Train.m_PendingEnterTrain = false;
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Boarding the arrived train failed: {e.Message}"); }
                for (int deepIndex = 0; deepIndex < 6; deepIndex++) yield return null;
                if (liveTrain.Train == null || PlayerInCab(liveTrain.Train)) break;
                SteamRailwayMod.Log.Warning($"Player not in the cab after boarding (attempt {attempt + 1}); trying again.");
            }
            if (liveTrain.Train == null) yield break;
            // the game may still move the player to the entry point a moment later: put them back aboard
            float watch = Time.realtimeSinceStartup;
            int reboards = 0;
            while (Time.realtimeSinceStartup - watch < 4f && liveTrain.Train != null && reboards < 3)
            {
                yield return null;
                if (liveTrain.Train == null || PlayerInCab(liveTrain.Train)) continue;
                reboards++;
                try
                {
                    SteamTrain.s_TrackedSteamTrain = liveTrain.Train;
                    if (!liveTrain.Train.IsInsideTrain) liveTrain.Train.EnterSteamTrain();
                    liveTrain.Train.FinishEnteringSteamTrainAfterFadeOut();
                    // clear the pending-enter flag here as well (see above)
                    liveTrain.Train.m_PendingEnterTrain = false;
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Boarding the arrived train again failed: {e.Message}"); }
                for (int deepIndex = 0; deepIndex < 6; deepIndex++) yield return null;
            }
            if (liveTrain.Train == null) yield break;
            // nobody aboard after all: the train must not run off on its own
            if (!PlayerInCab(liveTrain.Train)) liveTrain.Sim.RestoreMotion(0f);
        }

        /// The game believes the player is in the cab (train controls, no walking) but they are standing somewhere
        /// else: after a region change the game may move the player once more after the train took them aboard.
        /// Take them aboard again; if that keeps failing, let them out properly and stop the train.
        static void GuardCab(LiveTrain liveTrain)
        {
            var train = liveTrain.Train;
            if (train == null || liveTrain.Sim == null || liveTrain.Boarding || s_Transiting || !train.IsInsideTrain) { liveTrain.AwaySince = -1f; return; }
            bool fading = false;
            try { fading = CameraFade.IsFadingIn || CameraFade.IsFadingOut || CameraFade.IsFadedOut; } catch { }
            // aboard but still "about to board" with no fade running: the game would never carry the player along
            if (!fading && train.m_PendingEnterTrain)
            {
                SteamRailwayMod.Log.Warning("Boarding was left pending: finishing it.");
                train.m_PendingEnterTrain = false;
                train.FinishEnteringSteamTrainAfterFadeOut();
            }
            if (fading || PlayerInCab(train)) { liveTrain.AwaySince = -1f; if (PlayerInCab(train)) liveTrain.Reboards = 0; return; }
            float now = Time.realtimeSinceStartup;
            if (liveTrain.AwaySince < 0f) { liveTrain.AwaySince = now; return; }
            if (now - liveTrain.AwaySince < 1.2f) return;
            liveTrain.AwaySince = now;
            var player = GameManager.GetPlayerObject();
            string where = player != null ? player.transform.position.ToString("F1") : "?";
            if (liveTrain.Reboards < 3)
            {
                liveTrain.Reboards++;
                SteamRailwayMod.Log.Warning($"Player at {where} is not in the cab at {train.m_CameraDummy.position:F1} while aboard: taking them aboard again ({liveTrain.Reboards}).");
                SteamTrain.s_TrackedSteamTrain = train;
                train.FinishEnteringSteamTrainAfterFadeOut();
                train.m_PendingEnterTrain = false;
                return;
            }
            SteamRailwayMod.Log.Warning($"Player at {where} could not be put back into the cab: leaving the train and stopping it.");
            liveTrain.Sim.S.Regulator = 0f;
            liveTrain.Sim.RestoreMotion(0f);
            try
            {
                train.ExitSteamTrain(train.transform.Find("ExitPoint"));
                train.FinishExitingSteamTrainAfterFadeOut();
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Leaving the train failed: {e.Message}"); }
            var playerManager = GameManager.GetPlayerManagerComponent();
            if (playerManager != null && playerManager.GetControlMode() == PlayerControlMode.InSteamTrain) playerManager.SetControlMode(PlayerControlMode.Normal);
            liveTrain.Reboards = 0;
            liveTrain.AwaySince = -1f;
        }

        static bool PlayerInCab(SteamTrain train)
        {
            var player = GameManager.GetPlayerObject();
            if (player == null || train.m_CameraDummy == null) return false;
            var direction = player.transform.position - train.m_CameraDummy.position;
            return new Vector2(direction.x, direction.z).magnitude < 2f && Mathf.Abs(direction.y) < 3f;
        }

        /// The next region has no railway: the train stays behind, stopped before the transition.
        static void ArrivalFailed(PendingArrival arrival)
        {
            s_Save.Arrival = null;
            var trainState = arrival.Train;
            trainState.RouteId = arrival.FallbackRouteId;
            trainState.SplinePos = Mathf.Max(1f, arrival.FallbackPos);
            trainState.Velocity = 0f;
            if (!s_Save.Scenes.TryGetValue(arrival.FromScene, out var list)) { list = new List<TrainState>(); s_Save.Scenes[arrival.FromScene] = list; }
            list.Add(trainState);
            CabControls.Message(L.T("Bu bölgede ray yok; tren geride, geçişin önünde kaldı.",
                                    "There is no railway in this region; the train stayed behind at the transition."));
            SteamRailwayMod.Log.Msg($"{s_Scene} has no railway; train {trainState.Guid} waits in {arrival.FromScene}.");
        }

        /// Destroys and rebuilds everything in the current scene (route editor).
        /// Where each train stood before a rebuild (its line may have changed shape or name).
        static readonly Dictionary<string, Vector3> s_Remap = new Dictionary<string, Vector3>();

        static void Remap(TrainState trainState)
        {
            if (!s_Remap.TryGetValue(trainState.Guid, out var point) || s_Tracks.Count == 0) return;
            BuiltTrack best = null; float bestDistance = float.MaxValue, bestArc = 0f;
            foreach (var track in s_Tracks)
            {
                float arcLength = track.Path.NearestDistance(point, out float distance);
                if (distance < bestDistance) { bestDistance = distance; best = track; bestArc = arcLength; }
            }
            if (best == null || bestDistance > 10f) return;
            trainState.RouteId = best.Id;
            trainState.SplinePos = bestArc * best.Spline.Length / Mathf.Max(1f, best.Path.Length);
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
            foreach (var liveTrain in s_Trains) if (liveTrain.Train != null) s_Remap[liveTrain.Guid] = liveTrain.Train.transform.position;
            foreach (var liveTrain in s_Trains) if (liveTrain.Train != null) Object.Destroy(liveTrain.Train.gameObject);
            foreach (var track in s_Tracks) if (track.Root != null) Object.Destroy(track.Root);
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

        static void Spawn(TrainState state, Material material)
        {
            var track = s_Tracks.Find(t => t.Id == state.RouteId) ?? s_Tracks[0];
            // a train left standing in a transition trigger (older versions) would carry the player off again
            float clear = ClearOfPortals(track.Id, state.SplinePos);
            if (Mathf.Abs(clear - state.SplinePos) > 0.01f)
            {
                SteamRailwayMod.Log.Msg($"Train {state.Guid} stood in a region transition; moved to {clear:F0} m.");
                state.SplinePos = clear;
            }
            var train = TrainBuilder.Build(state.Guid, track.Spline, state.SplinePos, material);
            var simulation = new SteamSim(train, state.Sim);
            simulation.Fire = train.m_Boiler != null ? train.m_Boiler.m_InvisibleFire : null;
            var stoveT = train.transform.Find("Firebox");
            simulation.Stove = stoveT != null ? stoveT.GetComponent<WoodStove>() : null;
            simulation.RestoreMotion(state.Velocity);
            simulation.Animator = LocomotiveAnimator.TryCreate(train.transform);
            simulation.Audio = new TrainAudio(train);
            simulation.Smoke = CreateSmoke(train);
            s_Sims[state.Guid] = simulation;
            CabControls.ApplyRealism(train);
            s_OurGuids.Add(state.Guid);
            s_Trains.Add(new LiveTrain { Train = train, Guid = state.Guid, RouteId = track.Id, Sim = simulation });
            if (!string.IsNullOrEmpty(state.TrainData) || !string.IsNullOrEmpty(state.FireData) || (state.CabItems != null && state.CabItems.Count > 0))
                MelonCoroutines.Start(RestoreNextFrame(train, simulation, state));
        }

        /// Chimney smoke from the bundle on the game's lit particle material (taken from the firebox stove's fire);
        /// the old smoke system stays as the game's emission target but no longer renders.
        static LocoSmoke CreateSmoke(SteamTrain train)
        {
            try
            {
                var old = train.transform.Find("ChimneySmoke");
                Vector3 position = old != null ? old.localPosition : TrainConfig.ChimneyTop;
                Material lit = null;
                var stove = train.transform.Find("Firebox");
                if (stove != null)
                    foreach (var particleRenderer in stove.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    {
                        var material = particleRenderer.sharedMaterial;
                        if (material != null && material.name.StartsWith("FX_SmokeA") && material.shader != null && material.shader.name.Contains("VertexLit")) { lit = material; break; }
                    }
                var smoke = LocoSmoke.Create(train.transform, position, lit);
                if (smoke != null && old != null)
                {
                    var particleRenderer = old.GetComponent<ParticleSystemRenderer>();
                    if (particleRenderer != null) particleRenderer.enabled = false;
                }
                return smoke;
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Warning($"Chimney smoke could not be built: {e.Message}");
                return null;
            }
        }

        static IEnumerator RestoreNextFrame(SteamTrain train, SteamSim simulation, TrainState state)
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
                if (!string.IsNullOrEmpty(state.FireData) && simulation.Fire != null)
                    simulation.Fire.Deserialize(state.FireData);
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
            for (int index = 0; index < s_Trains.Count; index++) PassJunctions(s_Trains[index]);
            for (int index = 0; index < s_Trains.Count; index++)
                if (s_Trains[index].Train != null)
                {
                    try { CabItems.Update(s_Trains[index].Train.transform); }
                    catch (System.Exception e) { SteamRailwayMod.Debug($"Cab items: {e.Message}"); }
                    try { CabCarry.Update(s_Trains[index].Train); }
                    catch (System.Exception e) { SteamRailwayMod.Debug($"Cab carry: {e.Message}"); }
                }
            if (!s_Transiting)
                for (int index = 0; index < s_Trains.Count; index++) CheckPortals(s_Trains[index]);
            for (int index = 0; index < s_Trains.Count; index++)
            {
                try { GuardCab(s_Trains[index]); }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Cab guard: {e.Message}"); }
            }
            if (Time.time >= s_NextHover)
            {
                s_NextHover = Time.time + 0.2f;
                foreach (var liveTrain in s_Trains)
                {
                    if (liveTrain.Train == null || liveTrain.Sim == null) continue;
                    CabControls.UpdateHoverTexts(liveTrain.Sim);
                    CabControls.UpdateEffects(liveTrain.Sim);
                }
            }
            if (Time.time < s_NextCapture) return;
            s_NextCapture = Time.time + CaptureInterval;
            CaptureStates();
            foreach (var liveTrain in s_Trains) KeepExitPointOnGround(liveTrain.Train);
        }

        static void PassJunctions(LiveTrain liveTrain)
        {
            if (liveTrain.Train == null || liveTrain.Sim == null) return;
            float position = liveTrain.Train.m_SplineController.Position;
            float prev = liveTrain.PrevPos;
            liveTrain.PrevPos = position;
            if (float.IsNaN(prev) || Switches.All.Count == 0) return;
            var line = s_Tracks.Find(track => track.Id == liveTrain.RouteId);
            if (line == null) return;
            var moved = Switches.Pass(liveTrain, line, prev, position);
            if (moved != null) liveTrain.PrevPos = liveTrain.Train.m_SplineController.Position;
        }

        public static void SaveSwitch(string identifier, bool diverging) => s_Save.Switches[identifier] = diverging;

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
            foreach (var liveTrain in s_Trains)
            {
                if (liveTrain.Train == null) continue;
                try
                {
                    list.Add(new TrainState
                    {
                        Guid = liveTrain.Guid,
                        RouteId = liveTrain.RouteId,
                        SplinePos = liveTrain.Train.m_SplineController.Position,
                        TrainData = liveTrain.Train.SerializeTrainCarData(),
                        FireData = liveTrain.Sim != null && liveTrain.Sim.Fire != null ? liveTrain.Sim.Fire.Serialize() : null,
                        Sim = liveTrain.Sim != null ? liveTrain.Sim.S : null,
                        Velocity = liveTrain.Sim != null ? liveTrain.Sim.Velocity : 0f,
                        CabItems = CabItems.Capture(liveTrain.Train.transform),
                    });
                }
                catch (System.Exception e)
                {
                    SteamRailwayMod.Debug($"Capture of {liveTrain.Guid} failed: {e.Message}");
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
            foreach (var pair in s_Save.Scenes) total += pair.Value != null ? pair.Value.Count : 0;
            if (total <= 1) return;
            string keepScene = null;
            if (s_Save.Arrival == null)
            {
                foreach (var value in s_Save.Visited)
                    if (s_Save.Scenes.TryGetValue(value, out var points) && points != null && points.Count > 0) { keepScene = value; break; }
                if (keepScene == null)
                    foreach (var pair in s_Save.Scenes) if (pair.Value != null && pair.Value.Count > 0) { keepScene = pair.Key; break; }
            }
            foreach (var pair in s_Save.Scenes)
            {
                if (pair.Value == null) continue;
                if (pair.Key == keepScene && pair.Value.Count > 1) pair.Value.RemoveRange(1, pair.Value.Count - 1);
                else if (pair.Key != keepScene) pair.Value.Clear();
            }
            SteamRailwayMod.Log.Msg($"{total - 1} extra locomotive(s) removed; one per game is kept" + (keepScene != null ? $" (in {keepScene})." : " (the arriving one)."));
        }

        /// A new game was started: everything of the railway starts from nothing (a brand new locomotive).
        public static void OnNewGame()
        {
            s_NewGame = true;
            s_NeedLoad = true;
            SteamRailwayMod.Debug("A new save name was generated.");
        }

        static bool StoredDataBelongsTo(string json, string slot)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(slot)) return false;
            try { return JsonSerializer.Deserialize<RailwaySave>(json, s_Json)?.Slot == slot; }
            catch { return false; }
        }

        static void LoadSave()
        {
            string slot = null;
            try { slot = SaveGameSystem.GetCurrentSaveName(); } catch { }
            try
            {
                string json = s_ModData.Load();
                // A new game starts from nothing, unless this very slot already holds railway data of its own
                // (the new-name call can also come while an existing game is being entered).
                bool isNewGame = s_NewGame && !StoredDataBelongsTo(json, slot);
                if (isNewGame) json = null;
                SteamRailwayMod.Log.Msg($"Railway data of {slot}: newGame={isNewGame}, stored={(string.IsNullOrEmpty(json) ? 0 : json.Length)} chars.");
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
            foreach (var pair in s_Save.Scenes) if (pair.Value != null && pair.Value.Count > 0) s_Save.TrainCreated = true;
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
            foreach (var meshRenderer in Object.FindObjectsOfType<MeshRenderer>())
            {
                var material = meshRenderer.sharedMaterial;
                if (material == null) continue;
                if (meshRenderer.name.StartsWith("OBJ_RailTrack")) { s_TrackMaterial = material; return material; }
                if (fallback == null && material.shader != null && material.shader.name.Contains("TLD_StandardDiffuse")) fallback = material;
            }
            return fallback;
        }
    }
}
