using Il2Cpp;
using Il2CppFluffyUnderware.Curvy;
using Il2CppFluffyUnderware.Curvy.Controllers;
using Il2CppTLD.Gameplay;
using Il2CppTLD.Interactions;
using Il2CppTLD.SteamTrain;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;

namespace SteamRailway
{
    /// Tuning values of the coal locomotive.
    internal static class TrainConfig
    {
        public const string StovePrefab = "Assets/Prefabs/Interactive/INTERACTIVE_PotBellyStove.prefab";
        public const string SmokeMaterial = "Assets/Materials/FX_SmokeChimneyA_Mat.mat";
        public const string SteamMaterial = "Assets/ArtAssets/FX/Materials/FX_m_steamDirtier.mat";
        /// Container prefabs are keyed by their short name, not by their asset path.
        public const string StoragePrefab = "CONTAINER_SteamerTrunk";
        /// Dummy Addressables key so TrainCar serialization never sees a null reference.
        /// Our trains are removed from the game's own save in SavePatches and saved by RailwayManager instead.
        public const string AssetKey = "steamrailway_coal_locomotive";

        public const long WaterCapacityUnits = 100_000_000_000L;   // ~100 L
        public const long WaterBoiledPerMinute = 500_000_000L;     // ~0.5 L per game minute
        /// The boiler is written for liquid fuel; we keep a token in the fuel tank and set its burn rate to zero.
        /// The real fuel is the coal burning in the firebox stove.
        public const long FuelTokenUnits = 1_000_000_000L;

        // Player cabin (local space of the train root, +Z forward).
        public static readonly Vector3 CabinFloor = new Vector3(0f, 1.35f, -2.6f);
        public static readonly Vector3 CabinSize = new Vector3(1.7f, 0.3f, 1.9f);   // walkable area: z -4.25 .. -2.35
        public static readonly Vector3 FireboxPos = new Vector3(0f, 1.35f, -0.8f);
        public const float FireFxScale = 0.75f;   // the stove's fire effect, shrunk to fit the fire hole
        public static readonly Vector3 DoorPos = new Vector3(1.3f, 2.3f, -2.6f);
        public static readonly Vector3 ExitPos = new Vector3(2.6f, 0.3f, -2.6f);
        public static readonly Vector3 ChimneyTop = new Vector3(0f, 4.4f, 3.8f);
        public static readonly Vector3 StoragePos = new Vector3(-0.55f, 1.35f, -3.4f);    }

    /// Builds a complete coal-fired SteamTrain from code. Everything is created under an inactive root,
    /// wired up, and only then activated, because the game's Awake methods expect their references to be set.
    internal static class TrainBuilder
    {
        public const string NamePrefix = "SteamRailway_Train_";

        public static SteamTrain Build(string guid, CurvySpline spline, float splinePos, Material bodyMat)
        {
            var root = new GameObject(NamePrefix + guid);
            root.SetActive(false);
            var R = root.transform;

            var model = LocomotiveModel.Spawn(R);
            s_Root = R;
            s_Model = model != null ? model.transform : null;
            if (s_Model == null) BuildPlaceholderBody(R, bodyMat);

            var sc = root.AddComponent<SplineController>();
            sc.Spline = spline;
            sc.PositionMode = CurvyPositionMode.WorldUnits;
            sc.Position = Mathf.Clamp(splinePos, 0f, spline.Length);
            sc.OrientationMode = OrientationModeEnum.Tangent;
            sc.OrientationAxis = OrientationAxisEnum.Forward; // without this the train stands on its nose
            // the engine always faces the line's +direction; running in reverse must not turn it round
            sc.IgnoreDirection = true;
            sc.Clamping = CurvyClamping.Clamp;
            sc.Speed = 0f;

            var tr = root.AddComponent<SteamTrain>();
            tr.m_SplineController = sc;
            tr.m_AssetReference = new AssetReferenceGameObject(TrainConfig.AssetKey);
            tr.m_TrainBoardedState = H.St();
            tr.m_TrainUnboardedState = H.St();
            tr.m_SteamWhistleToot = H.Ev();

            BuildCabin(R, tr);
            BuildColliders(R, tr);
            var comps = H.Child(R, "Components", H.V(0, 2.5f, -1f)).transform;
            var engine = BuildEngine(comps, tr);
            BuildBoiler(comps, R, tr, guid);
            BuildWearComponents(comps, tr);
            BuildTanks(comps, tr);
            BuildCockpit(R, tr);
            BuildEffects(R, tr);
            BuildDoor(R, tr, engine, bodyMat);
            BuildCabinStorage(R, tr, guid);
            BuildControls(R, tr, engine, bodyMat);
            tr.m_FleeTriggerVolume = H.Child(R, "Flee", Vector3.zero).AddComponent<AiFleeTriggerVolume>();

            ObjectGuid.MaybeAttachObjectGuidAddOrReplace(root, guid);
            root.SetActive(true);

            tr.m_WaterSource.Set(H.Vol(TrainConfig.WaterCapacityUnits));
            tr.m_FuelSource.Set(H.Vol(TrainConfig.FuelTokenUnits));
            ConfigureEffects(tr);
            return tr;
        }

        static Transform s_Root, s_Model;

        /// Position of a model socket in train space (see Docs/LOKOMOTIF.md), or the placeholder layout without a model.
        static Vector3 At(string socket, Vector3 fallback)
        {
            var t = LocomotiveModel.FindDeep(s_Model, socket);
            return t != null ? s_Root.InverseTransformPoint(t.position) : fallback;
        }

        static bool HasModel => s_Model != null;

        static void BuildPlaceholderBody(Transform R, Material mat)
        {
            H.Cube(R, "Body", H.V(0, 1.2f, 0), H.V(2.6f, 0.3f, 9f), mat, true);
            H.Cube(R, "CabRoof", H.V(0, 4.2f, -2.5f), H.V(2.6f, 0.15f, 3.5f), mat, false);
            H.Cube(R, "BoilerShell", H.V(0, 2.3f, 2.2f), H.V(1.6f, 1.6f, 4.6f), mat, false);
            H.Cube(R, "Chimney", H.V(0, 3.6f, 3.8f), H.V(0.5f, 1.2f, 0.5f), mat, false);
        }

        static void BuildCabin(Transform R, SteamTrain tr)
        {
            // The player is not parented to the train: each frame the game copies the player's position to
            // CameraDummy, which is the player's FEET, walking inside CabinLimits.
            var floor = At("Socket_CabFloor", TrainConfig.CabinFloor);
            var dummy = H.Child(R, "CameraDummy", floor);
            var sway = dummy.AddComponent<SimpleSway>();
            var offset = new MinMax(); offset.m_Min = 0f; offset.m_Max = 1f;
            sway.m_RandomTimeOffset = offset;
            sway.m_SwayPeriod = 4f; sway.m_SwayHalfAngle = 0.5f; sway.m_SwayMultiplier = 1f; sway.m_Direction = H.V(0, 0, 1);

            var cabin = H.Child(R, "CabinLimits", floor).AddComponent<BoxCollider>();
            cabin.size = TrainConfig.CabinSize;
            cabin.isTrigger = true;

            tr.m_CameraDummy = dummy.transform;
            tr.m_CameraSimpleSway = sway;
            tr.m_CabinLimits = cabin;
            tr.m_MovementSpeed = 2f;

            var curve = new AnimationCurve();
            curve.AddKey(0f, 0f); curve.AddKey(0.5f, 0.02f); curve.AddKey(1f, 0f);
            tr.m_VerticalCameraMotionPattern = curve;
            tr.m_CameraMotionPatternLengthSeconds = 5f;
            tr.m_CameraMotionDamper = 0.125f;
            tr.m_LurchDamper = 0.2f;
            tr.m_FadeInSeconds = 0.5f;
            tr.m_FadeOutSeconds = 0.5f;
            tr.m_FootstepBaseDistanceMultiplier = 0.65f;
            tr.m_PlayerEnterTrainEventName = "event_EnterTrain";
            tr.m_PlayerExitTrainEventName = "event_ExitTrain";
        }

        static void BuildColliders(Transform R, SteamTrain tr)
        {
            tr.m_FrontCollider = MakeTrainCollider(R, "FrontCollider", At("Socket_Front", H.V(0, 1.5f, 7.4f)), tr);
            tr.m_RearCollider = MakeTrainCollider(R, "RearCollider", At("Socket_Rear", H.V(0, 1.5f, -4.8f)), tr);
            tr.m_LowRamSpeedThreshold = 7f;
            tr.m_HighRamSpeedThreshold = 12f;
        }

        static TrainCarCollider MakeTrainCollider(Transform R, string name, Vector3 pos, SteamTrain tr)
        {
            var go = H.Child(R, name, pos);
            go.AddComponent<Rigidbody>().isKinematic = true;
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = H.V(2.4f, 2f, 0.6f);
            var c = go.AddComponent<TrainCarCollider>();
            c.m_ParentTrainCar = tr;
            c.m_BoxCollider = box;
            return c;
        }

        static SteamTrainEngine BuildEngine(Transform comps, SteamTrain tr)
        {
            var e = H.Child(comps, "Engine", Vector3.zero).AddComponent<SteamTrainEngine>();
            e.m_SteamTrain = tr;
            e.m_SteamPSI = H.Rtpc(); e.m_TrainVelocity = H.Rtpc(); e.m_FireboxExplosionEvent = H.Ev();
            e.m_EngineStateNormal = H.St(); e.m_EngineStateOverfire = H.St();
            e.m_EngineStateExplosionRiskLow = H.St(); e.m_EngineStateExplosionRiskHigh = H.St(); e.m_AudioLastEngineState = H.St();
            e.m_OverfireStartEvent = new UnityEvent(); e.m_OverfireEndEvent = new UnityEvent();

            e.m_MinSteamPressureForLowSpeedMovement = 25; e.m_MinSteamPressureForHighSpeedMovement = 26;
            e.m_MaxSteamPressure = 300; e.m_OverfireMaxSteamPressure = 400; e.m_OverfireBonusMaxSpeedPercent = 15;
            e.m_MaxVelocityReverse = -8; e.m_MaxVelocityForwardLow = 8; e.m_MaxVelocityForwardHigh = 15;
            e.m_MinBrakeVelocityDecreasePerSecond = 1.25f; e.m_MaxBrakeVelocityDecreasePerSecond = 3;
            e.m_VelocityDecreasePerSecondNeutral = 0.5f; e.m_VelocityDecreaseInsufficientSteamPSI = 0.8f; e.m_VelocityDecreasePerSecondDownshift = 0.5f;
            e.m_VelocityIncreasePerSecondReverse = -1; e.m_VelocityIncreasePerSecondForwardLow = 1; e.m_VelocityIncreasePerSecondForwardHigh = 1.75f;
            e.m_MaxReverseSpeedToAllowShiftGearUp = -2; e.m_MaxForwardSpeedToAllowShiftGearDown = 2;
            e.m_SteamPSIBuiltPerSecond = 7;
            e.m_SteamPressurePSIConsumedPerSecondReverse = 5; e.m_SteamPressurePSIConsumedPerSecondForwardLow = 5;
            e.m_SteamPressurePSIConsumedPerSecondForwardHigh = 14; e.m_SteamPressurePSIConsumedPerBrakeSecond = 0.5f;
            tr.m_Engine = e;
            return e;
        }

        static void BuildBoiler(Transform comps, Transform R, SteamTrain tr, string guid)
        {
            var b = H.Child(comps, "Boiler", H.V(0, 0, 1)).AddComponent<SteamTrainBoiler>();
            SetupComponent(b, tr, "GAMEPLAY_Boiler");
            b.m_MaxTemperature = 350; b.m_TemperatureChangePerMinute = 600; b.m_AmbientHeatTemperature = 20;
            b.m_AmbientHeatHourStoredPerHourOn = 0.2f; b.m_MaxAmbientHeatHoursStored = 3; b.m_ExplosionCheckIntervalSeconds = 10;
            b.m_LowRiskMaxCondition = 49; b.m_LowRiskExplosionChance = 50; b.m_HighRiskMaxCondition = 24; b.m_HighRiskExplosionChance = 75;
            b.m_OverfireDamagePerHour = 100;
            b.m_WaterBoiledPerMinute = H.Vol(TrainConfig.WaterBoiledPerMinute);
            b.m_FuelBurnedPerMinute = H.Vol(0L);
            b.m_ExplosionEvent = new UnityEvent();
            b.m_BoilerTemperature = H.Rtpc(); b.m_BoilerStateOn = H.St(); b.m_BoilerStateOff = H.St();
            b.m_FurnaceLights = H.Arr(H.Child(b.transform, "FurnaceLight", Vector3.zero));
            b.m_FurnaceParticlesParent = H.Child(b.transform, "FurnaceParticles", Vector3.zero);
            b.m_InvisibleFire = AddFireboxStove(R, guid);
            tr.m_Boiler = b;
        }

        /// The firebox is the base game's pot belly stove, hidden behind the fire hole: clicking the fire doors opens
        /// the game's own fire menus (start fire / add fuel) and the stove's Fire is what heats the boiler.
        /// Only its invisible click box at the doors, its warmth and its fire sound remain.
        static Fire AddFireboxStove(Transform R, string trainGuid)
        {
            var prefab = H.LoadAsset<GameObject>(TrainConfig.StovePrefab);
            if (prefab == null) return null;
            var stove = Object.Instantiate(prefab);
            stove.SetActive(false);
            stove.name = "Firebox";
            stove.transform.SetParent(R, false);
            stove.transform.localPosition = At("Socket_Firebox", TrainConfig.FireboxPos);
            stove.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            foreach (var og in stove.GetComponentsInChildren<ObjectGuid>(true))
                og.SetRuntimeCachedPdid(trainGuid + "_" + og.gameObject.name);

            // Hide the stove's meshes and its own lights (our firelight comes out of the fire hole). Its fire effect
            // (flames, embers, sparks) stays and is moved onto the grate inside the fire hole.
            foreach (var r in stove.GetComponentsInChildren<Renderer>(true))
                if (r.TryCast<ParticleSystemRenderer>() == null) r.enabled = false;
            var fireT = stove.GetComponentInChildren<Fire>(true)?.transform;
            if (fireT != null)
            {
                fireT.position = R.TransformPoint(At("Socket_FireFX", H.V(0f, 1.86f, -1.79f)));
                fireT.rotation = R.rotation;
                fireT.localScale = Vector3.one * TrainConfig.FireFxScale;
            }
            foreach (var ps in stove.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;   // otherwise flames trail behind the moving train
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;       // follow the fire's scale to fit the fire hole
            }
            foreach (var l in stove.GetComponentsInChildren<Light>(true)) l.gameObject.SetActive(false);
            var lod = stove.GetComponent<LODGroup>();
            if (lod != null) lod.enabled = false;
            // No cooking or placing items on the firebox.
            foreach (var name in new[] { "Cylinder", "GearPlacePoint", "PlacePoints", "GearExclusionCollider" })
            {
                var t = LocomotiveModel.FindDeep(stove.transform, name);
                if (t != null) t.gameObject.SetActive(false);
            }
            foreach (var c in stove.GetComponentsInChildren<Collider>(true))
                if (!c.isTrigger) c.enabled = false;
            var ws = stove.GetComponent<WoodStove>();
            if (ws != null) ws.m_CookingSlots = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CookingSlot>(0);
            // The click box covers the fire doors.
            var box = stove.AddComponent<BoxCollider>();
            box.size = HasModel ? H.V(0.62f, 0.46f, 0.14f) : H.V(0.5f, 0.4f, 0.2f);

            var fire = stove.GetComponentInChildren<Fire>(true);
            if (fire != null) fire.m_SkipSerialization = true;   // saved with the train (TrainState.FireData)
            stove.SetActive(true);
            return fire;
        }

        static void BuildWearComponents(Transform comps, SteamTrain tr)
        {
            var wheels = H.Child(comps, "Wheels", H.V(0, -1.5f, 0)).AddComponent<SteamTrainWheels>();
            SetupComponent(wheels, tr, "GAMEPLAY_Wheels");
            var tiers = new Il2CppSystem.Collections.Generic.List<SteamTrainWheels.PerformanceTier>();
            int[] threshold = { 75, 50, 25, 0 };
            float[] speed = { 1f, 0.9f, 0.75f, 0.5f };
            float[] consumption = { 1f, 1.1f, 1.25f, 1.5f };
            for (int i = 0; i < threshold.Length; i++)
            {
                var t = new SteamTrainWheels.PerformanceTier();
                t.m_ConditionThreshold = threshold[i]; t.m_MaxVelocityIncreaseModifier = speed[i]; t.m_OilConsumedModifier = consumption[i];
                tiers.Add(t);
            }
            wheels.m_PerformanceTiers = tiers;
            tr.m_Wheels = wheels;

            var brake = H.Child(comps, "BrakeShoe", H.V(0.8f, -1.5f, 0)).AddComponent<SteamTrainBrakeShoe>();
            SetupComponent(brake, tr, "GAMEPLAY_BrakeShoes");
            tr.m_BrakeShoe = brake;

            var cow = H.Child(comps, "CowCatcher", H.V(0, -1.5f, 7f)).AddComponent<SteamTrainCowCatcher>();
            SetupComponent(cow, tr, "GAMEPLAY_CowCatcher");
            tr.m_CowCatcher = cow;

            var cook = H.Child(comps, "CookingSurface", H.V(-0.6f, 0, -2f)).AddComponent<SteamTrainCookingSurface>();
            SetupComponent(cook, tr, "GAMEPLAY_CookingSurface");
            cook.m_GearPlacePoints = new Il2CppSystem.Collections.Generic.List<GearPlacePoint>();
            cook.m_PendingDeserialization = new Il2CppSystem.Collections.Generic.List<SteamTrainCookingSurface.CookingSurfaceState>();
            tr.m_CookingSurface = cook;
        }

        static void BuildTanks(Transform comps, SteamTrain tr)
        {
            var fuel = H.Child(comps, "FuelSource", Vector3.zero).AddComponent<GenericLiquidContainer>();
            fuel.m_VolumeRTPC = H.Rtpc();
            fuel.m_MaxCapacity = H.Vol(TrainConfig.FuelTokenUnits * 2);
            tr.m_FuelSource = fuel;

            var water = H.Child(comps, "WaterSource", Vector3.zero).AddComponent<GenericLiquidContainer>();
            water.m_VolumeRTPC = H.Rtpc();
            water.m_MaxCapacity = H.Vol(TrainConfig.WaterCapacityUnits);
            tr.m_WaterSource = water;
        }

        static void BuildCockpit(Transform R, SteamTrain tr)
        {
            var ck = H.Child(R, "Cockpit", H.V(0, 2.5f, -1.2f)).AddComponent<SteamTrainCockpit>();
            var t = ck.transform;
            ck.m_SteamGaugePSINeedlePivot = H.Child(t, "PsiNeedle", Vector3.zero).transform;
            ck.m_SpeedometerNeedlePivot = H.Child(t, "SpeedNeedle", Vector3.zero).transform;
            ck.m_GearLeverPivot = H.Child(t, "GearLever", Vector3.zero).transform;
            ck.m_GearGaugePivot = H.Child(t, "GearGauge", Vector3.zero).transform;
            ck.m_BrakeLeverPivot = H.Child(t, "BrakeLever", Vector3.zero).transform;
            ck.m_BrakeGaugePivot = H.Child(t, "BrakeGauge", Vector3.zero).transform;
            ck.m_WaterTankGauge = H.Child(t, "WaterGauge", Vector3.zero).transform;
            ck.m_OilTankGauge = H.Child(t, "FuelGauge", Vector3.zero).transform;
            ck.m_LightRoot = H.Child(t, "Lights", Vector3.zero);
            ck.m_MaxSteamPSINeedleAngle = 120; ck.m_MaxSpeedometerNeedAngle = 95;
            ck.m_GearLeverMoveSpeed = 50; ck.m_GearGaugeMoveRatio = 6;
            ck.m_GearLeverAngleNeutral = -10; ck.m_GearLeverAngleLow = -20; ck.m_GearLeverAngleHigh = -30;
            ck.m_BrakeLeverMoveSpeed = 300; ck.m_BrakeGaugeMoveRatio = 2; ck.m_BrakeGaugeAngleOffset = 180; ck.m_BrakeLeverAngleOn = -90;
            ck.m_FullTankOffset = 0.1f;
            tr.m_Cockpit = ck;
        }

        static void BuildEffects(Transform R, SteamTrain tr)
        {
            var whistle = H.Child(R, "Whistle", At("Socket_SafetyValve", H.V(0, 4.5f, 0)));
            tr.m_SteamWhistleParent = whistle;
            tr.m_SteamWhistleLightParticleSystem = H.Child(whistle.transform, "SteamLight", Vector3.zero).AddComponent<ParticleSystem>();
            tr.m_SteamWhistleDenseParticleSystem = H.Child(whistle.transform, "SteamDense", Vector3.zero).AddComponent<ParticleSystem>();
            tr.m_SteamWhistleTootLengthSeconds = 1.6f;

            var smoke = H.Child(R, "ChimneySmoke", At("Socket_Chimney", TrainConfig.ChimneyTop)).AddComponent<ParticleSystem>();
            tr.m_SmokeStackParticleSystems = H.Arr(smoke);
            tr.m_MinEmissionOverTimeRate = 40;
            tr.m_MaxEmissionOverTimeRate = 100;
        }

        static void ConfigureEffects(SteamTrain tr)
        {
            var smokeMat = H.LoadAsset<Material>(TrainConfig.SmokeMaterial);
            var steamMat = H.LoadAsset<Material>(TrainConfig.SteamMaterial);

            var smoke = tr.m_SmokeStackParticleSystems[0];
            smoke.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var m = smoke.main;
            m.simulationSpace = ParticleSystemSimulationSpace.World; // smoke should trail behind the train
            m.startLifetime = new ParticleSystem.MinMaxCurve(6f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(2.2f);
            m.startSize = new ParticleSystem.MinMaxCurve(2.2f);
            m.startColor = new ParticleSystem.MinMaxGradient(new Color(0.25f, 0.24f, 0.23f, 0.6f));
            m.maxParticles = 400;
            var shape = smoke.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 12f; shape.radius = 0.25f;
            smoke.GetComponent<ParticleSystemRenderer>().sharedMaterial = smokeMat;
            smoke.Play();

            ConfigureSteam(tr.m_SteamWhistleLightParticleSystem, steamMat);
            ConfigureSteam(tr.m_SteamWhistleDenseParticleSystem, steamMat);
        }

        static void ConfigureSteam(ParticleSystem ps, Material mat)
        {
            var m = ps.main;
            m.simulationSpace = ParticleSystemSimulationSpace.World;
            m.startLifetime = new ParticleSystem.MinMaxCurve(1.5f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(4f);
            m.startSize = new ParticleSystem.MinMaxCurve(0.6f);
            m.playOnAwake = false;
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 8f; shape.radius = 0.05f;
            ps.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ps.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
            ps.Stop();
        }

        static void BuildDoor(Transform R, SteamTrain tr, SteamTrainEngine engine, Material mat)
        {
            int interactiveLayer = LayerMask.NameToLayer("InteractiveProp");
            var door = H.Child(R, "Door", At("Socket_Door_R", TrainConfig.DoorPos));
            door.layer = interactiveLayer;
            // The model's cab has an open side entrance; the door is an invisible collider filling it.
            if (!HasModel) H.Cube(door.transform, "DoorPanel", Vector3.zero, H.V(0.08f, 1.9f, 0.9f), mat, false);
            door.AddComponent<BoxCollider>().size = HasModel ? H.V(0.2f, 1.8f, 0.85f) : H.V(0.15f, 1.9f, 0.9f);

            var exit = H.Child(R, "ExitPoint", At("Socket_Exit_R", TrainConfig.ExitPos));
            exit.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

            // The Lock must exist before the door's Awake looks for it.
            var lk = door.AddComponent<Lock>();
            lk.m_ChanceLocked = 0f;
            lk.m_LockState = LockState.Unlocked;
            lk.m_LockStateRolled = true;
            lk.m_LockedKeypadText = H.Loc("GAMEPLAY_Locked");

            var d = door.AddComponent<SteamTrainDoor>();
            d.m_ParentSteamTrain = tr;
            d.m_ExitPoint = exit.transform;
            d.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
            d.m_SteamEngineEnter = H.Ev();
            d.m_SteamEngineExit = H.Ev();
            d.m_DefaultHoverText = H.Loc("GAMEPLAY_Door");
            d.m_DefaultHoldText = H.Loc("GAMEPLAY_Door");
            d.m_CanInteract = true;
            d.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            foreach (var ls in Object.FindObjectsOfType<LoadScene>())
                if (ls.m_HUD != null) { d.m_HUD = ls.m_HUD; break; }

            // SteamTrainDoor.UpdateInteraction throws every frame without a door window.
            var windowGo = H.Child(door.transform, "DoorWindow", H.V(0, 0.5f, 0));
            var look = H.Child(windowGo.transform, "LookTarget", H.V(0.6f, 0, 0));
            var w = windowGo.AddComponent<SteamTrainWindow>();
            w.m_Train = tr; w.m_Engine = engine; w.m_Door = d; w.m_WindowLookTarget = look.transform;
            w.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
            w.m_LeanOutEvent = H.Ev(); w.m_LeanInEvent = H.Ev();
            w.m_DefaultHoverText = H.Loc("GAMEPLAY_Window");
            w.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            d.m_DoorWindow = w;

            ObjectGuid.MaybeAttachObjectGuidAddOrReplace(door, System.Guid.NewGuid().ToString());
        }

        /// Cab levers and gauges (see CabControls). Two-way controls use SteamTrainGearLever (click / alternative click),
        /// one-way controls and gauges use SteamTrainBrakeLever. Positions are on the cab's front wall.
        static void BuildControls(Transform R, SteamTrain tr, SteamTrainEngine engine, Material mat)
        {
            var lever = H.V(0.06f, 0.5f, 0.06f);
            var knob = H.V(0.12f, 0.12f, 0.08f);
            var gauge = H.V(0.18f, 0.18f, 0.04f);
            bool m = HasModel;

            // Colliders are invisible boxes sized to each modelled fitting so neighbours never overlap
            // (overlapping boxes made the hover texts of the regulator and the steam gauge swap).
            TwoWay(R, tr, engine, mat, CabControls.Regulator, At("Socket_Regulator", H.V(0.5f, 2.6f, -1.45f)), m ? H.V(0.42f, 0.3f, 0.1f) : lever);
            TwoWay(R, tr, engine, mat, CabControls.Reverser, At("Socket_Reverser", H.V(0.65f, 2.0f, -1.5f)), m ? H.V(0.14f, 0.5f, 0.2f) : lever);
            OneWay(R, tr, engine, mat, CabControls.Brake, At("Socket_Brake", H.V(-0.55f, 2.2f, -1.5f)), m ? H.V(0.16f, 0.14f, 0.42f) : lever);
            OneWay(R, tr, engine, mat, CabControls.HandBrake, At("Socket_HandBrake", H.V(-0.9f, 2.2f, -3.6f)), m ? H.V(0.36f, 0.14f, 0.36f) : knob);
            OneWay(R, tr, engine, mat, CabControls.Injector, At("Socket_Injector", H.V(-0.65f, 2.75f, -1.45f)), m ? H.V(0.16f, 0.16f, 0.08f) : knob);
            OneWay(R, tr, engine, mat, CabControls.Blower, At("Socket_Blower", H.V(0.3f, 3.0f, -1.45f)), m ? H.V(0.16f, 0.16f, 0.08f) : knob);
            OneWay(R, tr, engine, mat, CabControls.PressureGauge, At("Socket_PressureGauge", H.V(-0.2f, 3.0f, -1.42f)), m ? H.V(0.36f, 0.36f, 0.06f) : gauge);
            OneWay(R, tr, engine, mat, CabControls.SpeedGauge, At("Socket_SpeedGauge", H.V(0.2f, 3.0f, -1.42f)), m ? H.V(0.25f, 0.25f, 0.06f) : gauge);
            OneWay(R, tr, engine, mat, CabControls.HeatGauge, At("Socket_HeatGauge", H.V(0f, 2.75f, -1.42f)), m ? H.V(0.18f, 0.18f, 0.06f) : gauge);
            OneWay(R, tr, engine, mat, CabControls.WaterGlass, At("Socket_WaterGlass", H.V(-0.35f, 2.55f, -1.42f)), H.V(0.1f, 0.55f, 0.08f));
            OneWay(R, tr, engine, mat, CabControls.TenderGauge, At("Socket_TenderGauge", H.V(0f, 2.6f, -3.75f)), m ? H.V(0.16f, 0.48f, 0.06f) : gauge);
            OneWay(R, tr, engine, mat, CabControls.Pump, At("Socket_Pump", H.V(0.55f, 2.6f, -3.75f)), m ? H.V(0.16f, 0.16f, 0.08f) : knob);
            OneWay(R, tr, engine, mat, CabControls.Repair, At("Socket_Repair", H.V(-0.55f, 2.6f, -3.75f)), m ? H.V(0.82f, 0.48f, 0.38f) : knob);
            OneWay(R, tr, engine, mat, CabControls.Damper, At("Socket_Damper", H.V(0.52f, 1.8f, -2.0f)), H.V(0.3f, 0.26f, 0.1f));
            OneWay(R, tr, engine, mat, CabControls.Headlight, At("Socket_HeadlightSwitch", H.V(-1.2f, 2.7f, -1.66f)), H.V(0.16f, 0.22f, 0.1f));
            BuildFirebox(R, tr, mat);
            BuildLamps(R);

            // Outside: water tank filler and the coal bunker's rear hatch (snow).
            var hatch = HasModel ? H.V(0.5f, 0.3f, 0.5f) : H.V(0.5f, 0.1f, 0.5f);
            Outside(R, tr, mat, CabControls.WaterHatch, At("Socket_WaterHatch", H.V(-0.6f, 2.0f, -4.65f)), hatch);
            Outside(R, tr, mat, CabControls.SnowHatch, At("Socket_SnowHatch", H.V(0.6f, 2.0f, -4.65f)), HasModel ? H.V(0.7f, 0.6f, 0.15f) : hatch);

            var horn = MakeCabControl(R, "Ctl_Whistle", At("Socket_Whistle", H.V(0f, 3.6f, -1.8f)), H.V(0.06f, 0.5f, 0.06f), mat).AddComponent<SteamTrainHorn>();
            SetupCabControl(horn, tr, engine, "GAMEPLAY_Horn");
            horn.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
        }

        /// Headlamp on the smokebox and a lamp on the bunker back: a spot light down the line and a small glow
        /// in front of the lens (LocomotiveAnimator switches them).
        static void BuildLamps(Transform R)
        {
            Lamp(R, "HeadLamp_Front", At("Socket_HeadLamp", H.V(0f, 3.95f, 6.43f)), 0f);
            Lamp(R, "HeadLamp_Rear", At("Socket_RearLamp", H.V(0f, 3.1f, -5.74f)), 180f);
        }

        static void Lamp(Transform R, string name, Vector3 pos, float yaw)
        {
            var go = H.Child(R, name, pos);
            go.transform.localRotation = Quaternion.Euler(4f, yaw, 0f);      // aimed a little down the track
            var spot = go.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = 70f;
            spot.spotAngle = 48f;
            spot.color = new Color(1f, 0.9f, 0.72f);
            spot.intensity = 0f;
            spot.shadows = LightShadows.None;
            spot.enabled = false;
            var glow = H.Child(go.transform, "Glow", H.V(0f, 0f, 0.25f)).AddComponent<Light>();
            glow.type = LightType.Point;
            glow.range = 1.3f;
            glow.color = spot.color;
            glow.intensity = 0f;
            glow.shadows = LightShadows.None;
            glow.enabled = false;
            LampBeam.Create(go.transform);
        }

        /// The firelight that falls out of the fire hole into the cab (the warmth is the stove's own HeatSource).
        static void BuildFirebox(Transform R, SteamTrain tr, Material mat)
        {
            var heat = H.Child(R, "FireHeat", At("Socket_FireLight", H.V(0f, 2.0f, -1.7f)));
            var light = heat.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.55f, 0.25f);
            light.range = 4.5f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
        }

        static void TwoWay(Transform R, SteamTrain tr, SteamTrainEngine engine, Material mat, string name, Vector3 pos, Vector3 size)
        {
            var c = MakeCabControl(R, name, pos, size, mat).AddComponent<SteamTrainGearLever>();
            SetupCabControl(c, tr, engine, name);
            c.m_GearLeverUp = H.Ev(); c.m_GearLeverDown = H.Ev(); c.m_BrakesOnButPlayerTriedToShift = H.Ev();
        }

        /// Cab interactions only work from inside the train; tank intakes are the game's outside interactions.
        static void Outside(Transform R, SteamTrain tr, Material mat, string name, Vector3 pos, Vector3 size)
        {
            var c = MakeCabControl(R, name, pos, size, mat).AddComponent<SteamTrainWaterTankIntake>();
            c.m_Train = tr;
            c.m_DefaultHoverText = H.Loc(name);
            c.m_TransferName = H.Loc(name);
            c.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            c.m_TransferLiquidAudioEvent = H.Ev();
        }

        static void OneWay(Transform R, SteamTrain tr, SteamTrainEngine engine, Material mat, string name, Vector3 pos, Vector3 size)
        {
            var c = MakeCabControl(R, name, pos, size, mat).AddComponent<SteamTrainBrakeLever>();
            SetupCabControl(c, tr, engine, name);
            c.m_BrakeLeverOn = H.Ev(); c.m_BrakeLeverOff = H.Ev();
        }

        static GameObject MakeCabControl(Transform R, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = H.Child(R, name, pos);
            go.layer = LayerMask.NameToLayer("InteractiveProp");
            if (!HasModel) H.Cube(go.transform, "Visual", Vector3.zero, size, mat, false);
            go.AddComponent<BoxCollider>().size = size + H.V(0.1f, 0.1f, 0.1f);
            return go;
        }

        static void SetupCabControl(SteamTrainCabinInteraction c, SteamTrain tr, SteamTrainEngine engine, string locId)
        {
            c.m_Train = tr;
            c.m_Engine = engine;
            c.m_DefaultHoverText = H.Loc(locId);
            c.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
        }

        static Vector3 s_StoragePos;
        static Quaternion s_StorageRot = Quaternion.identity;

        /// Keeps the cupboard's container where it was built (anything that still moves it is undone).
        public static void PinStorage(Transform trainRoot)
        {
            var t = trainRoot != null ? trainRoot.Find("CabinStorage") : null;
            if (t == null) return;
            if ((t.localPosition - s_StoragePos).sqrMagnitude > 1e-4f || Quaternion.Angle(t.localRotation, s_StorageRot) > 0.1f)
                t.SetLocalPositionAndRotation(s_StoragePos, s_StorageRot);
            // the game's container code switches the trunk's colliders off every few seconds (its own meshes
            // are hidden): the cupboard's click box is kept on
            var box = t.GetComponent<BoxCollider>();
            if (box != null && !box.enabled) box.enabled = true;
        }

        /// SteamTrain.SerializeTrainCarData throws without a cabin container, so the cab gets a base-game trunk.
        /// Its contents are saved inside the train data; it starts empty and never rolls loot.
        static void BuildCabinStorage(Transform R, SteamTrain tr, string trainGuid)
        {
            var prefab = H.LoadAsset<GameObject>(TrainConfig.StoragePrefab);
            if (prefab == null) return;
            var trunk = Object.Instantiate(prefab);
            trunk.name = "CabinStorage";
            trunk.transform.SetParent(R, false);
            trunk.transform.localPosition = At("Socket_Storage", TrainConfig.StoragePos);
            trunk.transform.localRotation = HasModel ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);
            foreach (var og in trunk.GetComponentsInChildren<ObjectGuid>(true))
                og.SetRuntimeCachedPdid(trainGuid + "_" + og.gameObject.name);
            // A base-game trunk is a decoration: the game may pick it up, break it down or put it back at a saved
            // place. Here it only serves as the cupboard's container.
            foreach (var x in trunk.GetComponentsInChildren<DecorationItem>(true)) Object.Destroy(x);
            foreach (var x in trunk.GetComponentsInChildren<Il2CppTLD.Placement.Placeable>(true)) Object.Destroy(x);
            foreach (var x in trunk.GetComponentsInChildren<BreakDown>(true)) Object.Destroy(x);
            s_StoragePos = trunk.transform.localPosition;
            s_StorageRot = trunk.transform.localRotation;

            var c = trunk.GetComponentInChildren<Container>(true);
            if (c == null) return;
            if (HasModel)
            {
                // The model has a cupboard on the rear plate: the trunk only lends its Container and becomes an
                // invisible click box the size of the cupboard (LocomotiveAnimator swings the cupboard door).
                foreach (var r in trunk.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                // The game picks the container from the object its own click collider sits on, so the new box goes
                // on that object (same layer); the trunk's other solid colliders are switched off.
                GameObject clickGo = null;
                foreach (var col in trunk.GetComponentsInChildren<Collider>(true))
                {
                    if (col.isTrigger) continue;
                    if (clickGo == null) clickGo = col.gameObject;
                    col.enabled = false;
                }
                if (clickGo == null) clickGo = c.gameObject;
                var box = clickGo.AddComponent<BoxCollider>();
                var socket = R.TransformPoint(At("Socket_Storage", TrainConfig.StoragePos));
                box.center = clickGo.transform.InverseTransformPoint(socket);
                var ls = clickGo.transform.lossyScale;
                box.size = new Vector3(0.72f / Mathf.Max(ls.x, 0.01f), 0.6f / Mathf.Max(ls.y, 0.01f), 0.3f / Mathf.Max(ls.z, 0.01f));
                // Inside the train only the cab interaction layer is ray-cast; put the cupboard on the same layer as the
                // cab controls (the trunk's own layer is ignored while the player is in the cab).
                clickGo.layer = LayerMask.NameToLayer("InteractiveProp");
                SteamRailwayMod.Debug($"Cupboard click box on {clickGo.name} (layer {clickGo.layer}).");
                c.m_LocalizedDisplayName = H.Loc("GAMEPLAY_Cupboard");
            }
            c.m_NotPopulated = false;
            c.m_RolledSpawnChance = true;
            c.m_ChanceEmpty = 100f;
            c.m_MinRandomItems = 0;
            c.m_MaxRandomItems = 0;
            c.m_LootTablePrefab = null;
            c.m_LootTableData = null;
            c.m_LootTable = null;
            if (c.m_GearToInstantiate != null) c.m_GearToInstantiate.Clear();
            tr.m_Container = c;
        }

        static void SetupComponent(SteamTrainComponent c, SteamTrain tr, string locId)
        {
            c.m_Train = tr;
            c.m_DefaultHoverText = H.Loc(locId);
            c.m_Description = H.Loc(locId + "Description");
            c.m_RepairConfigurations = new Il2CppSystem.Collections.Generic.List<SteamTrainRepair>();
        }
    }
}
