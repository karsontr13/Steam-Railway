using UnityEngine;

namespace SteamRailway
{
    /// Coal smoke from the chimney. A lazy plume while the fire burns at a stand (stronger with the blower on),
    /// and a dark blast on every exhaust beat while the engine works: four beats per driving wheel turn.
    internal sealed class LocoSmoke
    {
        const float HalfTurnInRadians = 3.14159265f;
        const float WheelRadiusInMeters = 0.7f;
        const float WheelCircumference = 2f * HalfTurnInRadians * WheelRadiusInMeters;
        const float BeatsPerWheelTurn = 4f;
        const float BeatDistance = WheelCircumference / BeatsPerWheelTurn;
        const float PlumeBaseRate = 1.5f;
        const float PlumeFireRate = 4f;
        const float PlumeBlowerRate = 6f;
        const float WorkingMinimumPressureInPsi = 5f;
        const float WorkingMinimumSpeedInMetersPerSecond = 0.02f;
        const float EffortMinimumPressureFactor = 0.5f;
        const float LiftStartingInMetersPerSecond = 7f;
        const float LiftAtSpeedInMetersPerSecond = 4f;
        const float LiftMinimumEffortFactor = 0.6f;
        const float LiftMaximumEffortFactor = 1.2f;
        const float PrefabMeanStartSpeedInMetersPerSecond = 3.75f;
        const float BeatEmitBase = 1f;
        const float BeatEmitPerEffort = 3f;
        const int BeatEmitMinimum = 1;
        const int BeatEmitMaximum = 4;
        const float ChimneyPitchInDegrees = -90f;
        const string TintColorPropertyName = "_TintColor";

        readonly ParticleSystem m_ParticleSystem;
        readonly Material m_Material;
        float m_ToBeat;

        // coal smoke: near black at the chimney, a dark grey haze as it thins (multiplied with the particle colours)
        static readonly Color DayTint = new Color(0.36f, 0.35f, 0.34f, 0.5f);
        const float NightLight = 0.14f;      // fraction of the day's brightness left at night

        LocoSmoke(ParticleSystem particleSystem, Material material) { m_ParticleSystem = particleSystem; m_Material = material; }

        /// An unlit alpha-blended particle shader that keeps the particles' own dark colours.
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
        public static LocoSmoke Create(Transform trainRoot, Vector3 localPosition, Material litParticleMaterial)
        {
            if (!LocomotiveModel.SmokeAssets(out var prefab, out var sheet)) return null;
            var targetObject = Object.Instantiate(prefab, trainRoot, false);
            targetObject.name = "LocoSmoke";
            targetObject.transform.localPosition = localPosition;
            targetObject.transform.localRotation = Quaternion.Euler(ChimneyPitchInDegrees, 0f, 0f);
            var particleRenderer = targetObject.GetComponent<ParticleSystemRenderer>();
            Material material = null;
            var shader = FindBlendShader();
            if (shader != null)
            {
                material = new Material(shader) { name = "SteamRailway_Smoke", mainTexture = sheet };
                if (material.HasProperty(TintColorPropertyName)) material.SetColor(TintColorPropertyName, DayTint);
                particleRenderer.sharedMaterial = material;
            }
            else if (litParticleMaterial != null) particleRenderer.sharedMaterial = new Material(litParticleMaterial) { name = "SteamRailway_Smoke", mainTexture = sheet };
            else particleRenderer.sharedMaterial.mainTexture = sheet;
            var particleSystem = targetObject.GetComponent<ParticleSystem>();
            particleSystem.Play();
            return new LocoSmoke(particleSystem, material);
        }

        public void Update(SteamSimulation simulation, float deltaTime)
        {
            if (m_ParticleSystem == null) return;
            if (m_Material != null)
            {
                float light = Mathf.Lerp(1f, NightLight, LampBeam.Darkness());
                var color = DayTint * light; color.a = DayTint.a;
                m_Material.SetColor(TintColorPropertyName, color);
            }
            var simulationState = simulation.State;
            bool burning = simulation.FireBurning && !simulationState.Exploded;
            float fire = Mathf.Clamp01(simulationState.FireLevel);
            float speed = Mathf.Abs(simulation.Velocity);
            float pressureFraction = Mathf.Clamp01(simulation.Pressure / SteamSimulation.MaxPressure);

            // standing plume
            float idle = burning ? PlumeBaseRate + PlumeFireRate * fire + (simulationState.Blower ? PlumeBlowerRate : 0f) : 0f;
            var emission = m_ParticleSystem.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(idle);

            // exhaust beats
            bool working = burning && simulationState.Regulator > 0f && simulationState.Reverser != 0 && simulation.Pressure > WorkingMinimumPressureInPsi && speed > WorkingMinimumSpeedInMetersPerSecond;
            if (!working) { m_ToBeat = 0f; return; }
            m_ToBeat -= speed * deltaTime;
            if (m_ToBeat > 0f) return;
            m_ToBeat += BeatDistance;
            if (m_ToBeat < 0f) m_ToBeat = BeatDistance;   // very fast: at most one beat per frame

            float effort = simulationState.Regulator * Mathf.Lerp(EffortMinimumPressureFactor, 1f, pressureFraction);
            var main = m_ParticleSystem.main;
            // hard, tall blasts when starting; shorter, flatter ones at speed
            float lift = Mathf.Lerp(LiftStartingInMetersPerSecond, LiftAtSpeedInMetersPerSecond, Mathf.Clamp01(speed / SteamSimulation.MaxSpeed)) * Mathf.Lerp(LiftMinimumEffortFactor, LiftMaximumEffortFactor, effort);
            main.startSpeedMultiplier = lift / PrefabMeanStartSpeedInMetersPerSecond;          // the prefab's 3..4.5 m/s, scaled
            m_ParticleSystem.Emit(Mathf.Clamp(Mathf.RoundToInt(BeatEmitBase + BeatEmitPerEffort * effort), BeatEmitMinimum, BeatEmitMaximum));
            main.startSpeedMultiplier = 1f;
        }
    }
}
