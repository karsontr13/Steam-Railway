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

        LocoSmoke(ParticleSystem particleSystem, Material material) { m_Ps = particleSystem; m_Mat = material; }

        /// An unlit alpha-blended particle shader that keeps the particles' own dark colours. The brightness follows
        /// the time of day in Update (the game's lit smoke shader turned the smoke white, even at night).
        internal static Shader FindBlendShader()
        {
            foreach (var name in new[] { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended" })
            {
                var shader = Shader.Find(name);
                if (shader != null) return shader;
            }
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material != null && material.shader != null && (material.shader.name == "Legacy Shaders/Particles/Alpha Blended" || material.shader.name == "Particles/Alpha Blended"))
                    return material.shader;
            return null;
        }

        /// Builds the smoke at the chimney mouth; null when the bundle has no smoke (the old effect stays).
        public static LocoSmoke Create(Transform trainRoot, Vector3 localPos, Material litParticleMat)
        {
            if (!LocomotiveModel.SmokeAssets(out var prefab, out var sheet)) return null;
            var targetObject = Object.Instantiate(prefab, trainRoot, false);
            targetObject.name = "LocoSmoke";
            targetObject.transform.localPosition = localPos;
            targetObject.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var particleRenderer = targetObject.GetComponent<ParticleSystemRenderer>();
            Material material = null;
            var shader = FindBlendShader();
            if (shader != null)
            {
                material = new Material(shader) { name = "SteamRailway_Smoke", mainTexture = sheet };
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", DayTint);
                particleRenderer.sharedMaterial = material;
            }
            else if (litParticleMat != null) particleRenderer.sharedMaterial = new Material(litParticleMat) { name = "SteamRailway_Smoke", mainTexture = sheet };
            else particleRenderer.sharedMaterial.mainTexture = sheet;
            var particleSystem = targetObject.GetComponent<ParticleSystem>();
            particleSystem.Play();
            return new LocoSmoke(particleSystem, material);
        }

        public void Update(SteamSim simulation, float deltaTime)
        {
            if (m_Ps == null) return;
            if (m_Mat != null)
            {
                float light = Mathf.Lerp(1f, NightLight, LampBeam.Darkness());
                var color = DayTint * light; color.a = DayTint.a;
                m_Mat.SetColor("_TintColor", color);
            }
            var simState = simulation.S;
            bool burning = simulation.FireBurning && !simState.Exploded;
            float fire = Mathf.Clamp01(simState.FireLevel);
            float speed = Mathf.Abs(simulation.Velocity);
            float pressure01 = Mathf.Clamp01(simulation.Pressure / SteamSim.MaxPressure);

            // standing plume
            float idle = burning ? 1.5f + 4f * fire + (simState.Blower ? 6f : 0f) : 0f;
            var em = m_Ps.emission;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(idle);

            // exhaust beats
            bool working = burning && simState.Regulator > 0f && simState.Reverser != 0 && simulation.Pressure > 5f && speed > 0.02f;
            if (!working) { m_ToBeat = 0f; return; }
            m_ToBeat -= speed * deltaTime;
            if (m_ToBeat > 0f) return;
            m_ToBeat += BeatDistance;
            if (m_ToBeat < 0f) m_ToBeat = BeatDistance;   // very fast: at most one beat per frame

            float effort = simState.Regulator * Mathf.Lerp(0.5f, 1f, pressure01);
            var main = m_Ps.main;
            // hard, tall blasts when starting; shorter, flatter ones at speed
            float lift = Mathf.Lerp(7f, 4f, Mathf.Clamp01(speed / SteamSim.MaxSpeed)) * Mathf.Lerp(0.6f, 1.2f, effort);
            main.startSpeedMultiplier = lift / 3.75f;          // the prefab's 3..4.5 m/s, scaled
            m_Ps.Emit(Mathf.Clamp(Mathf.RoundToInt(1f + 3f * effort), 1, 4));
            main.startSpeedMultiplier = 1f;
        }
    }
}
