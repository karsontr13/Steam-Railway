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
            var rootTransform = root.transform;

            var model = LocomotiveModel.Spawn(rootTransform);
            s_Root = rootTransform;
            s_Model = model != null ? model.transform : null;
            if (s_Model == null) BuildPlaceholderBody(rootTransform, bodyMat);

            var splineController = root.AddComponent<SplineController>();
            splineController.Spline = spline;
            splineController.PositionMode = CurvyPositionMode.WorldUnits;
            splineController.Position = Mathf.Clamp(splinePos, 0f, spline.Length);
            splineController.OrientationMode = OrientationModeEnum.Tangent;
            splineController.OrientationAxis = OrientationAxisEnum.Forward; // without this the train stands on its nose
            // the engine always faces the line's +direction; running in reverse must not turn it round
            splineController.IgnoreDirection = true;
            splineController.Clamping = CurvyClamping.Clamp;
            splineController.Speed = 0f;

            var train = root.AddComponent<SteamTrain>();
            train.m_SplineController = splineController;
            train.m_AssetReference = new AssetReferenceGameObject(TrainConfig.AssetKey);
            train.m_TrainBoardedState = H.St();
            train.m_TrainUnboardedState = H.St();
            train.m_SteamWhistleToot = H.Ev();

            BuildCabin(rootTransform, train);
            BuildColliders(rootTransform, train);
            var comps = H.Child(rootTransform, "Components", H.V(0, 2.5f, -1f)).transform;
            var engine = BuildEngine(comps, train);
            BuildBoiler(comps, rootTransform, train, guid);
            BuildWearComponents(comps, train);
            BuildTanks(comps, train);
            BuildCockpit(rootTransform, train);
            BuildEffects(rootTransform, train);
            BuildDoor(rootTransform, train, engine, bodyMat);
            BuildCabinStorage(rootTransform, train, guid);
            BuildControls(rootTransform, train, engine, bodyMat);
            train.m_FleeTriggerVolume = H.Child(rootTransform, "Flee", Vector3.zero).AddComponent<AiFleeTriggerVolume>();

            ObjectGuid.MaybeAttachObjectGuidAddOrReplace(root, guid);
            root.SetActive(true);

            train.m_WaterSource.Set(H.Vol(TrainConfig.WaterCapacityUnits));
            train.m_FuelSource.Set(H.Vol(TrainConfig.FuelTokenUnits));
            ConfigureEffects(train);
            return train;
        }

        static Transform s_Root, s_Model;

        /// Position of a model socket in train space (see Docs/LOKOMOTIF.md), or the placeholder layout without a model.
        static Vector3 At(string socket, Vector3 fallback)
        {
            var transform = LocomotiveModel.FindDeep(s_Model, socket);
            return transform != null ? s_Root.InverseTransformPoint(transform.position) : fallback;
        }

        static bool HasModel => s_Model != null;

        static void BuildPlaceholderBody(Transform rootTransform, Material material)
        {
            H.Cube(rootTransform, "Body", H.V(0, 1.2f, 0), H.V(2.6f, 0.3f, 9f), material, true);
            H.Cube(rootTransform, "CabRoof", H.V(0, 4.2f, -2.5f), H.V(2.6f, 0.15f, 3.5f), material, false);
            H.Cube(rootTransform, "BoilerShell", H.V(0, 2.3f, 2.2f), H.V(1.6f, 1.6f, 4.6f), material, false);
            H.Cube(rootTransform, "Chimney", H.V(0, 3.6f, 3.8f), H.V(0.5f, 1.2f, 0.5f), material, false);
        }

        static void BuildCabin(Transform rootTransform, SteamTrain train)
        {
            // The player is not parented to the train: each frame the game copies the player's position to
            // CameraDummy, which is the player's FEET, walking inside CabinLimits.
            var floor = At("Socket_CabFloor", TrainConfig.CabinFloor);
            var dummy = H.Child(rootTransform, "CameraDummy", floor);
            var sway = dummy.AddComponent<SimpleSway>();
            var offset = new MinMax(); offset.m_Min = 0f; offset.m_Max = 1f;
            sway.m_RandomTimeOffset = offset;
            sway.m_SwayPeriod = 4f; sway.m_SwayHalfAngle = 0.5f; sway.m_SwayMultiplier = 1f; sway.m_Direction = H.V(0, 0, 1);

            var cabin = H.Child(rootTransform, "CabinLimits", floor).AddComponent<BoxCollider>();
            cabin.size = TrainConfig.CabinSize;
            cabin.isTrigger = true;

            train.m_CameraDummy = dummy.transform;
            train.m_CameraSimpleSway = sway;
            train.m_CabinLimits = cabin;
            train.m_MovementSpeed = 2f;

            var curve = new AnimationCurve();
            curve.AddKey(0f, 0f); curve.AddKey(0.5f, 0.02f); curve.AddKey(1f, 0f);
            train.m_VerticalCameraMotionPattern = curve;
            train.m_CameraMotionPatternLengthSeconds = 5f;
            train.m_CameraMotionDamper = 0.125f;
            train.m_LurchDamper = 0.2f;
            train.m_FadeInSeconds = 0.5f;
            train.m_FadeOutSeconds = 0.5f;
            train.m_FootstepBaseDistanceMultiplier = 0.65f;
            train.m_PlayerEnterTrainEventName = "event_EnterTrain";
            train.m_PlayerExitTrainEventName = "event_ExitTrain";
        }

        static void BuildColliders(Transform rootTransform, SteamTrain train)
        {
            train.m_FrontCollider = MakeTrainCollider(rootTransform, "FrontCollider", At("Socket_Front", H.V(0, 1.5f, 7.4f)), train);
            train.m_RearCollider = MakeTrainCollider(rootTransform, "RearCollider", At("Socket_Rear", H.V(0, 1.5f, -4.8f)), train);
            train.m_LowRamSpeedThreshold = 7f;
            train.m_HighRamSpeedThreshold = 12f;
        }

        static TrainCarCollider MakeTrainCollider(Transform rootTransform, string name, Vector3 position, SteamTrain train)
        {
            var targetObject = H.Child(rootTransform, name, position);
            targetObject.AddComponent<Rigidbody>().isKinematic = true;
            var box = targetObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = H.V(2.4f, 2f, 0.6f);
            var carCollider = targetObject.AddComponent<TrainCarCollider>();
            carCollider.m_ParentTrainCar = train;
            carCollider.m_BoxCollider = box;
            return carCollider;
        }

        static SteamTrainEngine BuildEngine(Transform comps, SteamTrain train)
        {
            var engine = H.Child(comps, "Engine", Vector3.zero).AddComponent<SteamTrainEngine>();
            engine.m_SteamTrain = train;
            engine.m_SteamPSI = H.Rtpc(); engine.m_TrainVelocity = H.Rtpc(); engine.m_FireboxExplosionEvent = H.Ev();
            engine.m_EngineStateNormal = H.St(); engine.m_EngineStateOverfire = H.St();
            engine.m_EngineStateExplosionRiskLow = H.St(); engine.m_EngineStateExplosionRiskHigh = H.St(); engine.m_AudioLastEngineState = H.St();
            engine.m_OverfireStartEvent = new UnityEvent(); engine.m_OverfireEndEvent = new UnityEvent();

            engine.m_MinSteamPressureForLowSpeedMovement = 25; engine.m_MinSteamPressureForHighSpeedMovement = 26;
            engine.m_MaxSteamPressure = 300; engine.m_OverfireMaxSteamPressure = 400; engine.m_OverfireBonusMaxSpeedPercent = 15;
            engine.m_MaxVelocityReverse = -8; engine.m_MaxVelocityForwardLow = 8; engine.m_MaxVelocityForwardHigh = 15;
            engine.m_MinBrakeVelocityDecreasePerSecond = 1.25f; engine.m_MaxBrakeVelocityDecreasePerSecond = 3;
            engine.m_VelocityDecreasePerSecondNeutral = 0.5f; engine.m_VelocityDecreaseInsufficientSteamPSI = 0.8f; engine.m_VelocityDecreasePerSecondDownshift = 0.5f;
            engine.m_VelocityIncreasePerSecondReverse = -1; engine.m_VelocityIncreasePerSecondForwardLow = 1; engine.m_VelocityIncreasePerSecondForwardHigh = 1.75f;
            engine.m_MaxReverseSpeedToAllowShiftGearUp = -2; engine.m_MaxForwardSpeedToAllowShiftGearDown = 2;
            engine.m_SteamPSIBuiltPerSecond = 7;
            engine.m_SteamPressurePSIConsumedPerSecondReverse = 5; engine.m_SteamPressurePSIConsumedPerSecondForwardLow = 5;
            engine.m_SteamPressurePSIConsumedPerSecondForwardHigh = 14; engine.m_SteamPressurePSIConsumedPerBrakeSecond = 0.5f;
            train.m_Engine = engine;
            return engine;
        }

        static void BuildBoiler(Transform comps, Transform rootTransform, SteamTrain train, string guid)
        {
            var boiler = H.Child(comps, "Boiler", H.V(0, 0, 1)).AddComponent<SteamTrainBoiler>();
            SetupComponent(boiler, train, "GAMEPLAY_Boiler");
            boiler.m_MaxTemperature = 350; boiler.m_TemperatureChangePerMinute = 600; boiler.m_AmbientHeatTemperature = 20;
            boiler.m_AmbientHeatHourStoredPerHourOn = 0.2f; boiler.m_MaxAmbientHeatHoursStored = 3; boiler.m_ExplosionCheckIntervalSeconds = 10;
            boiler.m_LowRiskMaxCondition = 49; boiler.m_LowRiskExplosionChance = 50; boiler.m_HighRiskMaxCondition = 24; boiler.m_HighRiskExplosionChance = 75;
            boiler.m_OverfireDamagePerHour = 100;
            boiler.m_WaterBoiledPerMinute = H.Vol(TrainConfig.WaterBoiledPerMinute);
            boiler.m_FuelBurnedPerMinute = H.Vol(0L);
            boiler.m_ExplosionEvent = new UnityEvent();
            boiler.m_BoilerTemperature = H.Rtpc(); boiler.m_BoilerStateOn = H.St(); boiler.m_BoilerStateOff = H.St();
            boiler.m_FurnaceLights = H.Arr(H.Child(boiler.transform, "FurnaceLight", Vector3.zero));
            boiler.m_FurnaceParticlesParent = H.Child(boiler.transform, "FurnaceParticles", Vector3.zero);
            boiler.m_InvisibleFire = AddFireboxStove(rootTransform, guid);
            train.m_Boiler = boiler;
        }

        /// The firebox is the base game's pot belly stove, hidden behind the fire hole: clicking the fire doors opens
        /// the game's own fire menus (start fire / add fuel) and the stove's Fire is what heats the boiler.
        /// Only its invisible click box at the doors, its warmth and its fire sound remain.
        static Fire AddFireboxStove(Transform rootTransform, string trainGuid)
        {
            var prefab = H.LoadAsset<GameObject>(TrainConfig.StovePrefab);
            if (prefab == null) return null;
            var stove = Object.Instantiate(prefab);
            stove.SetActive(false);
            stove.name = "Firebox";
            stove.transform.SetParent(rootTransform, false);
            stove.transform.localPosition = At("Socket_Firebox", TrainConfig.FireboxPos);
            stove.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            foreach (var objectGuid in stove.GetComponentsInChildren<ObjectGuid>(true))
                objectGuid.SetRuntimeCachedPdid(trainGuid + "_" + objectGuid.gameObject.name);

            // Hide the stove's meshes and its own lights (our firelight comes out of the fire hole). Its fire effect
            // (flames, embers, sparks) stays and is moved onto the grate inside the fire hole.
            foreach (var renderer in stove.GetComponentsInChildren<Renderer>(true))
                if (renderer.TryCast<ParticleSystemRenderer>() == null) renderer.enabled = false;
            var fireT = stove.GetComponentInChildren<Fire>(true)?.transform;
            if (fireT != null)
            {
                fireT.position = rootTransform.TransformPoint(At("Socket_FireFX", H.V(0f, 1.86f, -1.79f)));
                fireT.rotation = rootTransform.rotation;
                fireT.localScale = Vector3.one * TrainConfig.FireFxScale;
            }
            foreach (var particleSystem in stove.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particleSystem.main;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;   // otherwise flames trail behind the moving train
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;       // follow the fire's scale to fit the fire hole
            }
            foreach (var light in stove.GetComponentsInChildren<Light>(true)) light.gameObject.SetActive(false);
            var lodGroup = stove.GetComponent<LODGroup>();
            if (lodGroup != null) lodGroup.enabled = false;
            // No cooking or placing items on the firebox.
            foreach (var name in new[] { "Cylinder", "GearPlacePoint", "PlacePoints", "GearExclusionCollider" })
            {
                var hiddenPart = LocomotiveModel.FindDeep(stove.transform, name);
                if (hiddenPart != null) hiddenPart.gameObject.SetActive(false);
            }
            foreach (var collider in stove.GetComponentsInChildren<Collider>(true))
                if (!collider.isTrigger) collider.enabled = false;
            var woodStove = stove.GetComponent<WoodStove>();
            if (woodStove != null) woodStove.m_CookingSlots = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CookingSlot>(0);
            // The click box covers the fire doors.
            var box = stove.AddComponent<BoxCollider>();
            box.size = HasModel ? H.V(0.62f, 0.46f, 0.14f) : H.V(0.5f, 0.4f, 0.2f);

            var fire = stove.GetComponentInChildren<Fire>(true);
            if (fire != null) fire.m_SkipSerialization = true;   // saved with the train (TrainState.FireData)
            stove.SetActive(true);
            return fire;
        }

        static void BuildWearComponents(Transform comps, SteamTrain train)
        {
            var wheels = H.Child(comps, "Wheels", H.V(0, -1.5f, 0)).AddComponent<SteamTrainWheels>();
            SetupComponent(wheels, train, "GAMEPLAY_Wheels");
            var tiers = new Il2CppSystem.Collections.Generic.List<SteamTrainWheels.PerformanceTier>();
            int[] threshold = { 75, 50, 25, 0 };
            float[] speed = { 1f, 0.9f, 0.75f, 0.5f };
            float[] consumption = { 1f, 1.1f, 1.25f, 1.5f };
            for (int index = 0; index < threshold.Length; index++)
            {
                var tier = new SteamTrainWheels.PerformanceTier();
                tier.m_ConditionThreshold = threshold[index]; tier.m_MaxVelocityIncreaseModifier = speed[index]; tier.m_OilConsumedModifier = consumption[index];
                tiers.Add(tier);
            }
            wheels.m_PerformanceTiers = tiers;
            train.m_Wheels = wheels;

            var brake = H.Child(comps, "BrakeShoe", H.V(0.8f, -1.5f, 0)).AddComponent<SteamTrainBrakeShoe>();
            SetupComponent(brake, train, "GAMEPLAY_BrakeShoes");
            train.m_BrakeShoe = brake;

            var cow = H.Child(comps, "CowCatcher", H.V(0, -1.5f, 7f)).AddComponent<SteamTrainCowCatcher>();
            SetupComponent(cow, train, "GAMEPLAY_CowCatcher");
            train.m_CowCatcher = cow;

            var cook = H.Child(comps, "CookingSurface", H.V(-0.6f, 0, -2f)).AddComponent<SteamTrainCookingSurface>();
            SetupComponent(cook, train, "GAMEPLAY_CookingSurface");
            cook.m_GearPlacePoints = new Il2CppSystem.Collections.Generic.List<GearPlacePoint>();
            cook.m_PendingDeserialization = new Il2CppSystem.Collections.Generic.List<SteamTrainCookingSurface.CookingSurfaceState>();
            train.m_CookingSurface = cook;
        }

        static void BuildTanks(Transform comps, SteamTrain train)
        {
            var fuel = H.Child(comps, "FuelSource", Vector3.zero).AddComponent<GenericLiquidContainer>();
            fuel.m_VolumeRTPC = H.Rtpc();
            fuel.m_MaxCapacity = H.Vol(TrainConfig.FuelTokenUnits * 2);
            train.m_FuelSource = fuel;

            var water = H.Child(comps, "WaterSource", Vector3.zero).AddComponent<GenericLiquidContainer>();
            water.m_VolumeRTPC = H.Rtpc();
            water.m_MaxCapacity = H.Vol(TrainConfig.WaterCapacityUnits);
            train.m_WaterSource = water;
        }

        static void BuildCockpit(Transform rootTransform, SteamTrain train)
        {
            var cockpit = H.Child(rootTransform, "Cockpit", H.V(0, 2.5f, -1.2f)).AddComponent<SteamTrainCockpit>();
            var cockpitTransform = cockpit.transform;
            cockpit.m_SteamGaugePSINeedlePivot = H.Child(cockpitTransform, "PsiNeedle", Vector3.zero).transform;
            cockpit.m_SpeedometerNeedlePivot = H.Child(cockpitTransform, "SpeedNeedle", Vector3.zero).transform;
            cockpit.m_GearLeverPivot = H.Child(cockpitTransform, "GearLever", Vector3.zero).transform;
            cockpit.m_GearGaugePivot = H.Child(cockpitTransform, "GearGauge", Vector3.zero).transform;
            cockpit.m_BrakeLeverPivot = H.Child(cockpitTransform, "BrakeLever", Vector3.zero).transform;
            cockpit.m_BrakeGaugePivot = H.Child(cockpitTransform, "BrakeGauge", Vector3.zero).transform;
            cockpit.m_WaterTankGauge = H.Child(cockpitTransform, "WaterGauge", Vector3.zero).transform;
            cockpit.m_OilTankGauge = H.Child(cockpitTransform, "FuelGauge", Vector3.zero).transform;
            cockpit.m_LightRoot = H.Child(cockpitTransform, "Lights", Vector3.zero);
            cockpit.m_MaxSteamPSINeedleAngle = 120; cockpit.m_MaxSpeedometerNeedAngle = 95;
            cockpit.m_GearLeverMoveSpeed = 50; cockpit.m_GearGaugeMoveRatio = 6;
            cockpit.m_GearLeverAngleNeutral = -10; cockpit.m_GearLeverAngleLow = -20; cockpit.m_GearLeverAngleHigh = -30;
            cockpit.m_BrakeLeverMoveSpeed = 300; cockpit.m_BrakeGaugeMoveRatio = 2; cockpit.m_BrakeGaugeAngleOffset = 180; cockpit.m_BrakeLeverAngleOn = -90;
            cockpit.m_FullTankOffset = 0.1f;
            train.m_Cockpit = cockpit;
        }

        static void BuildEffects(Transform rootTransform, SteamTrain train)
        {
            var whistle = H.Child(rootTransform, "Whistle", At("Socket_SafetyValve", H.V(0, 4.5f, 0)));
            train.m_SteamWhistleParent = whistle;
            train.m_SteamWhistleLightParticleSystem = H.Child(whistle.transform, "SteamLight", Vector3.zero).AddComponent<ParticleSystem>();
            train.m_SteamWhistleDenseParticleSystem = H.Child(whistle.transform, "SteamDense", Vector3.zero).AddComponent<ParticleSystem>();
            train.m_SteamWhistleTootLengthSeconds = 1.6f;

            var smoke = H.Child(rootTransform, "ChimneySmoke", At("Socket_Chimney", TrainConfig.ChimneyTop)).AddComponent<ParticleSystem>();
            train.m_SmokeStackParticleSystems = H.Arr(smoke);
            train.m_MinEmissionOverTimeRate = 40;
            train.m_MaxEmissionOverTimeRate = 100;
        }

        static void ConfigureEffects(SteamTrain train)
        {
            var smokeMat = H.LoadAsset<Material>(TrainConfig.SmokeMaterial);
            var steamMat = H.LoadAsset<Material>(TrainConfig.SteamMaterial);

            var smoke = train.m_SmokeStackParticleSystems[0];
            smoke.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var mainModule = smoke.main;
            mainModule.simulationSpace = ParticleSystemSimulationSpace.World; // smoke should trail behind the train
            mainModule.startLifetime = new ParticleSystem.MinMaxCurve(6f);
            mainModule.startSpeed = new ParticleSystem.MinMaxCurve(2.2f);
            mainModule.startSize = new ParticleSystem.MinMaxCurve(2.2f);
            mainModule.startColor = new ParticleSystem.MinMaxGradient(new Color(0.25f, 0.24f, 0.23f, 0.6f));
            mainModule.maxParticles = 400;
            var shape = smoke.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 12f; shape.radius = 0.25f;
            smoke.GetComponent<ParticleSystemRenderer>().sharedMaterial = smokeMat;
            smoke.Play();

            ConfigureSteam(train.m_SteamWhistleLightParticleSystem, steamMat);
            ConfigureSteam(train.m_SteamWhistleDenseParticleSystem, steamMat);
        }

        static void ConfigureSteam(ParticleSystem particleSystem, Material material)
        {
            var mainModule = particleSystem.main;
            mainModule.simulationSpace = ParticleSystemSimulationSpace.World;
            mainModule.startLifetime = new ParticleSystem.MinMaxCurve(1.5f);
            mainModule.startSpeed = new ParticleSystem.MinMaxCurve(4f);
            mainModule.startSize = new ParticleSystem.MinMaxCurve(0.6f);
            mainModule.playOnAwake = false;
            var shape = particleSystem.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 8f; shape.radius = 0.05f;
            particleSystem.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            particleSystem.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            particleSystem.Stop();
        }

        static void BuildDoor(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material)
        {
            int interactiveLayer = LayerMask.NameToLayer("InteractiveProp");
            var door = H.Child(rootTransform, "Door", At("Socket_Door_R", TrainConfig.DoorPos));
            door.layer = interactiveLayer;
            // The model's cab has an open side entrance; the door is an invisible collider filling it.
            if (!HasModel) H.Cube(door.transform, "DoorPanel", Vector3.zero, H.V(0.08f, 1.9f, 0.9f), material, false);
            door.AddComponent<BoxCollider>().size = HasModel ? H.V(0.2f, 1.8f, 0.85f) : H.V(0.15f, 1.9f, 0.9f);

            var exit = H.Child(rootTransform, "ExitPoint", At("Socket_Exit_R", TrainConfig.ExitPos));
            exit.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

            // The Lock must exist before the door's Awake looks for it.
            var lockComponent = door.AddComponent<Lock>();
            lockComponent.m_ChanceLocked = 0f;
            lockComponent.m_LockState = LockState.Unlocked;
            lockComponent.m_LockStateRolled = true;
            lockComponent.m_LockedKeypadText = H.Loc("GAMEPLAY_Locked");

            var d = door.AddComponent<SteamTrainDoor>();
            d.m_ParentSteamTrain = train;
            d.m_ExitPoint = exit.transform;
            d.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
            d.m_SteamEngineEnter = H.Ev();
            d.m_SteamEngineExit = H.Ev();
            d.m_DefaultHoverText = H.Loc("GAMEPLAY_Door");
            d.m_DefaultHoldText = H.Loc("GAMEPLAY_Door");
            d.m_CanInteract = true;
            d.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            foreach (var loadScene in Object.FindObjectsOfType<LoadScene>())
                if (loadScene.m_HUD != null) { d.m_HUD = loadScene.m_HUD; break; }

            // SteamTrainDoor.UpdateInteraction throws every frame without a door window.
            var windowGo = H.Child(door.transform, "DoorWindow", H.V(0, 0.5f, 0));
            var look = H.Child(windowGo.transform, "LookTarget", H.V(0.6f, 0, 0));
            var window = windowGo.AddComponent<SteamTrainWindow>();
            window.m_Train = train; window.m_Engine = engine; window.m_Door = d; window.m_WindowLookTarget = look.transform;
            window.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
            window.m_LeanOutEvent = H.Ev(); window.m_LeanInEvent = H.Ev();
            window.m_DefaultHoverText = H.Loc("GAMEPLAY_Window");
            window.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            d.m_DoorWindow = window;

            ObjectGuid.MaybeAttachObjectGuidAddOrReplace(door, System.Guid.NewGuid().ToString());
        }

        /// Cab levers and gauges (see CabControls). Two-way controls use SteamTrainGearLever (click / alternative click),
        /// one-way controls and gauges use SteamTrainBrakeLever. Positions are on the cab's front wall.
        static void BuildControls(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material)
        {
            var lever = H.V(0.06f, 0.5f, 0.06f);
            var knob = H.V(0.12f, 0.12f, 0.08f);
            var gauge = H.V(0.18f, 0.18f, 0.04f);
            bool matched = HasModel;

            // Colliders are invisible boxes sized to each modelled fitting so neighbours never overlap
            // (overlapping boxes made the hover texts of the regulator and the steam gauge swap).
            TwoWay(rootTransform, train, engine, material, CabControls.Regulator, At("Socket_Regulator", H.V(0.5f, 2.6f, -1.45f)), matched ? H.V(0.42f, 0.3f, 0.1f) : lever);
            TwoWay(rootTransform, train, engine, material, CabControls.Reverser, At("Socket_Reverser", H.V(0.65f, 2.0f, -1.5f)), matched ? H.V(0.14f, 0.5f, 0.2f) : lever);
            OneWay(rootTransform, train, engine, material, CabControls.Brake, At("Socket_Brake", H.V(-0.55f, 2.2f, -1.5f)), matched ? H.V(0.16f, 0.14f, 0.42f) : lever);
            OneWay(rootTransform, train, engine, material, CabControls.HandBrake, At("Socket_HandBrake", H.V(-0.9f, 2.2f, -3.6f)), matched ? H.V(0.36f, 0.14f, 0.36f) : knob);
            OneWay(rootTransform, train, engine, material, CabControls.Injector, At("Socket_Injector", H.V(-0.65f, 2.75f, -1.45f)), matched ? H.V(0.16f, 0.16f, 0.08f) : knob);
            OneWay(rootTransform, train, engine, material, CabControls.Blower, At("Socket_Blower", H.V(0.3f, 3.0f, -1.45f)), matched ? H.V(0.16f, 0.16f, 0.08f) : knob);
            OneWay(rootTransform, train, engine, material, CabControls.PressureGauge, At("Socket_PressureGauge", H.V(-0.2f, 3.0f, -1.42f)), matched ? H.V(0.36f, 0.36f, 0.06f) : gauge);
            OneWay(rootTransform, train, engine, material, CabControls.SpeedGauge, At("Socket_SpeedGauge", H.V(0.2f, 3.0f, -1.42f)), matched ? H.V(0.25f, 0.25f, 0.06f) : gauge);
            OneWay(rootTransform, train, engine, material, CabControls.HeatGauge, At("Socket_HeatGauge", H.V(0f, 2.75f, -1.42f)), matched ? H.V(0.18f, 0.18f, 0.06f) : gauge);
            OneWay(rootTransform, train, engine, material, CabControls.WaterGlass, At("Socket_WaterGlass", H.V(-0.35f, 2.55f, -1.42f)), H.V(0.1f, 0.55f, 0.08f));
            OneWay(rootTransform, train, engine, material, CabControls.TenderGauge, At("Socket_TenderGauge", H.V(0f, 2.6f, -3.75f)), matched ? H.V(0.16f, 0.48f, 0.06f) : gauge);
            OneWay(rootTransform, train, engine, material, CabControls.Pump, At("Socket_Pump", H.V(0.55f, 2.6f, -3.75f)), matched ? H.V(0.16f, 0.16f, 0.08f) : knob);
            OneWay(rootTransform, train, engine, material, CabControls.Repair, At("Socket_Repair", H.V(-0.55f, 2.6f, -3.75f)), matched ? H.V(0.82f, 0.48f, 0.38f) : knob);
            OneWay(rootTransform, train, engine, material, CabControls.Damper, At("Socket_Damper", H.V(0.52f, 1.8f, -2.0f)), H.V(0.3f, 0.26f, 0.1f));
            OneWay(rootTransform, train, engine, material, CabControls.Headlight, At("Socket_HeadlightSwitch", H.V(-1.2f, 2.7f, -1.66f)), H.V(0.16f, 0.22f, 0.1f));
            BuildFirebox(rootTransform, train, material);
            BuildLamps(rootTransform);

            // Outside: water tank filler and the coal bunker's rear hatch (snow).
            var hatch = HasModel ? H.V(0.5f, 0.3f, 0.5f) : H.V(0.5f, 0.1f, 0.5f);
            Outside(rootTransform, train, material, CabControls.WaterHatch, At("Socket_WaterHatch", H.V(-0.6f, 2.0f, -4.65f)), hatch);
            Outside(rootTransform, train, material, CabControls.SnowHatch, At("Socket_SnowHatch", H.V(0.6f, 2.0f, -4.65f)), HasModel ? H.V(0.7f, 0.6f, 0.15f) : hatch);

            var horn = MakeCabControl(rootTransform, "Ctl_Whistle", At("Socket_Whistle", H.V(0f, 3.6f, -1.8f)), H.V(0.06f, 0.5f, 0.06f), material).AddComponent<SteamTrainHorn>();
            SetupCabControl(horn, train, engine, "GAMEPLAY_Horn");
            horn.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
        }

        /// Headlamp on the smokebox and a lamp on the bunker back: a spot light down the line and a small glow
        /// in front of the lens (LocomotiveAnimator switches them).
        static void BuildLamps(Transform rootTransform)
        {
            Lamp(rootTransform, "HeadLamp_Front", At("Socket_HeadLamp", H.V(0f, 3.95f, 6.43f)), 0f);
            Lamp(rootTransform, "HeadLamp_Rear", At("Socket_RearLamp", H.V(0f, 3.1f, -5.74f)), 180f);
        }

        static void Lamp(Transform rootTransform, string name, Vector3 position, float yaw)
        {
            var targetObject = H.Child(rootTransform, name, position);
            targetObject.transform.localRotation = Quaternion.Euler(4f, yaw, 0f);      // aimed a little down the track
            var spot = targetObject.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = 70f;
            spot.spotAngle = 48f;
            spot.color = new Color(1f, 0.9f, 0.72f);
            spot.intensity = 0f;
            spot.shadows = LightShadows.None;
            spot.enabled = false;
            var glow = H.Child(targetObject.transform, "Glow", H.V(0f, 0f, 0.25f)).AddComponent<Light>();
            glow.type = LightType.Point;
            glow.range = 1.3f;
            glow.color = spot.color;
            glow.intensity = 0f;
            glow.shadows = LightShadows.None;
            glow.enabled = false;
            LampBeam.Create(targetObject.transform);
        }

        /// The firelight that falls out of the fire hole into the cab (the warmth is the stove's own HeatSource).
        static void BuildFirebox(Transform rootTransform, SteamTrain train, Material material)
        {
            var heat = H.Child(rootTransform, "FireHeat", At("Socket_FireLight", H.V(0f, 2.0f, -1.7f)));
            var light = heat.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.55f, 0.25f);
            light.range = 4.5f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
        }

        static void TwoWay(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material, string name, Vector3 position, Vector3 size)
        {
            var gearLever = MakeCabControl(rootTransform, name, position, size, material).AddComponent<SteamTrainGearLever>();
            SetupCabControl(gearLever, train, engine, name);
            gearLever.m_GearLeverUp = H.Ev(); gearLever.m_GearLeverDown = H.Ev(); gearLever.m_BrakesOnButPlayerTriedToShift = H.Ev();
        }

        /// Cab interactions only work from inside the train; tank intakes are the game's outside interactions.
        static void Outside(Transform rootTransform, SteamTrain train, Material material, string name, Vector3 position, Vector3 size)
        {
            var waterIntake = MakeCabControl(rootTransform, name, position, size, material).AddComponent<SteamTrainWaterTankIntake>();
            waterIntake.m_Train = train;
            waterIntake.m_DefaultHoverText = H.Loc(name);
            waterIntake.m_TransferName = H.Loc(name);
            waterIntake.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            waterIntake.m_TransferLiquidAudioEvent = H.Ev();
        }

        static void OneWay(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material, string name, Vector3 position, Vector3 size)
        {
            var brakeLever = MakeCabControl(rootTransform, name, position, size, material).AddComponent<SteamTrainBrakeLever>();
            SetupCabControl(brakeLever, train, engine, name);
            brakeLever.m_BrakeLeverOn = H.Ev(); brakeLever.m_BrakeLeverOff = H.Ev();
        }

        static GameObject MakeCabControl(Transform rootTransform, string name, Vector3 position, Vector3 size, Material material)
        {
            var targetObject = H.Child(rootTransform, name, position);
            targetObject.layer = LayerMask.NameToLayer("InteractiveProp");
            if (!HasModel) H.Cube(targetObject.transform, "Visual", Vector3.zero, size, material, false);
            targetObject.AddComponent<BoxCollider>().size = size + H.V(0.1f, 0.1f, 0.1f);
            return targetObject;
        }

        static void SetupCabControl(SteamTrainCabinInteraction interaction, SteamTrain train, SteamTrainEngine engine, string locId)
        {
            interaction.m_Train = train;
            interaction.m_Engine = engine;
            interaction.m_DefaultHoverText = H.Loc(locId);
            interaction.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
        }

        static Vector3 s_StoragePos;
        static Quaternion s_StorageRot = Quaternion.identity;

        /// Keeps the cupboard's container where it was built (anything that still moves it is undone).
        public static void PinStorage(Transform trainRoot)
        {
            var transform = trainRoot != null ? trainRoot.Find("CabinStorage") : null;
            if (transform == null) return;
            if ((transform.localPosition - s_StoragePos).sqrMagnitude > 1e-4f || Quaternion.Angle(transform.localRotation, s_StorageRot) > 0.1f)
                transform.SetLocalPositionAndRotation(s_StoragePos, s_StorageRot);
            // the game's container code switches the trunk's colliders off every few seconds (its own meshes
            // are hidden): the cupboard's click box is kept on
            var box = transform.GetComponent<BoxCollider>();
            if (box != null && !box.enabled) box.enabled = true;
        }

        /// SteamTrain.SerializeTrainCarData throws without a cabin container, so the cab gets a base-game trunk.
        /// Its contents are saved inside the train data; it starts empty and never rolls loot.
        static void BuildCabinStorage(Transform rootTransform, SteamTrain train, string trainGuid)
        {
            var prefab = H.LoadAsset<GameObject>(TrainConfig.StoragePrefab);
            if (prefab == null) return;
            var trunk = Object.Instantiate(prefab);
            trunk.name = "CabinStorage";
            trunk.transform.SetParent(rootTransform, false);
            trunk.transform.localPosition = At("Socket_Storage", TrainConfig.StoragePos);
            trunk.transform.localRotation = HasModel ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);
            foreach (var objectGuid in trunk.GetComponentsInChildren<ObjectGuid>(true))
                objectGuid.SetRuntimeCachedPdid(trainGuid + "_" + objectGuid.gameObject.name);
            // A base-game trunk is a decoration: the game may pick it up, break it down or put it back at a saved
            // place. Here it only serves as the cupboard's container.
            foreach (var decoration in trunk.GetComponentsInChildren<DecorationItem>(true)) Object.Destroy(decoration);
            foreach (var placeable in trunk.GetComponentsInChildren<Il2CppTLD.Placement.Placeable>(true)) Object.Destroy(placeable);
            foreach (var breakDown in trunk.GetComponentsInChildren<BreakDown>(true)) Object.Destroy(breakDown);
            s_StoragePos = trunk.transform.localPosition;
            s_StorageRot = trunk.transform.localRotation;

            var container = trunk.GetComponentInChildren<Container>(true);
            if (container == null) return;
            if (HasModel)
            {
                // The model has a cupboard on the rear plate: the trunk only lends its Container and becomes an
                // invisible click box the size of the cupboard (LocomotiveAnimator swings the cupboard door).
                foreach (var renderer in trunk.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                // The game picks the container from the object its own click collider sits on, so the new box goes
                // on that object (same layer); the trunk's other solid colliders are switched off.
                GameObject clickGo = null;
                foreach (var collider in trunk.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.isTrigger) continue;
                    if (clickGo == null) clickGo = collider.gameObject;
                    collider.enabled = false;
                }
                if (clickGo == null) clickGo = container.gameObject;
                var box = clickGo.AddComponent<BoxCollider>();
                var socket = rootTransform.TransformPoint(At("Socket_Storage", TrainConfig.StoragePos));
                box.center = clickGo.transform.InverseTransformPoint(socket);
                var ls = clickGo.transform.lossyScale;
                box.size = new Vector3(0.72f / Mathf.Max(ls.x, 0.01f), 0.6f / Mathf.Max(ls.y, 0.01f), 0.3f / Mathf.Max(ls.z, 0.01f));
                // Inside the train only the cab interaction layer is ray-cast; put the cupboard on the same layer as the
                // cab controls (the trunk's own layer is ignored while the player is in the cab).
                clickGo.layer = LayerMask.NameToLayer("InteractiveProp");
                SteamRailwayMod.Debug($"Cupboard click box on {clickGo.name} (layer {clickGo.layer}).");
                container.m_LocalizedDisplayName = H.Loc("GAMEPLAY_Cupboard");
            }
            container.m_NotPopulated = false;
            container.m_RolledSpawnChance = true;
            container.m_ChanceEmpty = 100f;
            container.m_MinRandomItems = 0;
            container.m_MaxRandomItems = 0;
            container.m_LootTablePrefab = null;
            container.m_LootTableData = null;
            container.m_LootTable = null;
            if (container.m_GearToInstantiate != null) container.m_GearToInstantiate.Clear();
            train.m_Container = container;
        }

        static void SetupComponent(SteamTrainComponent component, SteamTrain train, string locId)
        {
            component.m_Train = train;
            component.m_DefaultHoverText = H.Loc(locId);
            component.m_Description = H.Loc(locId + "Description");
            component.m_RepairConfigurations = new Il2CppSystem.Collections.Generic.List<SteamTrainRepair>();
        }
    }
}
