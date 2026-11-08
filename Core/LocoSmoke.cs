using UnityEngine;

namespace SteamRailway
{
    /// Coal smoke from the chimney. A lazy plume while the fire burns at a stand (stronger with the blower on),
    /// and a dark blast on every exhaust beat while the engine works: four beats per driving wheel turn.
    internal sealed class LocoSmoke
    {
        const float WheelCircumference = 2f * 3.14159265f * 0.7f;
        const float BeatDistance = WheelCircumference / 4f;

        readonly ParticleSystem m_Ps;
        readonly Material m_Mat;
        float m_ToBeat;

        // coal smoke: near black at the chimney, a dark grey haze as it thins (multiplied with the particle colours)
        static readonly Color DayTint = new Color(0.36f, 0.35f, 0.34f, 0.5f);
        const float NightLight = 0.14f;      // fraction of the day's brightness left at night

        LocoSmoke(ParticleSystem ps, Material mat) { m_Ps = ps; m_Mat = mat; }

        /// An unlit alpha-blended particle shader that keeps the particles' own dark colours. The brightness follows
        /// the time of day in Update (the game's lit smoke shader turned the smoke white, even at night).
        internal static Shader FindBlendShader()
        {
            foreach (var n in new[] { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended" })
            {
                var sh = Shader.Find(n);
                if (sh != null) return sh;
            }
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.shader != null && (m.shader.name == "Legacy Shaders/Particles/Alpha Blended" || m.shader.name == "Particles/Alpha Blended"))
                    return m.shader;
            return null;
        }

        /// Builds the smoke at the chimney mouth; null when the bundle has no smoke (the old effect stays).
        public static LocoSmoke Create(Transform trainRoot, Vector3 localPos, Material litParticleMat)
        {
            if (!LocomotiveModel.SmokeAssets(out var prefab, out var sheet)) return null;
            var go = Object.Instantiate(prefab, trainRoot, false);
            go.name = "LocoSmoke";
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            Material mat = null;
            var shader = FindBlendShader();
            if (shader != null)
            {
                mat = new Material(shader) { name = "SteamRailway_Smoke", mainTexture = sheet };
                if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", DayTint);
                r.sharedMaterial = mat;
            }
            else if (litParticleMat != null) r.sharedMaterial = new Material(litParticleMat) { name = "SteamRailway_Smoke", mainTexture = sheet };
            else r.sharedMaterial.mainTexture = sheet;
            var ps = go.GetComponent<ParticleSystem>();
            ps.Play();
            return new LocoSmoke(ps, mat);
        }

        public void Update(SteamSim sim, float dt)
        {
            if (m_Ps == null) return;
            if (m_Mat != null)
            {
                float light = Mathf.Lerp(1f, NightLight, LampBeam.Darkness());
                var c = DayTint * light; c.a = DayTint.a;
                m_Mat.SetColor("_TintColor", c);
            }
            var s = sim.S;
            bool burning = sim.FireBurning && !s.Exploded;
            float fire = Mathf.Clamp01(s.FireLevel);
            float speed = Mathf.Abs(sim.Velocity);
            float pressure01 = Mathf.Clamp01(sim.Pressure / SteamSim.MaxPressure);

            // standing plume
            float idle = burning ? 1.5f + 4f * fire + (s.Blower ? 6f : 0f) : 0f;
            var em = m_Ps.emission;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(idle);

            // exhaust beats
            bool working = burning && s.Regulator > 0f && s.Reverser != 0 && sim.Pressure > 5f && speed > 0.02f;
            if (!working) { m_ToBeat = 0f; return; }
            m_ToBeat -= speed * dt;
            if (m_ToBeat > 0f) return;
            m_ToBeat += BeatDistance;
            if (m_ToBeat < 0f) m_ToBeat = BeatDistance;   // very fast: at most one beat per frame

            float effort = s.Regulator * Mathf.Lerp(0.5f, 1f, pressure01);
            var main = m_Ps.main;
            // hard, tall blasts when starting; shorter, flatter ones at speed
            float lift = Mathf.Lerp(7f, 4f, Mathf.Clamp01(speed / SteamSim.MaxSpeed)) * Mathf.Lerp(0.6f, 1.2f, effort);
            main.startSpeedMultiplier = lift / 3.75f;          // the prefab's 3..4.5 m/s, scaled
            m_Ps.Emit(Mathf.Clamp(Mathf.RoundToInt(1f + 3f * effort), 1, 4));
            main.startSpeedMultiplier = 1f;
        }
    }
}
