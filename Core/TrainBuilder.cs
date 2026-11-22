using Il2Cpp;
using Il2CppFluffyUnderware.Curvy;
using Il2CppFluffyUnderware.Curvy.Controllers;
using Il2CppTLD.Gameplay;
using Il2CppTLD.IntBackedUnit;
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
        /// The game's own mattress bed: its sleep menu is what the unfolded folding bed uses.
        public const string SleepBedPrefab = "Assets/Prefabs/Interactive/INTERACTIVE_BedMattressA.prefab";
        public static readonly Vector3 SleepBedSize = new Vector3(0.64f, 0.18f, 1.3f);
        /// Container prefabs are keyed by their short name, not by their asset path.
        public const string StoragePrefab = "CONTAINER_SteamerTrunk";
        /// Dummy Addressables key so TrainCar serialization never sees a null reference.
        public const string AssetKey = "steamrailway_coal_locomotive";

        public const long WaterCapacityUnits = 100_000_000_000L;   // ~100 L
        public const long WaterBoiledPerMinute = 500_000_000L;     // ~0.5 L per game minute
        /// The boiler is written for liquid fuel; a token stays in the fuel tank and its burn rate is zero.
        public const long FuelTokenUnits = 1_000_000_000L;

        // Player cabin (local space of the train root, +Z forward).
        public static readonly Vector3 CabinFloor = new Vector3(0f, 1.35f, -2.6f);
        public static readonly Vector3 CabinSize = new Vector3(1.7f, 0.3f, 1.9f);   // walkable area: z -4.25 .. -2.35
        public static readonly Vector3 FireboxPosition = new Vector3(0f, 1.35f, -0.8f);
        public const float FireFxScale = 0.75f;   // the stove's fire effect, shrunk to fit the fire hole
        public static readonly Vector3 DoorPosition = new Vector3(1.3f, 2.3f, -2.6f);
        public static readonly Vector3 ExitPosition = new Vector3(2.6f, 0.3f, -2.6f);
        public static readonly Vector3 ChimneyTop = new Vector3(0f, 4.4f, 3.8f);
        public static readonly Vector3 StoragePosition = new Vector3(-0.55f, 1.35f, -3.4f);
        /// Warm, modest cab lighting: the ceiling lamp.
        public static readonly Color CabLampColor = new Color(1f, 0.78f, 0.5f);
        public const float CabLampRange = 4.2f;
        /// The fusible plug's click box: on the brass plate of the smokebox door, reachable from the ground in front of the pilot.
        public static readonly Vector3 PlugRepairPosition = new Vector3(0f, 3.25f, 6.62f);
        /// The cab cupboard holds at most this much (kg).
        public const float CupboardCapacityKilograms = 50f;
    }

    /// Builds a complete coal-fired SteamTrain from code. Everything is created under an inactive root,
    /// wired up, and only then activated, because the game's Awake methods expect their references to be set.
    internal static class TrainBuilder
    {
        public const string NamePrefix = "SteamRailway_Train_";

        // ---- spline controller and root
        const float ComponentsRootHeightInMeters = 2.5f;
        const float ComponentsRootDepthInMeters = -1f;

        // ---- placeholder body (used without the model)
        static readonly Vector3 PlaceholderBodyPosition = Il2CppHelpers.CreateVector(0, 1.2f, 0);
        static readonly Vector3 PlaceholderBodySize = Il2CppHelpers.CreateVector(2.6f, 0.3f, 9f);
        static readonly Vector3 PlaceholderRoofPosition = Il2CppHelpers.CreateVector(0, 4.2f, -2.5f);
        static readonly Vector3 PlaceholderRoofSize = Il2CppHelpers.CreateVector(2.6f, 0.15f, 3.5f);
        static readonly Vector3 PlaceholderBoilerPosition = Il2CppHelpers.CreateVector(0, 2.3f, 2.2f);
        static readonly Vector3 PlaceholderBoilerSize = Il2CppHelpers.CreateVector(1.6f, 1.6f, 4.6f);
        static readonly Vector3 PlaceholderChimneyPosition = Il2CppHelpers.CreateVector(0, 3.6f, 3.8f);
        static readonly Vector3 PlaceholderChimneySize = Il2CppHelpers.CreateVector(0.5f, 1.2f, 0.5f);

        // ---- cabin
        const float CameraSwayRandomOffsetMaximum = 1f;
        const float CameraSwayPeriodInSeconds = 4f;
        const float CameraSwayHalfAngleInDegrees = 0.5f;
        const float CameraSwayMultiplier = 1f;
        const float CabinMovementSpeedInMetersPerSecond = 2f;
        const float CameraMotionPeakTime = 0.5f;
        const float CameraMotionPeakAmplitude = 0.02f;
        const float CameraMotionPatternLengthInSeconds = 5f;
        const float CameraMotionDamper = 0.125f;
        const float LurchDamper = 0.2f;
        const float CabinFadeInSeconds = 0.5f;
        const float FootstepBaseDistanceMultiplier = 0.65f;

        // ---- collision boxes at the train's ends
        static readonly Vector3 FrontColliderFallbackPosition = Il2CppHelpers.CreateVector(0, 1.5f, 7.4f);
        static readonly Vector3 RearColliderFallbackPosition = Il2CppHelpers.CreateVector(0, 1.5f, -4.8f);
        static readonly Vector3 EndColliderSize = Il2CppHelpers.CreateVector(2.4f, 2f, 0.6f);
        const float LowRamSpeedThresholdInMetersPerSecond = 7f;
        const float HighRamSpeedThresholdInMetersPerSecond = 12f;

        // ---- engine (values of the game's SteamTrainEngine fields)
        const float EngineMinimumPressureForLowSpeedMovement = 25;
        const float EngineMinimumPressureForHighSpeedMovement = 26;
        const float EngineMaximumSteamPressure = 300;
        const float EngineOverfireMaximumSteamPressure = 400;
        const float EngineOverfireBonusMaximumSpeedPercent = 15;
        const float EngineMaximumVelocityReverse = -8;
        const float EngineMaximumVelocityForwardLow = 8;
        const float EngineMaximumVelocityForwardHigh = 15;
        const float EngineMinimumBrakeDecreasePerSecond = 1.25f;
        const float EngineMaximumBrakeDecreasePerSecond = 3;
        const float EngineDecreasePerSecondNeutral = 0.5f;
        const float EngineDecreaseInsufficientSteam = 0.8f;
        const float EngineDecreasePerSecondDownshift = 0.5f;
        const float EngineIncreasePerSecondReverse = -1;
        const float EngineIncreasePerSecondForwardLow = 1;
        const float EngineIncreasePerSecondForwardHigh = 1.75f;
        const float EngineMaximumReverseSpeedToShiftGearUp = -2;
        const float EngineMaximumForwardSpeedToShiftGearDown = 2;
        const float EngineSteamBuiltPerSecond = 7;
        const float EngineSteamConsumedPerSecondReverse = 5;
        const float EngineSteamConsumedPerSecondForwardLow = 5;
        const float EngineSteamConsumedPerSecondForwardHigh = 14;
        const float EngineSteamConsumedPerBrakeSecond = 0.5f;

        // ---- boiler (values of the game's SteamTrainBoiler fields)
        static readonly Vector3 BoilerLocalPosition = Il2CppHelpers.CreateVector(0, 0, 1);
        const float BoilerMaximumTemperature = 350;
        const float BoilerTemperatureChangePerMinute = 600;
        const float BoilerAmbientHeatTemperature = 20;
        const float BoilerAmbientHeatHoursStoredPerHourOn = 0.2f;
        const float BoilerMaximumAmbientHeatHoursStored = 3;
        const float BoilerExplosionCheckIntervalInSeconds = 10;
        const int BoilerLowRiskMaximumCondition = 49;
        const int BoilerLowRiskExplosionChance = 50;
        const int BoilerHighRiskMaximumCondition = 24;
        const int BoilerHighRiskExplosionChance = 75;
        const int BoilerOverfireDamagePerHour = 100;

        // ---- firebox stove
        const float FireboxStoveYawInDegrees = 180f;
        static readonly Vector3 FireEffectFallbackPosition = Il2CppHelpers.CreateVector(0f, 1.86f, -1.79f);
        static readonly Vector3 FireboxClickBoxSizeWithModel = Il2CppHelpers.CreateVector(0.62f, 0.46f, 0.14f);
        static readonly Vector3 FireboxClickBoxSizeWithoutModel = Il2CppHelpers.CreateVector(0.5f, 0.4f, 0.2f);

        // ---- wear components
        static readonly Vector3 WheelsLocalPosition = Il2CppHelpers.CreateVector(0, -1.5f, 0);
        static readonly Vector3 BrakeShoeLocalPosition = Il2CppHelpers.CreateVector(0.8f, -1.5f, 0);
        static readonly Vector3 CowCatcherLocalPosition = Il2CppHelpers.CreateVector(0, -1.5f, 7f);
        static readonly Vector3 CookingSurfaceLocalPosition = Il2CppHelpers.CreateVector(-0.6f, 0, -2f);
        static readonly int[] WheelTierConditionThresholds = { 75, 50, 25, 0 };
        static readonly float[] WheelTierSpeedModifiers = { 1f, 0.9f, 0.75f, 0.5f };
        static readonly float[] WheelTierOilModifiers = { 1f, 1.1f, 1.25f, 1.5f };

        // ---- tanks
        const int FuelCapacityFactor = 2;

        // ---- cockpit (values of the game's SteamTrainCockpit fields)
        static readonly Vector3 CockpitLocalPosition = Il2CppHelpers.CreateVector(0, 2.5f, -1.2f);
        const float CockpitMaximumPressureNeedleAngle = 120;
        const float CockpitMaximumSpeedNeedleAngle = 95;
        const float CockpitGearLeverMoveSpeed = 50;
        const float CockpitGearGaugeMoveRatio = 6;
        const float CockpitGearLeverAngleNeutral = -10;
        const float CockpitGearLeverAngleLow = -20;
        const float CockpitGearLeverAngleHigh = -30;
        const float CockpitBrakeLeverMoveSpeed = 300;
        const float CockpitBrakeGaugeMoveRatio = 2;
        const float CockpitBrakeGaugeAngleOffset = 180;
        const float CockpitBrakeLeverAngleOn = -90;
        const float CockpitFullTankOffset = 0.1f;

        // ---- steam and smoke effects
        static readonly Vector3 WhistleFallbackPosition = Il2CppHelpers.CreateVector(0, 4.5f, 0);
        const float WhistleTootLengthInSeconds = 1.6f;
        const int SmokeMinimumEmissionRate = 40;
        const int SmokeMaximumEmissionRate = 100;
        const float EmitterPitchInDegrees = -90f;
        const float SmokeLifetimeInSeconds = 6f;
        const float SmokeSpeedInMetersPerSecond = 2.2f;
        const float SmokeSizeInMeters = 2.2f;
        static readonly Color SmokeColor = new Color(0.25f, 0.24f, 0.23f, 0.6f);
        const int SmokeMaximumParticles = 400;
        const float SmokeConeAngleInDegrees = 12f;
        const float SmokeConeRadiusInMeters = 0.25f;
        const float SteamLifetimeInSeconds = 1.5f;
        const float SteamSpeedInMetersPerSecond = 4f;
        const float SteamSizeInMeters = 0.6f;
        const float SteamConeAngleInDegrees = 8f;
        const float SteamConeRadiusInMeters = 0.05f;

        // ---- door
        static readonly Vector3 DoorPanelSize = Il2CppHelpers.CreateVector(0.08f, 1.9f, 0.9f);
        static readonly Vector3 DoorColliderSizeWithModel = Il2CppHelpers.CreateVector(0.2f, 1.8f, 0.85f);
        static readonly Vector3 DoorColliderSizeWithoutModel = Il2CppHelpers.CreateVector(0.15f, 1.9f, 0.9f);
        const float ExitPointYawInDegrees = 90f;
        static readonly Vector3 DoorWindowLocalPosition = Il2CppHelpers.CreateVector(0, 0.5f, 0);
        static readonly Vector3 DoorLookTargetLocalPosition = Il2CppHelpers.CreateVector(0.6f, 0, 0);

        // ---- cab controls: fallback positions without the model and collider sizes with the model
        static readonly Vector3 LeverSize = Il2CppHelpers.CreateVector(0.06f, 0.5f, 0.06f);
        static readonly Vector3 KnobSize = Il2CppHelpers.CreateVector(0.12f, 0.12f, 0.08f);
        static readonly Vector3 GaugeSize = Il2CppHelpers.CreateVector(0.18f, 0.18f, 0.04f);
        static readonly Vector3 RegulatorFallbackPosition = Il2CppHelpers.CreateVector(0.5f, 2.6f, -1.45f);
        static readonly Vector3 RegulatorColliderSize = Il2CppHelpers.CreateVector(0.42f, 0.3f, 0.1f);
        static readonly Vector3 ReverserFallbackPosition = Il2CppHelpers.CreateVector(0.65f, 2.0f, -1.5f);
        static readonly Vector3 ReverserColliderSize = Il2CppHelpers.CreateVector(0.14f, 0.5f, 0.2f);
        static readonly Vector3 BrakeFallbackPosition = Il2CppHelpers.CreateVector(-0.55f, 2.2f, -1.5f);
        static readonly Vector3 BrakeColliderSize = Il2CppHelpers.CreateVector(0.16f, 0.14f, 0.42f);
        static readonly Vector3 HandBrakeFallbackPosition = Il2CppHelpers.CreateVector(-0.9f, 2.2f, -3.6f);
        static readonly Vector3 HandBrakeColliderSize = Il2CppHelpers.CreateVector(0.36f, 0.14f, 0.36f);
        static readonly Vector3 InjectorFallbackPosition = Il2CppHelpers.CreateVector(-0.65f, 2.75f, -1.45f);
        static readonly Vector3 SmallValveColliderSize = Il2CppHelpers.CreateVector(0.16f, 0.16f, 0.08f);
        static readonly Vector3 BlowerFallbackPosition = Il2CppHelpers.CreateVector(0.3f, 3.0f, -1.45f);
        static readonly Vector3 PressureGaugeFallbackPosition = Il2CppHelpers.CreateVector(-0.2f, 3.0f, -1.42f);
        static readonly Vector3 PressureGaugeColliderSize = Il2CppHelpers.CreateVector(0.36f, 0.36f, 0.06f);
        static readonly Vector3 SpeedGaugeFallbackPosition = Il2CppHelpers.CreateVector(0.2f, 3.0f, -1.42f);
        static readonly Vector3 SpeedGaugeColliderSize = Il2CppHelpers.CreateVector(0.25f, 0.25f, 0.06f);
        static readonly Vector3 HeatGaugeFallbackPosition = Il2CppHelpers.CreateVector(0f, 2.75f, -1.42f);
        static readonly Vector3 HeatGaugeColliderSize = Il2CppHelpers.CreateVector(0.18f, 0.18f, 0.06f);
        static readonly Vector3 WaterGlassFallbackPosition = Il2CppHelpers.CreateVector(-0.35f, 2.55f, -1.42f);
        static readonly Vector3 WaterGlassColliderSize = Il2CppHelpers.CreateVector(0.1f, 0.55f, 0.08f);
        static readonly Vector3 TenderGaugeFallbackPosition = Il2CppHelpers.CreateVector(0f, 2.6f, -3.75f);
        static readonly Vector3 TenderGaugeColliderSize = Il2CppHelpers.CreateVector(0.16f, 0.48f, 0.06f);
        static readonly Vector3 PumpFallbackPosition = Il2CppHelpers.CreateVector(0.55f, 2.6f, -3.75f);
        static readonly Vector3 DamperFallbackPosition = Il2CppHelpers.CreateVector(0.52f, 1.8f, -2.0f);
        static readonly Vector3 DamperColliderSize = Il2CppHelpers.CreateVector(0.3f, 0.26f, 0.1f);
        static readonly Vector3 HeadlightSwitchFallbackPosition = Il2CppHelpers.CreateVector(-1.2f, 2.7f, -1.66f);
        static readonly Vector3 HeadlightSwitchColliderSize = Il2CppHelpers.CreateVector(0.16f, 0.22f, 0.1f);
        static readonly Vector3 CabLightSwitchFallbackPosition = Il2CppHelpers.CreateVector(-1.4f, 2.45f, -1.75f);
        static readonly Vector3 CabLightSwitchColliderSize = Il2CppHelpers.CreateVector(0.1f, 0.16f, 0.1f);
        static readonly Vector3 BedFallbackPosition = Il2CppHelpers.CreateVector(-0.98f, 2.4f, -3.3f);
        static readonly Vector3 BedColliderSize = Il2CppHelpers.CreateVector(0.9f, 0.7f, 1.55f);
        static readonly Vector3 HatchSizeWithModel = Il2CppHelpers.CreateVector(0.5f, 0.3f, 0.5f);
        static readonly Vector3 HatchSizeWithoutModel = Il2CppHelpers.CreateVector(0.5f, 0.1f, 0.5f);
        static readonly Vector3 WaterHatchFallbackPosition = Il2CppHelpers.CreateVector(-0.6f, 2.0f, -4.65f);
        static readonly Vector3 SnowHatchFallbackPosition = Il2CppHelpers.CreateVector(0.6f, 2.0f, -4.65f);
        // the snow hatch and the fusible plug stick out past the bunker's and the boiler's sides: reachable from the sides too
        static readonly Vector3 SnowHatchColliderSize = Il2CppHelpers.CreateVector(2.9f, 0.6f, 0.4f);
        static readonly Vector3 BoilerRepairLeftFallbackPosition = Il2CppHelpers.CreateVector(-0.95f, 2.3f, 4.95f);
        static readonly Vector3 BoilerRepairRightFallbackPosition = Il2CppHelpers.CreateVector(0.95f, 2.3f, 4.95f);
        static readonly Vector3 BoilerRepairColliderSize = Il2CppHelpers.CreateVector(0.3f, 0.9f, 0.4f);
        static readonly Vector3 PlugRepairColliderSize = Il2CppHelpers.CreateVector(2.2f, 0.6f, 0.5f);
        static readonly Vector3 WhistleControlFallbackPosition = Il2CppHelpers.CreateVector(0f, 3.6f, -1.8f);
        static readonly Vector3 ControlColliderMargin = Il2CppHelpers.CreateVector(0.1f, 0.1f, 0.1f);

        // ---- lamps
        static readonly Vector3 HeadLampFallbackPosition = Il2CppHelpers.CreateVector(0f, 3.95f, 6.43f);
        static readonly Vector3 RearLampFallbackPosition = Il2CppHelpers.CreateVector(0f, 3.1f, -5.74f);
        const float RearLampYawInDegrees = 180f;
        const float LampPitchInDegrees = 4f;
        const float LampRangeInMeters = 110f;
        const float LampSpotAngleInDegrees = 78f;
        static readonly Color LampColor = new Color(1f, 0.9f, 0.72f);
        static readonly Vector3 LampGlowLocalPosition = Il2CppHelpers.CreateVector(0f, 0f, 0.25f);
        const float LampGlowRangeInMeters = 1.3f;
        static readonly Vector3 CabLightFallbackPosition = Il2CppHelpers.CreateVector(0f, 4.4f, -3.1f);
        static readonly Vector3 FireLightFallbackPosition = Il2CppHelpers.CreateVector(0f, 2.0f, -1.7f);
        static readonly Color FireLightColor = new Color(1f, 0.55f, 0.25f);
        const float FireLightRangeInMeters = 4.5f;

        // ---- cupboard and bed
        const float PinPositionToleranceSquaredInMeters = 1e-4f;
        const float PinRotationToleranceInDegrees = 0.1f;
        const float MinimumColliderScale = 0.01f;
        const float CupboardWidthInMeters = 0.78f;
        const float CupboardHeightInMeters = 0.64f;
        const float CupboardDepthInMeters = 0.34f;
        const float TrunkYawInDegreesWithoutModel = 90f;
        const float EmptyContainerChance = 100f;
        const float SleepBedDropInMeters = 0.2f;
        const float SleepBedClickBoxDropInMeters = 0.1f;

        static Transform s_Root, s_Model;

        public static SteamTrain Build(string guid, CurvySpline spline, float splinePosition, Material bodyMaterial)
        {
            var root = new GameObject(NamePrefix + guid);
            root.SetActive(false);
            var rootTransform = root.transform;

            var model = LocomotiveModel.Spawn(rootTransform);
            s_Root = rootTransform;
            s_Model = model != null ? model.transform : null;
            if (s_Model == null) BuildPlaceholderBody(rootTransform, bodyMaterial);

            var splineController = root.AddComponent<SplineController>();
            splineController.Spline = spline;
            splineController.PositionMode = CurvyPositionMode.WorldUnits;
            splineController.Position = Mathf.Clamp(splinePosition, 0f, spline.Length);
            splineController.OrientationMode = OrientationModeEnum.Tangent;
            splineController.OrientationAxis = OrientationAxisEnum.Forward; // without this the train stands on its nose
            // the engine always faces the line's +direction; running in reverse must not turn it round
            splineController.IgnoreDirection = true;
            splineController.Clamping = CurvyClamping.Clamp;
            splineController.Speed = 0f;

            var train = root.AddComponent<SteamTrain>();
            train.m_SplineController = splineController;
            train.m_AssetReference = new AssetReferenceGameObject(TrainConfig.AssetKey);
            train.m_TrainBoardedState = Il2CppHelpers.CreateWwiseState();
            train.m_TrainUnboardedState = Il2CppHelpers.CreateWwiseState();
            train.m_SteamWhistleToot = Il2CppHelpers.CreateWwiseEvent();

            BuildCabin(rootTransform, train);
            BuildColliders(rootTransform, train);
            var componentsRoot = Il2CppHelpers.Child(rootTransform, "Components", Il2CppHelpers.CreateVector(0, ComponentsRootHeightInMeters, ComponentsRootDepthInMeters)).transform;
            var engine = BuildEngine(componentsRoot, train);
            BuildBoiler(componentsRoot, rootTransform, train, guid);
            BuildWearComponents(componentsRoot, train);
            BuildTanks(componentsRoot, train);
            BuildCockpit(rootTransform, train);
            BuildEffects(rootTransform, train);
            BuildDoor(rootTransform, train, engine, bodyMaterial);
            BuildCabinStorage(rootTransform, train, guid);
            if (HasModel) BuildSleepBed(rootTransform, guid);
            BuildControls(rootTransform, train, engine, bodyMaterial);
            train.m_FleeTriggerVolume = Il2CppHelpers.Child(rootTransform, "Flee", Vector3.zero).AddComponent<AiFleeTriggerVolume>();

            ObjectGuid.MaybeAttachObjectGuidAddOrReplace(root, guid);
            root.SetActive(true);

            train.m_WaterSource.Set(Il2CppHelpers.Vol(TrainConfig.WaterCapacityUnits));
            train.m_FuelSource.Set(Il2CppHelpers.Vol(TrainConfig.FuelTokenUnits));
            ConfigureEffects(train);
            return train;
        }

        /// Position of a model socket in train space (see Docs/LOKOMOTIF.md), or the placeholder layout without a model.
        static Vector3 SocketPosition(string socket, Vector3 fallback)
        {
            var transform = LocomotiveModel.FindDeep(s_Model, socket);
            return transform != null ? s_Root.InverseTransformPoint(transform.position) : fallback;
        }

        static bool HasModel => s_Model != null;

        static void BuildPlaceholderBody(Transform rootTransform, Material material)
        {
            Il2CppHelpers.Cube(rootTransform, "Body", PlaceholderBodyPosition, PlaceholderBodySize, material, true);
            Il2CppHelpers.Cube(rootTransform, "CabRoof", PlaceholderRoofPosition, PlaceholderRoofSize, material, false);
            Il2CppHelpers.Cube(rootTransform, "BoilerShell", PlaceholderBoilerPosition, PlaceholderBoilerSize, material, false);
            Il2CppHelpers.Cube(rootTransform, "Chimney", PlaceholderChimneyPosition, PlaceholderChimneySize, material, false);
        }

        static void BuildCabin(Transform rootTransform, SteamTrain train)
        {
            // The player is not parented to the train: each frame the game copies the player's position to
            // CameraDummy, which is the player's FEET, walking inside CabinLimits.
            var floor = SocketPosition("Socket_CabFloor", TrainConfig.CabinFloor);
            var dummy = Il2CppHelpers.Child(rootTransform, "CameraDummy", floor);
            var sway = dummy.AddComponent<SimpleSway>();
            var offset = new MinMax(); offset.m_Min = 0f; offset.m_Max = CameraSwayRandomOffsetMaximum;
            sway.m_RandomTimeOffset = offset;
            sway.m_SwayPeriod = CameraSwayPeriodInSeconds; sway.m_SwayHalfAngle = CameraSwayHalfAngleInDegrees; sway.m_SwayMultiplier = CameraSwayMultiplier; sway.m_Direction = Il2CppHelpers.CreateVector(0, 0, 1);

            var cabin = Il2CppHelpers.Child(rootTransform, "CabinLimits", floor).AddComponent<BoxCollider>();
            cabin.size = TrainConfig.CabinSize;
            cabin.isTrigger = true;

            train.m_CameraDummy = dummy.transform;
            train.m_CameraSimpleSway = sway;
            train.m_CabinLimits = cabin;
            train.m_MovementSpeed = CabinMovementSpeedInMetersPerSecond;

            var curve = new AnimationCurve();
            curve.AddKey(0f, 0f); curve.AddKey(CameraMotionPeakTime, CameraMotionPeakAmplitude); curve.AddKey(1f, 0f);
            train.m_VerticalCameraMotionPattern = curve;
            train.m_CameraMotionPatternLengthSeconds = CameraMotionPatternLengthInSeconds;
            train.m_CameraMotionDamper = CameraMotionDamper;
            train.m_LurchDamper = LurchDamper;
            train.m_FadeInSeconds = CabinFadeInSeconds;
            train.m_FadeOutSeconds = CabinFadeInSeconds;
            train.m_FootstepBaseDistanceMultiplier = FootstepBaseDistanceMultiplier;
            train.m_PlayerEnterTrainEventName = "event_EnterTrain";
            train.m_PlayerExitTrainEventName = "event_ExitTrain";
        }

        static void BuildColliders(Transform rootTransform, SteamTrain train)
        {
            train.m_FrontCollider = MakeTrainCollider(rootTransform, "FrontCollider", SocketPosition("Socket_Front", FrontColliderFallbackPosition), train);
            train.m_RearCollider = MakeTrainCollider(rootTransform, "RearCollider", SocketPosition("Socket_Rear", RearColliderFallbackPosition), train);
            train.m_LowRamSpeedThreshold = LowRamSpeedThresholdInMetersPerSecond;
            train.m_HighRamSpeedThreshold = HighRamSpeedThresholdInMetersPerSecond;
        }

        const int IgnoreRaycastLayer = 2;

        static TrainCarCollider MakeTrainCollider(Transform rootTransform, string name, Vector3 position, SteamTrain train)
        {
            var targetObject = Il2CppHelpers.Child(rootTransform, name, position);
            // a trigger still, but the player's look ray passes it (it hid the plug and the snow hatch from the front and back)
            targetObject.layer = IgnoreRaycastLayer;
            targetObject.AddComponent<Rigidbody>().isKinematic = true;
            var box = targetObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = EndColliderSize;
            var carCollider = targetObject.AddComponent<TrainCarCollider>();
            carCollider.m_ParentTrainCar = train;
            carCollider.m_BoxCollider = box;
            return carCollider;
        }

        static SteamTrainEngine BuildEngine(Transform componentsRoot, SteamTrain train)
        {
            var engine = Il2CppHelpers.Child(componentsRoot, "Engine", Vector3.zero).AddComponent<SteamTrainEngine>();
            engine.m_SteamTrain = train;
            engine.m_SteamPSI = Il2CppHelpers.Rtpc(); engine.m_TrainVelocity = Il2CppHelpers.Rtpc(); engine.m_FireboxExplosionEvent = Il2CppHelpers.CreateWwiseEvent();
            engine.m_EngineStateNormal = Il2CppHelpers.CreateWwiseState(); engine.m_EngineStateOverfire = Il2CppHelpers.CreateWwiseState();
            engine.m_EngineStateExplosionRiskLow = Il2CppHelpers.CreateWwiseState(); engine.m_EngineStateExplosionRiskHigh = Il2CppHelpers.CreateWwiseState(); engine.m_AudioLastEngineState = Il2CppHelpers.CreateWwiseState();
            engine.m_OverfireStartEvent = new UnityEvent(); engine.m_OverfireEndEvent = new UnityEvent();

            engine.m_MinSteamPressureForLowSpeedMovement = EngineMinimumPressureForLowSpeedMovement; engine.m_MinSteamPressureForHighSpeedMovement = EngineMinimumPressureForHighSpeedMovement;
            engine.m_MaxSteamPressure = EngineMaximumSteamPressure; engine.m_OverfireMaxSteamPressure = EngineOverfireMaximumSteamPressure; engine.m_OverfireBonusMaxSpeedPercent = EngineOverfireBonusMaximumSpeedPercent;
            engine.m_MaxVelocityReverse = EngineMaximumVelocityReverse; engine.m_MaxVelocityForwardLow = EngineMaximumVelocityForwardLow; engine.m_MaxVelocityForwardHigh = EngineMaximumVelocityForwardHigh;
            engine.m_MinBrakeVelocityDecreasePerSecond = EngineMinimumBrakeDecreasePerSecond; engine.m_MaxBrakeVelocityDecreasePerSecond = EngineMaximumBrakeDecreasePerSecond;
            engine.m_VelocityDecreasePerSecondNeutral = EngineDecreasePerSecondNeutral; engine.m_VelocityDecreaseInsufficientSteamPSI = EngineDecreaseInsufficientSteam; engine.m_VelocityDecreasePerSecondDownshift = EngineDecreasePerSecondDownshift;
            engine.m_VelocityIncreasePerSecondReverse = EngineIncreasePerSecondReverse; engine.m_VelocityIncreasePerSecondForwardLow = EngineIncreasePerSecondForwardLow; engine.m_VelocityIncreasePerSecondForwardHigh = EngineIncreasePerSecondForwardHigh;
            engine.m_MaxReverseSpeedToAllowShiftGearUp = EngineMaximumReverseSpeedToShiftGearUp; engine.m_MaxForwardSpeedToAllowShiftGearDown = EngineMaximumForwardSpeedToShiftGearDown;
            engine.m_SteamPSIBuiltPerSecond = EngineSteamBuiltPerSecond;
            engine.m_SteamPressurePSIConsumedPerSecondReverse = EngineSteamConsumedPerSecondReverse; engine.m_SteamPressurePSIConsumedPerSecondForwardLow = EngineSteamConsumedPerSecondForwardLow;
            engine.m_SteamPressurePSIConsumedPerSecondForwardHigh = EngineSteamConsumedPerSecondForwardHigh; engine.m_SteamPressurePSIConsumedPerBrakeSecond = EngineSteamConsumedPerBrakeSecond;
            train.m_Engine = engine;
            return engine;
        }

        static void BuildBoiler(Transform componentsRoot, Transform rootTransform, SteamTrain train, string guid)
        {
            var boiler = Il2CppHelpers.Child(componentsRoot, "Boiler", BoilerLocalPosition).AddComponent<SteamTrainBoiler>();
            SetupComponent(boiler, train, "GAMEPLAY_Boiler");
            boiler.m_MaxTemperature = BoilerMaximumTemperature; boiler.m_TemperatureChangePerMinute = BoilerTemperatureChangePerMinute; boiler.m_AmbientHeatTemperature = BoilerAmbientHeatTemperature;
            boiler.m_AmbientHeatHourStoredPerHourOn = BoilerAmbientHeatHoursStoredPerHourOn; boiler.m_MaxAmbientHeatHoursStored = BoilerMaximumAmbientHeatHoursStored; boiler.m_ExplosionCheckIntervalSeconds = BoilerExplosionCheckIntervalInSeconds;
            boiler.m_LowRiskMaxCondition = BoilerLowRiskMaximumCondition; boiler.m_LowRiskExplosionChance = BoilerLowRiskExplosionChance; boiler.m_HighRiskMaxCondition = BoilerHighRiskMaximumCondition; boiler.m_HighRiskExplosionChance = BoilerHighRiskExplosionChance;
            boiler.m_OverfireDamagePerHour = BoilerOverfireDamagePerHour;
            boiler.m_WaterBoiledPerMinute = Il2CppHelpers.Vol(TrainConfig.WaterBoiledPerMinute);
            boiler.m_FuelBurnedPerMinute = Il2CppHelpers.Vol(0L);
            boiler.m_ExplosionEvent = new UnityEvent();
            boiler.m_BoilerTemperature = Il2CppHelpers.Rtpc(); boiler.m_BoilerStateOn = Il2CppHelpers.CreateWwiseState(); boiler.m_BoilerStateOff = Il2CppHelpers.CreateWwiseState();
            boiler.m_FurnaceLights = Il2CppHelpers.Arr(Il2CppHelpers.Child(boiler.transform, "FurnaceLight", Vector3.zero));
            boiler.m_FurnaceParticlesParent = Il2CppHelpers.Child(boiler.transform, "FurnaceParticles", Vector3.zero);
            boiler.m_InvisibleFire = AddFireboxStove(rootTransform, guid);
            train.m_Boiler = boiler;
        }

        /// The firebox is the base game's pot belly stove, hidden behind the fire hole: clicking the fire doors opens
        /// the game's own fire menus and the stove's Fire is what heats the boiler.
        static Fire AddFireboxStove(Transform rootTransform, string trainGuid)
        {
            var prefab = Il2CppHelpers.LoadAsset<GameObject>(TrainConfig.StovePrefab);
            if (prefab == null) return null;
            var stove = Object.Instantiate(prefab);
            stove.SetActive(false);
            stove.name = "Firebox";
            stove.transform.SetParent(rootTransform, false);
            stove.transform.localPosition = SocketPosition("Socket_Firebox", TrainConfig.FireboxPosition);
            stove.transform.localRotation = Quaternion.Euler(0f, FireboxStoveYawInDegrees, 0f);
            foreach (var objectGuid in stove.GetComponentsInChildren<ObjectGuid>(true))
                objectGuid.SetRuntimeCachedPdid(trainGuid + "_" + objectGuid.gameObject.name);

            // Hide the stove's meshes and its own lights (our firelight comes out of the fire hole). Its fire effect
            // (flames, embers, sparks) stays and is moved onto the grate inside the fire hole.
            foreach (var renderer in stove.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;                                   // the mod's own embers and flames (FireEmbers) replace the stove's effect
            var fireTransform = stove.GetComponentInChildren<Fire>(true)?.transform;
            if (fireTransform != null)
            {
                fireTransform.position = rootTransform.TransformPoint(SocketPosition("Socket_FireFX", FireEffectFallbackPosition));
                fireTransform.rotation = rootTransform.rotation;
                fireTransform.localScale = Vector3.one * TrainConfig.FireFxScale;
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
            box.size = HasModel ? FireboxClickBoxSizeWithModel : FireboxClickBoxSizeWithoutModel;

            var fire = stove.GetComponentInChildren<Fire>(true);
            if (fire != null) fire.m_SkipSerialization = true;   // saved with the train (TrainState.FireData)
            stove.SetActive(true);
            return fire;
        }

        static void BuildWearComponents(Transform componentsRoot, SteamTrain train)
        {
            var wheels = Il2CppHelpers.Child(componentsRoot, "Wheels", WheelsLocalPosition).AddComponent<SteamTrainWheels>();
            SetupComponent(wheels, train, "GAMEPLAY_Wheels");
            var tiers = new Il2CppSystem.Collections.Generic.List<SteamTrainWheels.PerformanceTier>();
            for (int index = 0; index < WheelTierConditionThresholds.Length; index++)
            {
                var tier = new SteamTrainWheels.PerformanceTier();
                tier.m_ConditionThreshold = WheelTierConditionThresholds[index]; tier.m_MaxVelocityIncreaseModifier = WheelTierSpeedModifiers[index]; tier.m_OilConsumedModifier = WheelTierOilModifiers[index];
                tiers.Add(tier);
            }
            wheels.m_PerformanceTiers = tiers;
            train.m_Wheels = wheels;

            var brake = Il2CppHelpers.Child(componentsRoot, "BrakeShoe", BrakeShoeLocalPosition).AddComponent<SteamTrainBrakeShoe>();
            SetupComponent(brake, train, "GAMEPLAY_BrakeShoes");
            train.m_BrakeShoe = brake;

            var cowCatcher = Il2CppHelpers.Child(componentsRoot, "CowCatcher", CowCatcherLocalPosition).AddComponent<SteamTrainCowCatcher>();
            SetupComponent(cowCatcher, train, "GAMEPLAY_CowCatcher");
            train.m_CowCatcher = cowCatcher;

            var cookingSurface = Il2CppHelpers.Child(componentsRoot, "CookingSurface", CookingSurfaceLocalPosition).AddComponent<SteamTrainCookingSurface>();
            SetupComponent(cookingSurface, train, "GAMEPLAY_CookingSurface");
            cookingSurface.m_GearPlacePoints = new Il2CppSystem.Collections.Generic.List<GearPlacePoint>();
            cookingSurface.m_PendingDeserialization = new Il2CppSystem.Collections.Generic.List<SteamTrainCookingSurface.CookingSurfaceState>();
            train.m_CookingSurface = cookingSurface;
        }

        static void BuildTanks(Transform componentsRoot, SteamTrain train)
        {
            var fuel = Il2CppHelpers.Child(componentsRoot, "FuelSource", Vector3.zero).AddComponent<GenericLiquidContainer>();
            fuel.m_VolumeRTPC = Il2CppHelpers.Rtpc();
            fuel.m_MaxCapacity = Il2CppHelpers.Vol(TrainConfig.FuelTokenUnits * FuelCapacityFactor);
            train.m_FuelSource = fuel;

            var water = Il2CppHelpers.Child(componentsRoot, "WaterSource", Vector3.zero).AddComponent<GenericLiquidContainer>();
            water.m_VolumeRTPC = Il2CppHelpers.Rtpc();
            water.m_MaxCapacity = Il2CppHelpers.Vol(TrainConfig.WaterCapacityUnits);
            train.m_WaterSource = water;
        }

        static void BuildCockpit(Transform rootTransform, SteamTrain train)
        {
            var cockpit = Il2CppHelpers.Child(rootTransform, "Cockpit", CockpitLocalPosition).AddComponent<SteamTrainCockpit>();
            var cockpitTransform = cockpit.transform;
            cockpit.m_SteamGaugePSINeedlePivot = Il2CppHelpers.Child(cockpitTransform, "PsiNeedle", Vector3.zero).transform;
            cockpit.m_SpeedometerNeedlePivot = Il2CppHelpers.Child(cockpitTransform, "SpeedNeedle", Vector3.zero).transform;
            cockpit.m_GearLeverPivot = Il2CppHelpers.Child(cockpitTransform, "GearLever", Vector3.zero).transform;
            cockpit.m_GearGaugePivot = Il2CppHelpers.Child(cockpitTransform, "GearGauge", Vector3.zero).transform;
            cockpit.m_BrakeLeverPivot = Il2CppHelpers.Child(cockpitTransform, "BrakeLever", Vector3.zero).transform;
            cockpit.m_BrakeGaugePivot = Il2CppHelpers.Child(cockpitTransform, "BrakeGauge", Vector3.zero).transform;
            cockpit.m_WaterTankGauge = Il2CppHelpers.Child(cockpitTransform, "WaterGauge", Vector3.zero).transform;
            cockpit.m_OilTankGauge = Il2CppHelpers.Child(cockpitTransform, "FuelGauge", Vector3.zero).transform;
            cockpit.m_LightRoot = Il2CppHelpers.Child(cockpitTransform, "Lights", Vector3.zero);
            cockpit.m_MaxSteamPSINeedleAngle = CockpitMaximumPressureNeedleAngle; cockpit.m_MaxSpeedometerNeedAngle = CockpitMaximumSpeedNeedleAngle;
            cockpit.m_GearLeverMoveSpeed = CockpitGearLeverMoveSpeed; cockpit.m_GearGaugeMoveRatio = CockpitGearGaugeMoveRatio;
            cockpit.m_GearLeverAngleNeutral = CockpitGearLeverAngleNeutral; cockpit.m_GearLeverAngleLow = CockpitGearLeverAngleLow; cockpit.m_GearLeverAngleHigh = CockpitGearLeverAngleHigh;
            cockpit.m_BrakeLeverMoveSpeed = CockpitBrakeLeverMoveSpeed; cockpit.m_BrakeGaugeMoveRatio = CockpitBrakeGaugeMoveRatio; cockpit.m_BrakeGaugeAngleOffset = CockpitBrakeGaugeAngleOffset; cockpit.m_BrakeLeverAngleOn = CockpitBrakeLeverAngleOn;
            cockpit.m_FullTankOffset = CockpitFullTankOffset;
            train.m_Cockpit = cockpit;
        }

        static void BuildEffects(Transform rootTransform, SteamTrain train)
        {
            var whistle = Il2CppHelpers.Child(rootTransform, "Whistle", SocketPosition("Socket_SafetyValve", WhistleFallbackPosition));
            train.m_SteamWhistleParent = whistle;
            train.m_SteamWhistleLightParticleSystem = Il2CppHelpers.Child(whistle.transform, "SteamLight", Vector3.zero).AddComponent<ParticleSystem>();
            train.m_SteamWhistleDenseParticleSystem = Il2CppHelpers.Child(whistle.transform, "SteamDense", Vector3.zero).AddComponent<ParticleSystem>();
            train.m_SteamWhistleTootLengthSeconds = WhistleTootLengthInSeconds;

            var smoke = Il2CppHelpers.Child(rootTransform, "ChimneySmoke", SocketPosition("Socket_Chimney", TrainConfig.ChimneyTop)).AddComponent<ParticleSystem>();
            train.m_SmokeStackParticleSystems = Il2CppHelpers.Arr(smoke);
            train.m_MinEmissionOverTimeRate = SmokeMinimumEmissionRate;
            train.m_MaxEmissionOverTimeRate = SmokeMaximumEmissionRate;
        }

        static void ConfigureEffects(SteamTrain train)
        {
            var smokeMaterial = Il2CppHelpers.LoadAsset<Material>(TrainConfig.SmokeMaterial);
            var steamMaterial = Il2CppHelpers.LoadAsset<Material>(TrainConfig.SteamMaterial);

            var smoke = train.m_SmokeStackParticleSystems[0];
            smoke.transform.localRotation = Quaternion.Euler(EmitterPitchInDegrees, 0f, 0f);
            var mainModule = smoke.main;
            mainModule.simulationSpace = ParticleSystemSimulationSpace.World; // smoke should trail behind the train
            mainModule.startLifetime = new ParticleSystem.MinMaxCurve(SmokeLifetimeInSeconds);
            mainModule.startSpeed = new ParticleSystem.MinMaxCurve(SmokeSpeedInMetersPerSecond);
            mainModule.startSize = new ParticleSystem.MinMaxCurve(SmokeSizeInMeters);
            mainModule.startColor = new ParticleSystem.MinMaxGradient(SmokeColor);
            mainModule.maxParticles = SmokeMaximumParticles;
            var shape = smoke.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = SmokeConeAngleInDegrees; shape.radius = SmokeConeRadiusInMeters;
            smoke.GetComponent<ParticleSystemRenderer>().sharedMaterial = smokeMaterial;
            smoke.Play();

            ConfigureSteam(train.m_SteamWhistleLightParticleSystem, steamMaterial);
            ConfigureSteam(train.m_SteamWhistleDenseParticleSystem, steamMaterial);
        }

        static void ConfigureSteam(ParticleSystem particleSystem, Material material)
        {
            var mainModule = particleSystem.main;
            mainModule.simulationSpace = ParticleSystemSimulationSpace.World;
            mainModule.startLifetime = new ParticleSystem.MinMaxCurve(SteamLifetimeInSeconds);
            mainModule.startSpeed = new ParticleSystem.MinMaxCurve(SteamSpeedInMetersPerSecond);
            mainModule.startSize = new ParticleSystem.MinMaxCurve(SteamSizeInMeters);
            mainModule.playOnAwake = false;
            var shape = particleSystem.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = SteamConeAngleInDegrees; shape.radius = SteamConeRadiusInMeters;
            particleSystem.transform.localRotation = Quaternion.Euler(EmitterPitchInDegrees, 0f, 0f);
            particleSystem.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            particleSystem.Stop();
        }

        static void BuildDoor(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material)
        {
            int interactiveLayer = LayerMask.NameToLayer("InteractiveProp");
            var door = Il2CppHelpers.Child(rootTransform, "Door", SocketPosition("Socket_Door_R", TrainConfig.DoorPosition));
            door.layer = interactiveLayer;
            // The model's cab has an open side entrance; the door is an invisible collider filling it.
            if (!HasModel) Il2CppHelpers.Cube(door.transform, "DoorPanel", Vector3.zero, DoorPanelSize, material, false);
            door.AddComponent<BoxCollider>().size = HasModel ? DoorColliderSizeWithModel : DoorColliderSizeWithoutModel;

            var exit = Il2CppHelpers.Child(rootTransform, "ExitPoint", SocketPosition("Socket_Exit_R", TrainConfig.ExitPosition));
            exit.transform.localRotation = Quaternion.Euler(0f, ExitPointYawInDegrees, 0f);

            // The Lock must exist before the door's Awake looks for it.
            var lockComponent = door.AddComponent<Lock>();
            lockComponent.m_ChanceLocked = 0f;
            lockComponent.m_LockState = LockState.Unlocked;
            lockComponent.m_LockStateRolled = true;
            lockComponent.m_LockedKeypadText = Il2CppHelpers.Loc("GAMEPLAY_Locked");

            var doorInteraction = door.AddComponent<SteamTrainDoor>();
            doorInteraction.m_ParentSteamTrain = train;
            doorInteraction.m_ExitPoint = exit.transform;
            doorInteraction.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
            doorInteraction.m_SteamEngineEnter = Il2CppHelpers.CreateWwiseEvent();
            doorInteraction.m_SteamEngineExit = Il2CppHelpers.CreateWwiseEvent();
            doorInteraction.m_DefaultHoverText = Il2CppHelpers.Loc("GAMEPLAY_Door");
            doorInteraction.m_DefaultHoldText = Il2CppHelpers.Loc("GAMEPLAY_Door");
            doorInteraction.m_CanInteract = true;
            doorInteraction.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            foreach (var loadScene in Object.FindObjectsOfType<LoadScene>())
                if (loadScene.m_HUD != null) { doorInteraction.m_HUD = loadScene.m_HUD; break; }

            // SteamTrainDoor.UpdateInteraction throws every frame without a door window.
            var windowObject = Il2CppHelpers.Child(door.transform, "DoorWindow", DoorWindowLocalPosition);
            var look = Il2CppHelpers.Child(windowObject.transform, "LookTarget", DoorLookTargetLocalPosition);
            var window = windowObject.AddComponent<SteamTrainWindow>();
            window.m_Train = train; window.m_Engine = engine; window.m_Door = doorInteraction; window.m_WindowLookTarget = look.transform;
            window.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
            window.m_LeanOutEvent = Il2CppHelpers.CreateWwiseEvent(); window.m_LeanInEvent = Il2CppHelpers.CreateWwiseEvent();
            window.m_DefaultHoverText = Il2CppHelpers.Loc("GAMEPLAY_Window");
            window.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            doorInteraction.m_DoorWindow = window;

            ObjectGuid.MaybeAttachObjectGuidAddOrReplace(door, System.Guid.NewGuid().ToString());
        }

        /// Cab levers and gauges (see CabControls). Two-way controls use SteamTrainGearLever (click / alternative click),
        /// one-way controls and gauges use SteamTrainBrakeLever. Positions are on the cab's front wall.
        static void BuildControls(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material)
        {
            bool matched = HasModel;

            // Colliders are invisible boxes sized to each modelled fitting so neighbours never overlap.
            TwoWay(rootTransform, train, engine, material, CabControls.Regulator, SocketPosition("Socket_Regulator", RegulatorFallbackPosition), matched ? RegulatorColliderSize : LeverSize);
            TwoWay(rootTransform, train, engine, material, CabControls.Reverser, SocketPosition("Socket_Reverser", ReverserFallbackPosition), matched ? ReverserColliderSize : LeverSize);
            OneWay(rootTransform, train, engine, material, CabControls.Brake, SocketPosition("Socket_Brake", BrakeFallbackPosition), matched ? BrakeColliderSize : LeverSize);
            OneWay(rootTransform, train, engine, material, CabControls.HandBrake, SocketPosition("Socket_HandBrake", HandBrakeFallbackPosition), matched ? HandBrakeColliderSize : KnobSize);
            OneWay(rootTransform, train, engine, material, CabControls.Injector, SocketPosition("Socket_Injector", InjectorFallbackPosition), matched ? SmallValveColliderSize : KnobSize);
            OneWay(rootTransform, train, engine, material, CabControls.Blower, SocketPosition("Socket_Blower", BlowerFallbackPosition), matched ? SmallValveColliderSize : KnobSize);
            OneWay(rootTransform, train, engine, material, CabControls.PressureGauge, SocketPosition("Socket_PressureGauge", PressureGaugeFallbackPosition), matched ? PressureGaugeColliderSize : GaugeSize);
            OneWay(rootTransform, train, engine, material, CabControls.SpeedGauge, SocketPosition("Socket_SpeedGauge", SpeedGaugeFallbackPosition), matched ? SpeedGaugeColliderSize : GaugeSize);
            OneWay(rootTransform, train, engine, material, CabControls.HeatGauge, SocketPosition("Socket_HeatGauge", HeatGaugeFallbackPosition), matched ? HeatGaugeColliderSize : GaugeSize);
            OneWay(rootTransform, train, engine, material, CabControls.WaterGlass, SocketPosition("Socket_WaterGlass", WaterGlassFallbackPosition), WaterGlassColliderSize);
            OneWay(rootTransform, train, engine, material, CabControls.TenderGauge, SocketPosition("Socket_TenderGauge", TenderGaugeFallbackPosition), matched ? TenderGaugeColliderSize : GaugeSize);
            OneWay(rootTransform, train, engine, material, CabControls.Pump, SocketPosition("Socket_Pump", PumpFallbackPosition), matched ? SmallValveColliderSize : KnobSize);
            OneWay(rootTransform, train, engine, material, CabControls.Damper, SocketPosition("Socket_Damper", DamperFallbackPosition), DamperColliderSize);
            OneWay(rootTransform, train, engine, material, CabControls.Headlight, SocketPosition("Socket_HeadlightSwitch", HeadlightSwitchFallbackPosition), HeadlightSwitchColliderSize);
            if (HasModel)
            {
                OneWay(rootTransform, train, engine, material, CabControls.CabLight, SocketPosition("Socket_CabLightSwitch", CabLightSwitchFallbackPosition), CabLightSwitchColliderSize);
                OneWay(rootTransform, train, engine, material, CabControls.Bed, SocketPosition("Socket_Bed", BedFallbackPosition), BedColliderSize);
                BuildCabLights(rootTransform);
            }
            BuildFirebox(rootTransform, train, material);
            BuildLamps(rootTransform);

            // Outside: water tank filler and the coal bunker's rear hatch (snow).
            var hatch = HasModel ? HatchSizeWithModel : HatchSizeWithoutModel;
            Outside(rootTransform, train, material, CabControls.WaterHatch, SocketPosition("Socket_WaterHatch", WaterHatchFallbackPosition), hatch);
            Outside(rootTransform, train, material, CabControls.SnowHatch, SocketPosition("Socket_SnowHatch", SnowHatchFallbackPosition), HasModel ? SnowHatchColliderSize : hatch);

            // Repairs are done from outside: the boiler barrel (both sides) and the fusible plug on the boiler's top.
            Outside(rootTransform, train, material, CabControls.BoilerRepairLeft, SocketPosition("Socket_BoilerRepairLeft", BoilerRepairLeftFallbackPosition), BoilerRepairColliderSize);
            Outside(rootTransform, train, material, CabControls.BoilerRepairRight, SocketPosition("Socket_BoilerRepairRight", BoilerRepairRightFallbackPosition), BoilerRepairColliderSize);
            Outside(rootTransform, train, material, CabControls.PlugRepair, TrainConfig.PlugRepairPosition, PlugRepairColliderSize);

            var horn = MakeCabControl(rootTransform, "Ctl_Whistle", SocketPosition("Socket_Whistle", WhistleControlFallbackPosition), LeverSize, material).AddComponent<SteamTrainHorn>();
            SetupCabControl(horn, train, engine, "GAMEPLAY_Horn");
            horn.m_ObjectAnims = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectAnim>(0);
        }

        /// Headlamp on the smokebox and a lamp on the bunker back: a spot light down the line and a small glow
        /// in front of the lens (LocomotiveAnimator switches them).
        static void BuildLamps(Transform rootTransform)
        {
            Lamp(rootTransform, "HeadLamp_Front", SocketPosition("Socket_HeadLamp", HeadLampFallbackPosition), 0f);
            Lamp(rootTransform, "HeadLamp_Rear", SocketPosition("Socket_RearLamp", RearLampFallbackPosition), RearLampYawInDegrees);
        }

        /// The cab's small ceiling lamp (switched by the player, powered by boiler steam).
        static void BuildCabLights(Transform rootTransform)
        {
            AddCabLight(rootTransform, "CabLight", SocketPosition("Socket_CabLight", CabLightFallbackPosition), TrainConfig.CabLampColor, TrainConfig.CabLampRange);
        }

        static void AddCabLight(Transform rootTransform, string name, Vector3 position, Color color, float range)
        {
            var light = Il2CppHelpers.Child(rootTransform, name, position).AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
        }

        static void Lamp(Transform rootTransform, string name, Vector3 position, float yaw)
        {
            var targetObject = Il2CppHelpers.Child(rootTransform, name, position);
            targetObject.transform.localRotation = Quaternion.Euler(LampPitchInDegrees, yaw, 0f);      // aimed a little down the track
            var spot = targetObject.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = LampRangeInMeters;
            spot.spotAngle = LampSpotAngleInDegrees;
            spot.color = LampColor;
            spot.intensity = 0f;
            spot.shadows = LightShadows.None;
            spot.enabled = false;
            var glow = Il2CppHelpers.Child(targetObject.transform, "Glow", LampGlowLocalPosition).AddComponent<Light>();
            glow.type = LightType.Point;
            glow.range = LampGlowRangeInMeters;
            glow.color = spot.color;
            glow.intensity = 0f;
            glow.shadows = LightShadows.None;
            glow.enabled = false;
            LampBeam.Create(targetObject.transform);
        }

        /// The firelight that falls out of the fire hole into the cab (the warmth is the stove's own HeatSource).
        static void BuildFirebox(Transform rootTransform, SteamTrain train, Material material)
        {
            var heat = Il2CppHelpers.Child(rootTransform, "FireHeat", SocketPosition("Socket_FireLight", FireLightFallbackPosition));
            var light = heat.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = FireLightColor;
            light.range = FireLightRangeInMeters;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
        }

        static void TwoWay(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material, string name, Vector3 position, Vector3 size)
        {
            var gearLever = MakeCabControl(rootTransform, name, position, size, material).AddComponent<SteamTrainGearLever>();
            SetupCabControl(gearLever, train, engine, name);
            gearLever.m_GearLeverUp = Il2CppHelpers.CreateWwiseEvent(); gearLever.m_GearLeverDown = Il2CppHelpers.CreateWwiseEvent(); gearLever.m_BrakesOnButPlayerTriedToShift = Il2CppHelpers.CreateWwiseEvent();
        }

        /// Cab interactions only work from inside the train; tank intakes are the game's outside interactions.
        static void Outside(Transform rootTransform, SteamTrain train, Material material, string name, Vector3 position, Vector3 size)
        {
            var waterIntake = MakeCabControl(rootTransform, name, position, size, material).AddComponent<SteamTrainWaterTankIntake>();
            waterIntake.m_Train = train;
            waterIntake.m_DefaultHoverText = Il2CppHelpers.Loc(name);
            waterIntake.m_TransferName = Il2CppHelpers.Loc(name);
            waterIntake.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            waterIntake.m_TransferLiquidAudioEvent = Il2CppHelpers.CreateWwiseEvent();
        }

        static void OneWay(Transform rootTransform, SteamTrain train, SteamTrainEngine engine, Material material, string name, Vector3 position, Vector3 size)
        {
            var brakeLever = MakeCabControl(rootTransform, name, position, size, material).AddComponent<SteamTrainBrakeLever>();
            SetupCabControl(brakeLever, train, engine, name);
            brakeLever.m_BrakeLeverOn = Il2CppHelpers.CreateWwiseEvent(); brakeLever.m_BrakeLeverOff = Il2CppHelpers.CreateWwiseEvent();
        }

        static GameObject MakeCabControl(Transform rootTransform, string name, Vector3 position, Vector3 size, Material material)
        {
            var targetObject = Il2CppHelpers.Child(rootTransform, name, position);
            targetObject.layer = LayerMask.NameToLayer("InteractiveProp");
            if (!HasModel) Il2CppHelpers.Cube(targetObject.transform, "Visual", Vector3.zero, size, material, false);
            targetObject.AddComponent<BoxCollider>().size = size + ControlColliderMargin;
            return targetObject;
        }

        static void SetupCabControl(SteamTrainCabinInteraction interaction, SteamTrain train, SteamTrainEngine engine, string locId)
        {
            interaction.m_Train = train;
            interaction.m_Engine = engine;
            interaction.m_DefaultHoverText = Il2CppHelpers.Loc(locId);
            interaction.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
        }

        static Vector3 s_StoragePosition;
        static Quaternion s_StorageRotation = Quaternion.identity;
        static Transform s_PinnedStorageRoot;
        static Transform s_PinnedStorage;
        static BoxCollider s_PinnedStorageBox;

        /// Keeps the cupboard's container where it was built (anything that still moves it is undone).
        public static void PinStorage(Transform trainRoot)
        {
            if (trainRoot == null) return;
            if (s_PinnedStorageRoot != trainRoot || s_PinnedStorage == null)
            {
                s_PinnedStorageRoot = trainRoot;
                s_PinnedStorage = trainRoot.Find("CabinStorage");
                s_PinnedStorageBox = s_PinnedStorage != null ? s_PinnedStorage.GetComponent<BoxCollider>() : null;
            }
            var transform = s_PinnedStorage;
            if (transform == null) return;
            if ((transform.localPosition - s_StoragePosition).sqrMagnitude > PinPositionToleranceSquaredInMeters || Quaternion.Angle(transform.localRotation, s_StorageRotation) > PinRotationToleranceInDegrees)
                transform.SetLocalPositionAndRotation(s_StoragePosition, s_StorageRotation);
            // the game's container code switches the trunk's colliders off every few seconds: the click box is kept on
            var box = s_PinnedStorageBox;
            if (box != null && !box.enabled) box.enabled = true;
        }

        /// SteamTrain.SerializeTrainCarData throws without a cabin container, so the cab gets a base-game trunk.
        /// Its contents are saved inside the train data; it starts empty and never rolls loot.
        static void BuildCabinStorage(Transform rootTransform, SteamTrain train, string trainGuid)
        {
            var prefab = Il2CppHelpers.LoadAsset<GameObject>(TrainConfig.StoragePrefab);
            if (prefab == null) return;
            var trunk = Object.Instantiate(prefab);
            trunk.name = "CabinStorage";
            trunk.transform.SetParent(rootTransform, false);
            trunk.transform.localPosition = SocketPosition("Socket_Storage", TrainConfig.StoragePosition);
            trunk.transform.localRotation = HasModel ? Quaternion.identity : Quaternion.Euler(0f, TrunkYawInDegreesWithoutModel, 0f);
            foreach (var objectGuid in trunk.GetComponentsInChildren<ObjectGuid>(true))
                objectGuid.SetRuntimeCachedPdid(trainGuid + "_" + objectGuid.gameObject.name);
            // A base-game trunk is a decoration: the game may pick it up, break it down or put it back at a saved
            // place. Here it only serves as the cupboard's container.
            foreach (var decoration in trunk.GetComponentsInChildren<DecorationItem>(true)) Object.Destroy(decoration);
            foreach (var placeable in trunk.GetComponentsInChildren<Il2CppTLD.Placement.Placeable>(true)) Object.Destroy(placeable);
            foreach (var breakDown in trunk.GetComponentsInChildren<BreakDown>(true)) Object.Destroy(breakDown);
            s_StoragePosition = trunk.transform.localPosition;
            s_StorageRotation = trunk.transform.localRotation;

            var container = trunk.GetComponentInChildren<Container>(true);
            if (container == null) return;
            if (HasModel)
            {
                // The model has a cupboard on the rear plate: the trunk only lends its Container and becomes an
                // invisible click box the size of the cupboard (LocomotiveAnimator swings the cupboard door).
                foreach (var renderer in trunk.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                // The game picks the container from the object its own click collider sits on, so the new box goes
                // on that object (same layer); the trunk's other solid colliders are switched off.
                GameObject clickObject = null;
                foreach (var collider in trunk.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.isTrigger) continue;
                    if (clickObject == null) clickObject = collider.gameObject;
                    collider.enabled = false;
                }
                if (clickObject == null) clickObject = container.gameObject;
                var box = clickObject.AddComponent<BoxCollider>();
                var socket = rootTransform.TransformPoint(SocketPosition("Socket_Storage", TrainConfig.StoragePosition));
                box.center = clickObject.transform.InverseTransformPoint(socket);
                var lossyScale = clickObject.transform.lossyScale;
                box.size = new Vector3(CupboardWidthInMeters / Mathf.Max(lossyScale.x, MinimumColliderScale), CupboardHeightInMeters / Mathf.Max(lossyScale.y, MinimumColliderScale), CupboardDepthInMeters / Mathf.Max(lossyScale.z, MinimumColliderScale));
                // Inside the train only the cab interaction layer is ray-cast; put the cupboard on the same layer as the
                // cab controls (the trunk's own layer is ignored while the player is in the cab).
                clickObject.layer = LayerMask.NameToLayer("InteractiveProp");
                SteamRailwayMod.Debug($"Cupboard click box on {clickObject.name} (layer {clickObject.layer}).");
                container.m_LocalizedDisplayName = Il2CppHelpers.Loc("GAMEPLAY_Cupboard");
            }
            container.m_Capacity = ItemWeight.FromKilograms(TrainConfig.CupboardCapacityKilograms);
            container.m_NotPopulated = false;
            container.m_RolledSpawnChance = true;
            container.m_ChanceEmpty = EmptyContainerChance;
            container.m_MinRandomItems = 0;
            container.m_MaxRandomItems = 0;
            container.m_LootTablePrefab = null;
            container.m_LootTableData = null;
            container.m_LootTable = null;
            if (container.m_GearToInstantiate != null) container.m_GearToInstantiate.Clear();
            train.m_Container = container;
        }

        /// The unfolded folding bed is a base-game bed in disguise (meshes hidden, click box fitted to the bed), so the game's
        /// own sleep menu works on it. LocomotiveAnimator switches it on only while the bed is unfolded.
        static void BuildSleepBed(Transform rootTransform, string trainGuid)
        {
            var prefab = Il2CppHelpers.LoadAsset<GameObject>(TrainConfig.SleepBedPrefab);
            if (prefab == null) return;
            var bed = Object.Instantiate(prefab);
            bed.SetActive(false);
            bed.name = "SleepBed";
            bed.transform.SetParent(rootTransform, false);
            var bedCenter = SocketPosition("Socket_Bed", BedFallbackPosition);
            bed.transform.localPosition = new Vector3(bedCenter.x, bedCenter.y - SleepBedDropInMeters, bedCenter.z);
            bed.transform.localRotation = Quaternion.identity;
            foreach (var objectGuid in bed.GetComponentsInChildren<ObjectGuid>(true))
                objectGuid.SetRuntimeCachedPdid(trainGuid + "_" + objectGuid.gameObject.name);
            foreach (var decoration in bed.GetComponentsInChildren<DecorationItem>(true)) Object.Destroy(decoration);
            foreach (var placeable in bed.GetComponentsInChildren<Il2CppTLD.Placement.Placeable>(true)) Object.Destroy(placeable);
            foreach (var breakDown in bed.GetComponentsInChildren<BreakDown>(true)) Object.Destroy(breakDown);
            foreach (var renderer in bed.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            var lodGroup = bed.GetComponent<LODGroup>();
            if (lodGroup != null) lodGroup.enabled = false;
            GameObject clickObject = null;
            foreach (var collider in bed.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger) continue;
                if (clickObject == null) clickObject = collider.gameObject;
                collider.enabled = false;
            }
            if (clickObject == null) clickObject = bed;
            var box = clickObject.AddComponent<BoxCollider>();
            var worldCenter = rootTransform.TransformPoint(new Vector3(bedCenter.x, bedCenter.y - SleepBedClickBoxDropInMeters, bedCenter.z));
            box.center = clickObject.transform.InverseTransformPoint(worldCenter);
            var scale = clickObject.transform.lossyScale;
            box.size = new Vector3(TrainConfig.SleepBedSize.x / Mathf.Max(scale.x, MinimumColliderScale), TrainConfig.SleepBedSize.y / Mathf.Max(scale.y, MinimumColliderScale), TrainConfig.SleepBedSize.z / Mathf.Max(scale.z, MinimumColliderScale));
            clickObject.layer = LayerMask.NameToLayer("InteractiveProp");
            bed.SetActive(false);
        }

        static void SetupComponent(SteamTrainComponent component, SteamTrain train, string locId)
        {
            component.m_Train = train;
            component.m_DefaultHoverText = Il2CppHelpers.Loc(locId);
            component.m_Description = Il2CppHelpers.Loc(locId + "Description");
            component.m_RepairConfigurations = new Il2CppSystem.Collections.Generic.List<SteamTrainRepair>();
        }
    }
}
