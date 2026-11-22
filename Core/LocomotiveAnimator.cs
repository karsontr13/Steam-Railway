using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Drives the model's moving parts from the simulation (train-local space, +Z forward).
    /// Constants and object names must match Blender/build_locomotive.py.
    internal sealed class LocomotiveAnimator
    {
        const float DriveRadius = 0.70f, CrankRadius = 0.28f, CylinderY = 0.95f, ConnRodLength = 2.0f;
        const float PressureFullScale = 250f, SpeedFullScale = 60f, HeatFullScale = 600f, DialSweep = 270f;
        const float GlassBottomLevel = 0.10f;
        const float TankScaleTravel = 0.44f * 0.78f;       // TANK plate height x the painted scale's span
        const float RegulatorSweep = 120f;                  // quadrant -60 .. +60
        const float ReverserLean = 9.5f;                    // lever top over the GERİ / İLERİ notches
        const float BrakeSwing = -55f, HandBrakeTurn = 160f, ValveTurn = 90f, FireDoorOpenAngle = 100f;
        const float WhistlePull = 0.05f;                    // less than the rope's 0.06 m of length hidden inside the valve body: no gap opens
        const float BedSleepableAngle = 3f;
        static readonly Vector3 BedFoldedControlSize = new Vector3(0.2f, 0.74f, 1.4f);       // only the folded panel against the wall
        static readonly Vector3 BedFoldedControlCenter = new Vector3(-0.32f, 0.05f, 0f);   // from the bed socket (mid-bed when unfolded)
        static readonly Vector3 BedHandleControlSize = new Vector3(0.2f, 0.2f, 0.8f);
        static readonly Vector3 BedHandleControlCenter = new Vector3(0.37f, -0.3f, 0f);   // from the bed socket (mid-bed) to the handle on the free edge
        const float BedFoldedAngle = 90f, BedEasing = 4f;   // the bed swings up against the left wall about the z axis
        const float CabLightIntensity = 1.1f, LightFadeSpeed = 3f;
        const float LockerOpenAngle = -105f;                // cupboard door swings out into the cab
        const float CabDoorOpenAngle = -62f;                 // right cab door swings outwards
        const float DoorLeavingOpenSeconds = 1.4f, DoorEasing = 7f;
        const float HeadlightLever = 40f, DamperLean = 55f;
        const float SpotIntensity = 3.4f, GlowIntensity = 2.2f;
        const float DoorEnteringOpenSeconds = 4f;
        const float MetersPerSecondToKilometersPerHour = 3.6f;
        const float MinimumWheelRadiusInMeters = 0.1f;
        const float PointerEasingInMetersPerSecond = 0.2f;
        const float MinimumGlassScale = 0.001f;
        const float RegulatorSpeed = 240f;
        const float ReverserSpeed = 40f;
        const float BrakeSpeed = 220f;
        const float WheelAndValveSpeed = 300f;
        const float WhistleSpeed = 0.6f;
        const float FireDoorSpeed = 260f;
        const float LockerSpeed = 240f;
        const float HeadlightSpeed = 400f;
        const float DamperSpeed = 200f;
        const float DialSpeed = 180f;
        const float DoorSoundVolume = 1f;
        const float DoorOpenPitch = 0.7f;
        const float DoorClosePitch = 0.9f;
        const float LampWarmUpPerSecond = 2.5f;
        const float LampOffThreshold = 0.001f;
        const float CabLampOffThreshold = 0.01f;
        static readonly string[] SideSuffixes = { "L", "R" };
        static readonly string[] RodNames = { "Rod_L", "Rod_R" };
        static readonly string[] CrossheadNames = { "Crosshead_L", "Crosshead_R" };
        static readonly string[] ConnectingRodNames = { "ConnRod_L", "ConnRod_R" };
        static readonly string[] WaterLevelNames = { "WaterLevel_L", "WaterLevel_R" };

        static float s_DoorOpenUntil;
        static bool s_DoorForEntering;
        bool m_DoorWasOpen;

        /// The player uses the door. Going in, the door is open only until the player is inside; going out, it
        /// stays open a moment while they step out.
        public static void SwingDoor(bool entering)
        {
            s_DoorForEntering = entering;
            s_DoorOpenUntil = Time.time + (entering ? DoorEnteringOpenSeconds : DoorLeavingOpenSeconds);
        }

        sealed class Part { public Transform PartTransform; public Vector3 LocalPosition; public Quaternion LocalRotation; public float Radius; public Vector3 InitialDirection; public float Value; }

        /// Snow comes in stages modelled over the engine's surfaces (Blender/snow_cover.py): Snow_Top_S1..S4 settled on top,
        /// Snow_Front_S1..S4 and Snow_Pilot_S1..S4 driven onto the front by the run. One stage of each group shows at a time.
        sealed class SnowLayer { public Transform LayerTransform; public bool Driven; public int Stage; }
        const int SnowStageCount = 4;                  // keep in sync with SNOW_STAGES in the Blender build
        const string SnowStageMarker = "_S";
        const float SnowVisibleAmount = 0.02f;         // below this no stage shows
        const float SnowEasingPerSecond = 0.5f;        // the stage follows the simulated cover smoothly

        static SnowLayer CreateSnowLayer(Transform transform)
        {
            string name = transform.name;
            int marker = name.LastIndexOf(SnowStageMarker);
            int stage = 0;
            if (marker > 0) int.TryParse(name.Substring(marker + SnowStageMarker.Length), out stage);
            return new SnowLayer
            {
                LayerTransform = transform,
                Driven = name.StartsWith("Snow_Front") || name.StartsWith("Snow_Pilot"),
                Stage = stage,
            };
        }

        /// The stage that shows for a cover of 0..1 (0: none).
        static int SnowStageFor(float amount) =>
            amount <= SnowVisibleAmount ? 0 : Mathf.Clamp(Mathf.CeilToInt(amount * SnowStageCount), 1, SnowStageCount);
        readonly System.Collections.Generic.List<SnowLayer> m_SnowLayers = new System.Collections.Generic.List<SnowLayer>();
        float m_SnowTop, m_SnowFront;

        readonly Transform m_Root;
        readonly List<Part> m_Wheels = new List<Part>();
        readonly Dictionary<string, Part> m_Named = new Dictionary<string, Part>();
        readonly GameObject m_Glow;
        readonly float m_MainZ;      // z of the axle the connecting rods drive (read from the crosshead's rest place)
        Light m_FrontSpot, m_FrontGlow, m_RearSpot, m_RearGlow;
        MeshRenderer m_FrontBeam, m_RearBeam;
        float m_Angle;
        bool m_BedStarted, m_BacklightTried;
        int m_BedControlState = -1;
        Transform m_SleepBed;
        BoxCollider m_BedControl;
        GaugeBacklight m_GaugeBacklight;
        Light m_CabLamp;

        LocomotiveAnimator(Transform root, Transform model)
        {
            m_Root = root;
            foreach (var transform in model.GetComponentsInChildren<Transform>(true))
            {
                var part = new Part { PartTransform = transform, LocalPosition = root.InverseTransformPoint(transform.position), LocalRotation = Quaternion.Inverse(root.rotation) * transform.rotation };
                string objectName = transform.name;
                if (objectName.StartsWith("Wheel_"))
                {
                    var meshFilter = transform.GetComponent<MeshFilter>();
                    var extents = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.bounds.extents : Vector3.one * DriveRadius;
                    part.Radius = Mathf.Max(Mathf.Min(extents.x, extents.y), Mathf.Min(Mathf.Max(extents.x, extents.y), extents.z));
                    m_Wheels.Add(part);
                }
                else if (objectName.StartsWith("ConnRod_")) { part.InitialDirection = ConnRodDir(0f); m_Named[objectName] = part; }
                else if (objectName.StartsWith("Snow_"))
                    m_SnowLayers.Add(CreateSnowLayer(transform));
                else if (objectName == "FireboxGlow") m_Glow = transform.gameObject;
                else if (objectName.StartsWith("Rod_") || objectName.StartsWith("Crosshead_") || objectName.StartsWith("Needle_") || objectName.StartsWith("WaterLevel_")
                         || objectName.StartsWith("Lever_") || objectName.StartsWith("Valve_") || objectName.StartsWith("FireDoor_") || objectName == "HandBrakeWheel" || objectName == "Pointer_Tank" || objectName == "LockerDoor"
                         || objectName == "CabDoor_R" || objectName == "Bed_Frame" || objectName == "Bed_Legs")
                    m_Named[objectName] = part;
            }
            if (m_Named.TryGetValue("Crosshead_L", out var crosshead)) m_MainZ = crosshead.LocalPosition.z - CrossheadZ(0f);
        }

        public static LocomotiveAnimator TryCreate(Transform trainRoot)
        {
            var model = trainRoot.Find("Model");
            return model != null ? new LocomotiveAnimator(trainRoot, model) : null;
        }

        static Vector2 CrankPin(float firstValue) => new Vector2(DriveRadius + CrankRadius * Mathf.Cos(firstValue), CrankRadius * Mathf.Sin(firstValue));   // (y, z)

        static float CrossheadZ(float firstValue)
        {
            var pinPoint = CrankPin(firstValue);
            float deltaY = CylinderY - pinPoint.x;
            return pinPoint.y + Mathf.Sqrt(ConnRodLength * ConnRodLength - deltaY * deltaY);
        }

        static Vector3 ConnRodDir(float firstValue)
        {
            var pinPoint = CrankPin(firstValue);
            return new Vector3(0f, CylinderY - pinPoint.x, CrossheadZ(firstValue) - pinPoint.y).normalized;
        }

        void Place(Part part, Vector3 localPosition, Quaternion localRotation)
        {
            part.PartTransform.position = m_Root.TransformPoint(localPosition);
            part.PartTransform.rotation = m_Root.rotation * localRotation;
        }

        bool Get(string name, out Part part) => m_Named.TryGetValue(name, out part) && part.PartTransform != null;

        /// Eases a part's stored value towards a target (degrees or metres per second).
        static float Ease(Part part, float target, float speed, float deltaTime) => part.Value = Mathf.MoveTowards(part.Value, target, speed * deltaTime);

        void Turn(string name, float target, Vector3 axis, float speed, float deltaTime)
        {
            if (!Get(name, out var part)) return;
            Place(part, part.LocalPosition, Quaternion.AngleAxis(Ease(part, target, speed, deltaTime), axis) * part.LocalRotation);
        }

        /// A turn that slows down as it nears its target (a door settling, not a motor).
        void TurnEased(string name, float target, Vector3 axis, float rate, float deltaTime)
        {
            if (!Get(name, out var part)) return;
            part.Value = Mathf.Lerp(part.Value, target, 1f - Mathf.Exp(-rate * deltaTime));
            Place(part, part.LocalPosition, Quaternion.AngleAxis(part.Value, axis) * part.LocalRotation);
        }

        public void Update(SteamSimulation simulation, float deltaTime)
        {
            var simulationState = simulation.State;
            float velocity = simulation.Velocity;
            m_Angle = Mathf.Repeat(m_Angle + velocity * deltaTime / DriveRadius, Mathf.PI * 2f);

            // ---- running gear
            foreach (var part in m_Wheels)
            {
                if (part.PartTransform == null) continue;
                float wheelAngle = m_Angle * DriveRadius / Mathf.Max(part.Radius, MinimumWheelRadiusInMeters);
                Place(part, part.LocalPosition, Quaternion.AngleAxis(wheelAngle * Mathf.Rad2Deg, Vector3.right) * part.LocalRotation);
            }
            var crank = new Vector3(0f, Mathf.Cos(m_Angle) - 1f, Mathf.Sin(m_Angle)) * CrankRadius;
            for (int sideIndex = 0; sideIndex < SideSuffixes.Length; sideIndex++)
            {
                if (Get(RodNames[sideIndex], out var rod)) Place(rod, rod.LocalPosition + crank, rod.LocalRotation);
                if (Get(CrossheadNames[sideIndex], out var part)) Place(part, new Vector3(part.LocalPosition.x, part.LocalPosition.y, m_MainZ + CrossheadZ(m_Angle)), part.LocalRotation);
                if (Get(ConnectingRodNames[sideIndex], out var connectingRod))
                {
                    var pinPoint = CrankPin(m_Angle);
                    var start = new Vector3(connectingRod.LocalPosition.x, pinPoint.x, m_MainZ + pinPoint.y);
                    var end = new Vector3(connectingRod.LocalPosition.x, CylinderY, m_MainZ + CrossheadZ(m_Angle));
                    Place(connectingRod, (start + end) * 0.5f, Quaternion.FromToRotation(connectingRod.InitialDirection, (end - start).normalized) * connectingRod.LocalRotation);
                }
            }

            // ---- gauges (needles move quickly but not instantly, like a real gauge)
            Dial("Needle_Pressure", simulation.Pressure / PressureFullScale, deltaTime);
            Dial("Needle_Speed", Mathf.Abs(velocity) * MetersPerSecondToKilometersPerHour / SpeedFullScale, deltaTime);
            Dial("Needle_Heat", simulation.FireboxTemp / HeatFullScale, deltaTime);
            if (Get("Pointer_Tank", out var tank))
                Place(tank, tank.LocalPosition + Vector3.up * Ease(tank, TankScaleTravel * Mathf.Clamp01(simulationState.TenderWater / SteamSimulation.TenderCapacity), PointerEasingInMetersPerSecond, deltaTime), tank.LocalRotation);
            float glass = Mathf.Clamp01((simulation.BoilerLevel - GlassBottomLevel) / (1f - GlassBottomLevel));
            for (int sideIndex = 0; sideIndex < WaterLevelNames.Length; sideIndex++)
                if (Get(WaterLevelNames[sideIndex], out var waterLevel)) waterLevel.PartTransform.localScale = new Vector3(1f, Mathf.Max(MinimumGlassScale, glass), 1f);

            // ---- controls follow their state
            Turn("Lever_Regulator", -RegulatorSweep / 2f + RegulatorSweep * simulationState.Regulator, Vector3.back, RegulatorSpeed, deltaTime);
            Turn("Lever_Reverser", simulationState.Reverser * ReverserLean, Vector3.right, ReverserSpeed, deltaTime);
            Turn("Lever_Brake", simulationState.SteamBrake ? BrakeSwing : 0f, Vector3.up, BrakeSpeed, deltaTime);
            Turn("HandBrakeWheel", simulationState.HandBrake ? HandBrakeTurn : 0f, Vector3.up, WheelAndValveSpeed, deltaTime);
            Turn("Valve_Injector", simulationState.Injector ? ValveTurn : 0f, Vector3.forward, WheelAndValveSpeed, deltaTime);
            Turn("Valve_Blower", simulationState.Blower ? ValveTurn : 0f, Vector3.forward, WheelAndValveSpeed, deltaTime);
            Turn("Valve_Pump", simulationState.Pump ? ValveTurn : 0f, Vector3.back, WheelAndValveSpeed, deltaTime);
            if (Get("Lever_Whistle", out var whistleLever))
                Place(whistleLever, whistleLever.LocalPosition + Vector3.down * Ease(whistleLever, simulation.Train.m_SteamWhistleOpen ? WhistlePull : 0f, WhistleSpeed, deltaTime), whistleLever.LocalRotation);
            float door = simulation.FireDoorOpen ? FireDoorOpenAngle : 0f;
            Turn("FireDoor_L", door, Vector3.up, FireDoorSpeed, deltaTime);
            Turn("FireDoor_R", -door, Vector3.up, FireDoorSpeed, deltaTime);
            Turn("LockerDoor", simulation.LockerOpen ? LockerOpenAngle : 0f, Vector3.up, LockerSpeed, deltaTime);
            bool doorOpen = Time.time < s_DoorOpenUntil && !(s_DoorForEntering && simulation.Train.IsInsideTrain);
            if (doorOpen != m_DoorWasOpen)
            {
                m_DoorWasOpen = doorOpen;
                if (!TrainSounds.PlayDoor(simulation.Train, doorOpen))                       // the game's metal door sound; the generated one only if that fails
                    SynthSounds.PlayCab(simulation.Train, doorOpen ? SynthSounds.DoorOpen : SynthSounds.DoorClose, DoorSoundVolume, doorOpen ? DoorOpenPitch : DoorClosePitch);
            }
            TurnEased("CabDoor_R", doorOpen ? CabDoorOpenAngle : 0f, Vector3.up, DoorEasing, deltaTime);
            // the switch swings sideways along the cab's front wall (towards the cab's middle = on)
            Turn("Lever_Headlight", simulationState.Headlight ? -HeadlightLever : HeadlightLever, Vector3.forward, HeadlightSpeed, deltaTime);

            // the damper lever leans over when the fire is out
            Turn("Lever_Damper", simulation.FireBurning ? 0f : DamperLean, Vector3.forward, DamperSpeed, deltaTime);

            UpdateBed(simulationState.BedOpen, deltaTime);
            UpdateCabLights(simulationState.CabLight && simulation.Pressure > SteamSimulation.LampPressure, deltaTime);
            UpdateSnow(simulationState, deltaTime);   // the lamp runs on boiler steam like the headlamps
            TrainBuilder.PinStorage(m_Root);

            // ---- lamps: the one at the leading end (both with the reverser in neutral)
            if (m_FrontSpot == null) FindLamps();
            bool lit = simulation.LampsLit;
            Lamp(m_FrontSpot, m_FrontGlow, m_FrontBeam, lit && simulationState.Reverser >= 0, deltaTime);
            Lamp(m_RearSpot, m_RearGlow, m_RearBeam, lit && simulationState.Reverser <= 0, deltaTime);

            if (m_Glow != null && m_Glow.activeSelf != simulation.FireBurning) m_Glow.SetActive(simulation.FireBurning);
        }

        /// Eases the snow layers towards the simulated covers.
        void UpdateSnow(SteamSimulationState simulationState, float deltaTime)
        {
            if (m_SnowLayers.Count == 0) return;
            m_SnowTop = Mathf.MoveTowards(m_SnowTop, simulationState.SnowOnTop, SnowEasingPerSecond * deltaTime);
            m_SnowFront = Mathf.MoveTowards(m_SnowFront, simulationState.SnowOnFront, SnowEasingPerSecond * deltaTime);
            int topStage = SnowStageFor(m_SnowTop), drivenStage = SnowStageFor(m_SnowFront);
            foreach (var layer in m_SnowLayers)
            {
                if (layer.LayerTransform == null) continue;
                bool visible = layer.Stage == (layer.Driven ? drivenStage : topStage);
                if (layer.LayerTransform.gameObject.activeSelf != visible) layer.LayerTransform.gameObject.SetActive(visible);
            }
        }

        /// The bed's frame swings up against the wall; its legs always hang down from the frame's free edge.
        void UpdateBed(bool open, float deltaTime)
        {
            if (!Get("Bed_Frame", out var frame)) return;
            frame.Value = Mathf.Lerp(frame.Value, open ? 0f : BedFoldedAngle, 1f - Mathf.Exp(-BedEasing * deltaTime));
            if (!m_BedStarted) { frame.Value = open ? 0f : BedFoldedAngle; m_BedStarted = true; }
            var swing = Quaternion.AngleAxis(frame.Value, Vector3.forward);
            Place(frame, frame.LocalPosition, swing * frame.LocalRotation);
            if (Get("Bed_Legs", out var legs)) Place(legs, frame.LocalPosition + swing * (legs.LocalPosition - frame.LocalPosition), legs.LocalRotation);

            // unfolded: the fold control shrinks to the bed's free edge; folded: it covers the wall panel
            bool sleepable = open && frame.Value < BedSleepableAngle;
            if (m_SleepBed == null) m_SleepBed = m_Root.Find("SleepBed");
            if (m_SleepBed != null && m_SleepBed.gameObject.activeSelf != sleepable) m_SleepBed.gameObject.SetActive(sleepable);
            if (m_BedControl == null) m_BedControl = m_Root.Find(CabControls.Bed)?.GetComponent<BoxCollider>();
            int controlState = sleepable ? 1 : 0;
            if (m_BedControl != null && controlState != m_BedControlState)
            {
                m_BedControlState = controlState;
                m_BedControl.size = sleepable ? BedHandleControlSize : BedFoldedControlSize;
                m_BedControl.center = sleepable ? BedHandleControlCenter : BedFoldedControlCenter;
            }
        }

        /// The switchable cab lamp (needs steam pressure).
        void UpdateCabLights(bool lampOn, float deltaTime)
        {
            if (m_GaugeBacklight == null && !m_BacklightTried && Get("Needle_Pressure", out var pressureNeedle) && Get("Needle_Speed", out var speedNeedle))
            {
                m_BacklightTried = true;
                m_GaugeBacklight = GaugeBacklight.Create(m_Root, pressureNeedle.PartTransform, speedNeedle.PartTransform);
            }
            m_GaugeBacklight?.Update();
            if (m_CabLamp == null) m_CabLamp = m_Root.Find("CabLight")?.GetComponent<Light>();
            if (m_CabLamp != null)
            {
                m_CabLamp.intensity = Mathf.MoveTowards(m_CabLamp.intensity, lampOn ? CabLightIntensity : 0f, LightFadeSpeed * deltaTime);
                m_CabLamp.enabled = m_CabLamp.intensity > CabLampOffThreshold;
            }
        }

        void FindLamps()
        {
            var frameTransform = m_Root.Find("HeadLamp_Front");
            var rootTransform = m_Root.Find("HeadLamp_Rear");
            if (frameTransform != null) { m_FrontSpot = frameTransform.GetComponent<Light>(); m_FrontGlow = frameTransform.Find("Glow")?.GetComponent<Light>(); m_FrontBeam = frameTransform.Find("Beam")?.GetComponent<MeshRenderer>(); }
            if (rootTransform != null) { m_RearSpot = rootTransform.GetComponent<Light>(); m_RearGlow = rootTransform.Find("Glow")?.GetComponent<Light>(); m_RearBeam = rootTransform.Find("Beam")?.GetComponent<MeshRenderer>(); }
        }

        static void Lamp(Light spot, Light glow, MeshRenderer beam, bool lit, float deltaTime)
        {
            if (spot == null) return;
            // a dynamo lamp warms up and fades out over a moment
            float scale = Mathf.MoveTowards(spot.intensity / SpotIntensity, lit ? 1f : 0f, deltaTime * LampWarmUpPerSecond);
            spot.intensity = scale * SpotIntensity;
            spot.enabled = scale > LampOffThreshold;
            if (glow != null) { glow.intensity = scale * GlowIntensity; glow.enabled = spot.enabled; }
            LampBeam.Set(beam, scale);
        }

        void Dial(string name, float fraction, float deltaTime)
        {
            if (!Get(name, out var part)) return;
            float target = -DialSweep / 2f + DialSweep * Mathf.Clamp01(fraction);
            Place(part, part.LocalPosition, Quaternion.AngleAxis(Ease(part, target, DialSpeed, deltaTime), Vector3.back) * part.LocalRotation);
        }
    }
}
