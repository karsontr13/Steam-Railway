using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// Where a movable wagon stands, kept in the mod's save (key: scene, wagon name and original position).
    internal sealed class WagonState
    {
        public string RouteId { get; set; }
        /// Spline metres along the line.
        public float SplinePos { get; set; }
        /// The wagon stands turned round against the way it stood when it was first put on the line.
        public bool Flipped { get; set; }
        /// What it is coupled to: "train:" + the locomotive's guid, another wagon's key, or null.
        public string CoupledTo { get; set; }
        /// The wagon's line direction when its locomotive runs chimney first (+1 / -1; 0 when not coupled).
        public int ConsistSign { get; set; }
        /// The region the wagon stands in now (null: the region it belongs to).
        public string Scene { get; set; }
        // what a wagon needs to be built again in another region (a copy of the game's prefab, posed like the original)
        public string Name { get; set; }
        public string Prefab { get; set; }
        public float[] LocalOffset { get; set; }
        public float[] LocalRotation { get; set; }
        public float ReachForward { get; set; }
        public float ReachBackward { get; set; }
        public float Mass { get; set; }
        // on its way into another region with a locomotive: it is placed behind that train when the region is built
        public string ArrivalTrain { get; set; }
        /// Metres along the train's own forward direction from the locomotive's position.
        public float ArrivalOffset { get; set; }
        /// The wagon stands turned round against the train's forward direction.
        public bool ArrivalAgainstTrain { get; set; }
        /// Loot that rode on it with the train, placed relative to its root: built again on it when it arrives.
        public List<CabItemState> ArrivalCargo { get; set; }
        /// Rolled on its own through a transition: the region it left (it comes in on the line's way back there).
        public string StrayFromScene { get; set; }
        /// Its speed when it left (metres per second): it comes in rolling at that speed.
        public float StraySpeed { get; set; }
        /// It rolled turned round against the way it ran.
        public bool StrayAgainstMotion { get; set; }
        /// Loot that rode on it, placed relative to its root: built again on it when it comes in.
        public List<CabItemState> StrayCargo { get; set; }
        /// The wagon it was coupled to when it left (its key): coupled again if that one comes in with it.
        public string StrayCoupledTo { get; set; }
        /// When it left (UTC ticks): wagons that left one after another come in in the same order.
        public long StrayDepartedTicks { get; set; }
    }

    /// An object lying on a wagon and its place relative to the wagon's root.
    internal sealed class CargoItem
    {
        public Transform Object;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        /// Set for loot (a loose gear item): it leaves the cargo when it is picked up or put in a container.
        public GearItem Gear;
    }

    /// One of the region's own wagons (marked movable in the object editor) standing on one of our lines.
    internal sealed class RailWagon
    {
        public string Key;
        public string Name;
        public Transform Root;
        public BuiltTrack Track;
        /// Spline metres along the line (the same unit as the train's spline position).
        public float Position;
        /// Spline metres per second along the line (+ towards the line's end).
        public float Velocity;
        public bool Flipped;
        /// How far the wagon reaches from its pivot, along the line, measured when it was put on the line (spline metres):
        /// towards the line's end and towards its start. Turned round, the two swap.
        public float ReachForward, ReachBackward;
        /// How far the wagon's couplers (the game's FrameClamp parts) reach, measured on their vertices (spline metres);
        /// the body reach where a side has none. A wagon on a locomotive is drawn up until its coupler meets the engine's.
        public float CouplerReachForward, CouplerReachBackward;
        /// Where each coupler mounts on the wagon (its inner end, spline metres from the pivot) and its object (all LODs),
        /// hidden while the locomotive shows a copy of it locked into its own coupler. Null: not measured (rebuilt wagon).
        public float CouplerMountForward, CouplerMountBackward;
        public Transform CouplerGroupForward, CouplerGroupBackward;
        /// Every coupler object at each end (an end can carry more than one, drawn over each other): all are hidden and
        /// copied together.
        public readonly List<Transform> CouplerGroupsForward = new List<Transform>(), CouplerGroupsBackward = new List<Transform>();
        public Material CouplerMaterial;
        // the locked pose: which couplers are hidden (physical sides: forward / backward as measured), the copies shown
        // locked together where this wagon receives another one, and whether its own link has locked (for the sound)
        public bool HideForwardWanted, HideBackwardWanted, HiddenForward, HiddenBackward;
        public bool ReceiveForwardWanted, ReceiveBackwardWanted;
        public GameObject LockCopyForward, LockCopyBackward;
        public bool LockEngaged, LockEngagedWanted;
        /// What the wagon is locked on to (the engine's guid or the wagon) and the emitter its lock was heard from.
        public object EngagedLink;
        public GameObject EngagedEmitter;
        /// The pivot's offset and rotation in the line's frame at its position.
        public Vector3 LocalOffset;
        public Quaternion LocalRotation;
        public float MassInKilograms;
        public GameObject SoundEmitter;
        public float NextSoundTime;
        public bool Moved;

        // coupling: to a locomotive (its guid) or to another wagon; the link keeps the gap between their ends
        public string CoupledToTrain;
        public RailWagon CoupledToWagon;
        /// Restored from the save: the key of the wagon it is coupled to, resolved once all wagons exist.
        public string PendingCoupledToKey;
        /// The key of the wagon it hung on that rolled out of the region before it (the link goes on in the next region).
        public string StrayCoupledToKey;
        /// The wagon's line velocity is this sign times its locomotive's own velocity (+ chimney first).
        public int ConsistSign;
        /// The gap between the coupled ends (spline metres); NaN until measured on a shared line.
        public float CouplingGap = float.NaN;
        // sounds of a rolling wagon
        public AudioSource RumbleSource, JointSource;
        public float JointDistance;
        /// Loose objects lying on the wagon (logs): they ride with it, kept at their place relative to its root.
        public readonly List<CargoItem> Cargo = new List<CargoItem>();
        /// The wagon's body in its own space (for finding loot on it) and when loot was last looked for.
        public Bounds LocalFootprint;
        public bool HasFootprint;
        public float NextGearScanTime;
        /// Where the region placed the wagon (it may stand elsewhere now): loot left lying there is taken along for a while.
        public Matrix4x4 HomeMatrix;
        public float HomeScanEndTime;
        /// The region it stands in now (its own, or the one it was taken to).
        public string Scene;
        /// The game's prefab it can be built from in another region (null: it cannot leave its region).
        public string PrefabPath;
        // on its way into another region with a locomotive
        public string TransitDestination, TransitTrain;
        public float TransitOffset;
        public bool TransitAgainstTrain;
        public List<CabItemState> TransitCargo;

        public float ReachTowardsEnd => Flipped ? ReachBackward : ReachForward;
        public float ReachTowardsStart => Flipped ? ReachForward : ReachBackward;
        // a wagon rebuilt from a prefab in another region is not re-measured: its body reach stands in
        public float CouplerReachTowardsEnd => Flipped ? CouplerOrBody(CouplerReachBackward, ReachBackward) : CouplerOrBody(CouplerReachForward, ReachForward);
        public float CouplerReachTowardsStart => Flipped ? CouplerOrBody(CouplerReachForward, ReachForward) : CouplerOrBody(CouplerReachBackward, ReachBackward);
        static float CouplerOrBody(float coupler, float body) => coupler > 0f ? coupler : body;
        // a wagon rebuilt from a prefab is not measured: its coupler is taken as a typical length shorter than its reach
        public float CouplerMountTowardsEnd => Flipped ? MountOrEstimate(CouplerMountBackward, CouplerReachTowardsEnd) : MountOrEstimate(CouplerMountForward, CouplerReachTowardsEnd);
        public float CouplerMountTowardsStart => Flipped ? MountOrEstimate(CouplerMountForward, CouplerReachTowardsStart) : MountOrEstimate(CouplerMountBackward, CouplerReachTowardsStart);
        const float EstimatedCouplerLengthInMeters = 0.92f;
        static float MountOrEstimate(float mount, float reach) => mount > 0f ? mount : Mathf.Max(0f, reach - EstimatedCouplerLengthInMeters);
        public Transform CouplerGroupTowardsEnd => Flipped ? CouplerGroupBackward : CouplerGroupForward;
        public Transform CouplerGroupTowardsStart => Flipped ? CouplerGroupForward : CouplerGroupBackward;
        /// Where a wagon locked on at this end stands its own coupler's mount: the far end of the copy of this wagon's other
        /// coupler, set lock-depth deep into the coupler at this end (spline metres from the pivot).
        public float LockedMountTowardsEnd => CouplerReachTowardsEnd - CouplerLockDepthInMeters + CouplerReachTowardsStart - CouplerMountTowardsStart;
        public float LockedMountTowardsStart => CouplerReachTowardsStart - CouplerLockDepthInMeters + CouplerReachTowardsEnd - CouplerMountTowardsEnd;
        public bool HasCouplers => CouplerGroupsForward.Count > 0 && CouplerGroupsBackward.Count > 0;
        public const float CouplerLockDepthInMeters = 0.10f;
        public bool HasLink => CoupledToTrain != null || CoupledToWagon != null;
    }

    /// The region's movable wagons on our lines: pushed by the locomotive, rolling with resistance and slope.
    /// A standing locomotive can couple to a wagon and a coupled wagon to the next; coupled wagons add their mass.
    internal static class Wagons
    {
        const float AttachDistanceInMeters = 1.5f;
        const float GravityInMetersPerSecondSquared = 9.81f;
        /// Rusty bearings and dragging brakes: a pushed wagon slows by this much every second.
        const float RollingDecelerationInMetersPerSecondSquared = 0.12f;
        /// A wagon at rest stays put unless the slope pulls harder than this (about a 6 % grade).
        const float BrakeHoldInMetersPerSecondSquared = 0.6f;
        const float RestSpeedInMetersPerSecond = 0.05f;
        /// Line ends: the pivot stops this far from the end of its spline (as the train does).
        const float LineEndMarginInMeters = 0.05f;
        const float DefaultWagonMassInKilograms = 20000f;
        /// Knocks faster than this bounce (buffers); slower ones push the wagon along with the train.
        const float BounceSpeedInMetersPerSecond = 1f;
        const float BounceRestitution = 0.2f;
        const float BumpSoundSpeedInMetersPerSecond = 0.6f;
        const float BumpSoundIntervalInSeconds = 0.8f;
        const float HardKnockSpeedInMetersPerSecond = 3f;
        const float HardKnockMessageIntervalInSeconds = 5f;
        // The train's buffers from its spline position, used until its model has been measured.
        const float TrainBufferFrontInMeters = 8.3f;
        const float TrainBufferRearInMeters = 6.5f;
        const string TrainModelObjectName = "Model";
        // coupling
        /// The buffers must almost touch to couple (the joint is checked on the wagon's measured ends).
        const float CouplingReachInMeters = 0.45f;
        /// Coupled wagons close up to this gap between the ends (the buffers touch), at this speed.
        const float CoupledGapInMeters = 0.08f;
        const float CouplingCloseUpSpeedInMetersPerSecond = 1.2f;
        /// A wagon on the locomotive closes up until its coupler's jaws rest on the engine's coupler face (the engine's
        /// tongue then sits in the jaws' mouth): the gap between the two coupler faces.
        const float LocomotiveCoupledGapInMeters = 0f;
        /// The couplers show their locked pose once the wagon has closed up to within this distance.
        const float CouplerLockToleranceInMeters = 0.02f;
        /// The game's wagon couplers (OBJ_RailBaseA_FrameClampA/B) and how far off the centre line one may sit.
        const string CouplerPartName = "FrameClamp";
        const float CouplerLateralLimitInMeters = 0.4f;
        /// The locomotive model carries a copy of the wagon's coupler at each end (hidden until a wagon is locked on)
        /// and the rear claw in a free and a locked state.
        const string TrainCouplerPartnerPrefix = "CouplerPartner";
        const string TrainCouplerPartnerFrontName = "CouplerPartner_Front";
        const string TrainCouplerPartnerRearName = "CouplerPartner_Rear";
        const string TrainRearClawFreeName = "CouplerRear_Free";
        const string TrainRearClawLockedName = "CouplerRear_Locked";
        // the claw coupler's hinged fingers (OBJ_RailBaseA_FrameClampA, measured on the game's mesh from its mount): they lie
        // more than 0.627 m out, swing about vertical pins 0.235 m either side of the centre line, 0.697 m out
        const string CouplerClawName = "FrameClampA";
        const string CouplerSoundEmitterName = "Snd_Coupler";
        const float ClawFingerStartInMeters = 0.627f;
        const float ClawPinLateralInMeters = 0.235f;
        const float ClawPinAlongInMeters = 0.697f;
        const float ClawCentreToleranceInMeters = 0.005f;
        const float ClawFingerOpenDegrees = 16f;
        // sounds of a rolling wagon
        const float RollingSoundMinimumSpeedInMetersPerSecond = 0.15f;
        const float RumbleVolumePerMeterPerSecond = 0.07f;
        const float RumbleMaximumVolume = 0.35f;
        const float RumbleBasePitch = 0.65f;
        const float RumblePitchPerMeterPerSecond = 0.03f;
        const float JointSpacingInMeters = 12f;
        const float JointVolume = 0.55f;
        const float JointPitchMinimum = 0.92f;
        const float JointPitchMaximum = 1.08f;
        const float CrewSpeederMassInKilograms = 1200f;
        const float RailFreightMassInKilograms = 25000f;
        const float RailBaseMassInKilograms = 15000f;
        const float MinimumPathLengthInMeters = 0.01f;
        const float RightAngleInDegrees = 90f;
        const float MinimumAdvanceInMeters = 1e-6f;
        const float LineEndToleranceInMeters = 1e-4f;
        const float EngineStoppedVelocityInMetersPerSecond = 0.01f;
        const float FrontDirectionSign = 1f;
        const float RearDirectionSign = -1f;
        const int PositionComponentCount = 3;
        const int RotationComponentCount = 4;
        const float RollingSoundMinimumDistance = 6f;
        const float RollingSoundMaximumDistance = 70f;
        const float CouplingSpeedLimitInMetersPerSecond = 0.5f;
        const float CouplingZoneSizeInMeters = 1.6f;
        const float CouplingZoneHeightInMeters = 1.1f;
        /// The joint must be this close to the player for its prompt to appear.
        const float CouplingPlayerReachInMeters = 8f;
        const float CouplingRefreshIntervalInSeconds = 0.25f;
        const float CouplingHoldTimeInSeconds = 1.5f;
        const int MaximumChainLength = 16;
        const string CouplingZoneName = "SR_CouplingZone";
        const string TrainLinkPrefix = "train:";
        const float TransitMessageIntervalInSeconds = 6f;

        static readonly List<RailWagon> s_Wagons = new List<RailWagon>();
        static string s_Scene;
        static float s_NextHardKnockMessage;
        static float s_NextTransitMessage;

        public static IReadOnlyList<RailWagon> All => s_Wagons;

        static string KeyOf(string scene, MovableWagonData wagon) =>
            $"{scene}|{wagon.Name}@{wagon.Position[0]:F1},{wagon.Position[1]:F1},{wagon.Position[2]:F1}";

        static float MassOf(string name)
        {
            if (name.Contains("CrewSpeeder")) return CrewSpeederMassInKilograms;
            if (name.Contains("Locomotive")) return SteamSimulation.LocomotiveMassInKilograms;
            if (name.Contains("RailHopper") || name.Contains("RailCar")) return RailFreightMassInKilograms;
            if (name.Contains("RailBase")) return RailBaseMassInKilograms;
            return DefaultWagonMassInKilograms;
        }

        /// Forgets the wagons (a new region is loading). Their last state was written to the save first.
        public static void Clear()
        {
            s_Wagons.Clear();
            s_CargoGear.Clear();
            s_Scene = null;
            s_Coupling = null;
            s_CouplingZone = null;
            s_StrayHomecomers.Clear();
            s_StrayDepartures.Clear();
        }

        // ------------------------------------------------------------------ putting the wagons on the lines

        /// After the region's lines were built: puts the marked wagons on the line they stand on, where the save has
        /// them. A rebuild of the same region keeps the wagons where they are and only gives them the new lines.
        public static void Attach(string scene, List<BuiltTrack> tracks, Dictionary<string, WagonState> saved)
        {
            if (scene == s_Scene && s_Wagons.Count > 0)
            {
                for (int index = s_Wagons.Count - 1; index >= 0; index--)
                {
                    var wagon = s_Wagons[index];
                    var track = wagon.Track != null ? tracks.Find(candidate => candidate.Identifier == wagon.Track.Identifier) : null;
                    if (track == null || wagon.Root == null) { Unlink(wagon); s_Wagons.RemoveAt(index); continue; }
                    wagon.Track = track;
                    wagon.Velocity = 0f;
                    Pose(wagon);
                }
                return;
            }
            s_Wagons.Clear();
            // the last region's loot is gone; a stale entry could match a new item at a reused native address
            s_CargoGear.Clear();
            s_Scene = scene;
            var marked = Obstacles.Wagons;
            if (tracks.Count == 0) return;
            List<Transform> sceneSearch = null;
            for (int index = 0; index < marked.Count; index++)
            {
                var data = marked[index];
                if (data.Name == null || data.Position == null || data.Position.Length < PositionComponentCount) continue;
                var root = ObjectEdits.MovableWagonObject(data.Name, data.Position) ?? SearchScene(data, ref sceneSearch);
                if (root == null) { SteamRailwayMod.Debug($"Wagon {data.Name}: not found in the region."); continue; }
                string key = KeyOf(scene, data);
                WagonState state = null;
                if (saved != null) saved.TryGetValue(key, out state);
                var cargo = FindCargo(root);
                if (state != null && ((!string.IsNullOrEmpty(state.Scene) && state.Scene != scene) || !string.IsNullOrEmpty(state.ArrivalTrain)))
                {
                    // taken to another region (or on its way here with a train, built from the prefab when the train arrives):
                    // the original and what lay on it stay away from the rails
                    root.gameObject.SetActive(false);
                    foreach (var item in cargo) if (item.Object != null) item.Object.gameObject.SetActive(false);
                    continue;
                }
                // the region's own pose of the wagon: loot inside it is moved along when the wagon is levelled or restored
                var regionMatrix = root.localToWorldMatrix;
                var wagon = PutOnLine(key, data.Name, root, tracks);
                if (wagon == null) continue;
                wagon.Cargo.AddRange(cargo);
                wagon.Scene = scene;
                wagon.PrefabPath = PrefabPathFor(data.Name) ?? WagonSnapshots.Capture(root, data.Name);
                if (state != null && IsStrayArrival(state, scene))
                {
                    // rolled back home on its own: placed at the entry once the transitions are known
                    s_StrayHomecomers.Add(wagon);
                    Pose(wagon);
                    s_Wagons.Add(wagon);
                    continue;
                }
                wagon.HomeMatrix = regionMatrix;
                wagon.HomeScanEndTime = Time.time + HomeLootSearchInSeconds;
                if (state != null) Restore(wagon, state, tracks);
                Pose(wagon);
                UnpackLeftoverCargo(wagon, state);
                s_Wagons.Add(wagon);
            }
            // wagons taken here from other regions that stand here since
            if (saved != null)
                foreach (var pair in saved)
                {
                    var state = pair.Value;
                    if (state.Scene != scene || pair.Key.StartsWith(scene + "|") || !string.IsNullOrEmpty(state.ArrivalTrain) || IsStrayArrival(state, scene)) continue;
                    var wagon = CreateFromPrefab(pair.Key, state, tracks);
                    if (wagon != null) { Pose(wagon); UnpackLeftoverCargo(wagon, state); s_Wagons.Add(wagon); }
                }
            foreach (var wagon in s_Wagons)
            {
                if (wagon.PendingCoupledToKey == null) continue;
                wagon.CoupledToWagon = s_Wagons.Find(candidate => candidate.Key == wagon.PendingCoupledToKey);
                wagon.PendingCoupledToKey = null;
                if (wagon.CoupledToWagon == null) wagon.ConsistSign = 0;
            }
            if (s_Wagons.Count > 0) SteamRailwayMod.Log.Msg($"{scene}: {s_Wagons.Count} of {marked.Count} movable wagon(s) stand on the lines.");
        }

        static void Restore(RailWagon wagon, WagonState state, List<BuiltTrack> tracks)
        {
            var track = tracks.Find(candidate => candidate.Identifier == state.RouteId);
            if (track == null) return;
            wagon.Track = track;
            wagon.Position = Mathf.Clamp(state.SplinePos, LineEndMarginInMeters, track.Spline.Length - LineEndMarginInMeters);
            wagon.Flipped = state.Flipped;
            if (string.IsNullOrEmpty(state.CoupledTo) || state.ConsistSign == 0) return;
            wagon.ConsistSign = state.ConsistSign;
            if (state.CoupledTo.StartsWith(TrainLinkPrefix)) wagon.CoupledToTrain = state.CoupledTo.Substring(TrainLinkPrefix.Length);
            else wagon.PendingCoupledToKey = state.CoupledTo;
        }

        /// The fallback when the region's build did not find the wagon (a region without object edits): one search of the scene.
        static Transform SearchScene(MovableWagonData data, ref List<Transform> sceneSearch)
        {
            if (sceneSearch == null)
            {
                sceneSearch = new List<Transform>();
                foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
                    if (transform.gameObject.scene.name != null) sceneSearch.Add(transform);
            }
            var point = new Vector3(data.Position[0], data.Position[1], data.Position[2]);
            foreach (var transform in sceneSearch)
                if (transform != null && transform.name == data.Name && Vector3.Distance(ObjectEdits.OriginalPosition(transform), point) <= 0.5f) return transform;
            return null;
        }

        /// The wagon on the nearest line (null if no line passes under it): its pose relative to the line and its length.
        static RailWagon PutOnLine(string key, string name, Transform root, List<BuiltTrack> tracks)
        {
            var pivot = root.position;
            BuiltTrack best = null;
            float bestArc = 0f, bestDistance = float.MaxValue;
            foreach (var track in tracks)
            {
                if (track.Path == null || track.Spline == null) continue;
                float arc = track.Path.NearestDistance(pivot, out float flatDistance);
                if (flatDistance < bestDistance) { bestDistance = flatDistance; best = track; bestArc = arc; }
            }
            if (best == null || bestDistance > AttachDistanceInMeters)
            {
                SteamRailwayMod.Debug($"Wagon {name}: no line under it ({bestDistance:F1} m to the nearest); it stays as it is.");
                return null;
            }
            float splinePerPath = best.Spline.Length / Mathf.Max(MinimumPathLengthInMeters, best.Path.Length);
            best.Path.Sample(bestArc, out var point, out var tangent);
            var frame = Quaternion.LookRotation(tangent, Vector3.up);
            var inverse = Quaternion.Inverse(frame);
            var wagon = new RailWagon
            {
                Key = key,
                Name = name,
                Root = root,
                Track = best,
                Position = bestArc * splinePerPath,
                LocalOffset = inverse * (pivot - point),
                LocalRotation = inverse * root.rotation,
                MassInKilograms = MassOf(name),
            };
            MeasureReach(wagon, tangent, splinePerPath);
            AlignToLine(wagon, best);
            return wagon;
        }

        /// How far the wagon's meshes reach along the line from its pivot, both ways.
        static void MeasureReach(RailWagon wagon, Vector3 tangent, float splinePerPath)
        {
            var pivot = wagon.Root.position;
            float forward = 0f, backward = 0f;
            foreach (var filter in wagon.Root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                var bounds = mesh.bounds;
                var matrix = filter.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    var local = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                                            (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                                            (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    float along = Vector3.Dot(matrix.MultiplyPoint3x4(local) - pivot, tangent);
                    if (along > forward) forward = along;
                    if (-along > backward) backward = -along;
                }
            }
            wagon.ReachForward = forward * splinePerPath;
            wagon.ReachBackward = backward * splinePerPath;

            float couplerForward = 0f, couplerBackward = 0f;
            var rootUp = wagon.Root.up;
            wagon.CouplerGroupsForward.Clear();
            wagon.CouplerGroupsBackward.Clear();
            foreach (var filter in wagon.Root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || !filter.name.Contains(CouplerPartName) || !mesh.isReadable) continue;
                var matrix = filter.transform.localToWorldMatrix;
                var vertices = mesh.vertices;
                float outermost = float.MinValue, innermost = float.MaxValue;
                for (int index = 0; index < vertices.Length; index++)
                {
                    var offset = matrix.MultiplyPoint3x4(vertices[index]) - pivot;
                    float along = Vector3.Dot(offset, tangent);
                    // only the couplers on the wagon's centre line (not clamps lying loose on its deck)
                    var sideways = offset - tangent * along - rootUp * Vector3.Dot(offset, rootUp);
                    if (sideways.magnitude > CouplerLateralLimitInMeters) continue;
                    if (along > outermost) outermost = along;
                    if (along < innermost) innermost = along;
                }
                if (outermost == float.MinValue) continue;
                var renderer = filter.GetComponent<Renderer>();
                var group = filter.transform.parent;
                if (filter.gameObject.activeInHierarchy && group != null)
                {
                    var groups = innermost > 0f ? wagon.CouplerGroupsForward : (outermost < 0f ? wagon.CouplerGroupsBackward : null);
                    if (groups != null && !groups.Contains(group)) groups.Add(group);
                }
                if (outermost > couplerForward)
                {
                    couplerForward = outermost;
                    wagon.CouplerMountForward = innermost * splinePerPath;
                    wagon.CouplerGroupForward = filter.transform.parent;
                    if (renderer != null) wagon.CouplerMaterial = renderer.sharedMaterial;
                }
                if (-innermost > couplerBackward)
                {
                    couplerBackward = -innermost;
                    wagon.CouplerMountBackward = -outermost * splinePerPath;
                    wagon.CouplerGroupBackward = filter.transform.parent;
                    if (renderer != null) wagon.CouplerMaterial = renderer.sharedMaterial;
                }
            }
            wagon.CouplerReachForward = couplerForward > 0f ? couplerForward * splinePerPath : wagon.ReachForward;
            wagon.CouplerReachBackward = couplerBackward > 0f ? couplerBackward * splinePerPath : wagon.ReachBackward;
        }

        /// Places the wagon's scene object at its line position.
        static void Pose(RailWagon wagon)
        {
            if (wagon.Root == null || wagon.Track == null) return;
            SampleLine(wagon.Track, wagon.Position, out var point, out var tangent);
            var frame = Quaternion.LookRotation(wagon.Flipped ? -tangent : tangent, Vector3.up);
            wagon.Root.SetPositionAndRotation(point + frame * wagon.LocalOffset, frame * wagon.LocalRotation);
            for (int index = wagon.Cargo.Count - 1; index >= 0; index--)
            {
                var item = wagon.Cargo[index];
                if (item.Gear == null && item.Object == null || item.Gear != null && (item.Gear.m_InPlayerInventory || item.Gear.m_InsideContainer)) { if (item.Gear != null) s_CargoGear.Remove(item.Gear); wagon.Cargo.RemoveAt(index); continue; }
                if (item.Object != null) item.Object.SetPositionAndRotation(wagon.Root.TransformPoint(item.LocalPosition), wagon.Root.rotation * item.LocalRotation);
            }
        }

        /// A rolling wagon's leading end meets a marked tree (knocked away, most of the speed kept) or a rockfall (it stops);
        /// a coupled wagon's engine slows or stops with it.
        static void MeetObstacles(RailWagon wagon, List<LiveTrain> trains)
        {
            if (wagon.Track == null || Mathf.Abs(wagon.Velocity) < RestSpeedInMetersPerSecond) return;
            float lead = wagon.Velocity > 0f ? wagon.Position + wagon.ReachTowardsEnd : wagon.Position - wagon.ReachTowardsStart;
            float velocity = RailwayManager.WagonAgainstObstacles(wagon.Track, wagon.Position, lead, wagon.Velocity);
            if (velocity == wagon.Velocity) return;
            float kept = velocity / wagon.Velocity;
            wagon.Velocity = velocity;
            var liveTrain = ConsistTrain(wagon, trains);
            if (liveTrain != null && liveTrain.Simulation != null) liveTrain.Simulation.RestoreMotion(liveTrain.Simulation.Velocity * kept);
        }

        // ------------------------------------------------------------------ loot on wagons

        const float GearScanIntervalInSeconds = 1f;
        const float StationarySpeedInMetersPerSecond = 0.05f;
        const int GearScanBufferSize = 512;
        // an Il2Cpp array: a managed Collider[] is copied on the way in and the results never come back
        static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> s_GearScanBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(GearScanBufferSize);
        static readonly HashSet<GearItem> s_CargoGear = new HashSet<GearItem>();

        /// The gear item rides on a wagon (the cab does not take it in).
        public static bool IsCargo(GearItem gearItem) => gearItem != null && s_CargoGear.Contains(gearItem);

        static int s_GearLayerMask;

        /// Only the gear layer is searched: a busy place has hundreds of other colliders that would fill the buffer.
        static int GearLayerMask()
        {
            if (s_GearLayerMask == 0) s_GearLayerMask = LayerMask.GetMask(GearLayerName);
            return s_GearLayerMask != 0 ? s_GearLayerMask : ~0;
        }

        const string GearLayerName = "Gear";

        static bool IsLoose(GearItem gearItem) =>
            gearItem != null && gearItem.gameObject.activeInHierarchy && !gearItem.m_InPlayerInventory && !gearItem.m_InsideContainer;

        /// A wagon at rest looks about once a second for loose loot lying on it (spawned by the region or put down by
        /// the player) and takes it along: from then on it keeps its place relative to the wagon.
        static void ScanGearCargo(RailWagon wagon)
        {
            if (Time.time < wagon.NextGearScanTime || Mathf.Abs(wagon.Velocity) > StationarySpeedInMetersPerSecond || wagon.Root == null) return;
            wagon.NextGearScanTime = Time.time + GearScanIntervalInSeconds;
            if (!wagon.HasFootprint && !MeasureFootprint(wagon)) return;
            var root = wagon.Root;
            var halfExtents = wagon.LocalFootprint.extents + new Vector3(CargoHorizontalMarginInMeters, CargoHeightAboveInMeters * 0.5f, CargoHorizontalMarginInMeters);
            var center = root.TransformPoint(wagon.LocalFootprint.center + Vector3.up * (CargoHeightAboveInMeters * 0.5f));
            if (Time.time < wagon.HomeScanEndTime) TakeLootLeftAtHome(wagon, halfExtents);
            int hits = Physics.OverlapBoxNonAlloc(center, halfExtents, s_GearScanBuffer, root.rotation, GearLayerMask(), QueryTriggerInteraction.Collide);
            for (int index = 0; index < hits; index++)
            {
                var collider = s_GearScanBuffer[index];
                var gearItem = collider != null ? collider.GetComponentInParent<GearItem>() : null;
                if (gearItem == null || s_CargoGear.Contains(gearItem) || !IsLoose(gearItem) || gearItem.transform.IsChildOf(root)) continue;
                if (CabItems.IsTracked(gearItem)) continue;
                var body = gearItem.GetComponent<Rigidbody>();
                if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
                wagon.Cargo.Add(new CargoItem
                {
                    Object = gearItem.transform,
                    Gear = gearItem,
                    LocalPosition = root.InverseTransformPoint(gearItem.transform.position),
                    LocalRotation = Quaternion.Inverse(root.rotation) * gearItem.transform.rotation,
                });
                s_CargoGear.Add(gearItem);
                SteamRailwayMod.Log.Msg($"Wagon {wagon.Name} took {gearItem.name} on board ({wagon.Cargo.Count} item(s) ride on it).");
            }
        }

        const float HomeLootSearchInSeconds = 20f;

        /// A wagon restored away from where the region placed it: loose loot still lying in its old place (left there when it
        /// was moved before loot rode along) is put back on it, at the same place relative to the wagon.
        static void TakeLootLeftAtHome(RailWagon wagon, Vector3 halfExtents)
        {
            var root = wagon.Root;
            var homeRotation = wagon.HomeMatrix.rotation;
            var homeCenter = wagon.HomeMatrix.MultiplyPoint3x4(wagon.LocalFootprint.center + Vector3.up * (CargoHeightAboveInMeters * 0.5f));
            var toHome = wagon.HomeMatrix.inverse;
            int hits = Physics.OverlapBoxNonAlloc(homeCenter, halfExtents, s_GearScanBuffer, homeRotation, GearLayerMask(), QueryTriggerInteraction.Collide);
            for (int index = 0; index < hits; index++)
            {
                var collider = s_GearScanBuffer[index];
                var gearItem = collider != null ? collider.GetComponentInParent<GearItem>() : null;
                if (gearItem == null || s_CargoGear.Contains(gearItem) || !IsLoose(gearItem) || CabItems.IsTracked(gearItem)) continue;
                var localPosition = toHome.MultiplyPoint3x4(gearItem.transform.position);
                var localRotation = Quaternion.Inverse(homeRotation) * gearItem.transform.rotation;
                gearItem.transform.SetPositionAndRotation(root.TransformPoint(localPosition), root.rotation * localRotation);
                var body = gearItem.GetComponent<Rigidbody>();
                if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
                wagon.Cargo.Add(new CargoItem { Object = gearItem.transform, Gear = gearItem, LocalPosition = localPosition, LocalRotation = localRotation });
                s_CargoGear.Add(gearItem);
            }
        }


        /// Lamps, light shafts and glows reach far beyond the wagon; they are left out of its footprint.
        static bool IsLightPart(string name) =>
            name.Contains("Light") || name.Contains("light") || name.Contains("Shaft") || name.Contains("Glow") || name.Contains("Beam") || name.Contains("Flare");

        static bool MeasureFootprint(RailWagon wagon)
        {
            var bodies = new List<Renderer>();
            BodyRenderers(wagon.Root, bodies);
            bodies.RemoveAll(renderer => IsLightPart(renderer.name) || IsLightPart(renderer.transform.parent != null ? renderer.transform.parent.name : ""));
            if (bodies.Count == 0) return false;
            var bounds = bodies[0].bounds;
            for (int index = 1; index < bodies.Count; index++) bounds.Encapsulate(bodies[index].bounds);
            var localBounds = new Bounds(wagon.Root.InverseTransformPoint(bounds.center), Vector3.zero);
            for (int corner = 0; corner < BoundsCornerCount; corner++)
                localBounds.Encapsulate(wagon.Root.InverseTransformPoint(bounds.center + new Vector3(
                    (corner & 1) == 0 ? -bounds.extents.x : bounds.extents.x, (corner & 2) == 0 ? -bounds.extents.y : bounds.extents.y, (corner & 4) == 0 ? -bounds.extents.z : bounds.extents.z)));
            wagon.LocalFootprint = localBounds;
            wagon.HasFootprint = true;
            return true;
        }

        const int BoundsCornerCount = 8;

        // ------------------------------------------------------------------ cargo and alignment

        const float MaximumAlignLateralInMeters = 1f;
        const float MaximumAlignVerticalInMeters = 0.6f;
        const float CargoHorizontalMarginInMeters = 0.3f;
        const float CargoHeightAboveInMeters = 2.5f;

        /// The renderers that decide the wagon's size: no snow piles, shadows or particles.
        static void BodyRenderers(Transform root, List<Renderer> result)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                string name = renderer.name;
                if (name.Contains("Snow") || name.Contains("Shadow")) continue;
                result.Add(renderer);
            }
        }

        static bool IsWheel(Renderer renderer, Transform root)
        {
            for (var node = renderer.transform; node != null && node != root.parent; node = node.parent)
                if (node.name.Contains("Wheel")) return true;
            return false;
        }

        /// Logs and timber the region laid on the wagon (separate objects, not parts of it): inside its footprint and above its
        /// floor. They are remembered with their place relative to the wagon.
        static List<CargoItem> FindCargo(Transform root)
        {
            var cargo = new List<CargoItem>();
            var bodies = new List<Renderer>();
            BodyRenderers(root, bodies);
            if (bodies.Count == 0) return cargo;
            var bounds = bodies[0].bounds;
            for (int index = 1; index < bodies.Count; index++) bounds.Encapsulate(bodies[index].bounds);
            bounds.Expand(new Vector3(CargoHorizontalMarginInMeters * 2f, 0f, CargoHorizontalMarginInMeters * 2f));
            IEnumerable<Transform> candidates = ObjectEdits.LooseLogsKnown ? (IEnumerable<Transform>)ObjectEdits.LooseLogs : SceneLogs();
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate == root || candidate.IsChildOf(root) || !candidate.gameObject.activeInHierarchy) continue;
                var point = candidate.position;
                if (point.x < bounds.min.x || point.x > bounds.max.x || point.z < bounds.min.z || point.z > bounds.max.z) continue;
                if (point.y < bounds.min.y || point.y > bounds.max.y + CargoHeightAboveInMeters) continue;
                if (Obstacles.IsMarkedTree(candidate.name, new[] { point.x, point.y, point.z })) continue;
                cargo.Add(new CargoItem
                {
                    Object = candidate,
                    LocalPosition = root.InverseTransformPoint(candidate.position),
                    LocalRotation = Quaternion.Inverse(root.rotation) * candidate.rotation,
                });
            }
            return cargo;
        }

        /// The fallback for a region without object edits (no name table): one search of the scene.
        static List<Transform> SceneLogs()
        {
            var logs = new List<Transform>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null) continue;
                string name = transform.name;
                if (name.Contains("_Prefab") && (name.StartsWith("OBJ_Log") || name.StartsWith("TRN_Log")) && !name.Contains("Rail")) logs.Add(transform);
            }
            return logs;
        }

        /// The wagon stands as the locomotive does: upright (no roll or pitch against the rails), centred between the rails,
        /// its wheels on the rail heads. The region placed some wagons a little crooked or sunk; it is not kept.
        static void AlignToLine(RailWagon wagon, BuiltTrack track)
        {
            var euler = wagon.LocalRotation.eulerAngles;
            wagon.LocalRotation = Quaternion.Euler(0f, Mathf.Round(euler.y / RightAngleInDegrees) * RightAngleInDegrees, 0f);
            wagon.LocalOffset = new Vector3(0f, wagon.LocalOffset.y, wagon.LocalOffset.z);
            Pose(wagon);
            var bodies = new List<Renderer>();
            BodyRenderers(wagon.Root, bodies);
            var wheels = new List<Renderer>();
            foreach (var renderer in bodies) if (IsWheel(renderer, wagon.Root)) wheels.Add(renderer);
            var measured = wheels.Count > 0 ? wheels : bodies;
            if (measured.Count == 0) return;
            var bounds = measured[0].bounds;
            for (int index = 1; index < measured.Count; index++) bounds.Encapsulate(measured[index].bounds);
            SampleLine(track, wagon.Position, out var point, out var tangent);
            var right = Quaternion.LookRotation(wagon.Flipped ? -tangent : tangent, Vector3.up) * Vector3.right;
            // a measurement from the meshes can be fooled (a wagon whose wheels are drawn by instancing): the correction is limited
            float lateral = Mathf.Clamp(Vector3.Dot(bounds.center - point, right), -MaximumAlignLateralInMeters, MaximumAlignLateralInMeters);
            float vertical = Mathf.Clamp(point.y - bounds.min.y, -MaximumAlignVerticalInMeters, MaximumAlignVerticalInMeters);
            // wheel bottoms on the rail tops (the line runs along the rail tops)
            wagon.LocalOffset = new Vector3(wagon.LocalOffset.x - lateral, wagon.LocalOffset.y + vertical, wagon.LocalOffset.z);
            SteamRailwayMod.Debug($"Wagon {wagon.Name}: aligned (roll/pitch levelled, {lateral * 100f:F0} cm sideways, {vertical * 100f:F0} cm up, {(wheels.Count > 0 ? "wheels" : "whole body")} measured).");
            Pose(wagon);
        }

        static void SampleLine(BuiltTrack track, float splinePosition, out Vector3 point, out Vector3 tangent)
        {
            float pathPerSpline = track.Path.Length / Mathf.Max(MinimumPathLengthInMeters, track.Spline.Length);
            track.Path.Sample(splinePosition * pathPerSpline, out point, out tangent);
        }

        /// Writes every wagon's place and link into the save data.
        public static void Capture(Dictionary<string, WagonState> saved)
        {
            if (saved == null) return;
            CaptureStrayDepartures(saved);
            foreach (var wagon in s_Wagons)
            {
                if (wagon.Track == null || s_StrayHomecomers.Contains(wagon)) continue;
                bool leaving = wagon.TransitDestination != null;
                saved[wagon.Key] = new WagonState
                {
                    RouteId = wagon.Track.Identifier,
                    SplinePos = wagon.Position,
                    Flipped = wagon.Flipped,
                    CoupledTo = wagon.CoupledToTrain != null ? TrainLinkPrefix + wagon.CoupledToTrain : wagon.CoupledToWagon?.Key,
                    ConsistSign = wagon.HasLink ? wagon.ConsistSign : 0,
                    Scene = leaving ? wagon.TransitDestination : wagon.Scene,
                    Name = wagon.Name,
                    Prefab = wagon.PrefabPath,
                    LocalOffset = new[] { wagon.LocalOffset.x, wagon.LocalOffset.y, wagon.LocalOffset.z },
                    LocalRotation = new[] { wagon.LocalRotation.x, wagon.LocalRotation.y, wagon.LocalRotation.z, wagon.LocalRotation.w },
                    ReachForward = wagon.ReachForward,
                    ReachBackward = wagon.ReachBackward,
                    Mass = wagon.MassInKilograms,
                    ArrivalTrain = leaving ? wagon.TransitTrain : null,
                    ArrivalOffset = leaving ? wagon.TransitOffset : 0f,
                    ArrivalAgainstTrain = leaving && wagon.TransitAgainstTrain,
                    ArrivalCargo = leaving ? wagon.TransitCargo : null,
                };
            }
        }

        // ------------------------------------------------------------------ consists

        /// The locomotive at the head of the wagon's chain of links, or null (free, or its chain ends in a free wagon).
        static LiveTrain ConsistTrain(RailWagon wagon, List<LiveTrain> trains)
        {
            var node = wagon;
            for (int depth = 0; node != null && depth < MaximumChainLength; depth++)
            {
                if (node.CoupledToTrain != null)
                {
                    for (int index = 0; index < trains.Count; index++)
                        if (trains[index].Guid == node.CoupledToTrain && trains[index].Train != null && trains[index].Simulation != null) return trains[index];
                    return null;
                }
                node = node.CoupledToWagon;
            }
            return null;
        }

        /// How many links the wagon is from its locomotive (1: coupled to it); 0 when not in a consist.
        static int ChainDepth(RailWagon wagon)
        {
            var node = wagon;
            for (int depth = 1; node != null && depth <= MaximumChainLength; depth++)
            {
                if (node.CoupledToTrain != null) return depth;
                node = node.CoupledToWagon;
            }
            return 0;
        }

        /// The locomotive has wagons coupled to it (it may not leave the region then).
        /// The farthest end of the train's coupled wagons on its line in the direction of travel (spline metres), or NaN.
        public static float ConsistLeadingEnd(LiveTrain liveTrain, List<LiveTrain> trains, string trackIdentifier, int direction)
        {
            float leading = float.NaN;
            foreach (var wagon in s_Wagons)
            {
                if (wagon.Track == null || wagon.Track.Identifier != trackIdentifier || ConsistTrain(wagon, trains) != liveTrain) continue;
                float end = direction > 0 ? wagon.Position + wagon.ReachTowardsEnd : wagon.Position - wagon.ReachTowardsStart;
                if (float.IsNaN(leading) || (direction > 0 ? end > leading : end < leading)) leading = end;
            }
            return leading;
        }

        public static bool HasCoupledWagons(LiveTrain liveTrain)
        {
            foreach (var wagon in s_Wagons)
                if (wagon.CoupledToTrain == liveTrain.Guid) return true;
            return false;
        }

        /// The train reached a region transition: the wagons coupled to it come along when they can be built again in the next
        /// region (a copy of the game's prefab) and stand in a row on the train's line. Otherwise `reason` says why not.
        public static bool CanTravel(LiveTrain liveTrain, List<LiveTrain> trains, out string reason)
        {
            reason = null;
            foreach (var wagon in s_Wagons)
            {
                if (ConsistTrain(wagon, trains) != liveTrain) continue;
                if (wagon.Track == null || wagon.Track.Identifier != liveTrain.RouteId || wagon.ConsistSign != liveTrain.Simulation.FacingSign)
                {
                    reason = Localizer.Translate("Vagonlar makasta ya da başka hatta: önce düzgün hizala.", "The wagons are on a switch or another line: straighten the train first.");
                    return false;
                }
                if (LoadPrefab(wagon.PrefabPath) == null)
                {
                    reason = Localizer.Translate("Bu vagon bölgeden çıkarılamaz: ayır.", "This wagon can't leave the region: uncouple it.");
                    return false;
                }
            }
            return true;
        }

        /// The train stops at the transition: the wagons cannot come along.
        public static void RefuseTransit(LiveTrain liveTrain, string reason)
        {
            liveTrain.Simulation.RestoreMotion(0f);
            if (Time.time < s_NextTransitMessage) return;
            s_NextTransitMessage = Time.time + TransitMessageIntervalInSeconds;
            CabControls.Message(reason);
        }

        /// The train goes into another region: its wagons are marked to be placed behind it when the region is built.
        public static void PrepareTransit(LiveTrain liveTrain, string destinationScene, List<LiveTrain> trains)
        {
            float trainPosition = liveTrain.Train.m_SplineController.Position;
            int facing = liveTrain.Simulation.FacingSign;
            foreach (var wagon in s_Wagons)
            {
                if (ConsistTrain(wagon, trains) != liveTrain) continue;
                wagon.TransitDestination = destinationScene;
                wagon.TransitTrain = liveTrain.Guid;
                wagon.TransitOffset = (wagon.Position - trainPosition) * facing;
                wagon.TransitAgainstTrain = wagon.Flipped ^ (facing < 0);
                wagon.CoupledToTrain = wagon.CoupledToTrain != null ? liveTrain.Guid : null;
                // the loot is taken out of this region before it is saved, and built again on the wagon in the next one
                var packed = PackCargo(wagon);
                wagon.TransitCargo = packed.Count > 0 ? packed : null;
            }
        }

        /// The next region has no railway to take the train: the wagons stay with it where it parks.
        public static void RevertTransit(string trainGuid, Dictionary<string, WagonState> saved)
        {
            foreach (var pair in saved)
            {
                var state = pair.Value;
                if (state.ArrivalTrain != trainGuid) continue;
                state.ArrivalTrain = null;
                state.Scene = null;
            }
        }

        /// A train arrived: the wagons that came with it are built from the game's prefabs and placed in a row behind it.
        public static void PlaceArrived(string scene, LiveTrain liveTrain, List<BuiltTrack> tracks, Dictionary<string, WagonState> saved)
        {
            if (saved == null || liveTrain.Train == null) return;
            var track = tracks.Find(candidate => candidate.Identifier == liveTrain.RouteId);
            if (track == null) return;
            float trainPosition = liveTrain.Train.m_SplineController.Position;
            int facing = liveTrain.Simulation.FacingSign;
            var arrived = new List<RailWagon>();
            foreach (var pair in saved)
            {
                var state = pair.Value;
                if (state.Scene != scene || state.ArrivalTrain != liveTrain.Guid) continue;
                state.RouteId = track.Identifier;
                state.SplinePos = Mathf.Clamp(trainPosition + state.ArrivalOffset * facing, LineEndMarginInMeters, track.Spline.Length - LineEndMarginInMeters);
                state.Flipped = state.ArrivalAgainstTrain ^ (facing < 0);
                if (state.ConsistSign != 0) state.ConsistSign = facing;
                state.ArrivalTrain = null;
                var wagon = CreateFromPrefab(pair.Key, state, tracks);
                if (wagon == null) continue;
                wagon.CouplingGap = float.NaN;
                Pose(wagon);
                UnpackCargo(wagon, state.ArrivalCargo);
                state.ArrivalCargo = null;
                s_Wagons.Add(wagon);
                arrived.Add(wagon);
            }
            foreach (var wagon in arrived)
            {
                if (wagon.PendingCoupledToKey == null) continue;
                wagon.CoupledToWagon = s_Wagons.Find(candidate => candidate.Key == wagon.PendingCoupledToKey);
                wagon.PendingCoupledToKey = null;
            }
            if (arrived.Count > 0) SteamRailwayMod.Log.Msg($"{arrived.Count} coupled wagon(s) came along into {scene}.");
        }

        // ------------------------------------------------------------------ wagons rolling into another region on their own

        /// A free wagon that may not leave stops this far short of the transition (spline metres).
        const float StrayStopShortInMeters = 1.5f;
        /// A wagon rolling in stands this far inside the transition's edge, and this far from what already stands there.
        const float StrayArrivalClearanceInMeters = 4f;
        const float StrayArrivalGapInMeters = 2f;
        const int StrayPlacementAttemptCount = 16;

        sealed class StrayDeparture
        {
            public RailWagon Wagon;
            public string FromScene, Destination;
            public float Speed;
            public bool AgainstMotion;
            public List<CabItemState> Cargo;
            public string CoupledToKey;
            public long DepartedTicks;
        }

        /// A wagon that rolled in, with the wagon it was coupled to (to couple them again once all have come in).
        sealed class StrayArrival
        {
            public RailWagon Wagon;
            public WagonState State;
            public string CoupledToKey;
            public Portal Portal;
            public int Inward;
            public float Depth => Inward * (Wagon.Position - Portal.SplineMeters);
        }

        static readonly List<string> s_StrayArrivalKeys = new List<string>();
        static readonly List<StrayArrival> s_StrayArrivals = new List<StrayArrival>();
        /// A wagon closes up to the one it was coupled to only if nothing stood between them (no further apart than this).
        const float StrayRecouplingReachInMeters = 4f;

        /// Wagons that rolled out of this region, written to the save with the next capture.
        static readonly List<StrayDeparture> s_StrayDepartures = new List<StrayDeparture>();
        /// The region's own wagons that rolled back home: on the line, waiting to be placed at the entry.
        static readonly List<RailWagon> s_StrayHomecomers = new List<RailWagon>();

        static bool IsStrayArrival(WagonState state, string scene) => !string.IsNullOrEmpty(state.StrayFromScene) && state.Scene == scene;

        /// A free wagon whose leading end rolls into a transition goes to the next region, uncoupled from its loose group
        /// (the rest follow one by one); one without a prefab to be built from stops short of it.
        static void LeaveThroughPortals(List<LiveTrain> trains)
        {
            for (int index = s_Wagons.Count - 1; index >= 0; index--)
            {
                var wagon = s_Wagons[index];
                if (wagon.Track == null || wagon.Root == null || Mathf.Abs(wagon.Velocity) < RestSpeedInMetersPerSecond || ConsistTrain(wagon, trains) != null) continue;
                float lead = wagon.Velocity > 0f ? wagon.Position + wagon.ReachTowardsEnd : wagon.Position - wagon.ReachTowardsStart;
                var portal = RailwayManager.StrayPortalReached(wagon.Track, lead, wagon.Velocity);
                if (portal == null) continue;
                if (LoadPrefab(wagon.PrefabPath) == null) { StopShortOf(wagon, portal, lead); continue; }
                string coupledToKey = wagon.CoupledToWagon != null ? wagon.CoupledToWagon.Key : wagon.StrayCoupledToKey;
                DetachFromGroup(wagon);
                Depart(wagon, index, portal.DestinationScene, coupledToKey);
            }
        }

        /// The wagon lets go of its group here; the links are remembered and made again in the next region.
        static void DetachFromGroup(RailWagon wagon)
        {
            Unlink(wagon);
            for (int index = 0; index < s_Wagons.Count; index++)
            {
                var other = s_Wagons[index];
                if (other.CoupledToWagon != wagon) continue;
                Unlink(other);
                other.StrayCoupledToKey = wagon.Key;
            }
        }

        static void StopShortOf(RailWagon wagon, Portal portal, float lead)
        {
            float stopLead = portal.AtEnd ? portal.SplineMeters - StrayStopShortInMeters : portal.SplineMeters + StrayStopShortInMeters;
            wagon.Position = Mathf.Clamp(wagon.Position - (lead - stopLead), LineEndMarginInMeters, wagon.Track.Spline.Length - LineEndMarginInMeters);
            wagon.Velocity = 0f;
            wagon.Moved = true;
            SteamRailwayMod.Debug($"Wagon {wagon.Name} stopped at the transition to {portal.DestinationScene}: it cannot leave the region.");
        }

        /// The wagon rolls out of the region: it is taken off the lines and its object hidden (a copy built from the
        /// prefab is destroyed). Loot riding on it is packed up and built again on it in the next region.
        static void Depart(RailWagon wagon, int index, string destinationScene, string coupledToKey)
        {
            var departure = new StrayDeparture
            {
                Wagon = wagon,
                CoupledToKey = coupledToKey,
                DepartedTicks = System.DateTime.UtcNow.Ticks,
                FromScene = s_Scene,
                Destination = destinationScene,
                Speed = Mathf.Abs(wagon.Velocity),
                AgainstMotion = wagon.Flipped ^ (wagon.Velocity < 0f),
            };
            s_StrayDepartures.Add(departure);
            s_Wagons.RemoveAt(index);
            if (s_Coupling != null && (s_Coupling.Wagon == wagon || s_Coupling.OtherWagon == wagon)) s_Coupling = null;
            wagon.Root.gameObject.SetActive(false);
            departure.Cargo = PackCargo(wagon);
            if (!wagon.Key.StartsWith(s_Scene + "|")) Object.Destroy(wagon.Root.gameObject);
            SteamRailwayMod.Log.Msg($"Wagon {wagon.Name} rolled out of {s_Scene} into {destinationScene} at {Mathf.Abs(wagon.Velocity):F1} m/s with {departure.Cargo.Count} loot item(s).");
        }

        /// Logs lying on the wagon stay hidden with its home object. Loose loot is saved with its place on the wagon and
        /// taken out of this region; what cannot be saved drops to the ground here.
        static List<CabItemState> PackCargo(RailWagon wagon)
        {
            var packed = new List<CabItemState>();
            foreach (var item in wagon.Cargo)
            {
                if (item.Object == null) continue;
                if (item.Gear == null) { item.Object.gameObject.SetActive(false); continue; }
                var gearItem = item.Gear;
                s_CargoGear.Remove(gearItem);
                // taken by the player or put in a container: no cargo any more (hidden or switched off, it still is)
                if (gearItem.m_InPlayerInventory || gearItem.m_InsideContainer) continue;
                try
                {
                    packed.Add(new CabItemState
                    {
                        Prefab = gearItem.name.Replace(CloneSuffix, "").Trim(),
                        Data = gearItem.SerializeToString(),
                        Position = new[] { item.LocalPosition.x, item.LocalPosition.y, item.LocalPosition.z },
                        Rotation = new[] { item.LocalRotation.x, item.LocalRotation.y, item.LocalRotation.z, item.LocalRotation.w },
                    });
                    GearManager.Remove(gearItem);
                    Object.Destroy(gearItem.gameObject);
                }
                catch (System.Exception exception)
                {
                    SteamRailwayMod.Debug($"Wagon loot {gearItem.name} not carried along: {exception.Message}");
                    DropToGround(item.Object);
                }
            }
            wagon.Cargo.Clear();
            return packed;
        }

        const string CloneSuffix = "(Clone)";

        static void DropToGround(Transform item)
        {
            // the item itself is hidden while the ground under it is sought, so the ray does not stop on it
            item.gameObject.SetActive(false);
            var ground = Il2CppHelpers.GroundBelow(item.position);
            item.gameObject.SetActive(true);
            item.position = ground;
        }

        /// The loot that came along is built again on the wagon at its old place and rides on as its cargo.
        static void UnpackCargo(RailWagon wagon, List<CabItemState> packed)
        {
            if (packed == null || wagon.Root == null) return;
            foreach (var itemState in packed)
            {
                if (string.IsNullOrEmpty(itemState.Prefab) || itemState.Position == null || itemState.Rotation == null
                    || itemState.Position.Length < PositionComponentCount || itemState.Rotation.Length < RotationComponentCount) continue;
                try
                {
                    var gearItem = GearItem.InstantiateAndDeserializeGearItem(itemState.Prefab, itemState.Data, null, false, false);
                    if (gearItem == null) { SteamRailwayMod.Log.Warning($"Wagon loot {itemState.Prefab} could not be made."); continue; }
                    var localPosition = new Vector3(itemState.Position[0], itemState.Position[1], itemState.Position[2]);
                    var localRotation = new Quaternion(itemState.Rotation[0], itemState.Rotation[1], itemState.Rotation[2], itemState.Rotation[3]);
                    gearItem.transform.SetPositionAndRotation(wagon.Root.TransformPoint(localPosition), wagon.Root.rotation * localRotation);
                    var body = gearItem.GetComponent<Rigidbody>();
                    if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
                    wagon.Cargo.Add(new CargoItem { Object = gearItem.transform, Gear = gearItem, LocalPosition = localPosition, LocalRotation = localRotation });
                    s_CargoGear.Add(gearItem);
                }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Wagon loot {itemState.Prefab} not restored: {exception.Message}"); }
            }
        }

        /// Loot packed for a train journey that did not happen (the next region had no railway): built again where it stands.
        static void UnpackLeftoverCargo(RailWagon wagon, WagonState state)
        {
            if (state == null || state.ArrivalCargo == null || !string.IsNullOrEmpty(state.ArrivalTrain)) return;
            UnpackCargo(wagon, state.ArrivalCargo);
            state.ArrivalCargo = null;
        }

        /// The wagons that rolled out are saved as standing in the region they went to, with where they came from.
        static void CaptureStrayDepartures(Dictionary<string, WagonState> saved)
        {
            foreach (var departure in s_StrayDepartures)
            {
                var wagon = departure.Wagon;
                saved[wagon.Key] = new WagonState
                {
                    RouteId = wagon.Track != null ? wagon.Track.Identifier : null,
                    SplinePos = wagon.Position,
                    Flipped = wagon.Flipped,
                    Scene = departure.Destination,
                    Name = wagon.Name,
                    Prefab = wagon.PrefabPath,
                    LocalOffset = new[] { wagon.LocalOffset.x, wagon.LocalOffset.y, wagon.LocalOffset.z },
                    LocalRotation = new[] { wagon.LocalRotation.x, wagon.LocalRotation.y, wagon.LocalRotation.z, wagon.LocalRotation.w },
                    ReachForward = wagon.ReachForward,
                    ReachBackward = wagon.ReachBackward,
                    Mass = wagon.MassInKilograms,
                    StrayFromScene = departure.FromScene,
                    StraySpeed = departure.Speed,
                    StrayAgainstMotion = departure.AgainstMotion,
                    StrayCargo = departure.Cargo != null && departure.Cargo.Count > 0 ? departure.Cargo : null,
                    StrayCoupledTo = departure.CoupledToKey,
                    StrayDepartedTicks = departure.DepartedTicks,
                };
            }
            s_StrayDepartures.Clear();
        }

        /// Once the region's transitions are known: wagons that rolled in from another region come in on the line's way
        /// back there, clear of its trigger and of what stands there, rolling inwards at the speed they left with.
        public static void PlaceStrayArrivals(string scene, List<BuiltTrack> tracks, Dictionary<string, WagonState> saved, List<LiveTrain> trains)
        {
            if (saved == null) { s_StrayHomecomers.Clear(); return; }
            s_StrayArrivalKeys.Clear();
            s_StrayArrivals.Clear();
            foreach (var pair in saved)
                if (IsStrayArrival(pair.Value, scene)) s_StrayArrivalKeys.Add(pair.Key);
            // the wagon that left last comes in first, at the edge: those that left before it stand further in, in their old order
            s_StrayArrivalKeys.Sort((first, second) => saved[second].StrayDepartedTicks.CompareTo(saved[first].StrayDepartedTicks));
            foreach (var key in s_StrayArrivalKeys)
            {
                var state = saved[key];
                var home = s_StrayHomecomers.Find(candidate => candidate.Key == key);
                var portal = RailwayManager.PortalTo(state.StrayFromScene);
                if (portal == null)
                {
                    SteamRailwayMod.Log.Warning($"Wagon {state.Name} rolled in from {state.StrayFromScene}, but no line here leads back there.");
                    // the region's own wagon simply stands where the region put it
                    if (home != null) { UnpackCargo(home, state.StrayCargo); ClearStray(state); }
                    continue;
                }
                int inward = portal.AtEnd ? -1 : 1;
                bool flipped = (inward < 0) ^ state.StrayAgainstMotion;
                var wagon = home;
                if (wagon == null)
                {
                    state.RouteId = portal.Track.Identifier;
                    state.SplinePos = portal.SplineMeters;
                    state.Flipped = flipped;
                    state.CoupledTo = null;
                    state.ConsistSign = 0;
                    wagon = CreateFromPrefab(key, state, tracks);
                    if (wagon == null) continue;
                    s_Wagons.Add(wagon);
                }
                Unlink(wagon);
                wagon.Track = portal.Track;
                wagon.Flipped = flipped;
                wagon.Position = ClearEntryPosition(wagon, portal, inward, trains);
                wagon.Velocity = inward * state.StraySpeed;
                state.RouteId = portal.Track.Identifier;
                state.SplinePos = wagon.Position;
                state.Flipped = flipped;
                Pose(wagon);
                UnpackCargo(wagon, state.StrayCargo);
                s_StrayArrivals.Add(new StrayArrival { Wagon = wagon, State = state, CoupledToKey = state.StrayCoupledTo, Portal = portal, Inward = inward });
                ClearStray(state);
                SteamRailwayMod.Log.Msg($"Wagon {wagon.Name} rolled into {scene} from the transition to {portal.DestinationScene} at {state.StraySpeed:F1} m/s.");
            }
            RecoupleStrayArrivals();
            s_StrayArrivals.Clear();
            s_StrayHomecomers.Clear();
        }

        static void ClearStray(WagonState state)
        {
            state.StrayFromScene = null;
            state.StraySpeed = 0f;
            state.StrayAgainstMotion = false;
            state.StrayCargo = null;
            state.StrayCoupledTo = null;
            state.StrayDepartedTicks = 0;
        }

        /// Wagons that were coupled when they left are coupled again: from the one furthest in outwards, each closes up to
        /// the one further in that it was coupled to, and rolls on at its speed.
        static void RecoupleStrayArrivals()
        {
            foreach (var arrival in s_StrayArrivals)
            {
                if (arrival.CoupledToKey == null) continue;
                var partner = s_StrayArrivals.Find(candidate => candidate.Wagon.Key == arrival.CoupledToKey);
                if (partner == null || partner.Wagon.Track != arrival.Wagon.Track) continue;
                arrival.Wagon.CoupledToWagon = partner.Wagon;
                // any direction marks the link; an engine coupling on sets the real ones (RefreshChainSigns)
                arrival.Wagon.ConsistSign = arrival.Inward;
            }
            s_StrayArrivals.Sort((first, second) => second.Depth.CompareTo(first.Depth));
            for (int index = 1; index < s_StrayArrivals.Count; index++)
            {
                var outer = s_StrayArrivals[index];
                for (int innerIndex = 0; innerIndex < index; innerIndex++)
                {
                    var inner = s_StrayArrivals[innerIndex];
                    bool outerHangs = outer.Wagon.CoupledToWagon == inner.Wagon;
                    if (!outerHangs && inner.Wagon.CoupledToWagon != outer.Wagon) continue;
                    CloseUp(outer, inner, outerHangs);
                    break;
                }
            }
        }

        static void CloseUp(StrayArrival outer, StrayArrival inner, bool outerHangs)
        {
            var hanger = outerHangs ? outer.Wagon : inner.Wagon;
            var holder = outerHangs ? inner.Wagon : outer.Wagon;
            // the outer wagon stands on the transition's side of the inner one
            bool hangerAhead = outerHangs ? outer.Inward < 0 : outer.Inward > 0;
            float separation = CoupledSeparation(hanger, holder, hangerAhead, out float gap);
            float wanted = inner.Wagon.Position - outer.Inward * separation;
            if (Mathf.Abs(wanted - outer.Wagon.Position) > StrayRecouplingReachInMeters) return;
            var wagon = outer.Wagon;
            wagon.Position = Mathf.Clamp(wanted, LineEndMarginInMeters, wagon.Track.Spline.Length - LineEndMarginInMeters);
            wagon.Velocity = inner.Wagon.Velocity;
            hanger.CouplingGap = gap;
            outer.State.SplinePos = wagon.Position;
            Pose(wagon);
            SteamRailwayMod.Log.Msg($"Wagon {wagon.Name} is coupled to {inner.Wagon.Name} again.");
        }

        /// How far apart the pivots of two coupled wagons stand (spline metres), as Follow stands them: wagons with the
        /// game's couplers lock into each other, others keep the buffer gap.
        static float CoupledSeparation(RailWagon hanger, RailWagon holder, bool hangerAhead, out float gap)
        {
            bool wagonLock = hanger.HasCouplers && holder.HasCouplers;
            gap = wagonLock ? LocomotiveCoupledGapInMeters : CoupledGapInMeters;
            if (hangerAhead)
                return (wagonLock ? holder.LockedMountTowardsEnd : holder.ReachTowardsEnd) + gap + (wagonLock ? hanger.CouplerMountTowardsStart : hanger.ReachTowardsStart);
            return (wagonLock ? holder.LockedMountTowardsStart : holder.ReachTowardsStart) + gap + (wagonLock ? hanger.CouplerMountTowardsEnd : hanger.ReachTowardsEnd);
        }

        /// Where the wagon comes in: inside the transition's edge, moved further in past wagons and trains standing there.
        static float ClearEntryPosition(RailWagon wagon, Portal portal, int inward, List<LiveTrain> trains)
        {
            var track = portal.Track;
            float reachTowardsPortal = inward > 0 ? wagon.ReachTowardsStart : wagon.ReachTowardsEnd;
            float position = portal.SplineMeters + inward * (reachTowardsPortal + StrayArrivalClearanceInMeters);
            for (int attempt = 0; attempt < StrayPlacementAttemptCount; attempt++)
            {
                float start = position - wagon.ReachTowardsStart - StrayArrivalGapInMeters;
                float end = position + wagon.ReachTowardsEnd + StrayArrivalGapInMeters;
                bool blocked = false;
                float blockerStart = 0f, blockerEnd = 0f;
                for (int index = 0; index < s_Wagons.Count && !blocked; index++)
                {
                    var other = s_Wagons[index];
                    if (other == wagon || other.Track == null || other.Track.Identifier != track.Identifier) continue;
                    blockerStart = other.Position - other.ReachTowardsStart;
                    blockerEnd = other.Position + other.ReachTowardsEnd;
                    blocked = blockerEnd >= start && blockerStart <= end;
                }
                for (int index = 0; index < trains.Count && !blocked; index++)
                {
                    var liveTrain = trains[index];
                    if (liveTrain.Train == null || liveTrain.Simulation == null || liveTrain.RouteId != track.Identifier) continue;
                    TrainEnds(liveTrain, track, out _, out blockerEnd, out blockerStart);
                    blocked = blockerEnd >= start && blockerStart <= end;
                }
                if (!blocked) break;
                position = inward > 0 ? blockerEnd + StrayArrivalGapInMeters + wagon.ReachTowardsStart
                                      : blockerStart - StrayArrivalGapInMeters - wagon.ReachTowardsEnd;
            }
            return Mathf.Clamp(position, LineEndMarginInMeters, track.Spline.Length - LineEndMarginInMeters);
        }

        // ------------------------------------------------------------------ the game's wagon prefabs

        const string WagonPrefabFolder = "Assets/ArtAssets/Env/Objects/OBJ_RailCar/";
        static readonly Dictionary<string, GameObject> s_PrefabCache = new Dictionary<string, GameObject>();

        /// The addressable prefab the region's wagon object is a copy of: its name without the " (n)" of copies.
        static string PrefabPathFor(string objectName)
        {
            string baseName = objectName;
            int bracket = baseName.LastIndexOf(" (");
            if (bracket > 0) baseName = baseName.Substring(0, bracket);
            return baseName.StartsWith("OBJ_Rail") || baseName.StartsWith("OBJ_LogRail") ? WagonPrefabFolder + baseName + ".prefab" : null;
        }

        static GameObject LoadPrefab(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (WagonSnapshots.IsSnapshotKey(path)) return WagonSnapshots.Template(path);
            if (s_PrefabCache.TryGetValue(path, out var cached)) return cached;
            GameObject prefab = null;
            try { prefab = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(path).WaitForCompletion(); }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Wagon prefab {path}: {exception.Message}"); }
            s_PrefabCache[path] = prefab;
            return prefab;
        }

        /// A wagon built from the game's prefab (a region it was taken to): posed on its line like the original was.
        static RailWagon CreateFromPrefab(string key, WagonState state, List<BuiltTrack> tracks)
        {
            var prefab = LoadPrefab(state.Prefab);
            var track = tracks.Find(candidate => candidate.Identifier == state.RouteId);
            if (prefab == null || track == null || state.LocalOffset == null || state.LocalRotation == null || state.LocalOffset.Length < PositionComponentCount || state.LocalRotation.Length < RotationComponentCount)
            {
                SteamRailwayMod.Log.Warning($"Wagon {state.Name} could not be built in this region (prefab {state.Prefab}).");
                return null;
            }
            var instance = Object.Instantiate(prefab);
            instance.name = state.Name;
            if (WagonSnapshots.IsSnapshotKey(state.Prefab)) WagonSnapshots.PrepareCopy(instance, key);
            instance.SetActive(true);
            var wagon = new RailWagon
            {
                Key = key,
                Name = state.Name,
                Root = instance.transform,
                Track = track,
                Position = Mathf.Clamp(state.SplinePos, LineEndMarginInMeters, track.Spline.Length - LineEndMarginInMeters),
                Flipped = state.Flipped,
                LocalOffset = new Vector3(state.LocalOffset[0], state.LocalOffset[1], state.LocalOffset[2]),
                LocalRotation = new Quaternion(state.LocalRotation[0], state.LocalRotation[1], state.LocalRotation[2], state.LocalRotation[3]),
                ReachForward = state.ReachForward,
                ReachBackward = state.ReachBackward,
                MassInKilograms = state.Mass > 0f ? state.Mass : MassOf(state.Name),
                Scene = state.Scene,
                PrefabPath = state.Prefab,
            };
            Restore(wagon, state, tracks);
            return wagon;
        }

        static float ConsistMass(LiveTrain liveTrain, List<LiveTrain> trains)
        {
            float mass = SteamSimulation.LocomotiveMassInKilograms;
            foreach (var wagon in s_Wagons)
                if (ConsistTrain(wagon, trains) == liveTrain) mass += wagon.MassInKilograms;
            return mass;
        }

        // ------------------------------------------------------------------ every frame

        public static void Update(List<LiveTrain> trains)
        {
            if (s_Wagons.Count == 0) return;
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;
            for (int index = 0; index < trains.Count; index++)
                if (trains[index].Simulation != null) trains[index].Simulation.TrailingMassInKilograms = ConsistMass(trains[index], trains) - SteamSimulation.LocomotiveMassInKilograms;

            for (int index = 0; index < s_Wagons.Count; index++)
                if (ConsistTrain(s_Wagons[index], trains) == null) Roll(s_Wagons[index], deltaTime);
            LeaveThroughPortals(trains);
            if (s_Wagons.Count == 0) return;
            // coupled wagons follow their engine, the nearest links first
            for (int depth = 1; depth <= MaximumChainLength; depth++)
            {
                bool any = false;
                for (int index = 0; index < s_Wagons.Count; index++)
                {
                    var wagon = s_Wagons[index];
                    if (ChainDepth(wagon) != depth) continue;
                    var liveTrain = ConsistTrain(wagon, trains);
                    if (liveTrain == null) continue;
                    any = true;
                    Follow(wagon, liveTrain, deltaTime);
                }
                if (!any) break;
            }
            for (int trainIndex = 0; trainIndex < trains.Count; trainIndex++)
                for (int index = 0; index < s_Wagons.Count; index++) TrainContact(trains[trainIndex], s_Wagons[index], trains);
            for (int first = 0; first < s_Wagons.Count; first++)
                for (int second = first + 1; second < s_Wagons.Count; second++) WagonContact(s_Wagons[first], s_Wagons[second], trains);
            for (int index = 0; index < s_Wagons.Count; index++)
            {
                var wagon = s_Wagons[index];
                UpdateRollingSound(wagon, deltaTime);
                MeetObstacles(wagon, trains);
                ScanGearCargo(wagon);
                if (!wagon.Moved) continue;
                wagon.Moved = false;
                Pose(wagon);
            }
            ApplyCouplerLocks(trains);
            UpdateCouplingZone(trains);
        }

        /// A moving wagon rumbles on the rails and clacks over a rail joint every few metres (a duller sound than the locomotive's).
        static void UpdateRollingSound(RailWagon wagon, float deltaTime)
        {
            if (wagon.Root == null) return;
            float speed = Mathf.Abs(wagon.Velocity);
            if (speed < RollingSoundMinimumSpeedInMetersPerSecond)
            {
                if (wagon.RumbleSource != null && wagon.RumbleSource.isPlaying) wagon.RumbleSource.Stop();
                return;
            }
            if (wagon.RumbleSource == null)
            {
                wagon.RumbleSource = SynthSounds.Source(wagon.Root.gameObject, true, RollingSoundMinimumDistance, RollingSoundMaximumDistance);
                wagon.RumbleSource.clip = SynthSounds.Rumble;
                wagon.JointSource = SynthSounds.Source(wagon.Root.gameObject, false, RollingSoundMinimumDistance, RollingSoundMaximumDistance);
            }
            wagon.RumbleSource.volume = Mathf.Min(RumbleMaximumVolume, speed * RumbleVolumePerMeterPerSecond);
            wagon.RumbleSource.pitch = RumbleBasePitch + RumblePitchPerMeterPerSecond * speed;
            if (!wagon.RumbleSource.isPlaying) wagon.RumbleSource.Play();
            wagon.JointDistance += speed * deltaTime;
            if (wagon.JointDistance < JointSpacingInMeters) return;
            wagon.JointDistance -= JointSpacingInMeters;
            wagon.JointSource.volume = 1f;
            wagon.JointSource.pitch = Random.Range(JointPitchMinimum, JointPitchMaximum);
            wagon.JointSource.PlayOneShot(WorldSounds.WagonJoint, JointVolume);
        }

        /// A free wagon: rolling resistance and the slope; the line's ends stop it; at the points it follows the switch.
        static void Roll(RailWagon wagon, float deltaTime)
        {
            if (wagon.Track == null || wagon.Root == null) return;
            SampleLine(wagon.Track, wagon.Position, out _, out var tangent);
            float slopePull = -GravityInMetersPerSecondSquared * tangent.y;
            float velocity = wagon.Velocity;
            if (Mathf.Abs(velocity) < RestSpeedInMetersPerSecond && Mathf.Abs(slopePull) <= BrakeHoldInMetersPerSecondSquared)
            {
                wagon.Velocity = 0f;
                return;
            }
            velocity += slopePull * deltaTime;
            float friction = RollingDecelerationInMetersPerSecondSquared * deltaTime;
            velocity = Mathf.Abs(velocity) <= friction ? 0f : velocity - Mathf.Sign(velocity) * friction;
            wagon.Velocity = velocity;
            if (velocity == 0f) return;
            if (Advance(wagon, velocity * deltaTime)) wagon.Velocity = 0f;
        }

        /// Moves the wagon along its line, over the points as they are set. Returns true when the end of its line stopped it.
        static bool Advance(RailWagon wagon, float distance)
        {
            if (wagon.Track == null || Mathf.Abs(distance) < MinimumAdvanceInMeters) return false;
            float previous = wagon.Position;
            float nextPosition = previous + distance;
            bool stopped = false;
            if (Switches.Cross(wagon.Track, previous, nextPosition, out var destination, out float destinationPosition, out int alongDestination))
            {
                int before = distance > 0f ? 1 : -1;
                wagon.Track = destination;
                wagon.Position = destinationPosition;
                if (alongDestination != before)
                {
                    // the wagon keeps its way round: on a line it enters the other way, it stands turned round against that line
                    wagon.Flipped = !wagon.Flipped;
                    wagon.Velocity = -wagon.Velocity;
                    wagon.ConsistSign = -wagon.ConsistSign;
                }
            }
            else
            {
                float length = wagon.Track.Spline.Length;
                if (nextPosition <= LineEndMarginInMeters) { nextPosition = LineEndMarginInMeters; stopped = true; }
                else if (nextPosition >= length - LineEndMarginInMeters) { nextPosition = length - LineEndMarginInMeters; stopped = true; }
                wagon.Position = nextPosition;
            }
            wagon.Moved = true;
            EnsureKinematicBody(wagon);
            return stopped;
        }

        /// A coupled wagon runs with its engine: it moves as far as the engine does, then (on a shared line) is put back at
        /// the coupled gap from what it hangs on. If the end of its line holds it, the whole train stops.
        static void Follow(RailWagon wagon, LiveTrain liveTrain, float deltaTime)
        {
            float engineVelocity = liveTrain.Simulation.Velocity;
            wagon.Velocity = wagon.ConsistSign * engineVelocity;
            bool stopped = Advance(wagon, wagon.Velocity * deltaTime);
            if (PartnerEnds(wagon, liveTrain, out float partnerPosition, out float partnerEndForward, out float partnerEndBackward))
            {
                bool ahead = wagon.Position >= partnerPosition;
                // on the locomotive the couplers themselves meet (the engine's ends are its coupler faces); between
                // wagons the measured bodies keep the usual buffer gap
                bool onLocomotive = wagon.CoupledToTrain != null;
                // two wagons with the game's couplers lock the same way: the wagon in front shows a copy of this wagon's
                // coupler locked into its own, and this wagon stands where that copy mounts
                bool wagonLock = !onLocomotive && wagon.HasCouplers && wagon.CoupledToWagon != null && wagon.CoupledToWagon.HasCouplers;
                if (wagonLock)
                {
                    var partner = wagon.CoupledToWagon;
                    partnerEndForward = partner.Position + partner.LockedMountTowardsEnd;
                    partnerEndBackward = partner.Position - partner.LockedMountTowardsStart;
                }
                // locked on the locomotive: the wagon's own coupler is replaced by the engine's copy of it, so the wagon stands
                // where that copy mounts; the engine's end is then that mount
                bool locking = onLocomotive || wagonLock;
                float reachTowardsStart = locking ? wagon.CouplerMountTowardsStart : wagon.ReachTowardsStart;
                float reachTowardsEnd = locking ? wagon.CouplerMountTowardsEnd : wagon.ReachTowardsEnd;
                float targetGap = locking ? LocomotiveCoupledGapInMeters : CoupledGapInMeters;
                float currentGap = ahead ? (wagon.Position - reachTowardsStart) - partnerEndForward
                                         : partnerEndBackward - (wagon.Position + reachTowardsEnd);
                if (float.IsNaN(wagon.CouplingGap)) wagon.CouplingGap = Mathf.Max(0f, currentGap);
                // the ends are drawn together until the couplers touch (aligned after coupling)
                wagon.CouplingGap = Mathf.MoveTowards(wagon.CouplingGap, targetGap, CouplingCloseUpSpeedInMetersPerSecond * deltaTime);
                float wanted = ahead ? partnerEndForward + wagon.CouplingGap + reachTowardsStart
                                     : partnerEndBackward - wagon.CouplingGap - reachTowardsEnd;
                float length = wagon.Track.Spline.Length;
                float clamped = Mathf.Clamp(wanted, LineEndMarginInMeters, length - LineEndMarginInMeters);
                if (Mathf.Abs(clamped - wanted) > LineEndToleranceInMeters) stopped = true;
                wagon.Position = clamped;
                wagon.Moved = true;
            }
            if (stopped && Mathf.Abs(engineVelocity) > EngineStoppedVelocityInMetersPerSecond)
            {
                liveTrain.Simulation.RestoreMotion(0f);
                wagon.Velocity = 0f;
            }
        }

        /// The spline position and the two ends of what the wagon is coupled to, when it is on the wagon's line.
        static bool PartnerEnds(RailWagon wagon, LiveTrain liveTrain, out float position, out float endForward, out float endBackward)
        {
            position = endForward = endBackward = 0f;
            if (wagon.CoupledToTrain != null)
            {
                if (liveTrain.RouteId != wagon.Track.Identifier) return false;
                TrainEnds(liveTrain, wagon.Track, out position, out endForward, out endBackward, true);
                return true;
            }
            var partner = wagon.CoupledToWagon;
            if (partner == null || partner.Track != wagon.Track) return false;
            position = partner.Position;
            endForward = partner.Position + partner.ReachTowardsEnd;
            endBackward = partner.Position - partner.ReachTowardsStart;
            return true;
        }

        /// The locomotive's two ends on the line (spline metres), from its measured model: the coupler of the model is where
        /// a wagon's coupler meets it, so the joint closes without a gap whatever the model's length.
        static void TrainEnds(LiveTrain liveTrain, BuiltTrack track, out float position, out float endForward, out float endBackward, bool lockedMounts = false)
        {
            position = liveTrain.Train.m_SplineController.Position;
            int facing = liveTrain.Simulation.FacingSign;
            float frontReach = TrainBufferFrontInMeters, rearReach = TrainBufferRearInMeters;
            if (MeasureTrainBody(liveTrain) && track != null)
            {
                float splinePerPath = track.Spline.Length / Mathf.Max(MinimumPathLengthInMeters, track.Path.Length);
                frontReach = (lockedMounts && liveTrain.LockedMountFrontInMeters > 0f ? liveTrain.LockedMountFrontInMeters : liveTrain.BodyFrontInMeters) * splinePerPath;
                rearReach = (lockedMounts && liveTrain.LockedMountRearInMeters > 0f ? liveTrain.LockedMountRearInMeters : liveTrain.BodyRearInMeters) * splinePerPath;
            }
            endForward = position + (facing > 0 ? frontReach : rearReach);
            endBackward = position - (facing > 0 ? rearReach : frontReach);
        }

        /// Measures how far the locomotive's model reaches forward and backward from its pivot (once per model).
        static bool MeasureTrainBody(LiveTrain liveTrain)
        {
            var root = liveTrain.Train.transform;
            var model = root.Find(TrainModelObjectName);
            if (model == null) return false;
            if (liveTrain.MeasuredModel == model) return true;
            float forward = 0f, backward = 0f;
            var pivot = root.position;
            var axis = root.forward;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(false))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || filter.name.StartsWith(TrainCouplerPartnerPrefix) || filter.name == TrainRearClawLockedName) continue;
                var bounds = mesh.bounds;
                var matrix = filter.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    var local = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                                            (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                                            (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    float along = Vector3.Dot(matrix.MultiplyPoint3x4(local) - pivot, axis);
                    if (along > forward) forward = along;
                    if (-along > backward) backward = -along;
                }
            }
            if (forward <= 0f || backward <= 0f) return false;
            liveTrain.CouplerPartnerFront = FindModelObject(model, TrainCouplerPartnerFrontName);
            liveTrain.CouplerPartnerRear = FindModelObject(model, TrainCouplerPartnerRearName);
            liveTrain.RearClawFree = FindModelObject(model, TrainRearClawFreeName);
            liveTrain.RearClawLocked = FindModelObject(model, TrainRearClawLockedName);
            liveTrain.LockedMountFrontInMeters = MeasureFarEnd(liveTrain.CouplerPartnerFront, pivot, axis, FrontDirectionSign);
            liveTrain.LockedMountRearInMeters = MeasureFarEnd(liveTrain.CouplerPartnerRear, pivot, axis, RearDirectionSign);
            SetActive(liveTrain.CouplerPartnerFront, false);
            SetActive(liveTrain.CouplerPartnerRear, false);
            SetActive(liveTrain.RearClawLocked, false);
            liveTrain.MeasuredModel = model;
            liveTrain.BodyFrontInMeters = forward;
            liveTrain.BodyRearInMeters = backward;
            MelonLoader.MelonLogger.Msg($"[Wagons] Locomotive body reaches {forward:F2} m forward, {backward:F2} m backward");
            return true;
        }

        static GameObject FindModelObject(Transform model, string objectName)
        {
            var found = model.Find(objectName);
            return found != null ? found.gameObject : null;
        }

        static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active) target.SetActive(active);
        }

        /// How far from the pivot a mesh object reaches in the given direction (metres, along the engine's axis).
        static float MeasureFarEnd(GameObject target, Vector3 pivot, Vector3 axis, float direction)
        {
            if (target == null) return 0f;
            var filter = target.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return 0f;
            var bounds = filter.sharedMesh.bounds;
            var matrix = filter.transform.localToWorldMatrix;
            float far = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                var local = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                                        (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                far = Mathf.Max(far, direction * Vector3.Dot(matrix.MultiplyPoint3x4(local) - pivot, axis));
            }
            return far;
        }

        /// Shows the locked pose wherever a link has closed up: on a locomotive the engine shows its copy of the
        /// wagon's coupler, between wagons the wagon in front shows both. Everywhere else couplers stand open.
        static void ApplyCouplerLocks(List<LiveTrain> trains)
        {
            for (int index = 0; index < trains.Count; index++)
            {
                trains[index].FrontLockedThisFrame = false;
                trains[index].RearLockedThisFrame = false;
            }
            for (int index = 0; index < s_Wagons.Count; index++)
            {
                var wagon = s_Wagons[index];
                wagon.HideForwardWanted = wagon.HideBackwardWanted = false;
                wagon.ReceiveForwardWanted = wagon.ReceiveBackwardWanted = false;
                wagon.LockEngagedWanted = false;
            }
            for (int index = 0; index < s_Wagons.Count; index++)
            {
                var wagon = s_Wagons[index];
                if (wagon.Track == null || float.IsNaN(wagon.CouplingGap) || wagon.CouplingGap > LocomotiveCoupledGapInMeters + CouplerLockToleranceInMeters) continue;
                if (wagon.CoupledToTrain != null)
                {
                    LiveTrain liveTrain = null;
                    for (int trainIndex = 0; trainIndex < trains.Count; trainIndex++)
                        if (trains[trainIndex].Guid == wagon.CoupledToTrain) { liveTrain = trains[trainIndex]; break; }
                    if (liveTrain == null || liveTrain.Train == null || liveTrain.Simulation == null || liveTrain.MeasuredModel == null) continue;
                    if (liveTrain.RouteId != wagon.Track.Identifier) continue;
                    bool wagonAhead = wagon.Position >= liveTrain.Train.m_SplineController.Position;
                    bool atFront = wagonAhead == (liveTrain.Simulation.FacingSign > 0);
                    if (atFront) liveTrain.FrontLockedThisFrame = true; else liveTrain.RearLockedThisFrame = true;
                    WantHidden(wagon, wagonAhead);
                    wagon.LockEngagedWanted = true;
                }
                else if (wagon.CoupledToWagon != null && wagon.HasCouplers && wagon.CoupledToWagon.HasCouplers && wagon.CoupledToWagon.Track == wagon.Track)
                {
                    var partner = wagon.CoupledToWagon;
                    bool wagonAhead = wagon.Position >= partner.Position;
                    WantHidden(wagon, wagonAhead);
                    // the partner's end facing this wagon: its end side when this wagon is ahead, else its start side
                    bool partnerForward = wagonAhead ? !partner.Flipped : partner.Flipped;
                    if (partnerForward) partner.ReceiveForwardWanted = partner.HideForwardWanted = true;
                    else partner.ReceiveBackwardWanted = partner.HideBackwardWanted = true;
                    wagon.LockEngagedWanted = true;
                }
            }
            for (int index = 0; index < trains.Count; index++)
            {
                var liveTrain = trains[index];
                if (liveTrain.MeasuredModel == null) continue;
                SetActive(liveTrain.CouplerPartnerFront, liveTrain.FrontLockedThisFrame);
                SetActive(liveTrain.CouplerPartnerRear, liveTrain.RearLockedThisFrame);
                SetActive(liveTrain.RearClawFree, !liveTrain.RearLockedThisFrame);
                SetActive(liveTrain.RearClawLocked, liveTrain.RearLockedThisFrame);
                ApplyPartnerMaterial(liveTrain);
            }
            for (int index = 0; index < s_Wagons.Count; index++)
            {
                var wagon = s_Wagons[index];
                if (wagon.HiddenForward != wagon.HideForwardWanted)
                {
                    SetCouplersVisible(wagon.CouplerGroupsForward, !wagon.HideForwardWanted);
                    wagon.HiddenForward = wagon.HideForwardWanted;
                }
                if (wagon.HiddenBackward != wagon.HideBackwardWanted)
                {
                    SetCouplersVisible(wagon.CouplerGroupsBackward, !wagon.HideBackwardWanted);
                    wagon.HiddenBackward = wagon.HideBackwardWanted;
                }
                if (wagon.ReceiveForwardWanted != (wagon.LockCopyForward != null))
                    wagon.LockCopyForward = wagon.ReceiveForwardWanted ? BuildLockedCopies(wagon, true) : DestroyCopies(wagon.LockCopyForward);
                if (wagon.ReceiveBackwardWanted != (wagon.LockCopyBackward != null))
                    wagon.LockCopyBackward = wagon.ReceiveBackwardWanted ? BuildLockedCopies(wagon, false) : DestroyCopies(wagon.LockCopyBackward);
                // a link that locks plays the lock (also when the wagon locks on to something new), one that lets go the release;
                // both heard from the joint itself, a registered emitter on the coupler at that end
                object wantedLink = wagon.LockEngagedWanted ? (object)wagon.CoupledToTrain ?? wagon.CoupledToWagon : null;
                if (!Equals(wantedLink, wagon.EngagedLink) && wagon.Root != null)
                {
                    if (wantedLink != null)
                    {
                        var groups = wagon.HideForwardWanted ? wagon.CouplerGroupsForward : wagon.CouplerGroupsBackward;
                        wagon.EngagedEmitter = groups.Count > 0 && groups[0] != null
                            ? TrainSounds.Emitter(groups[0], CouplerSoundEmitterName, Vector3.zero)
                            : TrainSounds.Emitter(wagon.Root, CouplerSoundEmitterName, Vector3.up);
                        TrainSounds.Play(TrainSounds.CouplerLock, wagon.EngagedEmitter);
                    }
                    else if (wagon.EngagedEmitter != null) TrainSounds.Play(TrainSounds.CouplerRelease, wagon.EngagedEmitter);
                }
                wagon.EngagedLink = wantedLink;
                wagon.LockEngaged = wagon.LockEngagedWanted;
            }
        }

        /// Marks the wagon's coupler at the end facing what it hangs on (its start side when it is ahead of it).
        static void WantHidden(RailWagon wagon, bool wagonAhead)
        {
            bool forward = wagonAhead ? wagon.Flipped : !wagon.Flipped;
            if (forward) wagon.HideForwardWanted = true; else wagon.HideBackwardWanted = true;
        }

        static GameObject DestroyCopies(GameObject copies)
        {
            if (copies != null) Object.Destroy(copies);
            return null;
        }

        static void SetCouplersVisible(List<Transform> groups, bool visible)
        {
            for (int index = 0; index < groups.Count; index++)
                if (groups[index] != null)
                    foreach (var renderer in groups[index].GetComponentsInChildren<Renderer>(true)) renderer.enabled = visible;
        }

        /// The joint at one end of a wagon, locked: a copy of its own coupler there and, set lock-depth deep into it, a copy of
        /// the coupler from its other end turned to face it (the follower's coupler is hidden and stands just there).
        static GameObject BuildLockedCopies(RailWagon wagon, bool forwardSide)
        {
            var receivingGroups = forwardSide ? wagon.CouplerGroupsForward : wagon.CouplerGroupsBackward;
            var otherGroups = forwardSide ? wagon.CouplerGroupsBackward : wagon.CouplerGroupsForward;
            if (wagon.Root == null || receivingGroups.Count == 0 || otherGroups.Count == 0) return null;
            var toRoot = wagon.Root.worldToLocalMatrix;
            var first = LodZero(receivingGroups[0]);
            if (first == null) return null;
            float outward = Mathf.Sign((toRoot * first.transform.localToWorldMatrix).MultiplyPoint3x4(first.sharedMesh.bounds.center).z);
            float receivingTip = FarthestAlong(receivingGroups, toRoot, outward);
            float otherTip = FarthestAlong(otherGroups, toRoot, -outward);
            var shift = Matrix4x4.Translate(new Vector3(0f, 0f, outward * (receivingTip + otherTip - RailWagon.CouplerLockDepthInMeters)));

            var copies = new GameObject("SR_CouplerLock");
            copies.transform.SetParent(wagon.Root, false);
            for (int index = 0; index < receivingGroups.Count; index++)
            {
                var source = LodZero(receivingGroups[index]);
                if (source != null) AddCopy(copies.transform, source, toRoot * source.transform.localToWorldMatrix, outward);
            }
            for (int index = 0; index < otherGroups.Count; index++)
            {
                var source = LodZero(otherGroups[index]);
                if (source != null) AddCopy(copies.transform, source, shift * (toRoot * source.transform.localToWorldMatrix), -outward);
            }
            return copies;
        }

        static float FarthestAlong(List<Transform> groups, Matrix4x4 toRoot, float direction)
        {
            float farthest = 0f;
            for (int index = 0; index < groups.Count; index++)
            {
                var source = LodZero(groups[index]);
                if (source != null) farthest = Mathf.Max(farthest, FarthestAlong(source.sharedMesh, toRoot * source.transform.localToWorldMatrix, direction));
            }
            return farthest;
        }

        static MeshFilter LodZero(Transform group)
        {
            if (group == null) return null;
            MeshFilter first = null;
            foreach (var filter in group.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                if (filter.name.EndsWith("_LOD0")) return filter;
                if (first == null) first = filter;
            }
            return first;
        }

        static float FarthestAlong(Mesh mesh, Matrix4x4 matrix, float direction)
        {
            var vertices = mesh.vertices;
            float farthest = float.MinValue;
            for (int index = 0; index < vertices.Length; index++)
                farthest = Mathf.Max(farthest, direction * matrix.MultiplyPoint3x4(vertices[index]).z);
            return farthest;
        }

        static void AddCopy(Transform parent, MeshFilter source, Matrix4x4 matrix, float outward)
        {
            var copy = new GameObject(source.name + "_Locked");
            copy.transform.SetParent(parent, false);
            copy.transform.localPosition = matrix.GetColumn(3);
            copy.transform.localRotation = matrix.rotation;
            copy.transform.localScale = matrix.lossyScale;
            var mesh = source.sharedMesh;
            if (source.name.Contains(CouplerClawName) && mesh.isReadable) mesh = OpenedClaw(mesh, matrix, outward);
            copy.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = copy.AddComponent<MeshRenderer>();
            var sourceRenderer = source.GetComponent<Renderer>();
            if (sourceRenderer != null) renderer.sharedMaterial = sourceRenderer.sharedMaterial;
        }

        static readonly Dictionary<long, Mesh> s_OpenedClaws = new Dictionary<long, Mesh>();

        /// The claw coupler (FrameClampA) with its two hinged fingers swung out sideways about their pins, the way it closes
        /// round a coupler head (the same pose the locomotive's model carries). Worked in the wagon's frame, outward along z.
        static Mesh OpenedClaw(Mesh source, Matrix4x4 toWagon, float outward)
        {
            long key = (long)source.GetInstanceID() * 2 + (outward > 0f ? 1 : 0);
            if (s_OpenedClaws.TryGetValue(key, out var cached) && cached != null) return cached;
            var vertices = source.vertices;
            var triangles = source.triangles;
            var lateral = new float[vertices.Length];
            var along = new float[vertices.Length];
            var height = new float[vertices.Length];
            float mount = float.MaxValue;
            for (int index = 0; index < vertices.Length; index++)
            {
                var point = toWagon.MultiplyPoint3x4(vertices[index]);
                lateral[index] = outward * point.x;
                along[index] = outward * point.z;
                height[index] = point.y;
                mount = Mathf.Min(mount, along[index]);
            }
            var side = new int[vertices.Length];
            for (int index = 0; index < triangles.Length; index += 3)
            {
                int first = triangles[index], second = triangles[index + 1], third = triangles[index + 2];
                if (Mathf.Min(along[first], Mathf.Min(along[second], along[third])) < mount + ClawFingerStartInMeters) continue;
                float most = Mathf.Max(lateral[first], Mathf.Max(lateral[second], lateral[third]));
                float least = Mathf.Min(lateral[first], Mathf.Min(lateral[second], lateral[third]));
                int triangleSide = most <= ClawCentreToleranceInMeters ? -1 : (least >= -ClawCentreToleranceInMeters ? 1 : 0);
                if (triangleSide == 0) continue;
                side[first] = side[second] = side[third] = triangleSide;
            }
            var fromWagon = toWagon.inverse;
            var opened = new Vector3[vertices.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                float lateralPosition = lateral[index], alongPosition = along[index];
                if (side[index] != 0)
                {
                    float pinX = side[index] * ClawPinLateralInMeters, pinZ = mount + ClawPinAlongInMeters;
                    float angle = ClawFingerOpenDegrees * Mathf.Deg2Rad * (side[index] < 0 ? 1f : -1f);
                    float cosine = Mathf.Cos(angle), sine = Mathf.Sin(angle);
                    float offsetX = lateralPosition - pinX, offsetZ = alongPosition - pinZ;
                    lateralPosition = pinX + offsetX * cosine - offsetZ * sine;
                    alongPosition = pinZ + offsetX * sine + offsetZ * cosine;
                }
                opened[index] = fromWagon.MultiplyPoint3x4(new Vector3(outward * lateralPosition, height[index], outward * alongPosition));
            }
            var mesh = new Mesh { name = source.name + "_Opened" };
            mesh.vertices = opened;
            mesh.triangles = triangles;
            mesh.uv = source.uv;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            s_OpenedClaws[key] = mesh;
            return mesh;
        }

        /// The copy of a wagon's coupler wears the game's own coupler texture (taken from a wagon).
        static void ApplyPartnerMaterial(LiveTrain liveTrain)
        {
            if (liveTrain.PartnerMaterialApplied) return;
            Material source = null;
            for (int index = 0; index < s_Wagons.Count && source == null; index++) source = s_Wagons[index].CouplerMaterial;
            if (source == null) return;
            SetMaterial(liveTrain.CouplerPartnerFront, source);
            SetMaterial(liveTrain.CouplerPartnerRear, source);
            liveTrain.PartnerMaterialApplied = true;
        }

        static void SetMaterial(GameObject target, Material material)
        {
            if (target == null) return;
            var renderer = target.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;
        }

        /// A moving wagon's many colliders move as one kinematic body (cheap for the physics engine).
        static void EnsureKinematicBody(RailWagon wagon)
        {
            if (wagon.SoundEmitter != null || wagon.Root == null) return;   // set up once, together with the sound emitter
            var body = wagon.Root.GetComponent<Rigidbody>() ?? wagon.Root.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            wagon.SoundEmitter = wagon.Root.gameObject;
            if (wagon.Root.GetComponent<AkGameObj>() == null) wagon.Root.gameObject.AddComponent<AkGameObj>();
        }

        // ------------------------------------------------------------------ knocks

        /// The locomotive's buffers against a wagon's ends, on the same line (its own coupled wagons excepted).
        static void TrainContact(LiveTrain liveTrain, RailWagon wagon, List<LiveTrain> trains)
        {
            if (liveTrain.Train == null || liveTrain.Simulation == null || wagon.Track == null || liveTrain.RouteId != wagon.Track.Identifier) return;
            if (ConsistTrain(wagon, trains) == liveTrain) return;
            var simulation = liveTrain.Simulation;
            TrainEnds(liveTrain, wagon.Track, out float trainPosition, out float trainEndForward, out float trainEndBackward);
            float trainVelocity = simulation.LineVelocity;
            bool wagonAhead = wagon.Position >= trainPosition;
            float gap = wagonAhead ? (wagon.Position - wagon.ReachTowardsStart) - trainEndForward
                                   : trainEndBackward - (wagon.Position + wagon.ReachTowardsEnd);
            if (gap >= 0f) return;
            float closing = wagonAhead ? trainVelocity - wagon.Velocity : wagon.Velocity - trainVelocity;
            if (closing <= 0f) return;

            float trainMass = ConsistMass(liveTrain, trains);
            float behindVelocity = wagonAhead ? trainVelocity : wagon.Velocity;
            float aheadVelocity = wagonAhead ? wagon.Velocity : trainVelocity;
            float behindMass = wagonAhead ? trainMass : wagon.MassInKilograms;
            float aheadMass = wagonAhead ? wagon.MassInKilograms : trainMass;
            Collide(behindMass, aheadMass, ref behindVelocity, ref aheadVelocity, closing);
            float newTrainVelocity = wagonAhead ? behindVelocity : aheadVelocity;
            wagon.Velocity = wagonAhead ? aheadVelocity : behindVelocity;

            // the wagon is pushed clear of the buffers; against the end of its line it cannot give way: the train stops
            float length = wagon.Track.Spline.Length;
            float pushed = wagon.Position + (wagonAhead ? -gap : gap);
            if (pushed <= LineEndMarginInMeters || pushed >= length - LineEndMarginInMeters)
            {
                pushed = Mathf.Clamp(pushed, LineEndMarginInMeters, length - LineEndMarginInMeters);
                wagon.Velocity = 0f;
                newTrainVelocity = 0f;
            }
            wagon.Position = pushed;
            wagon.Moved = true;
            EnsureKinematicBody(wagon);
            simulation.RestoreMotion(newTrainVelocity * simulation.FacingSign);
            Bump(wagon, closing);
        }

        /// Two wagons on one line. A wagon in a consist brings the whole train's mass, and the train takes the change.
        static void WagonContact(RailWagon first, RailWagon second, List<LiveTrain> trains)
        {
            if (first.Track == null || second.Track == null || first.Track != second.Track) return;
            var firstTrain = ConsistTrain(first, trains);
            var secondTrain = ConsistTrain(second, trains);
            if (firstTrain != null && secondTrain != null) return;   // the same consist, or two trains: their buffers handle it
            var behind = first.Position <= second.Position ? first : second;
            var ahead = behind == first ? second : first;
            float gap = (ahead.Position - ahead.ReachTowardsStart) - (behind.Position + behind.ReachTowardsEnd);
            if (gap >= 0f) return;
            float closing = behind.Velocity - ahead.Velocity;
            if (closing <= 0f) return;
            var behindTrain = behind == first ? firstTrain : secondTrain;
            var aheadTrain = behind == first ? secondTrain : firstTrain;
            float behindMass = behindTrain != null ? ConsistMass(behindTrain, trains) : behind.MassInKilograms;
            float aheadMass = aheadTrain != null ? ConsistMass(aheadTrain, trains) : ahead.MassInKilograms;
            float behindVelocity = behind.Velocity, aheadVelocity = ahead.Velocity;
            Collide(behindMass, aheadMass, ref behindVelocity, ref aheadVelocity, closing);
            behind.Velocity = behindVelocity;
            ahead.Velocity = aheadVelocity;
            // a coupled wagon's speed belongs to its engine (wagon line velocity = ConsistSign × engine velocity)
            if (behindTrain != null) behindTrain.Simulation.RestoreMotion(behindVelocity * behind.ConsistSign);
            if (aheadTrain != null) aheadTrain.Simulation.RestoreMotion(aheadVelocity * ahead.ConsistSign);
            // the free wagon gives way (a consist is held by its links)
            if (behindTrain == null && aheadTrain == null)
            {
                float total = behind.MassInKilograms + ahead.MassInKilograms;
                behind.Position += gap * ahead.MassInKilograms / total;
                ahead.Position -= gap * behind.MassInKilograms / total;
            }
            else if (behindTrain == null) behind.Position += gap;
            else ahead.Position -= gap;
            behind.Moved = ahead.Moved = true;
            EnsureKinematicBody(behind);
            EnsureKinematicBody(ahead);
            Bump(ahead, closing);
        }

        /// Momentum shared by mass along the line: slow pushes move together, harder knocks bounce a little.
        static void Collide(float behindMass, float aheadMass, ref float behindVelocity, ref float aheadVelocity, float closing)
        {
            float restitution = closing > BounceSpeedInMetersPerSecond ? BounceRestitution : 0f;
            float total = behindMass + aheadMass;
            float momentum = behindMass * behindVelocity + aheadMass * aheadVelocity;
            float newBehind = (momentum - aheadMass * restitution * closing) / total;
            float newAhead = (momentum + behindMass * restitution * closing) / total;
            behindVelocity = newBehind;
            aheadVelocity = newAhead;
        }

        static void Bump(RailWagon wagon, float closing)
        {
            if (closing >= BumpSoundSpeedInMetersPerSecond && Time.time >= wagon.NextSoundTime && wagon.Root != null)
            {
                wagon.NextSoundTime = Time.time + BumpSoundIntervalInSeconds;
                RailwayManager.CrashAt(wagon.Root.position, closing);
            }
            if (closing >= HardKnockSpeedInMetersPerSecond && Time.time >= s_NextHardKnockMessage)
            {
                s_NextHardKnockMessage = Time.time + HardKnockMessageIntervalInSeconds;
                CabControls.Message(Localizer.Translate("Vagona sert çarptın!", "You hit the wagon hard!"));
            }
        }

        // ------------------------------------------------------------------ coupling and uncoupling

        /// A joint the player can couple or uncouple: a wagon and what it touches (a locomotive or a wagon of a consist).
        sealed class CouplingCandidate
        {
            public RailWagon Wagon;
            public LiveTrain Train;          // the locomotive it would hang on (or hangs on)
            public RailWagon OtherWagon;     // or the consist's wagon it would hang on
            public bool Coupled;
            public Vector3 Joint;
        }

        static CouplingCandidate s_Coupling;
        static GameObject s_CouplingZone;
        static float s_NextCouplingRefresh;
        static List<LiveTrain> s_LastTrains;

        public static bool IsCouplingZone(GameObject candidate) => candidate != null && candidate.name == CouplingZoneName;

        /// A few times a second: the joint nearest the player that can be coupled or uncouped now gets the hold prompt.
        static void UpdateCouplingZone(List<LiveTrain> trains)
        {
            if (Time.time < s_NextCouplingRefresh) return;
            s_NextCouplingRefresh = Time.time + CouplingRefreshIntervalInSeconds;
            s_LastTrains = trains;
            var player = GameManager.GetPlayerObject();
            s_Coupling = player != null ? NearestJoint(player.transform.position, trains) : null;
            if (s_Coupling == null)
            {
                s_AnnouncedJoint = null;
                if (s_CouplingZone != null && s_CouplingZone.activeSelf) s_CouplingZone.SetActive(false);
                return;
            }
            AnnounceReadyToCouple();
            if (s_CouplingZone == null) s_CouplingZone = BuildCouplingZone();
            s_CouplingZone.transform.position = s_Coupling.Joint;
            if (!s_CouplingZone.activeSelf) s_CouplingZone.SetActive(true);
            var hold = s_CouplingZone.GetComponent<TimedHoldInteraction>();
            if (hold == null) return;
            hold.HoverText = s_Coupling.Coupled ? Localizer.Translate("Vagonu ayır (basılı tut)", "Uncouple the wagon (hold)") : Localizer.Translate("Vagonu bağla (basılı tut)", "Couple the wagon (hold)");
            hold.HoldText = s_Coupling.Coupled ? Localizer.Translate("Vagon ayrılıyor...", "Uncoupling...") : Localizer.Translate("Vagon bağlanıyor...", "Coupling...");
        }

        static string s_AnnouncedJoint;

        /// A short message when a joint comes within reach (once per pair, until it leaves the reach again).
        static void AnnounceReadyToCouple()
        {
            string identifier = s_Coupling.Coupled ? null
                : s_Coupling.Wagon.Key + ">" + (s_Coupling.Train != null ? s_Coupling.Train.Guid : s_Coupling.OtherWagon?.Key);
            if (identifier == s_AnnouncedJoint) return;
            s_AnnouncedJoint = identifier;
            if (identifier != null) CabControls.Message(Localizer.Translate("Bağlanmaya hazır: bağlantıya bak ve basılı tut.", "Ready to couple: look at the joint and hold."));
        }

        static GameObject BuildCouplingZone()
        {
            var zone = new GameObject(CouplingZoneName);
            zone.layer = LayerMask.NameToLayer("InteractiveProp");
            var box = zone.AddComponent<BoxCollider>();
            box.size = Vector3.one * CouplingZoneSizeInMeters;
            box.isTrigger = true;
            var hold = zone.AddComponent<TimedHoldInteraction>();
            hold.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            hold.m_DefaultHoverText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldTime = CouplingHoldTimeInSeconds;
            hold.HoldTime = CouplingHoldTimeInSeconds;
            hold.CanInteract = true;
            zone.SetActive(false);
            return zone;
        }

        /// Every joint that can change now: coupled links (to uncouple) and touching, slow pairs (to couple).
        static CouplingCandidate NearestJoint(Vector3 playerPosition, List<LiveTrain> trains)
        {
            CouplingCandidate best = null;
            float bestDistance = CouplingPlayerReachInMeters * CouplingPlayerReachInMeters;
            void Consider(CouplingCandidate candidate)
            {
                float distance = (candidate.Joint - playerPosition).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            foreach (var wagon in s_Wagons)
            {
                if (wagon.Track == null) continue;
                var consist = ConsistTrain(wagon, trains);
                if (consist != null)
                {
                    // its own link: uncouple (only while the train stands)
                    if (Mathf.Abs(consist.Simulation.Velocity) > CouplingSpeedLimitInMetersPerSecond) continue;
                    var link = new CouplingCandidate { Wagon = wagon, Train = wagon.CoupledToTrain != null ? consist : null, OtherWagon = wagon.CoupledToWagon, Coupled = true };
                    if (JointOf(link)) Consider(link);
                    continue;
                }
                if (Mathf.Abs(wagon.Velocity) > CouplingSpeedLimitInMetersPerSecond) continue;
                // a free wagon touching a standing locomotive...
                foreach (var liveTrain in trains)
                {
                    if (liveTrain.Train == null || liveTrain.Simulation == null || liveTrain.RouteId != wagon.Track.Identifier) continue;
                    if (Mathf.Abs(liveTrain.Simulation.Velocity) > CouplingSpeedLimitInMetersPerSecond) continue;
                    var candidate = new CouplingCandidate { Wagon = wagon, Train = liveTrain };
                    if (JointOf(candidate)) Consider(candidate);
                }
                // ...or the last wagon of a standing consist
                foreach (var other in s_Wagons)
                {
                    if (other == wagon || other.Track != wagon.Track) continue;
                    var otherConsist = ConsistTrain(other, trains);
                    if (otherConsist == null || Mathf.Abs(otherConsist.Simulation.Velocity) > CouplingSpeedLimitInMetersPerSecond) continue;
                    var candidate = new CouplingCandidate { Wagon = wagon, OtherWagon = other };
                    if (JointOf(candidate)) Consider(candidate);
                }
            }
            return best;
        }

        /// The joint's place between the two ends; false when they are on different lines or (to couple) not touching.
        static bool JointOf(CouplingCandidate candidate)
        {
            var wagon = candidate.Wagon;
            float partnerPosition, partnerEndForward, partnerEndBackward;
            if (candidate.Train != null)
            {
                if (candidate.Train.RouteId != wagon.Track.Identifier) return false;
                TrainEnds(candidate.Train, wagon.Track, out partnerPosition, out partnerEndForward, out partnerEndBackward);
            }
            else if (candidate.OtherWagon != null)
            {
                var other = candidate.OtherWagon;
                if (other.Track != wagon.Track) return false;
                partnerPosition = other.Position;
                partnerEndForward = other.Position + other.ReachTowardsEnd;
                partnerEndBackward = other.Position - other.ReachTowardsStart;
            }
            else return false;
            bool ahead = wagon.Position >= partnerPosition;
            float wagonEnd = ahead ? wagon.Position - wagon.ReachTowardsStart : wagon.Position + wagon.ReachTowardsEnd;
            float partnerEnd = ahead ? partnerEndForward : partnerEndBackward;
            float gap = ahead ? wagonEnd - partnerEnd : partnerEnd - wagonEnd;
            if (!candidate.Coupled && gap > CouplingReachInMeters) return false;
            SampleLine(wagon.Track, (wagonEnd + partnerEnd) * 0.5f, out var point, out _);
            candidate.Joint = point + Vector3.up * CouplingZoneHeightInMeters;
            return true;
        }

        /// The hold on the joint completed: couple or uncouple it.
        public static bool ToggleCoupling()
        {
            var candidate = s_Coupling;
            var trains = s_LastTrains;
            if (candidate == null || trains == null || candidate.Wagon == null) return false;
            var wagon = candidate.Wagon;
            if (candidate.Coupled)
            {
                Unlink(wagon);
                CabControls.Message(Localizer.Translate("Vagon ayrıldı.", "The wagon is uncoupled."));
            }
            else
            {
                if (candidate.Train != null)
                {
                    wagon.CoupledToTrain = candidate.Train.Guid;
                    wagon.ConsistSign = candidate.Train.Simulation.FacingSign;
                }
                else
                {
                    wagon.CoupledToWagon = candidate.OtherWagon;
                    wagon.ConsistSign = candidate.OtherWagon.ConsistSign;
                }
                wagon.CouplingGap = float.NaN;
                wagon.Velocity = 0f;
                RefreshChainSigns();
                CabControls.Message(Localizer.Translate("Vagon bağlandı.", "The wagon is coupled."));
            }
            s_Coupling = null;
            s_NextCouplingRefresh = 0f;
            return true;
        }

        /// Wagons hanging on other wagons take their direction from them, nearest links first (a group coupled to an engine
        /// again may stand the other way round to it than before).
        static void RefreshChainSigns()
        {
            for (int depth = 2; depth <= MaximumChainLength; depth++)
                foreach (var wagon in s_Wagons)
                    if (wagon.CoupledToWagon != null && ChainDepth(wagon) == depth && wagon.CoupledToWagon.Track == wagon.Track)
                        wagon.ConsistSign = wagon.CoupledToWagon.ConsistSign;
        }

        /// Uncouples the wagon from what it hangs on. Wagons hanging on it stay coupled to it (they now form a loose
        /// group that rolls freely until it is coupled to an engine again).
        static void Unlink(RailWagon wagon)
        {
            wagon.CoupledToTrain = null;
            wagon.CoupledToWagon = null;
            wagon.ConsistSign = 0;
            wagon.CouplingGap = float.NaN;
            wagon.StrayCoupledToKey = null;
        }
    }
}
