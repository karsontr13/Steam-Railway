using Il2CppTLD.SteamTrain;
using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Sparks from the driving wheels while the hand brake drags them along the rails: two emitters per train (one along
    /// each rail, over the four coupled axles), throwing glowing chips backwards and up, more the faster it runs.
    internal static class BrakeSparks
    {
        // where the coupled wheels meet the rails, in the train's frame (build_locomotive.py: WHEEL_X, DRIVE_Z)
        const float WheelTreadLateralInMeters = 0.86f;
        const float RailContactHeightInMeters = 0.03f;
        const float CoupledWheelsCentreInMeters = 0.825f;
        const float CoupledWheelsLengthInMeters = 5.2f;
        // how they fly
        const float MinimumSpeedInMetersPerSecond = 0.6f;
        const float SparksPerSecondPerMeterPerSecond = 22f;
        const float MaximumSparksPerSecond = 260f;
        const float SparkLifetimeMinimumInSeconds = 0.15f;
        const float SparkLifetimeMaximumInSeconds = 0.45f;
        const float SparkSizeMinimumInMeters = 0.012f;
        const float SparkSizeMaximumInMeters = 0.03f;
        const float BackwardThrowInMetersPerSecond = 3.5f;
        const float UpwardThrowInMetersPerSecond = 1.6f;
        const float SidewaysScatterInMetersPerSecond = 0.7f;
        const float SparkGravityModifier = 0.9f;
        const float SparkStretch = 0.12f;
        const float BackwardThrowMaximumFactor = 1.4f;
        const float BackwardThrowMinimumFactor = 0.5f;
        const float SidewaysScatterMinimumFactor = 0.2f;
        const float UpwardThrowMinimumFactor = 0.3f;
        const float EmitterWidthInMeters = 0.02f;
        const float EmitterHeightInMeters = 0.01f;
        const int MaximumParticles = 600;
        const float SparkColorRed = 1f;
        const float SparkColorGreen = 0.68f;
        const float SparkColorBlue = 0.28f;
        const float AdditiveTintLevel = 0.5f;
        const string EmitterNamePrefix = "SR_BrakeSparks_";

        sealed class TrainSparks
        {
            public ParticleSystem Left, Right;
            public float Direction;
        }

        static readonly Dictionary<SteamTrain, TrainSparks> s_Sparks = new Dictionary<SteamTrain, TrainSparks>();
        static Material s_Material;

        public static void Update(List<LiveTrain> trains)
        {
            for (int index = 0; index < trains.Count; index++)
            {
                var liveTrain = trains[index];
                if (liveTrain.Train == null || liveTrain.Simulation == null) continue;
                float velocity = liveTrain.Simulation.Velocity;
                float speed = Mathf.Abs(velocity);
                bool dragging = liveTrain.Simulation.State.HandBrake && speed > MinimumSpeedInMetersPerSecond;
                if (!s_Sparks.TryGetValue(liveTrain.Train, out var sparks) || sparks.Left == null)
                {
                    if (!dragging) continue;
                    sparks = Create(liveTrain.Train.transform);
                    s_Sparks[liveTrain.Train] = sparks;
                }
                float rate = dragging ? Mathf.Min(MaximumSparksPerSecond, speed * SparksPerSecondPerMeterPerSecond) : 0f;
                float direction = velocity >= 0f ? 1f : -1f;
                Apply(sparks.Left, rate, direction, sparks.Direction != direction);
                Apply(sparks.Right, rate, direction, sparks.Direction != direction);
                sparks.Direction = direction;
            }
        }

        static void Apply(ParticleSystem particleSystem, float rate, float direction, bool turned)
        {
            var emission = particleSystem.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(rate);
            if (turned)
            {
                // thrown out behind the wheels, against the way the train runs
                var velocity = particleSystem.velocityOverLifetime;
                velocity.z = Range(-direction * BackwardThrowInMetersPerSecond * BackwardThrowMaximumFactor, -direction * BackwardThrowInMetersPerSecond * BackwardThrowMinimumFactor);
            }
            if (rate > 0f && !particleSystem.isPlaying) particleSystem.Play();
        }

        static TrainSparks Create(Transform trainRoot)
        {
            return new TrainSparks
            {
                Left = CreateEmitter(trainRoot, -1f),
                Right = CreateEmitter(trainRoot, 1f),
                Direction = 0f,
            };
        }

        static ParticleSystem CreateEmitter(Transform trainRoot, float side)
        {
            string emitterName = EmitterNamePrefix + (side < 0f ? "L" : "R");
            var existing = trainRoot.Find(emitterName);
            if (existing != null) Object.Destroy(existing.gameObject);
            var emitterObject = new GameObject(emitterName);
            emitterObject.transform.SetParent(trainRoot, false);
            emitterObject.transform.localPosition = new Vector3(side * WheelTreadLateralInMeters, RailContactHeightInMeters, CoupledWheelsCentreInMeters);
            var particleSystem = emitterObject.AddComponent<ParticleSystem>();
            particleSystem.Stop();

            var main = particleSystem.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = Range(SparkLifetimeMinimumInSeconds, SparkLifetimeMaximumInSeconds);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            main.startSize = Range(SparkSizeMinimumInMeters, SparkSizeMaximumInMeters);
            // one glowing orange (this game's Unity build has no colour-over-lifetime module); they die out quickly instead
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(SparkColorRed, SparkColorGreen, SparkColorBlue, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(SparkGravityModifier);
            main.maxParticles = MaximumParticles;

            var emission = particleSystem.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(0f);

            var shape = particleSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            // the box is the emitter's own scale (only the shape follows it): a thin strip along the rail
            main.scalingMode = ParticleSystemScalingMode.Shape;
            emitterObject.transform.localScale = new Vector3(EmitterWidthInMeters, EmitterHeightInMeters, CoupledWheelsLengthInMeters);

            var velocity = particleSystem.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = Range(side * SidewaysScatterInMetersPerSecond * SidewaysScatterMinimumFactor, side * SidewaysScatterInMetersPerSecond);
            velocity.y = Range(UpwardThrowInMetersPerSecond * UpwardThrowMinimumFactor, UpwardThrowInMetersPerSecond);
            velocity.z = Range(-BackwardThrowInMetersPerSecond * BackwardThrowMaximumFactor, -BackwardThrowInMetersPerSecond * BackwardThrowMinimumFactor);

            var particleRenderer = emitterObject.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            particleRenderer.velocityScale = SparkStretch;
            particleRenderer.lengthScale = 1f;
            particleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;
            var material = SparkMaterial();
            if (material != null) particleRenderer.sharedMaterial = material;
            return particleSystem;
        }

        static ParticleSystem.MinMaxCurve Range(float minimum, float maximum)
        {
            var curve = new ParticleSystem.MinMaxCurve(maximum);
            curve.mode = ParticleSystemCurveMode.TwoConstants;
            curve.constantMin = minimum;
            curve.constantMax = maximum;
            return curve;
        }

        /// Additive and untextured: each spark a small glowing streak (the boiler blast's sparks use the same).
        static Material SparkMaterial()
        {
            if (s_Material != null) return s_Material;
            var shader = Shader.Find("Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? LocoSmoke.FindBlendShader();
            if (shader == null) return null;
            s_Material = new Material(shader) { name = "SteamRailway_BrakeSparks", mainTexture = Texture2D.whiteTexture };
            if (s_Material.HasProperty("_TintColor")) s_Material.SetColor("_TintColor", new Color(AdditiveTintLevel, AdditiveTintLevel, AdditiveTintLevel, AdditiveTintLevel));
            return s_Material;
        }

        /// A new region: the trains (and their emitters) are new objects.
        public static void Clear() => s_Sparks.Clear();
    }
}
