using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        [JsonPropertyName("SplinePos")] public float SplinePosition { get; set; }
        public string TrainData { get; set; }   // SteamTrain.SerializeTrainCarData()
        public string FireData { get; set; }   // firebox fire as the game serialises it
        [JsonPropertyName("Sim")] public SteamSimulationState SimulationState { get; set; }       // boiler, tender and controls
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
        [JsonPropertyName("FallbackPos")] public float FallbackPosition { get; set; }
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
        /// Movable wagon key (scene, name, original position) -> where it stands on the lines.
        public Dictionary<string, WagonState> Wagons { get; set; } = new Dictionary<string, WagonState>();
        /// The game's one locomotive has been placed (it is never placed again in this game).
        public bool TrainCreated { get; set; }
        /// The save slot this data belongs to (data of another game is never used).
        public string Slot { get; set; }
        /// Easy or Hard, chosen when the game is started.
        public string Difficulty { get; set; }
        /// Obstacles the player has cleared in this game (see Obstacles).
        public List<string> ClearedObstacles { get; set; } = new List<string>();
        /// Broken-bridge obstacle id -> repair stages finished.
        public Dictionary<string, int> BridgeStages { get; set; } = new Dictionary<string, int>();
        /// Broken-bridge obstacle id -> the stage under way: 0 (or missing) before its materials are paid,
        /// otherwise 1 + the working minutes done.
        public Dictionary<string, int> BridgeWork { get; set; } = new Dictionary<string, int>();
    }

    /// One railway line before it is built (kept so a line can be rebuilt reversed).
    internal sealed class TrackDef
    {
        public string Identifier;
        public List<Vector3> Points;
        public List<KeyValuePair<Vector3, Vector3>> Keep;   // stretches where the original piece stays (bridges)
        public RailLine Original;                           // null for hand-made routes
        public readonly List<RailLine> Originals = new List<RailLine>();   // the region's lines in it (several when joined)
        public bool HasCustom;                              // drawn with the track editor (or joined with such a line)
    }

    /// A region transition the line runs through (the game's TransitionContact plane).
    internal sealed class Portal
    {
        public LoadScene LoadSceneComponent;
        public BuiltTrack Track;
        public float SplineMeters;        // spline metres where the line first touches the trigger, seen from inside the region
        public bool AtEnd;     // near the end of the line (else near its start)
        public float Safe;   // spline metres where a train stands clear of the trigger
        public string DestinationScene;
        public bool Custom;    // made with the track editor (no game trigger: the scene is loaded through a copy)
        public string Spawn;   // Custom: the spawn point in the other region
    }

    internal sealed class LiveTrain
    {
        public SteamTrain Train;
        public string Guid;
        public string RouteId;
        public SteamSimulation Simulation;
        public float PreviousPosition = float.NaN;   // spline metres last frame (junctions)
        public bool Boarding;                // BoardAfterArrival is running
        public float AwaySince = -1f;        // realtime the player was first seen outside the cab while "inside"
        public int Reboards;
        public float DoorStuckSince = -1f;   // realtime the door was first seen "in use" with no fade running
        public Transform MeasuredModel;     // the model the body reach below was measured on (rebuilt models are measured again)
        public float BodyFrontInMeters, BodyRearInMeters;   // how far the model reaches from the pivot, forward and backward (world metres)
        // The wagon coupler shown locked into the engine's at each end (CouplerPartner_*), and how far from the pivot its far
        // end lies (world metres): a coupled wagon's own coupler is hidden and the wagon stands where that copy mounts.
        public GameObject CouplerPartnerFront, CouplerPartnerRear;
        public float LockedMountFrontInMeters, LockedMountRearInMeters;
        public bool FrontLockedThisFrame, RearLockedThisFrame, PartnerMaterialApplied;
        /// The saved state being put back into a freshly built train one frame after it is built;
        /// until then a save writes that state instead of the empty new train.
        public TrainState PendingRestore;
        public GameObject RearClawFree, RearClawLocked;
    }

    /// Builds the tracks and trains of the active scene and keeps their state in the save game (via ModData).
    internal static class RailwayManager
    {
        const float SwitchPointJoinRadiusInMeters = 1.5f;
        /// Lines shorter than this get no snow lowered along them (stubs left from cutting a line).
        const float MinimumCarvedTrackLengthInMeters = 8f;
        const float BranchSampleLengthInMeters = 32f;
        const float MaximumStartDistanceInMeters = 30f;
        const int Vector3ComponentCount = 3;
        const float MinimumDirectionSquaredLength = 0.01f;
        const float PortalLateralToleranceInMeters = 4f;
        const float PortalHeightToleranceInMeters = 5f;
        const float PortalAlongToleranceInMeters = 10f;
        const float ArrivalWaitTimeoutInSeconds = 10f;
        const float JumpSquaredDistanceInSquareMeters = 4f;
        const float StillSquaredDistanceInSquareMeters = 0.0025f;
        const float MinimumFadeInSeconds = 0.2f;
        const float StillSecondsBeforeContinue = 1.5f;
        const float WalkerPlacementDistanceInMeters = 6f;
        const float GroundProbeLiftInMeters = 1.5f;
        const float GroundProbeDropInMeters = 6f;
        const float WalkerGroundLiftInMeters = 0.05f;
        const int WalkerPlacementAttemptCount = 3;
        const int WalkerSettleFrameCount = 30;
        const float WalkerAcceptRadiusInMeters = 5f;
        const float PortalMaximumDistanceInMeters = 18f;
        const float PortalEdgeDistanceInMeters = 300f;
        const float NearestLineRadiusInMeters = 6f;
        const int TouchSampleHeightCount = 3;
        const float TouchSampleHeightStepInMeters = 1.5f;
        const float TouchInsideSquaredDistanceInSquareMeters = 0.04f;
        const float MinimumPortalVelocityInMetersPerSecond = 0.2f;
        const float CustomPortalReachInMeters = 25f;
        const float MinimumFacingVelocityInMetersPerSecond = 0.05f;
        const float PortalFallbackDistanceInMeters = 30f;
        const float PortalExitDistanceInMeters = 25f;
        const int BoardAttemptCount = 4;
        const int BoardSettleFrameCount = 6;
        const float BoardWatchSeconds = 4f;
        const int MaximumReboardCount = 3;
        const float AwaySecondsBeforeReboard = 1.2f;
        const float BoardedHeightToleranceInMeters = 3f;
        const float NearestTrackRadiusInMeters = 10f;
        const float MinimumPathLengthInMeters = 0.01f;
        const float PortalClearToleranceInMeters = 0.01f;
        const float HoverIntervalInSeconds = 0.2f;
        const float DefaultSpawnMeters = 15f;
        const float CaptureInterval = 2f;

        static readonly ModDataManager s_ModData = new ModDataManager("SteamRailway", false);
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions();
        static RailwaySave s_Save = new RailwaySave();

        /// How this game is played, chosen when it was started.
        public static Difficulty Difficulty =>
            s_Save != null && s_Save.Difficulty == Obstacles.HardName ? Difficulty.Hard : Difficulty.Easy;

        public static bool IsObstacleCleared(string obstacleIdentifier) => s_Save.ClearedObstacles != null && s_Save.ClearedObstacles.Contains(obstacleIdentifier);

        public static void MarkObstacleCleared(string obstacleIdentifier)
        {
            if (s_Save.ClearedObstacles == null) s_Save.ClearedObstacles = new List<string>();
            if (!s_Save.ClearedObstacles.Contains(obstacleIdentifier)) s_Save.ClearedObstacles.Add(obstacleIdentifier);
        }

        public static void MarkObstacleHeld(string obstacleIdentifier) { s_Save.ClearedObstacles?.Remove(obstacleIdentifier); s_Save.BridgeStages?.Remove(obstacleIdentifier); s_Save.BridgeWork?.Remove(obstacleIdentifier); }

        public static int BridgeStage(string obstacleIdentifier) => s_Save.BridgeStages != null && s_Save.BridgeStages.TryGetValue(obstacleIdentifier, out int stage) ? stage : 0;

        public static int BridgeWork(string obstacleIdentifier) => s_Save.BridgeWork != null && s_Save.BridgeWork.TryGetValue(obstacleIdentifier, out int work) ? work : 0;

        public static void SetBridgeWork(string obstacleIdentifier, int work)
        {
            if (s_Save.BridgeWork == null) s_Save.BridgeWork = new Dictionary<string, int>();
            s_Save.BridgeWork[obstacleIdentifier] = work;
        }

        public static void SetBridgeStage(string obstacleIdentifier, int stage)
        {
            if (s_Save.BridgeStages == null) s_Save.BridgeStages = new Dictionary<string, int>();
            s_Save.BridgeStages[obstacleIdentifier] = stage;
        }

        static string s_Scene;
        static readonly List<BuiltTrack> s_Tracks = new List<BuiltTrack>();
        static readonly List<LiveTrain> s_Trains = new List<LiveTrain>();
        static readonly HashSet<string> s_OurGuids = new HashSet<string>();
        static readonly List<TrackDef> s_TrackDefinitions = new List<TrackDef>();
        static readonly List<Portal> s_Portals = new List<Portal>();
        static float s_NextCapture;
        static bool s_NeedLoad = true;      // read the save slot again after the main menu
        static bool s_Transiting;

        public static string CurrentScene => s_Scene;
        public static IReadOnlyList<BuiltTrack> Tracks => s_Tracks;

        static readonly Dictionary<string, SteamSimulation> s_Sims = new Dictionary<string, SteamSimulation>();
        static float s_NextHover;

        public static bool IsOurTrain(TrainCar car) => car != null && car.name.StartsWith(TrainBuilder.NamePrefix);
        public static bool IsOurGuid(string guid) => guid != null && s_OurGuids.Contains(guid);

        public static SteamSimulation GetSimulation(TrainCar car)
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
            Wagons.Clear();
            BridgeSite.Clear();
            s_Tracks.Clear();
            s_Trains.Clear();
            s_Sims.Clear();
            s_Scene = sceneName;
            MelonCoroutines.Start(BuildSceneDelayed(sceneName));
        }

        public static void OnSceneUnloaded(string sceneName)
        {
            if (sceneName != s_Scene) return;
            // the wagons' places stay in memory for the next visit (written to the slot with the next save)
            if (s_Save.Wagons == null) s_Save.Wagons = new Dictionary<string, WagonState>();
            Wagons.Capture(s_Save.Wagons);
            Wagons.Clear();
            BridgeSite.Clear();
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
            s_BlockingStretches.Clear();
            s_TreeStretches.Clear();
            try { Obstacles.Load(s_Scene); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Reading the obstacles failed: {exception}"); }
            // the mod's object edits of this region first: the railway is laid in the changed region
            try { ObjectEdits.ApplyScene(s_Scene); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Applying the object edits failed: {exception}"); }
            // tunnel mouths placed with the track editor
            try { Tunnels.ApplyScene(s_Scene); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Placing the tunnels failed: {exception}"); }
            // the hand-made ground changes (once per loaded region)
            try { TerrainEdits.ApplyScene(s_Scene); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Applying the terrain edits failed: {exception}"); }
            var material = FindTrackMaterial();

            // The region's own railway comes first; hand-made routes (routes.json) only where a region has none.
            s_TrackDefinitions.Clear();
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
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Reading the region's rails failed: {exception}"); }
            if (lines.Count > 0)
            {
                for (int index = 0; index < lines.Count; index++)
                {
                    var trackDef = new TrackDef { Identifier = $"{s_Scene}_rail{index + 1}", Points = new List<Vector3>(lines[index].Points), Keep = lines[index].Keep, Original = lines[index] };
                    trackDef.Originals.Add(lines[index]);
                    s_TrackDefinitions.Add(trackDef);
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
                        s_TrackDefinitions.Add(new TrackDef { Identifier = route.Identifier, Points = points, Keep = null });
                    }
            }
            // the lines drawn with the track editor; one meeting another line's end becomes one line with it
            try
            {
                CustomLines.AddDefs(s_Scene, s_TrackDefinitions);
                CustomLines.Merge(s_TrackDefinitions);
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Adding the drawn lines failed: {exception}"); }
            foreach (var trackDef in s_TrackDefinitions)
            {
                // one direction for each line (the pieces are found in no particular order)
                var start = trackDef.Points[0]; var end = trackDef.Points[trackDef.Points.Count - 1];
                if (start.x + start.z > end.x + end.z) trackDef.Points.Reverse();
            }
            if (Switches.Automatic)
            {
                // branches start exactly in the main line's rails
                try { Switches.SnapBranches(s_TrackDefinitions); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Snapping the branches failed: {exception.Message}"); }
            }
            else
            {
                // no junctions for now: a line branching off another one is not built (its original pieces stay)
                try
                {
                    foreach (var identifier in Switches.Branches(s_TrackDefinitions))
                    {
                        s_TrackDefinitions.RemoveAll(trackDef => trackDef.Identifier == identifier);
                        SteamRailwayMod.Debug($"{identifier} branches off another line; left as it is (no switch).");
                    }
                }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Finding the branch lines failed: {exception.Message}"); }
            }
            // switches: only where a drawn line was joined to the middle of a line
            var switchPoints = CustomLines.SwitchPoints(s_Scene);
            // the points lie level: a branch leaves at the main line's height
            if (switchPoints.Count > 0)
            {
                try { Switches.LevelBranches(s_TrackDefinitions, switchPoints); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Levelling the branches failed: {exception.Message}"); }
            }
            foreach (var trackDef in s_TrackDefinitions)
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
                        (System.Func<Vector3, bool>)(point => switchPoints.Exists(otherPoint => (otherPoint - point).sqrMagnitude < SwitchPointJoinRadiusInMeters * SwitchPointJoinRadiusInMeters)));
                }
                catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Finding the junctions failed: {exception}"); }
            }
            OriginalRails.BeginClear();
            foreach (var track in s_Tracks)
            {
                try { TrackBuilder.LayRails(track); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Laying the rails of {track.Identifier} failed: {exception}"); }
                var trackDef = s_TrackDefinitions.Find(x => x.Identifier == track.Identifier);
                if (trackDef != null && trackDef.Originals.Count > 0) OriginalRails.ClearLine(track);
            }
            OriginalRails.EndClear();
            Switches.RailsLaid();
            InstancedHider.Apply();
            try { Wagons.Attach(s_Scene, s_Tracks, s_Save.Wagons); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Putting the wagons on the lines failed: {exception}"); }
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
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Snow embankment failed: {exception.Message}"); }
            }
            // under the points the branch was raised to the main line's height: snow under it there too
            foreach (var trackSwitch in Switches.All)
            {
                try
                {
                    var points = new List<Vector3>();
                    for (float distanceAlongBranch = 0f; distanceAlongBranch <= BranchSampleLengthInMeters; distanceAlongBranch += 2f)
                    {
                        float secondArc = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * distanceAlongBranch;
                        if (secondArc < 0f || secondArc > trackSwitch.BranchTrack.Path.Length) break;
                        trackSwitch.BranchTrack.Path.Sample(secondArc, out var otherPoint, out _);
                        points.Add(otherPoint);
                    }
                    if (points.Count >= 2) TerrainCarve.Fill(CurvePath.CatmullRom(points));
                }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Snow under the points failed: {exception.Message}"); }
            }
            foreach (var track in s_Tracks)
            {
                // a stub left from cutting a line cannot be driven on; carving round it would dig a round pit
                if (track.Path.Length < MinimumCarvedTrackLengthInMeters) continue;
                try { TerrainCarve.Lower(track.Path); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Lowering the snow along {track.Identifier} failed: {exception.Message}"); }
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
                    var trainState = new TrainState { Guid = System.Guid.NewGuid().ToString(), RouteId = s_Tracks[0].Identifier, SplinePosition = DefaultSpawnMeters };
                    if (start != null) PlaceAtStart(trainState, start);
                    Spawn(trainState, material);
                }
            }
            s_Remap.Clear();
            if (arrival != null) Arrive(arrival, entry, material);
            try { Wagons.PlaceStrayArrivals(s_Scene, s_Tracks, s_Save.Wagons, s_Trains); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Placing the wagons that rolled in failed: {exception}"); }
            if (s_WalkToScene != null && s_WalkToScene == s_Scene)
            {
                MelonCoroutines.Start(PlaceWalker(s_WalkToPosition, s_WalkToDirection));
                s_WalkToScene = null;
            }
            SteamRailwayMod.Log.Msg($"{s_Scene}: {s_Tracks.Count} track(s), {s_Trains.Count} train(s), {s_Portals.Count} rail transition(s).");
        }

        /// The new game's locomotive: on the line nearest to the start point, chimney facing the start direction,
        /// boiler and tender full, everything in order.
        static void PlaceAtStart(TrainState trainState, StartPoint start)
        {
            var point = new Vector3(start.Position[0], start.Position[1], start.Position[2]);
            BuiltTrack best = null; float bestDistance = float.MaxValue, bestArc = 0f;
            foreach (var track in s_Tracks)
            {
                float speed = track.Path.NearestDistance(point, out float distance);
                if (distance < bestDistance) { bestDistance = distance; best = track; bestArc = speed; }
            }
            if (best == null) return;
            if (bestDistance > MaximumStartDistanceInMeters) SteamRailwayMod.Log.Warning($"The start point is {bestDistance:F0} m from the nearest line; the train stands on the line anyway.");
            trainState.RouteId = best.Identifier;
            trainState.SplinePosition = Mathf.Clamp(bestArc * best.Spline.Length / Mathf.Max(1f, best.Path.Length), 2f, best.Spline.Length - 2f);
            best.Path.Sample(bestArc, out _, out var tangent);
            var direction = start.Direction != null && start.Direction.Length >= Vector3ComponentCount ? new Vector3(start.Direction[0], 0f, start.Direction[2]) : Vector3.zero;
            int facing = direction.sqrMagnitude < MinimumDirectionSquaredLength || Vector3.Dot(new Vector3(tangent.x, 0f, tangent.z), direction) >= 0f ? 1 : -1;
            trainState.SimulationState = new SteamSimulationState
            {
                Facing = facing,
                BoilerWater = SteamSimulation.BoilerCapacity,
                TenderWater = SteamSimulation.TenderCapacity,
                BoilerCondition = 100f,
                ValveCondition = 100f,
            };
            SteamRailwayMod.Log.Msg($"New game: the locomotive stands on {best.Identifier} at {trainState.SplinePosition:F0} m, chimney {(facing > 0 ? "towards the end" : "towards the start")} of the line, boiler and tender full.");
        }

        static BuiltTrack BuildTrack(TrackDef trackDef)
        {
            var points = new List<Vector3>(trackDef.Points);
            if (s_Save.Reversed.TryGetValue(trackDef.Identifier, out bool reversed) && reversed) points.Reverse();
            var built = TrackBuilder.Build(trackDef.Identifier, points, trackDef.Keep);
            if (built != null) foreach (var line in trackDef.Originals) OriginalRails.HideReplaced(line);
            return built;
        }

        // ---------------------------------------------------------------- region transitions

        /// The game's region transitions (TransitionContact planes) the lines run through near their ends.
        /// Hand-made transitions the player can walk through too (like the game's own).
        sealed class WalkPortal
        {
            public Vector3 Position, Out;
            public PortalEnd There;
            public float PrevAlong = float.NaN;
        }

        static readonly List<WalkPortal> s_Walk = new List<WalkPortal>();
        static string s_WalkToScene;
        static Vector3 s_WalkToPosition, s_WalkToDirection;

        static void CheckWalkPortals()
        {
            if (s_Transiting || s_Walk.Count == 0 || SteamTrain.IsInsideSteamTrain) return;
            var player = GameManager.GetPlayerObject();
            if (player == null) return;
            var playerPosition = player.transform.position;
            foreach (var walkPortal in s_Walk)
            {
                var direction = playerPosition - walkPortal.Position;
                float along = Vector3.Dot(direction, walkPortal.Out);
                float lateral = Mathf.Abs(Vector3.Dot(direction, Vector3.Cross(Vector3.up, walkPortal.Out)));
                if (lateral > PortalLateralToleranceInMeters || Mathf.Abs(direction.y) > PortalHeightToleranceInMeters || Mathf.Abs(along) > PortalAlongToleranceInMeters) { walkPortal.PrevAlong = float.NaN; continue; }
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
            ArrivalGuard.Begin();
            s_WalkToScene = walkPortal.There.Scene;
            s_WalkToPosition = CustomLines.ToVector3(walkPortal.There.Position);
            var outwardDirection = CustomLines.ToVector3(walkPortal.There.Out); outwardDirection.y = 0f;
            s_WalkToDirection = -outwardDirection.normalized;                     // into the other region, away from its portal
            SteamRailwayMod.Log.Msg($"On foot through the rail transition to {walkPortal.There.Scene}.");
            try
            {
                var loadScene = CustomPortals.TransitionTo(walkPortal.There.Scene, walkPortal.There.Spawn);
                if (loadScene == null) throw new System.Exception("no transition object to copy in this region");
                loadScene.Activate(true);
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Error($"Region transition failed: {exception.Message}");
                s_Transiting = false;
                s_WalkToScene = null;
                ArrivalGuard.End();
            }
        }

        /// The game puts the player at a spawn point of the region; they are moved to the transition's end.
        static IEnumerator PlaceWalker(Vector3 position, Vector3 direction)
        {
            // the player is held still from the transition until placed (see ArrivalGuard), however the placement ends
            try
            {
                var placement = PlaceWalkerSteps(position, direction);
                while (placement.MoveNext()) yield return placement.Current;
            }
            finally { ArrivalGuard.End(); }
        }

        static IEnumerator PlaceWalkerSteps(Vector3 position, Vector3 direction)
        {
            float start = Time.realtimeSinceStartup;
            var sourceObject = GameManager.GetPlayerObject();
            Vector3 last = sourceObject != null ? sourceObject.transform.position : Vector3.zero;
            float still = 0f;
            while (Time.realtimeSinceStartup - start < ArrivalWaitTimeoutInSeconds)
            {
                yield return null;
                var player = GameManager.GetPlayerObject();
                if (player == null) continue;
                var point = player.transform.position;
                bool jumped = (point - last).sqrMagnitude > JumpSquaredDistanceInSquareMeters;
                still = (point - last).sqrMagnitude < StillSquaredDistanceInSquareMeters ? still + Time.unscaledDeltaTime : 0f;
                last = point;
                bool fadingIn = false;
                try { fadingIn = !CameraFade.IsFadedOut || CameraFade.IsFadingIn; } catch { }
                if (jumped || (fadingIn && Time.realtimeSinceStartup - start > MinimumFadeInSeconds) || still > StillSecondsBeforeContinue) break;
            }
            // on the ground at the line's height (not on top of a rock or a tunnel roof above it)
            var flat = position + direction * WalkerPlacementDistanceInMeters;
            float groundY = float.MinValue;
            foreach (var raycastHit in Physics.RaycastAll(new Vector3(flat.x, position.y + GroundProbeLiftInMeters, flat.z), Vector3.down, GroundProbeDropInMeters, ~0, QueryTriggerInteraction.Ignore))
            {
                if (raycastHit.collider.isTrigger || raycastHit.point.y <= groundY) continue;
                bool ours = false;
                for (var node = raycastHit.collider.transform; node != null; node = node.parent) if (node.name.StartsWith("CHARACTER_FPSPlayer")) { ours = true; break; }
                if (!ours) groundY = raycastHit.point.y;
            }
            if (groundY == float.MinValue) groundY = position.y - RailPieces.RailTop;
            var target = new Vector3(flat.x, groundY + WalkerGroundLiftInMeters, flat.z);
            var rotation = Quaternion.LookRotation(direction.sqrMagnitude > MinimumDirectionSquaredLength ? direction : Vector3.forward, Vector3.up);
            for (int attempt = 0; attempt < WalkerPlacementAttemptCount; attempt++)
            {
                try { GameManager.GetPlayerManagerComponent().TeleportPlayer(target, rotation); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Placing the player failed: {exception.Message}"); yield break; }
                for (int deepIndex = 0; deepIndex < WalkerSettleFrameCount; deepIndex++) yield return null;
                var player = GameManager.GetPlayerObject();
                if (player == null || (player.transform.position - target).magnitude < WalkerAcceptRadiusInMeters) yield break;
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
                    float speed = track.Path.NearestDistance(loadScene.transform.position, out float horizontalDistance);
                    float length = track.Path.Length;
                    // where the line really runs through the trigger (a plane's centre may lie metres to the side)
                    bool touches = Touches(track, collider, out float in0, out float in1);
                    if (!touches && horizontalDistance > PortalMaximumDistanceInMeters) continue;
                    bool atEnd = touches ? (in0 + in1) * 0.5f > length * 0.5f : speed > length * 0.5f;
                    float edge = touches ? (atEnd ? in0 : in1) : speed;
                    if (atEnd ? edge < length - PortalEdgeDistanceInMeters : edge > PortalEdgeDistanceInMeters) continue;
                    // path metres -> spline metres (the two curves are nearly the same length)
                    float scale = track.Spline.Length / Mathf.Max(1f, length);
                    float arcLength = edge * scale;
                    float safe = atEnd ? arcLength - BufferFront - ClearOfTrigger : arcLength + BufferFront + ClearOfTrigger;
                    safe = Mathf.Clamp(safe, 2f, track.Spline.Length - 2f);
                    s_Portals.Add(new Portal { LoadSceneComponent = loadScene, Track = track, SplineMeters = arcLength, AtEnd = atEnd, DestinationScene = loadScene.m_SceneToLoad, Safe = safe });
                    SteamRailwayMod.Debug($"Rail transition to {loadScene.m_SceneToLoad} on {track.Identifier} at {arcLength:F0} m ({(atEnd ? "end" : "start")}, {(touches ? "trigger crossed" : "trigger beside the line")}), trains stand at {safe:F0} m.");
                }
            }
            // the transitions made with the track editor: a point on a line and the way out through it
            foreach (var (here, there) in CustomPortals.Ends(s_Scene))
            {
                var point = CustomLines.ToVector3(here.Position);
                var outwardDirection = CustomLines.ToVector3(here.Out);
                BuiltTrack best = null; float bestDistance = NearestLineRadiusInMeters, bestArc = 0f;
                foreach (var track in s_Tracks)
                {
                    float speed = track.Path.NearestDistance(point, out float horizontalDistance);
                    if (horizontalDistance < bestDistance) { bestDistance = horizontalDistance; best = track; bestArc = speed; }
                }
                if (best == null) { SteamRailwayMod.Log.Warning($"Transition to {there.Scene}: no line at {point}."); continue; }
                best.Path.Sample(bestArc, out _, out var tangent);
                bool forward = Vector3.Dot(tangent, outwardDirection) > 0f;          // leaves towards the line's end
                float scale = best.Spline.Length / Mathf.Max(1f, best.Path.Length);
                float arcLength = bestArc * scale;
                float safe = Mathf.Clamp(forward ? arcLength - BufferFront - ClearOfTrigger : arcLength + BufferFront + ClearOfTrigger, 2f, best.Spline.Length - 2f);
                s_Portals.Add(new Portal { Track = best, SplineMeters = arcLength, AtEnd = forward, DestinationScene = there.Scene, Safe = safe, Custom = true, Spawn = there.Spawn });
                best.Path.Sample(bestArc, out var onLine, out _);
                var flatOut = outwardDirection; flatOut.y = 0f;
                s_Walk.Add(new WalkPortal { Position = onLine, Out = flatOut.normalized, There = there });
                SteamRailwayMod.Log.Msg($"Hand-made rail transition to {there.Scene} on {best.Identifier} at {arcLength:F0} m, trains stand at {safe:F0} m.");
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
                for (int height = 0; height < TouchSampleHeightCount; height++)
                {
                    var otherPoint = point + Vector3.up * (0.5f + height * TouchSampleHeightStepInMeters);
                    if (!bounds.Contains(otherPoint)) continue;
                    bool inside = !exact || (collider.ClosestPoint(otherPoint) - otherPoint).sqrMagnitude < TouchInsideSquaredDistanceInSquareMeters;
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
                if (portal.Track.Identifier != trackId) continue;
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
            var portal = s_Portals.Find(x => x.LoadSceneComponent != null && x.LoadSceneComponent.Pointer == loadScene.Pointer && x.Track.Identifier == liveTrain.RouteId);
            if (portal == null) return;
            if (liveTrain.Train.IsInsideTrain) BeginTransit(liveTrain, portal, liveTrain.Simulation.LineVelocity);
            else
            {
                // nobody aboard: the train stops at the transition
                liveTrain.Simulation.RestoreMotion(0f);
                liveTrain.Train.m_SplineController.Position = portal.Safe;
            }
        }

        /// The player touches a transition trigger. Riding one of our trains on that line, the train goes along;
        /// the train's own colliders never set it off. Returns false to keep the game from handling it.
        public static bool AllowTransitionTrigger(LoadScene loadScene, Collider other)
        {
            if (loadScene == null) return true;
            // our own transition is under way: the game must not start a second one from the same trigger
            if (s_Transiting) return !s_Portals.Exists(portal => portal.LoadSceneComponent != null && portal.LoadSceneComponent.Pointer == loadScene.Pointer);
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
                var portal = s_Portals.Find(x => x.LoadSceneComponent != null && x.LoadSceneComponent.Pointer == loadScene.Pointer && x.Track.Identifier == liveTrain.RouteId);
                if (portal == null) return true;
                BeginTransit(liveTrain, portal, liveTrain.Simulation.LineVelocity);
                return false;
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Warning($"Transition trigger: {exception.Message}");
                return true;
            }
        }

        /// The player drives the train into a transition: take the train along to the next region.
        static void CheckPortals(LiveTrain liveTrain)
        {
            var train = liveTrain.Train;
            if (train == null || liveTrain.Simulation == null || !train.IsInsideTrain) return;
            float value = liveTrain.Simulation.LineVelocity;
            if (Mathf.Abs(value) < MinimumPortalVelocityInMetersPerSecond) return;
            float position = train.m_SplineController.Position;
            float lead = value > 0f ? position + BufferFront : position - BufferRear;
            // coupled wagons running ahead reach the transition first: the consist goes through when they do
            float wagonsLead = Wagons.ConsistLeadingEnd(liveTrain, s_Trains, liveTrain.RouteId, value > 0f ? 1 : -1);
            if (!float.IsNaN(wagonsLead)) lead = value > 0f ? Mathf.Max(lead, wagonsLead) : Mathf.Min(lead, wagonsLead);
            foreach (var portal in s_Portals)
            {
                if (portal.Track.Identifier != liveTrain.RouteId) continue;
                bool reachedPortal = portal.AtEnd ? value > 0f && lead >= portal.SplineMeters - 1f : value < 0f && lead <= portal.SplineMeters + 1f;
                // a hand-made transition lies in the middle of a line: only right at it
                if (reachedPortal && portal.Custom && Mathf.Abs(lead - portal.SplineMeters) > CustomPortalReachInMeters) reachedPortal = false;
                if (!reachedPortal) continue;
                if (Wagons.HasCoupledWagons(liveTrain) && !Wagons.CanTravel(liveTrain, s_Trains, out string wagonReason)) { Wagons.RefuseTransit(liveTrain, wagonReason); return; }
                BeginTransit(liveTrain, portal, value);
                return;
            }
        }

        /// A train's or wagon's leading end counts as in a transition this far before the trigger's edge (spline metres).
        const float PortalLeadToleranceInMeters = 1f;

        /// A free-rolling wagon's leading end on its line reached a transition going out (as CheckPortals tests a train):
        /// that transition, or null.
        public static Portal StrayPortalReached(BuiltTrack track, float lead, float velocity)
        {
            if (s_Transiting || track == null || Mathf.Abs(velocity) < MinimumPortalVelocityInMetersPerSecond) return null;
            for (int index = 0; index < s_Portals.Count; index++)
            {
                var portal = s_Portals[index];
                if (portal.Track.Identifier != track.Identifier) continue;
                bool reachedPortal = portal.AtEnd ? velocity > 0f && lead >= portal.SplineMeters - PortalLeadToleranceInMeters
                                                  : velocity < 0f && lead <= portal.SplineMeters + PortalLeadToleranceInMeters;
                if (reachedPortal && portal.Custom && Mathf.Abs(lead - portal.SplineMeters) > CustomPortalReachInMeters) reachedPortal = false;
                if (reachedPortal) return portal;
            }
            return null;
        }

        /// The transition on this region's lines that leads to the given region, or null.
        public static Portal PortalTo(string scene)
        {
            for (int index = 0; index < s_Portals.Count; index++)
                if (s_Portals[index].DestinationScene == scene) return s_Portals[index];
            return null;
        }

        const float ObstacleStopSpeed = 0.2f;
        const float TreeSpeedKept = 0.8f;
        /// A tree gives way: the shake is lighter than a crash against rock or metal.
        const float TreeShakeShare = 0.45f;
        const float ObstacleMessageInterval = 6f;
        static float s_NextObstacleMessage;

        /// A train that runs into an obstacle still in the way stops dead in front of it.
        static void StopAtObstacle(LiveTrain liveTrain)
        {
            if (!Obstacles.HardMode || liveTrain.Train == null || liveTrain.Simulation == null) return;
            float velocity = liveTrain.Simulation.LineVelocity;
            if (Mathf.Abs(velocity) < ObstacleStopSpeed) return;
            var track = FindTrack(liveTrain.RouteId);
            if (track == null) return;
            float position = liveTrain.Train.m_SplineController.Position;
            float lead = velocity > 0f ? position + BufferFront : position - BufferRear;
            lead = Mathf.Clamp(lead, 0f, track.Path.Length);
            track.Path.Sample(lead, out var leadPoint, out _);
            var obstacle = Obstacles.BlockingAt(leadPoint.x, leadPoint.z);
            if (obstacle != null)
            {
                // a rockfall does not stop the train at its circle: it runs on to the first rock that lies on the rails
                // (a fallen tree inside the circle is still met on the way)
                if (Obstacles.KindOf(obstacle) != ObstacleKind.Rockfall || ReachedFirstRock(obstacle, track, lead, position, velocity))
                {
                    float crashSpeed = Mathf.Abs(velocity);
                    liveTrain.Simulation.RestoreMotion(0f);
                    CrashFeedback(leadPoint, crashSpeed);
                    if (Time.time < s_NextObstacleMessage) return;
                    s_NextObstacleMessage = Time.time + ObstacleMessageInterval;
                    CabControls.Message(Localizer.Translate("Yol kapalı: önünde bir engel var.", "The line is blocked: there is an obstacle ahead."));
                    return;
                }
            }
            var tree = TreeOnLine(track, lead, leadPoint);
            if (tree == null) return;
            Obstacles.ClearTree(tree);
            // the engine's own speed (its sign is not the line direction's when the train stands turned round) keeps most of its value
            liveTrain.Simulation.RestoreMotion(liveTrain.Simulation.Velocity * TreeSpeedKept);
            float knockStrength = Mathf.Clamp01(Mathf.Abs(velocity) / CrashSpeedForFullStrength);
            WorldSounds.PlayTreeHit(leadPoint, knockStrength);
            var knocked = GameManager.GetPlayerObject();
            float knockDistance = knocked != null ? Vector3.Distance(knocked.transform.position, leadPoint) : float.MaxValue;
            WorldSounds.ShakeCamera(TreeShakeShare * knockStrength * Mathf.Clamp01(1f - knockDistance / CrashShakeRangeInMeters));
            CabControls.Message(Localizer.Translate("Devrilmiş ağaca çarptın; yoldan kalktı.", "You ran into a fallen tree; it is cleared from the line."));
        }

        /// A rolling wagon's leading end on its line (spline metres): a marked tree there is knocked away and the wagon keeps
        /// most of its speed; a rockfall stops it. Returns the wagon's new line velocity.
        public static float WagonAgainstObstacles(BuiltTrack track, float position, float lead, float velocity)
        {
            if (!Obstacles.HardMode || track == null || Mathf.Abs(velocity) < ObstacleStopSpeed) return velocity;
            lead = Mathf.Clamp(lead, 0f, track.Path.Length);
            track.Path.Sample(lead, out var leadPoint, out _);
            var obstacle = Obstacles.BlockingAt(leadPoint.x, leadPoint.z);
            if (obstacle != null && (Obstacles.KindOf(obstacle) != ObstacleKind.Rockfall || ReachedFirstRock(obstacle, track, lead, position, velocity)))
            {
                CrashFeedback(leadPoint, Mathf.Abs(velocity));
                return 0f;
            }
            var tree = TreeOnLine(track, lead, leadPoint);
            if (tree == null) return velocity;
            Obstacles.ClearTree(tree);
            WorldSounds.PlayTreeHit(leadPoint, Mathf.Clamp01(Mathf.Abs(velocity) / CrashSpeedForFullStrength));
            return velocity * TreeSpeedKept;
        }

        /// Where each marked tree lies across a line (tree key | line id -> stretch; NaN: it does not touch the line), found once.
        static readonly Dictionary<string, Vector2> s_TreeStretches = new Dictionary<string, Vector2>();
        const float TreeContactMarginInMeters = 0.5f;

        /// The marked tree (not yet knocked away) the train's nose runs into: where the tree's own trunk lies across the rails.
        /// A tree whose objects or colliders cannot be found counts by the distance to its marked point, as before.
        static MarkedTreeData TreeOnLine(BuiltTrack track, float lead, Vector3 leadPoint)
        {
            if (!Obstacles.HardMode) return null;
            var trees = Obstacles.Trees;
            var objects = new List<Transform>();
            for (int index = 0; index < trees.Count; index++)
            {
                var tree = trees[index];
                if (tree.Position == null || tree.Position.Length < Vector3ComponentCount || Obstacles.IsTreeCleared(tree.Name, tree.Position)) continue;
                string key = tree.Name + "@" + tree.Position[0].ToString("F1") + "," + tree.Position[2].ToString("F1") + "|" + track.Identifier;
                if (!s_TreeStretches.TryGetValue(key, out var stretch))
                {
                    objects.Clear();
                    stretch = new Vector2(float.NaN, float.NaN);
                    if (ObjectEdits.TryGetMarkedTreeObjects(tree.Name, tree.Position, objects) && OriginalRails.TreeStretch(track, objects, out var found)) stretch = found;
                    else if (objects.Count == 0) stretch = new Vector2(float.NegativeInfinity, float.NegativeInfinity);   // unknown: by distance
                    s_TreeStretches[key] = stretch;
                }
                if (float.IsNegativeInfinity(stretch.x))
                {
                    float deltaX = leadPoint.x - tree.Position[0], deltaZ = leadPoint.z - tree.Position[2];
                    if (deltaX * deltaX + deltaZ * deltaZ <= tree.Reach * tree.Reach) return tree;
                    continue;
                }
                if (float.IsNaN(stretch.x)) continue;   // it lies beside this line
                if (lead >= stretch.x - TreeContactMarginInMeters && lead <= stretch.y + TreeContactMarginInMeters) return tree;
            }
            return null;
        }

        /// The blocking rocks of an obstacle on a line, as stretches of the line (spline metres); found once per obstacle.
        static readonly Dictionary<string, List<Vector2>> s_BlockingStretches = new Dictionary<string, List<Vector2>>();

        static List<Vector2> BlockingStretchesOf(ObstacleData obstacle, BuiltTrack track)
        {
            string key = obstacle.Identifier + "|" + track.Identifier;
            if (s_BlockingStretches.TryGetValue(key, out var stretches)) return stretches;
            stretches = new List<Vector2>();
            try { OriginalRails.BlockingStretches(track, obstacle, stretches); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Searching {track.Identifier} for the rocks of {obstacle.Identifier} failed: {exception.Message}"); }
            s_BlockingStretches[key] = stretches;
            return stretches;
        }

        /// The train's nose is at the first rock of the rockfall on its way. A rockfall with no rock found on the line
        /// stops the train at its circle, as before.
        static bool ReachedFirstRock(ObstacleData obstacle, BuiltTrack track, float lead, float position, float velocity)
        {
            var stretches = BlockingStretchesOf(obstacle, track);
            if (stretches.Count == 0)
            {
                // no rock found on the rails: the train runs on to where the obstacle's middle lies beside the line
                float middle = track.Path.NearestDistance(Obstacles.CenterOf(obstacle), out _) * track.Spline.Length / Mathf.Max(MinimumLineLengthInMeters, track.Path.Length);
                stretches.Add(new Vector2(middle - ObstacleMiddleHalfWidthInMeters, middle + ObstacleMiddleHalfWidthInMeters));
            }
            bool forward = velocity > 0f;
            float stopAt = forward ? float.MaxValue : float.MinValue;
            foreach (var stretch in stretches)
            {
                if (forward && stretch.y > position && stretch.x < stopAt) stopAt = stretch.x;
                if (!forward && stretch.x < position && stretch.y > stopAt) stopAt = stretch.y;
            }
            if (forward && stopAt == float.MaxValue) return false;
            if (!forward && stopAt == float.MinValue) return false;
            return forward ? lead >= stopAt : lead <= stopAt;
        }

        const float ObstacleMiddleHalfWidthInMeters = 3f;
        const float MinimumLineLengthInMeters = 0.01f;

        /// The train ran into something hard: the cab is shaken and metal crashes, both by the speed of the knock.
        const float CrashSpeedForFullStrength = 8f;
        const float MinimumCrashSpeed = 0.8f;
        const float CrashShakeRangeInMeters = 40f;
        static void CrashFeedback(Vector3 point, float speed)
        {
            if (speed < MinimumCrashSpeed) return;
            float strength = Mathf.Clamp01(speed / CrashSpeedForFullStrength);
            WorldSounds.PlayCrash(point, strength);
            // the shake fades with the distance to the player (on the train they are right there)
            var player = GameManager.GetPlayerObject();
            float distance = player != null ? Vector3.Distance(player.transform.position, point) : float.MaxValue;
            WorldSounds.ShakeCamera(strength * Mathf.Clamp01(1f - distance / CrashShakeRangeInMeters));
        }

        public static void CrashAt(Vector3 point, float speed) => CrashFeedback(point, speed);

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
            Wagons.PrepareTransit(liveTrain, portal.DestinationScene, s_Trains);
            s_Save.Arrival = new PendingArrival
            {
                FromScene = s_Scene,
                ToScene = portal.DestinationScene,
                Train = state,
                Speed = Mathf.Abs(value),
                // chimney first? (standing still: the way that leads out through this transition)
                Forward = Mathf.Abs(value) > MinimumFacingVelocityInMetersPerSecond ? liveTrain.Simulation.Velocity > 0f : (portal.AtEnd ? 1 : -1) * liveTrain.Simulation.FacingSign > 0,
                FallbackRouteId = liveTrain.RouteId,
                FallbackPosition = portal.AtEnd ? portal.SplineMeters - PortalFallbackDistanceInMeters : portal.SplineMeters + PortalFallbackDistanceInMeters,
            };
            SteamRailwayMod.Log.Msg($"Train {liveTrain.Guid} leaves {s_Scene} for {portal.DestinationScene}.");
            // The train is no longer this region's (its save must not keep it), but it keeps running with the
            // player aboard while the screen fades; the player leaves the cab only once it is black.
            s_Trains.Remove(liveTrain);
            s_Departing = liveTrain;
            try
            {
                var loadScene = portal.LoadSceneComponent != null ? portal.LoadSceneComponent : CustomPortals.TransitionTo(portal.DestinationScene, portal.Spawn);
                if (loadScene == null) throw new System.Exception("no transition object to copy in this region");
                loadScene.Activate(true);
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Error($"Region transition failed: {exception.Message}");
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
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Leaving the cab for the transition: {exception.Message}"); }
            s_Sims.Remove(liveTrain.Guid);
            try { liveTrain.Simulation.Audio?.StopAll(); } catch { }
            if (train != null) Object.Destroy(train.gameObject);
        }

        /// Picks the line end the train comes in at and turns the line so the train runs the same way as before.
        static Portal PrepareArrival(PendingArrival arrival)
        {
            Portal entry = s_Portals.Find(portal => portal.DestinationScene == arrival.FromScene);
            if (entry == null)
            {
                // no transition back on the line: the line end nearest to where the game put the player
                var player = GameManager.GetPlayerObject();
                Vector3 playerPosition = player != null ? player.transform.position : Vector3.zero;
                float best = float.MaxValue;
                foreach (var track in s_Tracks)
                {
                    float startDistance = Vector3.Distance(track.Path[0], playerPosition), endDistance = Vector3.Distance(track.Path[track.Path.Count - 1], playerPosition);
                    if (startDistance < best) { best = startDistance; entry = new Portal { Track = track, SplineMeters = 0f, AtEnd = false, DestinationScene = arrival.FromScene }; }
                    if (endDistance < best) { best = endDistance; entry = new Portal { Track = track, SplineMeters = track.Spline.Length, AtEnd = true, DestinationScene = arrival.FromScene }; }
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
            float position = entry.LoadSceneComponent != null || entry.Custom ? entry.Safe : (entry.AtEnd ? length - PortalExitDistanceInMeters : PortalExitDistanceInMeters);
            trainState.RouteId = entry.Track.Identifier;
            trainState.SplinePosition = Mathf.Clamp(position, 1f, length - 1f);
            // The train moves into the region: along the line + at its start, - at its end. It keeps running the
            // way it ran before (chimney first or bunker first), so it stands on the line facing accordingly.
            int inward = entry.AtEnd ? -1 : 1;
            int run = arrival.Forward ? 1 : -1;
            if (trainState.SimulationState == null) trainState.SimulationState = new SteamSimulationState();
            trainState.SimulationState.Facing = inward * run;
            float velocity = run * arrival.Speed;
            trainState.Velocity = velocity;             // it comes in running, as it left
            Spawn(trainState, material);
            var live = s_Trains.Find(liveTrain => liveTrain.Guid == trainState.Guid);
            if (live != null)
            {
                try { Wagons.PlaceArrived(s_Scene, live, s_Tracks, s_Save.Wagons); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Placing the wagons that came along failed: {exception}"); }
                MelonCoroutines.Start(BoardAfterArrival(live, velocity));
            }
            SteamRailwayMod.Log.Msg($"Train {trainState.Guid} arrived in {s_Scene} from {arrival.FromScene}.");
        }

        /// Puts the player aboard as soon as the game has placed them at the region's entry point (a jump or the fade-in start),
        /// so the fade opens on the cab of a moving train.
        static IEnumerator BoardAfterArrival(LiveTrain liveTrain, float velocity)
        {
            liveTrain.Boarding = true;
            try
            {
                var enumerator = BoardAfterArrivalInner(liveTrain, velocity);
                while (enumerator.MoveNext()) yield return enumerator.Current;
            }
            finally { liveTrain.Boarding = false; liveTrain.AwaySince = -1f; liveTrain.Reboards = 0; }
        }

        static IEnumerator BoardAfterArrivalInner(LiveTrain liveTrain, float velocity)
        {
            float start = Time.realtimeSinceStartup;
            var sourceObject = GameManager.GetPlayerObject();
            Vector3 last = sourceObject != null ? sourceObject.transform.position : Vector3.zero;
            float still = 0f;
            while (Time.realtimeSinceStartup - start < ArrivalWaitTimeoutInSeconds)
            {
                yield return null;
                if (liveTrain.Train == null) yield break;
                var player = GameManager.GetPlayerObject();
                if (player == null) continue;
                var point = player.transform.position;
                bool jumped = (point - last).sqrMagnitude > JumpSquaredDistanceInSquareMeters;
                still = (point - last).sqrMagnitude < StillSquaredDistanceInSquareMeters ? still + Time.unscaledDeltaTime : 0f;
                last = point;
                bool fadingIn = false;
                try { fadingIn = !CameraFade.IsFadedOut || CameraFade.IsFadingIn; } catch { }
                if (jumped || (fadingIn && Time.realtimeSinceStartup - start > MinimumFadeInSeconds) || still > StillSecondsBeforeContinue) break;
            }

            for (int attempt = 0; attempt < BoardAttemptCount && liveTrain.Train != null; attempt++)
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
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Boarding the arrived train failed: {exception.Message}"); }
                for (int deepIndex = 0; deepIndex < BoardSettleFrameCount; deepIndex++) yield return null;
                if (liveTrain.Train == null || PlayerInCab(liveTrain.Train)) break;
                SteamRailwayMod.Log.Warning($"Player not in the cab after boarding (attempt {attempt + 1}); trying again.");
            }
            if (liveTrain.Train == null) yield break;
            // the game may still move the player to the entry point a moment later: put them back aboard
            float watch = Time.realtimeSinceStartup;
            int reboards = 0;
            while (Time.realtimeSinceStartup - watch < BoardWatchSeconds && liveTrain.Train != null && reboards < MaximumReboardCount)
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
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Boarding the arrived train again failed: {exception.Message}"); }
                for (int deepIndex = 0; deepIndex < BoardSettleFrameCount; deepIndex++) yield return null;
            }
            if (liveTrain.Train == null) yield break;
            // nobody aboard after all: the train must not run off on its own
            if (!PlayerInCab(liveTrain.Train)) liveTrain.Simulation.RestoreMotion(0f);
        }

        /// A fade that never ends (the player was attacked while the door opened) leaves the door "in use": the door then
        /// refuses everyone. With no fade running for this long, the door is let go.
        const float DoorStuckSeconds = 4f;

        static void ReleaseStuckDoor(LiveTrain liveTrain)
        {
            var train = liveTrain.Train;
            if (train == null || liveTrain.Boarding || s_Transiting) { liveTrain.DoorStuckSince = -1f; return; }
            bool fading = false;
            try { fading = CameraFade.IsFadingIn || CameraFade.IsFadingOut || CameraFade.IsFadedOut; } catch { }
            if (fading || !(train.m_PlayerUsingDoor || (!train.IsInsideTrain && train.m_PendingEnterTrain))) { liveTrain.DoorStuckSince = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (liveTrain.DoorStuckSince < 0f) { liveTrain.DoorStuckSince = now; return; }
            if (now - liveTrain.DoorStuckSince < DoorStuckSeconds) return;
            liveTrain.DoorStuckSince = -1f;
            SteamRailwayMod.Log.Warning("The cab door was left in use by an interrupted boarding: letting it go.");
            train.m_PlayerUsingDoor = false;
            train.m_PendingEnterTrain = false;
        }

        /// The game thinks the player is in the cab but they stand elsewhere: take them aboard again,
        /// or after repeated failures let them out and stop the train.
        static void GuardCab(LiveTrain liveTrain)
        {
            var train = liveTrain.Train;
            if (train == null || liveTrain.Simulation == null || liveTrain.Boarding || s_Transiting || !train.IsInsideTrain) { liveTrain.AwaySince = -1f; return; }
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
            if (now - liveTrain.AwaySince < AwaySecondsBeforeReboard) return;
            liveTrain.AwaySince = now;
            var player = GameManager.GetPlayerObject();
            string where = player != null ? player.transform.position.ToString("F1") : "?";
            if (liveTrain.Reboards < MaximumReboardCount)
            {
                liveTrain.Reboards++;
                SteamRailwayMod.Log.Warning($"Player at {where} is not in the cab at {train.m_CameraDummy.position:F1} while aboard: taking them aboard again ({liveTrain.Reboards}).");
                SteamTrain.s_TrackedSteamTrain = train;
                train.FinishEnteringSteamTrainAfterFadeOut();
                train.m_PendingEnterTrain = false;
                return;
            }
            SteamRailwayMod.Log.Warning($"Player at {where} could not be put back into the cab: leaving the train and stopping it.");
            liveTrain.Simulation.State.Regulator = 0f;
            liveTrain.Simulation.RestoreMotion(0f);
            try
            {
                train.ExitSteamTrain(train.transform.Find("ExitPoint"));
                train.FinishExitingSteamTrainAfterFadeOut();
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Leaving the train failed: {exception.Message}"); }
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
            return new Vector2(direction.x, direction.z).magnitude < 2f && Mathf.Abs(direction.y) < BoardedHeightToleranceInMeters;
        }

        /// The next region has no railway: the train stays behind, stopped before the transition.
        static void ArrivalFailed(PendingArrival arrival)
        {
            s_Save.Arrival = null;
            var trainState = arrival.Train;
            if (s_Save.Wagons != null) Wagons.RevertTransit(trainState.Guid, s_Save.Wagons);
            trainState.RouteId = arrival.FallbackRouteId;
            trainState.SplinePosition = Mathf.Max(1f, arrival.FallbackPosition);
            trainState.Velocity = 0f;
            if (!s_Save.Scenes.TryGetValue(arrival.FromScene, out var list)) { list = new List<TrainState>(); s_Save.Scenes[arrival.FromScene] = list; }
            list.Add(trainState);
            CabControls.Message(Localizer.Translate("Bu bölgede ray yok; tren geride, geçişin önünde kaldı.",
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
            if (best == null || bestDistance > NearestTrackRadiusInMeters) return;
            trainState.RouteId = best.Identifier;
            trainState.SplinePosition = bestArc * best.Spline.Length / Mathf.Max(1f, best.Path.Length);
        }

        /// An obstacle was cleared: removes the rocks and logs of the lines inside its circle that stayed in hard mode.
        /// Only the stretches of line inside the circle are searched.
        public static void ClearLinesAgain(ObstacleData obstacle)
        {
            s_BlockingStretches.Clear();
            foreach (var track in s_Tracks)
            {
                var trackDef = s_TrackDefinitions.Find(candidate => candidate.Identifier == track.Identifier);
                try { if (trackDef != null && trackDef.Originals.Count > 0) OriginalRails.ClearObstaclesWithin(track, obstacle); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Clearing {track.Identifier} again failed: {exception.Message}"); }
            }
        }

        /// The rocks and logs on the region's lines inside the obstacle that wait for it (clearing it hides them).
        public static void CollectHeldRocksWithin(ObstacleData obstacle, List<Transform> result)
        {
            foreach (var track in s_Tracks)
            {
                var trackDef = s_TrackDefinitions.Find(candidate => candidate.Identifier == track.Identifier);
                try { if (trackDef != null && trackDef.Originals.Count > 0) OriginalRails.CollectHeldWithin(track, obstacle, result); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Searching {track.Identifier} for rocks failed: {exception.Message}"); }
            }
        }

        const float DeveloperMaximumLineDistanceInMeters = 150f;
        const float DeveloperBoilerTemperatureInCelsius = 165f;
        const float DeveloperBoilerWaterShare = 0.8f;
        const float DeveloperFireMinutes = 240f;

        /// Developer key: puts the locomotive, ready to run (boiler hot, water full, fire lit, brake on),
        /// on the nearest line where the player stands, facing the way the player looks.
        public static void DeveloperPlaceTrain()
        {
            if (s_Scene == null || s_Tracks.Count == 0) { CabControls.Message(Localizer.Translate("Bu bölgede ray yok.", "There is no railway in this region.")); return; }
            var player = GameManager.GetPlayerObject();
            var camera = GameManager.GetMainCamera();
            if (player == null || camera == null) return;
            var point = player.transform.position;
            BuiltTrack best = null;
            float bestArc = 0f, bestDistance = float.MaxValue;
            foreach (var track in s_Tracks)
            {
                float arc = track.Path.NearestDistance(point, out float distance);
                if (distance < bestDistance) { bestDistance = distance; best = track; bestArc = arc; }
            }
            if (best == null || bestDistance > DeveloperMaximumLineDistanceInMeters)
            {
                CabControls.Message(Localizer.Translate($"En yakın hat {bestDistance:0} m uzakta: hatta ya da yakınında dur.", $"The nearest line is {bestDistance:0} m away: stand on or near a line."));
                return;
            }
            float splinePerPath = best.Spline.Length / Mathf.Max(MinimumPathLengthInMeters, best.Path.Length);
            float position = Mathf.Clamp(bestArc * splinePerPath, 2f, best.Spline.Length - 2f);
            best.Path.Sample(bestArc, out _, out var tangent);
            var look = camera.transform.forward; look.y = 0f;
            tangent.y = 0f;
            int facing = Vector3.Dot(look, tangent) >= 0f ? 1 : -1;

            var live = s_Trains.Count > 0 ? s_Trains[0] : null;
            if (live != null && live.Train != null)
            {
                ReadyForService(live.Simulation.State);
                var splineController = live.Train.m_SplineController;
                splineController.Spline = best.Spline;
                splineController.Position = position;
                live.Simulation.State.Facing = facing;
                live.Simulation.EnforceFacing();
                live.Simulation.RestoreMotion(0f);
                live.RouteId = best.Identifier;
                live.PreviousPosition = float.NaN;
                LightFire(live.Simulation);
            }
            else
            {
                // the locomotive stands in another region (or was never placed): take its state and build it here
                TrainState state = null;
                foreach (var pair in s_Save.Scenes)
                    if (pair.Value.Count > 0) { state = pair.Value[0]; pair.Value.RemoveAt(0); break; }
                if (state == null) state = new TrainState { Guid = System.Guid.NewGuid().ToString() };
                s_Save.TrainCreated = true;
                if (state.SimulationState == null) state.SimulationState = new SteamSimulationState();
                ReadyForService(state.SimulationState);
                state.SimulationState.Facing = facing;
                state.RouteId = best.Identifier;
                state.SplinePosition = position;
                state.Velocity = 0f;
                state.TrainData = null;
                state.FireData = null;
                if (!s_Save.Scenes.TryGetValue(s_Scene, out var list)) { list = new List<TrainState>(); s_Save.Scenes[s_Scene] = list; }
                list.Add(state);
                Spawn(state, FindTrackMaterial());
                var built = s_Trains.Find(liveTrain => liveTrain.Guid == state.Guid);
                if (built != null) LightFire(built.Simulation);
            }
            CabControls.Message(Localizer.Translate("Lokomotif çalışır vaziyette buraya getirildi (F4).", "The locomotive is brought here, ready to run (F4)."));
        }

        static void ReadyForService(SteamSimulationState state)
        {
            state.BoilerWater = SteamSimulation.BoilerCapacity * DeveloperBoilerWaterShare;
            state.TenderWater = SteamSimulation.TenderCapacity;
            state.BoilerTemp = DeveloperBoilerTemperatureInCelsius;
            state.BoilerCondition = 100f;
            state.ValveCondition = 100f;
            state.PlugBlown = false;
            state.Exploded = false;
            state.SnowKg = 0f;
            state.Regulator = 0f;
            state.Reverser = 0;
            state.SteamBrake = false;
            state.HandBrake = true;
        }

        /// The game's own fire in the firebox is lit and kept burning for a few hours.
        static void LightFire(SteamSimulation simulation)
        {
            try
            {
                if (simulation == null || simulation.Fire == null) return;
                simulation.Fire.StartFireLit();
                simulation.Fire.ForceBurnTimeInMinutes(DeveloperFireMinutes);
            }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Lighting the fire: {exception.Message}"); }
        }

        /// Rebuilds the region's railway for the editors (not while the player rides a train).
        /// A broken bridge was repaired: the rails are laid over its stretch of the line and the original pieces there are
        /// hidden, without rebuilding the region. False when no stretch lay inside it (the caller falls back to a rebuild).
        public static bool LayBridgeRails(ObstacleData obstacle)
        {
            bool laid = false;
            foreach (var track in s_Tracks)
            {
                if (track.Rails == null) continue;
                var ranges = Obstacles.RangesInsideObstacle(track.Path, obstacle);
                if (ranges.Count == 0) continue;
                var options = Switches.Combined(track);
                foreach (var range in ranges)
                {
                    if (range.y - range.x < MinimumRepairedStretchInMeters) continue;
                    try { RailPieces.Lay(track.Rails, track.Path, range.x, range.y, options); track.Laid.Add(range); laid = true; }
                    catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Laying the repaired rails of {track.Identifier} failed: {exception.Message}"); return false; }
                }
                var trackDef = s_TrackDefinitions.Find(candidate => candidate.Identifier == track.Identifier);
                if (trackDef != null) foreach (var line in trackDef.Originals) OriginalRails.HideReplaced(line, obstacle);
            }
            return laid;
        }

        const float MinimumRepairedStretchInMeters = 0.4f;
        /// Half the locomotive's length: a train this close to a bridge is already on it.
        const float BridgeOverlapMarginInMeters = 7f;

        /// The train stands over a bridge: on a kept original bridge piece of its line or inside a broken bridge's circle.
        public static bool IsOverBridge(SteamTrain train)
        {
            if (train == null) return false;
            if (Obstacles.IsOverBridge(train.transform.position)) return true;
            var liveTrain = s_Trains.Find(candidate => candidate.Train == train);
            var track = liveTrain != null ? s_Tracks.Find(candidate => candidate.Identifier == liveTrain.RouteId) : null;
            if (track == null || track.Spline == null || track.Path == null || track.Spline.Length <= 0f) return false;
            float pathMeters = train.m_SplineController.Position * track.Path.Length / track.Spline.Length;
            foreach (var range in track.Skip)
                if (pathMeters >= range.x - BridgeOverlapMarginInMeters && pathMeters <= range.y + BridgeOverlapMarginInMeters) return true;
            return false;
        }

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
            var track = s_Tracks.Find(candidateTrack => candidateTrack.Identifier == state.RouteId) ?? s_Tracks[0];
            // a train left standing in a transition trigger would carry the player off again
            float clear = ClearOfPortals(track.Identifier, state.SplinePosition);
            if (Mathf.Abs(clear - state.SplinePosition) > PortalClearToleranceInMeters)
            {
                SteamRailwayMod.Log.Msg($"Train {state.Guid} stood in a region transition; moved to {clear:F0} m.");
                state.SplinePosition = clear;
            }
            var train = TrainBuilder.Build(state.Guid, track.Spline, state.SplinePosition, material);
            var simulation = new SteamSimulation(train, state.SimulationState);
            simulation.Fire = train.m_Boiler != null ? train.m_Boiler.m_InvisibleFire : null;
            var stoveTransform = train.transform.Find("Firebox");
            simulation.Stove = stoveTransform != null ? stoveTransform.GetComponent<WoodStove>() : null;
            simulation.RestoreMotion(state.Velocity);
            simulation.Animator = LocomotiveAnimator.TryCreate(train.transform);
            simulation.Audio = new TrainAudio(train);
            simulation.Smoke = CreateSmoke(train);
            s_Sims[state.Guid] = simulation;
            CabControls.ApplyRealism(train);
            s_OurGuids.Add(state.Guid);
            var liveTrain = new LiveTrain { Train = train, Guid = state.Guid, RouteId = track.Identifier, Simulation = simulation };
            s_Trains.Add(liveTrain);
            if (!string.IsNullOrEmpty(state.TrainData) || !string.IsNullOrEmpty(state.FireData) || (state.CabItems != null && state.CabItems.Count > 0))
            {
                liveTrain.PendingRestore = state;
                MelonCoroutines.Start(RestoreNextFrame(liveTrain, train, simulation, state));
            }
        }

        /// Chimney smoke from the bundle on the game's lit particle material (taken from the firebox stove's fire);
        /// the old smoke system stays as the game's emission target but no longer renders.
        static LocoSmoke CreateSmoke(SteamTrain train)
        {
            try
            {
                var old = train.transform.Find("ChimneySmoke");
                Vector3 position = old != null ? old.localPosition : TrainConfig.ChimneyTop;
                Material litMaterial = null;
                var stove = train.transform.Find("Firebox");
                if (stove != null)
                    foreach (var particleRenderer in stove.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    {
                        var material = particleRenderer.sharedMaterial;
                        if (material != null && material.name.StartsWith("FX_SmokeA") && material.shader != null && material.shader.name.Contains("VertexLit")) { litMaterial = material; break; }
                    }
                var smoke = LocoSmoke.Create(train.transform, position, litMaterial);
                if (smoke != null && old != null)
                {
                    var particleRenderer = old.GetComponent<ParticleSystemRenderer>();
                    if (particleRenderer != null) particleRenderer.enabled = false;
                }
                return smoke;
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Warning($"Chimney smoke could not be built: {exception.Message}");
                return null;
            }
        }

        static IEnumerator RestoreNextFrame(LiveTrain liveTrain, SteamTrain train, SteamSimulation simulation, TrainState state)
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
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Error($"Restoring train {state.Guid} failed: {exception}");
            }
            try
            {
                CabItems.Restore(train.transform, state.CabItems);
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Error($"Restoring train {state.Guid} failed: {exception}");
            }
            liveTrain.PendingRestore = null;
        }

        public static void Update()
        {
            if (s_Scene != null) InstancedHider.Update();
            if (s_Scene != null) { try { BridgeSite.Update(); } catch (System.Exception exception) { LogUpdateFailure("BridgeSite", exception); } }
            if (s_Scene != null) { try { Dynamite.Update(); } catch (System.Exception exception) { LogUpdateFailure("Dynamite", exception); } }
            if (s_Scene != null) { try { CheckWalkPortals(); } catch (System.Exception exception) { LogUpdateFailure("Walk transition", exception); } }
            if (s_Scene != null) { try { Wagons.Update(s_Trains); } catch (System.Exception exception) { LogUpdateFailure("Wagons", exception); } }
            if (s_Scene == null || s_Trains.Count == 0) return;
            try { BrakeSparks.Update(s_Trains); } catch (System.Exception exception) { LogUpdateFailure("Brake sparks", exception); }
            try { TrainWildlife.Update(s_Trains); } catch (System.Exception exception) { LogUpdateFailure("Wildlife", exception); }
            Patches.CabPlace_ControlMode.Update();
            for (int index = 0; index < s_Trains.Count; index++) StopAtObstacle(s_Trains[index]);
            BoilerExplosion.Update();
            for (int index = 0; index < s_Trains.Count; index++) PassJunctions(s_Trains[index]);
            for (int index = 0; index < s_Trains.Count; index++)
                if (s_Trains[index].Train != null)
                {
                    try { CabItems.Update(s_Trains[index].Train.transform); }
                    catch (System.Exception exception) { LogUpdateFailure("Cab items", exception); }
                    try { CabCarry.Update(s_Trains[index].Train); }
                    catch (System.Exception exception) { LogUpdateFailure("Cab carry", exception); }
                }
            if (!s_Transiting)
                for (int index = 0; index < s_Trains.Count; index++) CheckPortals(s_Trains[index]);
            for (int index = 0; index < s_Trains.Count; index++)
            {
                try { GuardCab(s_Trains[index]); ReleaseStuckDoor(s_Trains[index]); }
                catch (System.Exception exception) { LogUpdateFailure("Cab guard", exception); }
            }
            if (Time.time >= s_NextHover)
            {
                s_NextHover = Time.time + HoverIntervalInSeconds;
                foreach (var liveTrain in s_Trains)
                {
                    if (liveTrain.Train == null || liveTrain.Simulation == null) continue;
                    CabControls.UpdateHoverTexts(liveTrain.Simulation);
                    CabControls.UpdateEffects(liveTrain.Simulation);
                }
            }
            if (Time.time < s_NextCapture) return;
            s_NextCapture = Time.time + CaptureInterval;
            CaptureStates();
            foreach (var liveTrain in s_Trains) KeepExitPointOnGround(liveTrain.Train);
        }

        internal static BuiltTrack FindTrack(string identifier)
        {
            for (int index = 0; index < s_Tracks.Count; index++)
                if (s_Tracks[index].Identifier == identifier) return s_Tracks[index];
            return null;
        }

        static void LogUpdateFailure(string label, System.Exception exception) => SteamRailwayMod.Debug($"{label}: {exception.Message}");

        static void PassJunctions(LiveTrain liveTrain)
        {
            if (liveTrain.Train == null || liveTrain.Simulation == null) return;
            float position = liveTrain.Train.m_SplineController.Position;
            float previousPosition = liveTrain.PreviousPosition;
            liveTrain.PreviousPosition = position;
            if (float.IsNaN(previousPosition) || Switches.All.Count == 0) return;
            var line = FindTrack(liveTrain.RouteId);
            if (line == null) return;
            var moved = Switches.Pass(liveTrain, line, previousPosition, position);
            if (moved != null) liveTrain.PreviousPosition = liveTrain.Train.m_SplineController.Position;
        }

        public static void SaveSwitch(string identifier, bool diverging) => s_Save.Switches[identifier] = diverging;

        static void KeepExitPointOnGround(SteamTrain train)
        {
            if (train == null || train.IsMoving) return;
            var exit = train.transform.Find("ExitPoint");
            if (exit == null) return;
            var local = exit.localPosition;
            exit.position = Il2CppHelpers.GroundBelow(train.transform.TransformPoint(new Vector3(local.x, 2f, local.z)));
        }

        // ---------------------------------------------------------------- save / load

        public static void CaptureStates()
        {
            if (s_Scene == null) return;
            if (s_Save.Wagons == null) s_Save.Wagons = new Dictionary<string, WagonState>();
            Wagons.Capture(s_Save.Wagons);
            if (s_Trains.Count == 0) return;
            var list = new List<TrainState>();
            foreach (var liveTrain in s_Trains)
            {
                if (liveTrain.Train == null) continue;
                try
                {
                    // built but not filled yet: its saved data is still the truth
                    var pending = liveTrain.PendingRestore;
                    if (pending != null)
                    {
                        list.Add(new TrainState
                        {
                            Guid = liveTrain.Guid,
                            RouteId = liveTrain.RouteId,
                            SplinePosition = liveTrain.Train.m_SplineController.Position,
                            TrainData = pending.TrainData,
                            FireData = pending.FireData,
                            SimulationState = liveTrain.Simulation != null ? liveTrain.Simulation.State : pending.SimulationState,
                            Velocity = liveTrain.Simulation != null ? liveTrain.Simulation.Velocity : 0f,
                            CabItems = pending.CabItems,
                        });
                        continue;
                    }
                    list.Add(new TrainState
                    {
                        Guid = liveTrain.Guid,
                        RouteId = liveTrain.RouteId,
                        SplinePosition = liveTrain.Train.m_SplineController.Position,
                        TrainData = liveTrain.Train.SerializeTrainCarData(),
                        FireData = liveTrain.Simulation != null && liveTrain.Simulation.Fire != null ? liveTrain.Simulation.Fire.Serialize() : null,
                        SimulationState = liveTrain.Simulation != null ? liveTrain.Simulation.State : null,
                        Velocity = liveTrain.Simulation != null ? liveTrain.Simulation.Velocity : 0f,
                        CabItems = CabItems.Capture(liveTrain.Train.transform),
                    });
                }
                catch (System.Exception exception)
                {
                    SteamRailwayMod.Debug($"Capture of {liveTrain.Guid} failed: {exception.Message}");
                }
            }
            if (list.Count > 0) s_Save.Scenes[s_Scene] = list;
        }

        /// Called right before the game writes the save slot (see SavePatches): writes the difficulty, cleared obstacles,
        /// wagons and train, never data that belongs to another slot.
        public static void WriteSave()
        {
            // this game's railway data has not been read yet (a new game, or one just entered): what is in memory belongs
            // to the game played before, and a new game may reuse that game's slot name
            if (s_NeedLoad) return;
            CaptureStates();
            string slot = null;
            try { slot = SaveGameSystem.GetCurrentSaveName(); } catch { }
            if (!string.IsNullOrEmpty(slot) && slot != "autosave" && !string.IsNullOrEmpty(s_Save.Slot) && s_Save.Slot != slot)
            {
                SteamRailwayMod.Log.Warning($"Railway data of {s_Save.Slot} not written into {slot}.");
                return;
            }
            if (!string.IsNullOrEmpty(slot) && slot != "autosave" && string.IsNullOrEmpty(s_Save.Slot)) s_Save.Slot = slot;
            if (string.IsNullOrEmpty(s_Save.Difficulty)) s_Save.Difficulty = Obstacles.EasyName;
            s_ModData.Save(JsonSerializer.Serialize(s_Save, s_Json));
            SteamRailwayMod.Debug($"Railway state written ({s_Save.Scenes.Count} scene(s), {s_Save.ClearedObstacles?.Count ?? 0} cleared obstacle(s)).");
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
            bool startedNewGame = false;
            try { slot = SaveGameSystem.GetCurrentSaveName(); } catch { }
            try
            {
                string json = s_ModData.Load();
                // A new game starts from nothing, unless this very slot already holds railway data of its own
                // (the new-name call can also come while an existing game is being entered).
                bool isNewGame = s_NewGame && !StoredDataBelongsTo(json, slot);
                startedNewGame = isNewGame;
                if (isNewGame) json = null;
                SteamRailwayMod.Log.Msg($"Railway data of {slot}: newGame={isNewGame}, stored={(string.IsNullOrEmpty(json) ? 0 : json.Length)} chars.");
                s_Save = string.IsNullOrEmpty(json) ? new RailwaySave() : JsonSerializer.Deserialize<RailwaySave>(json, s_Json) ?? new RailwaySave();
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Error($"Railway save data could not be read: {exception.Message}");
                s_Save = new RailwaySave();
            }
            if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(s_Save.Slot) && s_Save.Slot != slot)
            {
                SteamRailwayMod.Log.Msg($"Railway data of another game ({s_Save.Slot}) ignored; {slot} starts fresh.");
                s_Save = new RailwaySave();
            }
            if (!string.IsNullOrEmpty(slot)) s_Save.Slot = slot;
            if (string.IsNullOrEmpty(s_Save.Difficulty))
                s_Save.Difficulty = startedNewGame ? SteamRailwayMod.DifficultySetting : Obstacles.EasyName;
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
