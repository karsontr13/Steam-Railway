using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Drives the model's moving parts from the simulation (all maths in train-local space, +Z forward).
    /// Keep the constants and object names in sync with Blender/build_locomotive.py:
    ///   Wheel_*                    spin about their axle; radius is read from the mesh
    ///   Rod_L/R, ConnRod_*, Crosshead_*   coupling rods, connecting rods and crossheads
    ///   Needle_Pressure/Speed/Heat dials read looking +Z (clockwise = about -Z)
    ///   Pointer_Tank               slides up the TANK scale on the cab's rear wall
    ///   WaterLevel_L/R             water column in the gauge glasses
    ///   Lever_Regulator            swings over its quadrant (0 .. 10)
    ///   Lever_Reverser             leans forward (İLERİ) / back (GERİ)
    ///   Lever_Brake, HandBrakeWheel, Valve_*   show their on/off state
    ///   Lever_Whistle              pulled down while the whistle blows
    ///   FireDoor_L/R               swing open while the fireman works the fire
    internal sealed class LocomotiveAnimator
    {
        const float DriveRadius = 0.70f, CrankRadius = 0.28f, CylinderY = 0.95f, ConnRodLength = 2.0f;
        const float PressureFullScale = 250f, SpeedFullScale = 60f, HeatFullScale = 600f, DialSweep = 270f;
        const float GlassBottomLevel = 0.10f;
        const float TankScaleTravel = 0.44f * 0.78f;       // TANK plate height x the painted scale's span
        const float RegulatorSweep = 120f;                  // quadrant -60 .. +60
        const float ReverserLean = 9.5f;                    // lever top over the GERİ / İLERİ notches
        const float BrakeSwing = -55f, HandBrakeTurn = 160f, ValveTurn = 90f, FireDoorOpenAngle = 100f;
        const float WhistlePull = 0.06f;
        const float LockerOpenAngle = -105f;                // cupboard door swings out into the cab
        const float CabDoorOpenAngle = -100f, CabDoorSwingSeconds = 1.8f;   // right cab door swings outwards
        const float HeadlightLever = 40f, DamperLean = 55f;
        const float SpotIntensity = 2.6f, GlowIntensity = 2.2f;

        static float s_DoorOpenUntil;

        /// Swings the right cab door open and shut again (called when the player uses the door).
        public static void SwingDoor() => s_DoorOpenUntil = Time.time + CabDoorSwingSeconds;

        sealed class Part { public Transform T; public Vector3 Pos; public Quaternion Rot; public float Radius; public Vector3 Dir0; public float Value; }

        readonly Transform m_Root;
        readonly List<Part> m_Wheels = new List<Part>();
        readonly Dictionary<string, Part> m_Named = new Dictionary<string, Part>();
        readonly GameObject m_Glow;
        readonly float m_MainZ;      // z of the axle the connecting rods drive (read from the crosshead's rest place)
        Light m_FrontSpot, m_FrontGlow, m_RearSpot, m_RearGlow;
        MeshRenderer m_FrontBeam, m_RearBeam;
        float m_Angle;

        LocomotiveAnimator(Transform root, Transform model)
        {
            m_Root = root;
            foreach (var transform in model.GetComponentsInChildren<Transform>(true))
            {
                var part = new Part { T = transform, Pos = root.InverseTransformPoint(transform.position), Rot = Quaternion.Inverse(root.rotation) * transform.rotation };
                string n = transform.name;
                if (n.StartsWith("Wheel_"))
                {
                    var meshFilter = transform.GetComponent<MeshFilter>();
                    var e = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.bounds.extents : Vector3.one * DriveRadius;
                    part.Radius = Mathf.Max(Mathf.Min(e.x, e.y), Mathf.Min(Mathf.Max(e.x, e.y), e.z));
                    m_Wheels.Add(part);
                }
                else if (n.StartsWith("ConnRod_")) { part.Dir0 = ConnRodDir(0f); m_Named[n] = part; }
                else if (n == "FireboxGlow") m_Glow = transform.gameObject;
                else if (n.StartsWith("Rod_") || n.StartsWith("Crosshead_") || n.StartsWith("Needle_") || n.StartsWith("WaterLevel_")
                         || n.StartsWith("Lever_") || n.StartsWith("Valve_") || n.StartsWith("FireDoor_") || n == "HandBrakeWheel" || n == "Pointer_Tank" || n == "LockerDoor"
                         || n == "CabDoor_R")
                    m_Named[n] = part;
            }
            if (m_Named.TryGetValue("Crosshead_L", out var ch)) m_MainZ = ch.Pos.z - CrossheadZ(0f);
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

        void Place(Part part, Vector3 localPos, Quaternion localRot)
        {
            part.T.position = m_Root.TransformPoint(localPos);
            part.T.rotation = m_Root.rotation * localRot;
        }

        bool Get(string name, out Part part) => m_Named.TryGetValue(name, out part) && part.T != null;

        /// Eases a part's stored value towards a target (degrees or metres per second).
        static float Ease(Part part, float target, float speed, float deltaTime) => part.Value = Mathf.MoveTowards(part.Value, target, speed * deltaTime);

        void Turn(string name, float target, Vector3 axis, float speed, float deltaTime)
        {
            if (!Get(name, out var part)) return;
            Place(part, part.Pos, Quaternion.AngleAxis(Ease(part, target, speed, deltaTime), axis) * part.Rot);
        }

        public void Update(SteamSim simulation, float deltaTime)
        {
            var simState = simulation.S;
            float value = simulation.Velocity;
            m_Angle = Mathf.Repeat(m_Angle + value * deltaTime / DriveRadius, Mathf.PI * 2f);

            // ---- running gear
            foreach (var part in m_Wheels)
            {
                if (part.T == null) continue;
                float wheelAngle = m_Angle * DriveRadius / Mathf.Max(part.Radius, 0.1f);
                Place(part, part.Pos, Quaternion.AngleAxis(wheelAngle * Mathf.Rad2Deg, Vector3.right) * part.Rot);
            }
            var crank = new Vector3(0f, Mathf.Cos(m_Angle) - 1f, Mathf.Sin(m_Angle)) * CrankRadius;
            foreach (var side in new[] { "L", "R" })
            {
                if (Get("Rod_" + side, out var rod)) Place(rod, rod.Pos + crank, rod.Rot);
                if (Get("Crosshead_" + side, out var part)) Place(part, new Vector3(part.Pos.x, part.Pos.y, m_MainZ + CrossheadZ(m_Angle)), part.Rot);
                if (Get("ConnRod_" + side, out var cr))
                {
                    var pinPoint = CrankPin(m_Angle);
                    var start = new Vector3(cr.Pos.x, pinPoint.x, m_MainZ + pinPoint.y);
                    var end = new Vector3(cr.Pos.x, CylinderY, m_MainZ + CrossheadZ(m_Angle));
                    Place(cr, (start + end) * 0.5f, Quaternion.FromToRotation(cr.Dir0, (end - start).normalized) * cr.Rot);
                }
            }

            // ---- gauges (needles move quickly but not instantly, like a real gauge)
            Dial("Needle_Pressure", simulation.Pressure / PressureFullScale, deltaTime);
            Dial("Needle_Speed", Mathf.Abs(value) * 3.6f / SpeedFullScale, deltaTime);
            Dial("Needle_Heat", simulation.FireboxTemp / HeatFullScale, deltaTime);
            if (Get("Pointer_Tank", out var tank))
                Place(tank, tank.Pos + Vector3.up * Ease(tank, TankScaleTravel * Mathf.Clamp01(simState.TenderWater / SteamSim.TenderCapacity), 0.2f, deltaTime), tank.Rot);
            float glass = Mathf.Clamp01((simulation.BoilerLevel - GlassBottomLevel) / (1f - GlassBottomLevel));
            foreach (var side in new[] { "L", "R" })
                if (Get("WaterLevel_" + side, out var wheel)) wheel.T.localScale = new Vector3(1f, Mathf.Max(0.001f, glass), 1f);

            // ---- controls follow their state
            Turn("Lever_Regulator", -RegulatorSweep / 2f + RegulatorSweep * simState.Regulator, Vector3.back, 240f, deltaTime);
            Turn("Lever_Reverser", simState.Reverser * ReverserLean, Vector3.right, 40f, deltaTime);
            Turn("Lever_Brake", simState.SteamBrake ? BrakeSwing : 0f, Vector3.up, 220f, deltaTime);
            Turn("HandBrakeWheel", simState.HandBrake ? HandBrakeTurn : 0f, Vector3.up, 300f, deltaTime);
            Turn("Valve_Injector", simState.Injector ? ValveTurn : 0f, Vector3.forward, 300f, deltaTime);
            Turn("Valve_Blower", simState.Blower ? ValveTurn : 0f, Vector3.forward, 300f, deltaTime);
            Turn("Valve_Pump", simState.Pump ? ValveTurn : 0f, Vector3.back, 300f, deltaTime);
            if (Get("Lever_Whistle", out var wh))
                Place(wh, wh.Pos + Vector3.down * Ease(wh, simulation.Train.m_SteamWhistleOpen ? WhistlePull : 0f, 0.6f, deltaTime), wh.Rot);
            float door = simulation.FireDoorOpen ? FireDoorOpenAngle : 0f;
            Turn("FireDoor_L", door, Vector3.up, 260f, deltaTime);
            Turn("FireDoor_R", -door, Vector3.up, 260f, deltaTime);
            Turn("LockerDoor", simulation.LockerOpen ? LockerOpenAngle : 0f, Vector3.up, 240f, deltaTime);
            Turn("CabDoor_R", Time.time < s_DoorOpenUntil ? CabDoorOpenAngle : 0f, Vector3.up, 170f, deltaTime);
            // the switch swings sideways along the cab's front wall (towards the cab's middle = on)
            Turn("Lever_Headlight", simState.Headlight ? -HeadlightLever : HeadlightLever, Vector3.forward, 400f, deltaTime);

            // the damper lever leans over when the fire is out
            Turn("Lever_Damper", simulation.FireBurning ? 0f : DamperLean, Vector3.forward, 200f, deltaTime);

            TrainBuilder.PinStorage(m_Root);

            // ---- lamps: the one at the leading end (both with the reverser in neutral)
            if (m_FrontSpot == null) FindLamps();
            bool lit = simulation.LampsLit;
            Lamp(m_FrontSpot, m_FrontGlow, m_FrontBeam, lit && simState.Reverser >= 0, deltaTime);
            Lamp(m_RearSpot, m_RearGlow, m_RearBeam, lit && simState.Reverser <= 0, deltaTime);

            if (m_Glow != null && m_Glow.activeSelf != simulation.FireBurning) m_Glow.SetActive(simulation.FireBurning);
        }

        void FindLamps()
        {
            var frameTransform = m_Root.Find("HeadLamp_Front");
            var rootTransform = m_Root.Find("HeadLamp_Rear");
            if (frameTransform != null) { m_FrontSpot = frameTransform.GetComponent<Light>(); m_FrontGlow = frameTransform.Find("Glow")?.GetComponent<Light>(); m_FrontBeam = frameTransform.Find("Beam")?.GetComponent<MeshRenderer>(); }
            if (rootTransform != null) { m_RearSpot = rootTransform.GetComponent<Light>(); m_RearGlow = rootTransform.Find("Glow")?.GetComponent<Light>(); m_RearBeam = rootTransform.Find("Beam")?.GetComponent<MeshRenderer>(); }
        }

        static void Lamp(Light spot, Light glow, MeshRenderer beam, bool on, float deltaTime)
        {
            if (spot == null) return;
            // a dynamo lamp warms up and fades out over a moment
            float scale = Mathf.MoveTowards(spot.intensity / SpotIntensity, on ? 1f : 0f, deltaTime * 2.5f);
            spot.intensity = scale * SpotIntensity;
            spot.enabled = scale > 0.001f;
            if (glow != null) { glow.intensity = scale * GlowIntensity; glow.enabled = spot.enabled; }
            LampBeam.Set(beam, scale);
        }

        void Dial(string name, float fraction, float deltaTime)
        {
            if (!Get(name, out var part)) return;
            float target = -DialSweep / 2f + DialSweep * Mathf.Clamp01(fraction);
            Place(part, part.Pos, Quaternion.AngleAxis(Ease(part, target, 180f, deltaTime), Vector3.back) * part.Rot);
        }
    }
}
