using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// The blasting charge (GEAR_Dynamite) is a crafted item from a ModComponent pack that the DLL carries.
    /// In hard mode charges are placed on a blocking rockfall and fired from a detonator box set down 12-30 m away.
    internal static class Dynamite
    {
        public const string GearName = "GEAR_Dynamite";
        const string PackageFileName = "SteamRailwayDynamite.modcomponent";
        const string PackageResourceName = "Dynamite/SteamRailwayDynamite.modcomponent";
        const string ZoneNamePrefix = "SR_DynamiteZone_";
        /// The charge's own translated name is shown as the burn cause.
        const string BurnsCause = GearName;
        const string CableZoneName = "SR_DynamiteCableZone";
        const string PlungerName = "SR_DynamitePlunger";
        const string PlacedChargePrefab = "SR_PlacedCharge";
        const string PlungerPrefab = "SR_PlungerBox";

        /// One charge clears this much of an obstacle's radius; every rockfall needs at least one.
        const float RadiusPerChargeInMeters = 25f;
        /// The detonator box must stand this far from the charges, and the cable reaches this far.
        const float MinimumCableDistanceInMeters = 12f;
        const float MaximumCableDistanceInMeters = 30f;
        const int PlacementSettleFrames = 3;
        const float CableDistanceToleranceInMeters = 1f;
        const float CableGroundOffsetInMeters = 0.035f;
        const float CableWidthInMeters = 0.014f;
        const int CablePointCount = 48;
        const float CableLiftAtChargeInMeters = 0.1f;
        const float CableLiftAtBoxInMeters = 0.2f;
        const float CableColorLevel = 0.06f;
        const int CableCornerVertices = 2;
        const float GroundProbeHeightInMeters = 3f;
        const float GroundProbeDistanceInMeters = 8f;
        const float PlainBoxWidthInMeters = 0.3f;
        const float PlainBoxHeightInMeters = 0.2f;
        const float PlainBoxDepthInMeters = 0.22f;
        const float PlainPlungerWidthInMeters = 0.12f;
        const float PlainPlungerHeightInMeters = 0.5f;
        const float PlainPlungerOffsetInMeters = 1.1f;
        const float FallbackChargeSizeInMeters = 0.1f;
        const float MinimumForwardSquaredLength = 1e-4f;
        const float PlungerHoldTimeInSeconds = 0.9f;
        const float PlungerPressDepthInMeters = 0.12f;
        const float PlungerPressSeconds = 0.25f;
        const float BlastDelayAfterPressInSeconds = 0.6f;
        const float ChargeSpacingInMeters = 0.16f;
        /// Wiring is given up when the player is this far from the charges.
        const float AbandonDistanceInMeters = 150f;
        const float CleanupDelayInSeconds = 1.5f;
        const float ClickVolume = 0.8f;
        const float ClickPitch = 0.6f;
        const float BlastShakeRangeInMeters = 70f;
        const float BlastShakeStrength = 0.7f;
        /// Closer than this to the blast the player is scalded.
        const float BurnsRadiusInMeters = 7f;
        /// Within this distance the blast hurts, most at the charge (the player is never killed by it).
        const float BlastDamageRadiusInMeters = 16f;
        const float BlastDamageAtChargeInHealth = 90f;
        const float SurvivingHealth = 6f;
        /// The ears ring within this distance; the closer, the louder and the stronger the shake.
        const float TinnitusRadiusInMeters = 45f;
        const float HearRadiusInMeters = 80f;
        const float RefreshIntervalInSeconds = 1f;
        /// The zone works this far beyond a rockfall's circle (rocks can lie on its edge).
        const float CircleMarginInMeters = 5f;
        /// How far the player can reach to place a charge (the game's own interactions reach about as far).
        const float ReachInMeters = 4f;
        /// The small trigger that follows the player's gaze over the rockfall.
        const float ZoneSizeInMeters = 1.2f;
        /// Instanced rocks have no object of their own to hit: a hit this close to where one stands counts as that rock.
        const float InstancedRockReachInMeters = 4f;
        /// A target without a collider counts by its renderers' bounds, grown by this much.
        const float BoundsMarginInMeters = 1f;
        const float HoldTimeInSeconds = 1.5f;
        const float ClearDelayAfterBlastInSeconds = 0.35f;
        const float BlastScalePerCharge = 0.25f;
        const float BlastBaseScale = 0.5f;
        const float MaximumBlastScale = 2.5f;

        static readonly HashSet<string> s_Fusing = new HashSet<string>();
        /// The one placement zone: a small trigger put each frame where the player looks inside a blocking rockfall.
        static GameObject s_Zone;
        static ObstacleData s_ZoneObstacle;
        static ObstacleData s_InsideObstacle;
        static float s_NextRefreshTime;
        /// What clearing the current rockfall changes (only these take a charge), gathered once on entering it.
        static ObstacleData s_TargetsObstacle;
        static readonly HashSet<int> s_TargetIds = new HashSet<int>();
        /// Targets the ray cannot hit (instanced rocks, objects without colliders): a hit inside one of these boxes counts.
        static readonly List<Bounds> s_TargetAreas = new List<Bounds>();
        static readonly List<Vector3> s_InstancedScratch = new List<Vector3>();
        static readonly List<Transform> s_TargetScratch = new List<Transform>();
        /// The rockfall has nothing the ray can hit: anywhere in its circle takes the charge then.
        static bool s_AnywhereInCircle;

        // ------------------------------------------------------------------ the item pack

        /// Writes the item pack next to the other mods when it is missing or differs (it is the mod's own file).
        public static void ExtractPackage()
        {
            try
            {
                var assembly = typeof(Dynamite).Assembly;
                using (var stream = assembly.GetManifestResourceStream(PackageResourceName))
                {
                    if (stream == null) return;
                    var memory = new System.IO.MemoryStream();
                    stream.CopyTo(memory);
                    var embedded = memory.ToArray();
                    string target = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.ModsDirectory, PackageFileName);
                    if (System.IO.File.Exists(target) && SameBytes(System.IO.File.ReadAllBytes(target), embedded)) return;
                    System.IO.File.WriteAllBytes(target, embedded);
                    SteamRailwayMod.Log.Msg($"{PackageFileName} written to the Mods folder: ModComponent loads the blasting charge from it at this start.");
                }
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"The blasting charge package could not be written: {exception.Message}"); }
        }

        static bool SameBytes(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++) if (first[index] != second[index]) return false;
            return true;
        }

        // ------------------------------------------------------------------ the placement zones

        public static int ChargesNeeded(ObstacleData obstacle) =>
            Mathf.Max(1, Mathf.CeilToInt(obstacle.Radius / RadiusPerChargeInMeters));

        static int ChargesCarried()
        {
            var inventory = GameManager.GetInventoryComponent();
            return inventory == null ? 0 : inventory.GetNumGearWithName(GearName);
        }

        /// One of the blasting's hold zones: over the rocks, where the detonator box goes, or the plunger.
        public static bool IsZone(GameObject candidate) =>
            candidate != null && (candidate.name.StartsWith(ZoneNamePrefix) || candidate.name == CableZoneName || candidate.name == PlungerName);

        /// Called every frame: once a second it finds the rockfall the player stands in, every frame it moves
        /// the zone to where the player looks inside it.
        public static void Update()
        {
            if (s_Blast != null) { HideZone(); UpdateWiring(); return; }
            if (Time.time >= s_NextRefreshTime)
            {
                s_NextRefreshTime = Time.time + RefreshIntervalInSeconds;
                s_InsideObstacle = FindRockfallAroundPlayer();
                if (s_InsideObstacle != s_TargetsObstacle) GatherTargets(s_InsideObstacle);
                if (s_InsideObstacle != null) UpdateZoneText(EnsureZone(s_InsideObstacle), s_InsideObstacle, ChargesCarried());
            }
            // (a region change destroys the zone before the next refresh notices)
            if (s_InsideObstacle == null || s_Zone == null) { HideZone(); return; }
            var camera = GameManager.GetMainCamera();
            if (camera == null) { HideZone(); return; }
            var cameraTransform = camera.transform;
            if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out var rayHit, ReachInMeters, ~0, QueryTriggerInteraction.Ignore)
                || !WithinCircle(s_InsideObstacle, rayHit.point, CircleMarginInMeters)
                || !(s_AnywhereInCircle || IsTarget(rayHit)))
            {
                HideZone();
                return;
            }
            s_Zone.transform.position = rayHit.point;
            if (!s_Zone.activeSelf) s_Zone.SetActive(true);
        }

        /// Gathers what clearing this rockfall changes (null: forget the last one).
        static void GatherTargets(ObstacleData obstacle)
        {
            s_TargetsObstacle = obstacle;
            s_TargetIds.Clear();
            s_TargetAreas.Clear();
            s_AnywhereInCircle = false;
            if (obstacle == null) return;
            s_TargetScratch.Clear();
            s_InstancedScratch.Clear();
            ObjectEdits.CollectHeldWithin(obstacle, s_TargetScratch, s_InstancedScratch);
            RailwayManager.CollectHeldRocksWithin(obstacle, s_TargetScratch);
            for (int index = 0; index < s_InstancedScratch.Count; index++)
                s_TargetAreas.Add(new Bounds(s_InstancedScratch[index], Vector3.one * (InstancedRockReachInMeters * 2f)));
            for (int index = 0; index < s_TargetScratch.Count; index++)
            {
                var target = s_TargetScratch[index];
                if (target == null) continue;
                s_TargetIds.Add(target.GetInstanceID());
                if (!HasSolidCollider(target))
                {
                    var bounds = ObjectEdits.BoundsOf(target, out bool hasBounds);
                    if (hasBounds) { bounds.Expand(BoundsMarginInMeters * 2f); s_TargetAreas.Add(bounds); }
                }
            }
            s_TargetScratch.Clear();
            s_InstancedScratch.Clear();
            s_AnywhereInCircle = s_TargetIds.Count == 0 && s_TargetAreas.Count == 0;
            SteamRailwayMod.Debug($"{obstacle.Identifier}: {s_TargetIds.Count} object(s), {s_TargetAreas.Count} area(s) take a charge" +
                                  (s_AnywhereInCircle ? " (none found: anywhere in the circle)." : "."));
        }

        /// The ray hit something the clearing will change: an object, a part of one or an instanced rock's place.
        static bool IsTarget(RaycastHit rayHit)
        {
            for (var node = rayHit.collider.transform; node != null; node = node.parent)
                if (s_TargetIds.Contains(node.GetInstanceID())) return true;
            for (int index = 0; index < s_TargetAreas.Count; index++)
                if (s_TargetAreas[index].Contains(rayHit.point)) return true;
            return false;
        }

        static bool HasSolidCollider(Transform target)
        {
            foreach (var collider in target.GetComponentsInChildren<Collider>(false))
                if (collider.enabled && !collider.isTrigger) return true;
            return false;
        }

        /// The blocking rockfall (not being blasted) whose circle holds the player, or null. Hard mode only.
        static ObstacleData FindRockfallAroundPlayer()
        {
            if (!Obstacles.HardMode) return null;
            var player = GameManager.GetPlayerObject();
            if (player == null) return null;
            var playerPosition = player.transform.position;
            var obstacles = Obstacles.All;
            for (int index = 0; index < obstacles.Count; index++)
            {
                var obstacle = obstacles[index];
                if (Obstacles.KindOf(obstacle) != ObstacleKind.Rockfall || !Obstacles.IsHolding(obstacle) || s_Fusing.Contains(obstacle.Identifier)) continue;
                if (WithinCircle(obstacle, playerPosition, CircleMarginInMeters + ReachInMeters)) return obstacle;
            }
            return null;
        }

        static bool WithinCircle(ObstacleData obstacle, Vector3 point, float margin)
        {
            var center = Obstacles.CenterOf(obstacle);
            float deltaX = point.x - center.x, deltaZ = point.z - center.z, reach = obstacle.Radius + margin;
            return deltaX * deltaX + deltaZ * deltaZ <= reach * reach;
        }

        static void HideZone()
        {
            if (s_Zone != null && s_Zone.activeSelf) s_Zone.SetActive(false);
        }

        /// The zone object, named after the rockfall it belongs to (built again after a region change destroyed it).
        static GameObject EnsureZone(ObstacleData obstacle)
        {
            if (s_Zone == null) { s_Zone = BuildZone(); s_ZoneObstacle = null; }
            if (s_ZoneObstacle != obstacle) { s_Zone.name = ZoneNamePrefix + obstacle.Identifier; s_ZoneObstacle = obstacle; }
            return s_Zone;
        }

        static GameObject BuildZone() => BuildZone(ZoneNamePrefix, Localizer.Translate("Patlayıcı yük yerleştiriliyor...", "Placing the blasting charge..."), HoldTimeInSeconds);

        internal static GameObject BuildZone(string zoneName, string holdText, float holdTime)
        {
            var zone = new GameObject(zoneName);
            zone.layer = LayerMask.NameToLayer("InteractiveProp");
            var box = zone.AddComponent<BoxCollider>();
            box.size = Vector3.one * ZoneSizeInMeters;
            // a trigger: never in the player's way, but the game's interaction ray still finds it
            box.isTrigger = true;
            var hold = zone.AddComponent<TimedHoldInteraction>();
            hold.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            hold.m_DefaultHoverText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldTime = holdTime;
            hold.HoldTime = holdTime;
            hold.HoldText = holdText;
            zone.SetActive(false);
            return zone;
        }

        static void UpdateZoneText(GameObject zone, ObstacleData obstacle, int carried)
        {
            int needed = ChargesNeeded(obstacle);
            var interaction = zone.GetComponent<BaseInteraction>();
            if (interaction != null)
                interaction.HoverText = carried >= needed
                    ? Localizer.Translate($"Kaya yığınına patlayıcı yük yerleştir ({needed} adet, basılı tut)", $"Place blasting charges on the rockfall ({needed}, hold)")
                    : Localizer.Translate($"Kaya yığınını dağıtmak için {needed} patlayıcı yük gerekir (envanterde {carried})", $"Clearing this rockfall takes {needed} blasting charge(s) (you carry {carried})");
            var hold = zone.GetComponent<TimedHoldInteraction>();
            if (hold != null) hold.CanInteract = carried >= needed;
        }

        // ------------------------------------------------------------------ placing, wiring and blasting

        /// What is being blasted: the placed charges and the detonator box at the far end of the cable.
        sealed class Blast
        {
            public ObstacleData Obstacle;
            public Vector3 ChargePoint;
            public int Charges;
            public GameObject ChargeObject;
            public GameObject Box;
            public GameObject Cable;
            public bool Armed;
            /// The box while the player holds it in the game's placement mode.
            public GameObject PlacingBox;
            /// The frame the placement mode was seen to end (-1 while it runs): the game removes a cancelled box a frame later.
            public int PlacementEndedFrame = -1;
        }

        static Blast s_Blast;
        static GameObject s_CableZone;
        static BaseInteraction s_CableZoneInteraction;
        static TimedHoldInteraction s_CableZoneHold;

        /// The hold on one of the zones completed.
        public static bool Place(GameObject zone)
        {
            if (zone.name == CableZoneName) return PlaceDetonator(zone);
            if (zone.name == PlungerName) return PressPlunger();
            return PlaceCharges(zone);
        }

        /// The charges are placed on the rocks; the cable comes next.
        static bool PlaceCharges(GameObject zone)
        {
            string rockfallIdentifier = zone.name.Substring(ZoneNamePrefix.Length);
            var obstacle = Obstacles.Find(rockfallIdentifier);
            if (obstacle == null || !Obstacles.IsHolding(obstacle) || s_Fusing.Contains(rockfallIdentifier) || s_Blast != null) return false;
            int needed = ChargesNeeded(obstacle);
            if (ChargesCarried() < needed)
            {
                CabControls.Message(Localizer.Translate($"Yeterli patlayıcı yük yok ({needed} gerekir).", $"Not enough blasting charges ({needed} needed)."));
                return false;
            }
            GameManager.GetInventoryComponent().RemoveGearFromInventory(GearName, needed);
            s_Fusing.Add(rockfallIdentifier);
            var point = zone.transform.position;
            s_InsideObstacle = null;
            HideZone();
            s_Blast = new Blast { Obstacle = obstacle, ChargePoint = point, Charges = needed, ChargeObject = BuildPlacedCharges(point, needed) };
            CabControls.Message(Localizer.Translate($"{needed} patlayıcı yük yerleştirildi. Tetikleyici kutu elinde: uzaklaş, {Mathf.RoundToInt(MinimumCableDistanceInMeters)}-{Mathf.RoundToInt(MaximumCableDistanceInMeters)} m ötede sol tıkla yerleştir.",
                                    $"{needed} blasting charge(s) placed. The detonator box is in your hands: walk away and click to set it down {Mathf.RoundToInt(MinimumCableDistanceInMeters)}-{Mathf.RoundToInt(MaximumCableDistanceInMeters)} m away."));
            StartBoxPlacement(s_Blast);
            return true;
        }

        /// Every frame while charges lie waiting for their detonator: the box zone follows the player's gaze.
        static void UpdateWiring()
        {
            var blast = s_Blast;
            if (blast.Armed) return;
            var player = GameManager.GetPlayerObject();
            bool lost = blast.ChargeObject == null || Obstacles.Find(blast.Obstacle.Identifier) != blast.Obstacle || !Obstacles.IsHolding(blast.Obstacle);
            if (lost || (player != null && Vector3.Distance(player.transform.position, blast.ChargePoint) > AbandonDistanceInMeters))
            {
                Abandon(blast, lost);
                return;
            }
            if (blast.PlacingBox != null || PlacementModeActive(blast))
            {
                HideCableZone();
                var placing = blast.PlacingBox;
                var playerManager = GameManager.GetPlayerManagerComponent();
                bool stillPlacing = placing != null && playerManager != null && playerManager.GetObjectToPlace() == placing;
                if (stillPlacing) { blast.PlacementEndedFrame = -1; return; }
                // the placement mode ended: the box was set down (it stays) or put away (the game removes it a frame later)
                if (blast.PlacementEndedFrame < 0) { blast.PlacementEndedFrame = Time.frameCount; return; }
                if (Time.frameCount - blast.PlacementEndedFrame < PlacementSettleFrames) return;
                blast.PlacingBox = null;
                blast.PlacementEndedFrame = -1;
                if (placing != null && placing.activeInHierarchy && FlatDistance(placing.transform.position, blast.ChargePoint) >= MinimumCableDistanceInMeters - CableDistanceToleranceInMeters)
                    FinishBoxPlacement(blast, placing);
                else
                {
                    if (placing != null) Object.Destroy(placing);
                    CabControls.Message(Localizer.Translate("Kutu yerleştirilmedi: yeniden eline almak için yüklerin yakınına bak.", "The box was not set down: look near the charges to take it in hand again."));
                }
                return;
            }
            // not holding the box: a hold zone where the player looks takes it in hand again
            var camera = GameManager.GetMainCamera();
            if (camera == null) { HideCableZone(); return; }
            var cameraTransform = camera.transform;
            if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out var rayHit, ReachInMeters, ~0, QueryTriggerInteraction.Ignore))
            {
                HideCableZone();
                return;
            }
            if (s_CableZone == null)
            {
                s_CableZone = BuildZone(CableZoneName, Localizer.Translate("Tetikleyici kutu alınıyor...", "Taking the detonator box..."), HoldTimeInSeconds);
                s_CableZoneInteraction = s_CableZone.GetComponent<BaseInteraction>();
                s_CableZoneHold = s_CableZone.GetComponent<TimedHoldInteraction>();
            }
            s_CableZone.transform.position = rayHit.point;
            if (!s_CableZone.activeSelf) s_CableZone.SetActive(true);
            if (s_CableZoneInteraction != null) s_CableZoneInteraction.HoverText = Localizer.Translate("Tetikleyici kutuyu eline al (basılı tut)", "Take the detonator box in hand (hold)");
            if (s_CableZoneHold != null) s_CableZoneHold.CanInteract = true;
        }

        static bool PlacementModeActive(Blast blast)
        {
            var playerManager = GameManager.GetPlayerManagerComponent();
            return blast.PlacingBox != null && playerManager != null && playerManager.GetObjectToPlace() == blast.PlacingBox;
        }

        /// The detonator box goes into the game's placement mode: a ghost follows the view and a click sets it down.
        static bool StartBoxPlacement(Blast blast)
        {
            var box = InstantiatePrefab(PlungerPrefab) ?? BuildPlainBox();
            box.name = PlungerName;
            var playerManager = GameManager.GetPlayerManagerComponent();
            bool started = false;
            try { started = playerManager != null && playerManager.StartPlaceMesh(box, PlaceMeshFlags.DestroyOnCancel, Il2CppTLD.Placement.PlaceMeshRules.Default); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"The detonator box could not be put in placement mode: {exception.Message}"); }
            if (!started) { Object.Destroy(box); return false; }
            blast.PlacingBox = box;
            return true;
        }

        /// The click that would set the box down: only within the cable distance range from the charges.
        public static bool AllowPlacement(GameObject objectToPlace)
        {
            var blast = s_Blast;
            if (blast == null || blast.PlacingBox == null || objectToPlace != blast.PlacingBox) return true;
            float distance = FlatDistance(objectToPlace.transform.position, blast.ChargePoint);
            if (distance >= MinimumCableDistanceInMeters && distance <= MaximumCableDistanceInMeters) return true;
            CabControls.Message(distance < MinimumCableDistanceInMeters
                ? Localizer.Translate($"Çok yakın: kutu yüklerden en az {MinimumCableDistanceInMeters:0} m uzağa konmalı ({distance:0} m).", $"Too close: the box must stand at least {MinimumCableDistanceInMeters:0} m from the charges ({distance:0} m).")
                : Localizer.Translate($"Kablo yetmiyor: kutu yüklerden en çok {MaximumCableDistanceInMeters:0} m uzağa konabilir ({distance:0} m).", $"The cable is too short: the box can stand at most {MaximumCableDistanceInMeters:0} m from the charges ({distance:0} m)."));
            return false;
        }

        /// The game's placement mode ended with the box set down: it gets its hold interaction and the cable is laid.
        static void FinishBoxPlacement(Blast blast, GameObject box)
        {
            SetLayerRecursively(box, LayerMask.NameToLayer("InteractiveProp"));
            var hold = box.GetComponent<TimedHoldInteraction>() ?? box.AddComponent<TimedHoldInteraction>();
            hold.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            hold.m_DefaultHoverText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldTime = PlungerHoldTimeInSeconds;
            hold.HoldTime = PlungerHoldTimeInSeconds;
            hold.HoldText = Localizer.Translate("Kol aşağı bastırılıyor...", "Pushing the plunger down...");
            hold.CanInteract = true;
            hold.HoverText = Localizer.Translate("Patlat: kolu aşağı bas (basılı tut)", "Blast: push the plunger down (hold)");
            blast.Box = box;
            var anchor = box.transform.Find("CableAnchor");
            blast.Cable = BuildCable(blast.ChargePoint + Vector3.up * CableLiftAtChargeInMeters, anchor != null ? anchor.position : box.transform.position + Vector3.up * CableLiftAtBoxInMeters);
            blast.Armed = true;
            CabControls.Message(Localizer.Translate("Kablo döşendi. Güvenli bir uzaklıktan kolu aşağı bas!", "The cable is laid. Push the plunger down from a safe distance!"));
        }

        static void HideCableZone()
        {
            if (s_CableZone != null && s_CableZone.activeSelf) s_CableZone.SetActive(false);
        }

        static float FlatDistance(Vector3 first, Vector3 second)
        {
            float deltaX = first.x - second.x, deltaZ = first.z - second.z;
            return Mathf.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        }

        /// The hold zone: the box is taken in hand again (placement mode), e.g. after it was put away.
        static bool PlaceDetonator(GameObject zone)
        {
            var blast = s_Blast;
            if (blast == null || blast.Armed || blast.PlacingBox != null) return false;
            if (!StartBoxPlacement(blast)) { CabControls.Message(Localizer.Translate("Kutu elde tutulamadı.", "The box could not be taken in hand.")); return false; }
            HideCableZone();
            CabControls.Message(Localizer.Translate($"Kutuyu {Mathf.RoundToInt(MinimumCableDistanceInMeters)}-{Mathf.RoundToInt(MaximumCableDistanceInMeters)} m ötede sol tıkla yerleştir.", $"Click to set the box down {Mathf.RoundToInt(MinimumCableDistanceInMeters)}-{Mathf.RoundToInt(MaximumCableDistanceInMeters)} m away."));
            return true;
        }

        static void SetLayerRecursively(GameObject target, int layer)
        {
            foreach (var transform in target.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer;
        }

        /// Stand-in when the pack's prefab cannot be loaded: a plain box with a plunger rod.
        static GameObject BuildPlainBox()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.transform.localScale = new Vector3(PlainBoxWidthInMeters, PlainBoxHeightInMeters, PlainBoxDepthInMeters);
            var plunger = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(plunger.GetComponent<Collider>());
            plunger.name = "Plunger";
            plunger.transform.SetParent(root.transform, false);
            plunger.transform.localScale = new Vector3(PlainPlungerWidthInMeters, PlainPlungerHeightInMeters, PlainPlungerWidthInMeters);
            plunger.transform.localPosition = new Vector3(0f, PlainPlungerOffsetInMeters, 0f);
            return root;
        }

        /// The cable lies along the ground between the charges and the box (a line that follows the terrain).
        static GameObject BuildCable(Vector3 fromWorldPosition, Vector3 toWorldPosition)
        {
            var cable = new GameObject("SR_DynamiteCable");
            var line = cable.AddComponent<LineRenderer>();
            line.positionCount = CablePointCount;
            line.startWidth = line.endWidth = CableWidthInMeters;
            line.numCornerVertices = CableCornerVertices;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            var material = GameShaderMaterial();
            if (material != null) { material.color = new Color(CableColorLevel, CableColorLevel, CableColorLevel); line.sharedMaterial = material; }
            for (int index = 0; index < CablePointCount; index++)
            {
                float fraction = index / (float)(CablePointCount - 1);
                var point = Vector3.Lerp(fromWorldPosition, toWorldPosition, fraction);
                // follow the ground; the ends stay on the charge and the box
                if (index > 0 && index < CablePointCount - 1
                    && Physics.Raycast(point + Vector3.up * GroundProbeHeightInMeters, Vector3.down, out var ground, GroundProbeDistanceInMeters, ~0, QueryTriggerInteraction.Ignore))
                    point.y = ground.point.y + CableGroundOffsetInMeters;
                line.SetPosition(index, point);
            }
            return cable;
        }

        static Material s_GearReference;

        /// A material with the game's own gear shader, for things the mod draws itself.
        internal static Material GameShaderMaterial()
        {
            if (s_GearReference == null)
            {
                var cup = Il2CppHelpers.LoadAsset<GameObject>("GEAR_CoffeeCup");
                var renderer = cup != null ? cup.GetComponentInChildren<MeshRenderer>() : null;
                if (renderer != null) s_GearReference = renderer.sharedMaterial;
            }
            if (s_GearReference == null) return null;
            var material = new Material(s_GearReference);
            material.mainTexture = Texture2D.whiteTexture;
            return material;
        }

        static readonly Dictionary<string, GameObject> s_Prefabs = new Dictionary<string, GameObject>();

        /// A prefab of the item pack, instantiated with the game's shader; null when missing.
        static GameObject InstantiatePrefab(string key)
        {
            if (!s_Prefabs.TryGetValue(key, out var prefab))
            {
                try { prefab = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(key).WaitForCompletion(); }
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Dynamite prefab {key}: {exception.Message}"); }
                s_Prefabs[key] = prefab;
            }
            if (prefab == null) return null;
            var instance = Object.Instantiate(prefab);
            if (s_GearReference == null) GameShaderMaterial();
            if (s_GearReference != null)
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.materials)
                        if (material.shader.name == "Standard")
                        {
                            material.shader = s_GearReference.shader;
                            material.shaderKeywords = s_GearReference.shaderKeywords;
                        }
            return instance;
        }

        /// The charges lie on the ground side by side, facing the way the player looks.
        static GameObject BuildPlacedCharges(Vector3 point, int charges)
        {
            var root = new GameObject("SR_PlacedCharges");
            root.transform.position = point;
            var camera = GameManager.GetMainCamera();
            var forward = camera != null ? camera.transform.forward : Vector3.forward;
            forward.y = 0f;
            root.transform.rotation = Quaternion.LookRotation(forward.sqrMagnitude > MinimumForwardSquaredLength ? forward : Vector3.forward, Vector3.up);
            for (int index = 0; index < charges; index++)
            {
                var bundle = InstantiatePrefab(PlacedChargePrefab);
                if (bundle == null)
                {
                    bundle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Object.Destroy(bundle.GetComponent<Collider>());
                    bundle.transform.localScale = Vector3.one * FallbackChargeSizeInMeters;
                }
                bundle.transform.SetParent(root.transform, false);
                bundle.transform.localPosition = new Vector3((index - (charges - 1) * 0.5f) * ChargeSpacingInMeters, 0f, 0f);
            }
            return root;
        }

        /// The plunger is pushed down: it clicks, a moment later the charges go off.
        static bool PressPlunger()
        {
            var blast = s_Blast;
            if (blast == null || !blast.Armed) return false;
            blast.Armed = false;
            MelonLoader.MelonCoroutines.Start(Detonate(blast));
            return true;
        }

        static IEnumerator Detonate(Blast blast)
        {
            var plunger = blast.Box != null ? blast.Box.transform.Find("Plunger") : null;
            if (blast.Box != null) WorldSounds.PlayOneShotAt(SynthSounds.Notch, blast.Box.transform.position, ClickVolume, ClickPitch);
            if (plunger != null)
            {
                var start = plunger.localPosition;
                float elapsed = 0f;
                while (elapsed < PlungerPressSeconds && plunger != null)
                {
                    elapsed += Time.deltaTime;
                    plunger.localPosition = start + Vector3.down * (PlungerPressDepthInMeters * Mathf.Clamp01(elapsed / PlungerPressSeconds));
                    yield return null;
                }
            }
            yield return new WaitForSeconds(BlastDelayAfterPressInSeconds);

            var point = blast.ChargePoint;
            if (blast.ChargeObject != null) Object.Destroy(blast.ChargeObject);
            float scale = Mathf.Min(MaximumBlastScale, BlastBaseScale + BlastScalePerCharge * blast.Charges);
            BoilerExplosion.BlastAt(point, Quaternion.identity, scale);
            WorldSounds.PlayExplosion(point);
            var player = GameManager.GetPlayerObject();
            if (player != null)
                WorldSounds.ShakeCamera(BlastShakeStrength * Mathf.Clamp01(1f - Vector3.Distance(player.transform.position, point) / BlastShakeRangeInMeters));
            AnnounceBlast(point);

            // the fireball is drawn first; the rocks go a moment later, inside the smoke
            yield return new WaitForSeconds(ClearDelayAfterBlastInSeconds);
            // the region may have been left while the cable was laid
            if (Obstacles.Find(blast.Obstacle.Identifier) == blast.Obstacle) Obstacles.Clear(blast.Obstacle);
            s_Fusing.Remove(blast.Obstacle.Identifier);
            s_Blast = null;
            yield return new WaitForSeconds(CleanupDelayInSeconds);
            if (blast.Box != null) Object.Destroy(blast.Box);
            if (blast.Cable != null) Object.Destroy(blast.Cable);
        }

        /// The charges were left (the player went far away, the region changed or the rockfall was cleared another way).
        static void Abandon(Blast blast, bool lost)
        {
            if (blast.ChargeObject != null) Object.Destroy(blast.ChargeObject);
            if (blast.Box != null) Object.Destroy(blast.Box);
            if (blast.Cable != null) Object.Destroy(blast.Cable);
            if (blast.PlacingBox != null)
            {
                var playerManager = GameManager.GetPlayerManagerComponent();
                if (playerManager != null && playerManager.GetObjectToPlace() == blast.PlacingBox) playerManager.ExitMeshPlacement();
                if (blast.PlacingBox != null) Object.Destroy(blast.PlacingBox);
            }
            HideCableZone();
            s_Fusing.Remove(blast.Obstacle.Identifier);
            s_Blast = null;
            if (!lost) CabControls.Message(Localizer.Translate("Kabloyu bıraktın: yükler boşa gitti.", "You left the cable: the charges are wasted."));
        }

        /// The shock of a blast close by: health falls the nearer the player stands, never below a few points.
        static void HurtByBlast(float distance)
        {
            if (distance >= BlastDamageRadiusInMeters) return;
            var condition = GameManager.GetConditionComponent();
            if (condition == null) return;
            float damage = BlastDamageAtChargeInHealth * (1f - distance / BlastDamageRadiusInMeters);
            damage = Mathf.Min(damage, condition.m_CurrentHP - SurvivingHealth);
            if (damage <= 0f) return;
            try { condition.AddHealth(-damage, DamageSource.Unspecified); }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Blast damage: {exception.Message}"); }
        }

        static void AnnounceBlast(Vector3 point)
        {
            var player = GameManager.GetPlayerObject();
            if (player == null) return;
            float distance = Vector3.Distance(player.transform.position, point);
            if (distance > HearRadiusInMeters) return;
            HurtByBlast(distance);
            if (distance <= TinnitusRadiusInMeters && SteamRailwayMod.TinnitusEnabled) WorldSounds.PlayTinnitus(1f - distance / TinnitusRadiusInMeters);
            if (distance <= BurnsRadiusInMeters)
            {
                var burns = GameManager.GetBurnsComponent();
                if (burns != null && !burns.HasBurns())
                    burns.BurnsStart(BurnsCause, true, false, AfflictionOptions.PlayFX | AfflictionOptions.DisplayIcon);
                CabControls.Message(SteamRailwayMod.TinnitusEnabled
                    ? Localizer.Translate("PATLAMA! Çok yakındaydın: yandın ve kulakların çınlıyor.", "BLAST! You stood too close: you are burned and your ears ring.")
                    : Localizer.Translate("PATLAMA! Çok yakındaydın: yandın.", "BLAST! You stood too close: you are burned."));
            }
            else if (distance <= TinnitusRadiusInMeters && SteamRailwayMod.TinnitusEnabled)
                CabControls.Message(Localizer.Translate("PATLAMA! Kulakların çınlıyor.", "BLAST! Your ears ring."));
            else CabControls.Message(Localizer.Translate("PATLAMA! Kaya yığını dağıldı.", "BLAST! The rockfall is blown apart."));
        }
    }
}
