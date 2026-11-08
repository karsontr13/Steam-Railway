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
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                var p = new Part { T = t, Pos = root.InverseTransformPoint(t.position), Rot = Quaternion.Inverse(root.rotation) * t.rotation };
                string n = t.name;
                if (n.StartsWith("Wheel_"))
                {
                    var mf = t.GetComponent<MeshFilter>();
                    var e = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.extents : Vector3.one * DriveRadius;
                    p.Radius = Mathf.Max(Mathf.Min(e.x, e.y), Mathf.Min(Mathf.Max(e.x, e.y), e.z));
                    m_Wheels.Add(p);
                }
                else if (n.StartsWith("ConnRod_")) { p.Dir0 = ConnRodDir(0f); m_Named[n] = p; }
                else if (n == "FireboxGlow") m_Glow = t.gameObject;
                else if (n.StartsWith("Rod_") || n.StartsWith("Crosshead_") || n.StartsWith("Needle_") || n.StartsWith("WaterLevel_")
                         || n.StartsWith("Lever_") || n.StartsWith("Valve_") || n.StartsWith("FireDoor_") || n == "HandBrakeWheel" || n == "Pointer_Tank" || n == "LockerDoor"
                         || n == "CabDoor_R")
                    m_Named[n] = p;
            }
            if (m_Named.TryGetValue("Crosshead_L", out var ch)) m_MainZ = ch.Pos.z - CrossheadZ(0f);
        }

        public static LocomotiveAnimator TryCreate(Transform trainRoot)
        {
            var model = trainRoot.Find("Model");
            return model != null ? new LocomotiveAnimator(trainRoot, model) : null;
        }

        static Vector2 CrankPin(float a) => new Vector2(DriveRadius + CrankRadius * Mathf.Cos(a), CrankRadius * Mathf.Sin(a));   // (y, z)

        static float CrossheadZ(float a)
        {
            var pin = CrankPin(a);
            float dy = CylinderY - pin.x;
            return pin.y + Mathf.Sqrt(ConnRodLength * ConnRodLength - dy * dy);
        }

        static Vector3 ConnRodDir(float a)
        {
            var pin = CrankPin(a);
            return new Vector3(0f, CylinderY - pin.x, CrossheadZ(a) - pin.y).normalized;
        }

        void Place(Part p, Vector3 localPos, Quaternion localRot)
        {
            p.T.position = m_Root.TransformPoint(localPos);
            p.T.rotation = m_Root.rotation * localRot;
        }

        bool Get(string name, out Part p) => m_Named.TryGetValue(name, out p) && p.T != null;

        /// Eases a part's stored value towards a target (degrees or metres per second).
        static float Ease(Part p, float target, float speed, float dt) => p.Value = Mathf.MoveTowards(p.Value, target, speed * dt);

        void Turn(string name, float target, Vector3 axis, float speed, float dt)
        {
            if (!Get(name, out var p)) return;
            Place(p, p.Pos, Quaternion.AngleAxis(Ease(p, target, speed, dt), axis) * p.Rot);
        }

        public void Update(SteamSim sim, float dt)
        {
            var s = sim.S;
            float v = sim.Velocity;
            m_Angle = Mathf.Repeat(m_Angle + v * dt / DriveRadius, Mathf.PI * 2f);

            // ---- running gear
            foreach (var w in m_Wheels)
            {
                if (w.T == null) continue;
                float wheelAngle = m_Angle * DriveRadius / Mathf.Max(w.Radius, 0.1f);
                Place(w, w.Pos, Quaternion.AngleAxis(wheelAngle * Mathf.Rad2Deg, Vector3.right) * w.Rot);
            }
            var crank = new Vector3(0f, Mathf.Cos(m_Angle) - 1f, Mathf.Sin(m_Angle)) * CrankRadius;
            foreach (var side in new[] { "L", "R" })
            {
                if (Get("Rod_" + side, out var rod)) Place(rod, rod.Pos + crank, rod.Rot);
                if (Get("Crosshead_" + side, out var ch)) Place(ch, new Vector3(ch.Pos.x, ch.Pos.y, m_MainZ + CrossheadZ(m_Angle)), ch.Rot);
                if (Get("ConnRod_" + side, out var cr))
                {
                    var pin = CrankPin(m_Angle);
                    var a = new Vector3(cr.Pos.x, pin.x, m_MainZ + pin.y);
                    var b = new Vector3(cr.Pos.x, CylinderY, m_MainZ + CrossheadZ(m_Angle));
                    Place(cr, (a + b) * 0.5f, Quaternion.FromToRotation(cr.Dir0, (b - a).normalized) * cr.Rot);
                }
            }

            // ---- gauges (needles move quickly but not instantly, like a real gauge)
            Dial("Needle_Pressure", sim.Pressure / PressureFullScale, dt);
            Dial("Needle_Speed", Mathf.Abs(v) * 3.6f / SpeedFullScale, dt);
            Dial("Needle_Heat", sim.FireboxTemp / HeatFullScale, dt);
            if (Get("Pointer_Tank", out var tank))
                Place(tank, tank.Pos + Vector3.up * Ease(tank, TankScaleTravel * Mathf.Clamp01(s.TenderWater / SteamSim.TenderCapacity), 0.2f, dt), tank.Rot);
            float glass = Mathf.Clamp01((sim.BoilerLevel - GlassBottomLevel) / (1f - GlassBottomLevel));
            foreach (var side in new[] { "L", "R" })
                if (Get("WaterLevel_" + side, out var wl)) wl.T.localScale = new Vector3(1f, Mathf.Max(0.001f, glass), 1f);

            // ---- controls follow their state
            Turn("Lever_Regulator", -RegulatorSweep / 2f + RegulatorSweep * s.Regulator, Vector3.back, 240f, dt);
            Turn("Lever_Reverser", s.Reverser * ReverserLean, Vector3.right, 40f, dt);
            Turn("Lever_Brake", s.SteamBrake ? BrakeSwing : 0f, Vector3.up, 220f, dt);
            Turn("HandBrakeWheel", s.HandBrake ? HandBrakeTurn : 0f, Vector3.up, 300f, dt);
            Turn("Valve_Injector", s.Injector ? ValveTurn : 0f, Vector3.forward, 300f, dt);
            Turn("Valve_Blower", s.Blower ? ValveTurn : 0f, Vector3.forward, 300f, dt);
            Turn("Valve_Pump", s.Pump ? ValveTurn : 0f, Vector3.back, 300f, dt);
            if (Get("Lever_Whistle", out var wh))
                Place(wh, wh.Pos + Vector3.down * Ease(wh, sim.Train.m_SteamWhistleOpen ? WhistlePull : 0f, 0.6f, dt), wh.Rot);
            float door = sim.FireDoorOpen ? FireDoorOpenAngle : 0f;
            Turn("FireDoor_L", door, Vector3.up, 260f, dt);
            Turn("FireDoor_R", -door, Vector3.up, 260f, dt);
            Turn("LockerDoor", sim.LockerOpen ? LockerOpenAngle : 0f, Vector3.up, 240f, dt);
            Turn("CabDoor_R", Time.time < s_DoorOpenUntil ? CabDoorOpenAngle : 0f, Vector3.up, 170f, dt);
            // the switch swings sideways along the cab's front wall (towards the cab's middle = on)
            Turn("Lever_Headlight", s.Headlight ? -HeadlightLever : HeadlightLever, Vector3.forward, 400f, dt);

            // the damper lever leans over when the fire is out
            Turn("Lever_Damper", sim.FireBurning ? 0f : DamperLean, Vector3.forward, 200f, dt);

            TrainBuilder.PinStorage(m_Root);

            // ---- lamps: the one at the leading end (both with the reverser in neutral)
            if (m_FrontSpot == null) FindLamps();
            bool lit = sim.LampsLit;
            Lamp(m_FrontSpot, m_FrontGlow, m_FrontBeam, lit && s.Reverser >= 0, dt);
            Lamp(m_RearSpot, m_RearGlow, m_RearBeam, lit && s.Reverser <= 0, dt);

            if (m_Glow != null && m_Glow.activeSelf != sim.FireBurning) m_Glow.SetActive(sim.FireBurning);
        }

        void FindLamps()
        {
            var f = m_Root.Find("HeadLamp_Front");
            var r = m_Root.Find("HeadLamp_Rear");
            if (f != null) { m_FrontSpot = f.GetComponent<Light>(); m_FrontGlow = f.Find("Glow")?.GetComponent<Light>(); m_FrontBeam = f.Find("Beam")?.GetComponent<MeshRenderer>(); }
            if (r != null) { m_RearSpot = r.GetComponent<Light>(); m_RearGlow = r.Find("Glow")?.GetComponent<Light>(); m_RearBeam = r.Find("Beam")?.GetComponent<MeshRenderer>(); }
        }

        static void Lamp(Light spot, Light glow, MeshRenderer beam, bool on, float dt)
        {
            if (spot == null) return;
            // a dynamo lamp warms up and fades out over a moment
            float k = Mathf.MoveTowards(spot.intensity / SpotIntensity, on ? 1f : 0f, dt * 2.5f);
            spot.intensity = k * SpotIntensity;
            spot.enabled = k > 0.001f;
            if (glow != null) { glow.intensity = k * GlowIntensity; glow.enabled = spot.enabled; }
            LampBeam.Set(beam, k);
        }

        void Dial(string name, float fraction, float dt)
        {
            if (!Get(name, out var n)) return;
            float target = -DialSweep / 2f + DialSweep * Mathf.Clamp01(fraction);
            Place(n, n.Pos, Quaternion.AngleAxis(Ease(n, target, 180f, dt), Vector3.back) * n.Rot);
        }
    }
}
