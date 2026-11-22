using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// A boiler explosion scalds anyone close by with the game's burns affliction instead of taking health.
    /// The look is the bundle's BoilerBlast particle prefab plus a light flash that dies out.
    internal static class BoilerExplosion
    {
        const float HearRadius = 25f;
        const float BurnsRadius = 12f;
        const float FlashTime = 1.4f;
        const float AdditiveTintLevel = 0.5f;
        const float BarrelCenterHeightInMeters = 3.1f;
        const float BarrelCenterForwardInMeters = 2.2f;
        const float BlastPrefabLifetimeInSeconds = 30f;
        const float FlashLightHeightInMeters = 0.5f;
        const float FlashLightRangeInMeters = 40f;
        const float FlashLightIntensity = 8f;
        const float FlashLightExtraLifetimeInSeconds = 0.5f;
        const float FlashFlickerBase = 0.85f;
        const float FlashFlickerAmplitude = 0.15f;
        const float FlashFlickerRadiansPerSecond = 60f;
        const float FlashLightRed = 1f;
        const float FlashLightGreen = 0.62f;
        const float FlashLightBlue = 0.28f;
        const float NoFlashStartTimeInSeconds = -100f;
        const float BurstParticleSizeInMeters = 6f;
        const float BurstParticleSpeedInMetersPerSecond = 10f;
        const float BurstParticleLifetimeInSeconds = 4f;
        const int BurstMaximumParticles = 600;
        const float BurstShapeAngleInDegrees = 70f;
        const float BurstShapeRadiusInMeters = 1.5f;
        const int BurstEmitCount = 400;

        static Light s_Flash;
        static float s_FlashStart = NoFlashStartTimeInSeconds;
        static Vector3 s_BlastWorldPosition;

        public static void Play(SteamSimulation simulation)
        {
            var train = simulation.Train;
            Blast(train);
            TrainSounds.Explosion(train);

            var player = GameManager.GetPlayerObject();
            if (player == null) return;

            float distance = Vector3.Distance(player.transform.position, train.transform.position);
            if (distance > HearRadius)
            {
                CabControls.Message(Localizer.Translate("Uzaktan büyük bir patlama sesi geldi: lokomotifin kazanı patladı.",
                                        "A loud bang in the distance: the locomotive's boiler has exploded."));
                return;
            }
            if (distance <= BurnsRadius)
            {
                var burns = GameManager.GetBurnsComponent();
                if (burns != null && !burns.HasBurns())
                    burns.BurnsStart("GAMEPLAY_BurnsCauseSteam", true, false, AfflictionOptions.PlayFX | AfflictionOptions.DisplayIcon);
                CabControls.Message(Localizer.Translate("KAZAN PATLADI! Buhardan yandın. Kazan onarılmadan lokomotif çalışmaz.",
                                        "THE BOILER EXPLODED! You were scalded by the steam. The locomotive won't run until the boiler is repaired."));
            }
            else
                CabControls.Message(Localizer.Translate("KAZAN PATLADI! Kazan onarılmadan lokomotif çalışmaz.",
                                        "THE BOILER EXPLODED! The locomotive won't run until the boiler is repaired."));
        }

        static void Blast(SteamTrain train)
        {
            // the middle of the boiler barrel, train-local
            var barrelWorldPosition = train.transform.TransformPoint(new Vector3(0f, BarrelCenterHeightInMeters, BarrelCenterForwardInMeters));
            if (!BlastAt(barrelWorldPosition, train.transform.rotation, 1f)) SteamBurst(train);
        }

        /// The blast's look at any place: particles plus a fading light flash; `scale` 1 is a boiler.
        /// Returns false when the bundle's particle prefab is missing (only the light is shown then).
        public static bool BlastAt(Vector3 worldPosition, Quaternion rotation, float scale)
        {
            s_BlastWorldPosition = worldPosition;
            bool shown = false;
            var prefab = LocomotiveModel.BlastPrefab();
            if (prefab != null)
            {
                shown = true;
                var targetObject = Object.Instantiate(prefab);
                targetObject.name = "BoilerBlast";
                targetObject.transform.position = worldPosition;
                targetObject.transform.rotation = rotation;
                targetObject.transform.localScale = Vector3.one * scale;
                var blend = LocoSmoke.FindBlendShader();
                var add = Shader.Find("Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive");
                foreach (var particleSystem in targetObject.GetComponentsInChildren<ParticleSystem>())
                {
                    var particleRenderer = particleSystem.GetComponent<ParticleSystemRenderer>();
                    bool fire = particleSystem.name == "Fireball" || particleSystem.name == "Sparks" || particleSystem.name == "Flash" || particleSystem.name == "Embers";
                    var shader = fire ? add : blend;
                    if (shader != null)
                    {
                        var material = new Material(shader) { name = "SteamRailway_Blast_" + particleSystem.name };
                        material.mainTexture = particleSystem.name == "Sparks" || particleSystem.name == "Embers" ? Texture2D.whiteTexture : particleRenderer.sharedMaterial != null ? particleRenderer.sharedMaterial.mainTexture : null;
                        if (material.HasProperty("_TintColor"))
                            material.SetColor("_TintColor", fire ? new Color(AdditiveTintLevel, AdditiveTintLevel, AdditiveTintLevel, AdditiveTintLevel) : new Color(AdditiveTintLevel, AdditiveTintLevel, AdditiveTintLevel, AdditiveTintLevel));
                        particleRenderer.sharedMaterial = material;
                    }
                    particleSystem.Play();
                }
                Object.Destroy(targetObject, BlastPrefabLifetimeInSeconds);
            }

            // a flash of light that dies out
            var lampObject = new GameObject("BoilerBlastLight");
            lampObject.transform.position = worldPosition + Vector3.up * FlashLightHeightInMeters;
            s_Flash = lampObject.AddComponent<Light>();
            s_Flash.type = LightType.Point;
            s_Flash.color = new Color(FlashLightRed, FlashLightGreen, FlashLightBlue);
            s_Flash.range = FlashLightRangeInMeters;
            s_Flash.intensity = FlashLightIntensity;
            s_Flash.shadows = LightShadows.None;
            s_FlashStart = Time.time;
            Object.Destroy(lampObject, FlashTime + FlashLightExtraLifetimeInSeconds);
            return shown;
        }

        /// the flash fades.
        public static void Update()
        {
            float fraction = Time.time - s_FlashStart;
            if (fraction < 0f || fraction > FlashTime) return;
            if (s_Flash != null)
            {
                float scale = Mathf.Clamp01(1f - fraction / FlashTime);
                s_Flash.intensity = FlashLightIntensity * scale * scale * (FlashFlickerBase + FlashFlickerAmplitude * Mathf.Sin(fraction * FlashFlickerRadiansPerSecond));
            }
        }

        /// the whistle's dense steam emitter, enlarged for a moment.
        static void SteamBurst(SteamTrain train)
        {
            var particleSystem = train.m_SteamWhistleDenseParticleSystem;
            if (particleSystem == null) return;
            var main = particleSystem.main;
            main.startSize = new ParticleSystem.MinMaxCurve(BurstParticleSizeInMeters);
            main.startSpeed = new ParticleSystem.MinMaxCurve(BurstParticleSpeedInMetersPerSecond);
            main.startLifetime = new ParticleSystem.MinMaxCurve(BurstParticleLifetimeInSeconds);
            main.maxParticles = BurstMaximumParticles;
            var shape = particleSystem.shape; shape.angle = BurstShapeAngleInDegrees; shape.radius = BurstShapeRadiusInMeters;
            particleSystem.Emit(BurstEmitCount);
        }
    }
}
