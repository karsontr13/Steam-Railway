using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// A boiler explosion does not take health directly: anyone close by is scalded (the game's burns affliction).
    /// The player stays where they are and may leave the cab whenever they like.
    /// The look: a flash of fire at the boiler, a rolling steam cloud, a dark smoke column and glowing sparks
    /// (the bundle's BoilerBlast prefab), a light flash that dies out.
    internal static class BoilerExplosion
    {
        const float HearRadius = 25f;
        const float BurnsRadius = 12f;
        const float FlashTime = 1.4f;

        static Light s_Flash;
        static float s_FlashStart = -100f;
        static Vector3 s_BlastPos;

        public static void Play(SteamSim simulation)
        {
            var train = simulation.Train;
            Blast(train);
            TrainSounds.Explosion(train);

            var player = GameManager.GetPlayerObject();
            if (player == null) return;

            float distance = Vector3.Distance(player.transform.position, train.transform.position);
            if (distance > HearRadius)
            {
                CabControls.Message(L.T("Uzaktan büyük bir patlama sesi geldi: lokomotifin kazanı patladı.",
                                        "A loud bang in the distance: the locomotive's boiler has exploded."));
                return;
            }
            if (distance <= BurnsRadius)
            {
                var burns = GameManager.GetBurnsComponent();
                if (burns != null && !burns.HasBurns())
                    burns.BurnsStart("GAMEPLAY_BurnsCauseSteam", true, false, AfflictionOptions.PlayFX | AfflictionOptions.DisplayIcon);
                CabControls.Message(L.T("KAZAN PATLADI! Buhardan yandın. Kazan onarılmadan lokomotif çalışmaz.",
                                        "THE BOILER EXPLODED! You were scalded by the steam. The locomotive won't run until the boiler is repaired."));
            }
            else
                CabControls.Message(L.T("KAZAN PATLADI! Kazan onarılmadan lokomotif çalışmaz.",
                                        "THE BOILER EXPLODED! The locomotive won't run until the boiler is repaired."));
        }

        static void Blast(SteamTrain train)
        {
            // the middle of the boiler barrel, train-local
            var pos = train.transform.TransformPoint(new Vector3(0f, 3.1f, 2.2f));
            s_BlastPos = pos;
            var prefab = LocomotiveModel.BlastPrefab();
            if (prefab != null)
            {
                var targetObject = Object.Instantiate(prefab);
                targetObject.name = "BoilerBlast";
                targetObject.transform.position = pos;
                targetObject.transform.rotation = train.transform.rotation;
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
                            material.SetColor("_TintColor", fire ? new Color(0.5f, 0.5f, 0.5f, 0.5f) : new Color(0.5f, 0.5f, 0.5f, 0.5f));
                        particleRenderer.sharedMaterial = material;
                    }
                    particleSystem.Play();
                }
                Object.Destroy(targetObject, 30f);
            }
            else SteamBurst(train);

            // a flash of light that dies out
            var lampObject = new GameObject("BoilerBlastLight");
            lampObject.transform.position = pos + Vector3.up * 0.5f;
            s_Flash = lampObject.AddComponent<Light>();
            s_Flash.type = LightType.Point;
            s_Flash.color = new Color(1f, 0.62f, 0.28f);
            s_Flash.range = 40f;
            s_Flash.intensity = 8f;
            s_Flash.shadows = LightShadows.None;
            s_FlashStart = Time.time;
            Object.Destroy(lampObject, FlashTime + 0.5f);
        }

        /// the flash fades.
        public static void Update()
        {
            float fraction = Time.time - s_FlashStart;
            if (fraction < 0f || fraction > FlashTime) return;
            if (s_Flash != null)
            {
                float scale = Mathf.Clamp01(1f - fraction / FlashTime);
                s_Flash.intensity = 8f * scale * scale * (0.85f + 0.15f * Mathf.Sin(fraction * 60f));
            }
        }

        /// the whistle's dense steam emitter, enlarged for a moment.
        static void SteamBurst(SteamTrain train)
        {
            var particleSystem = train.m_SteamWhistleDenseParticleSystem;
            if (particleSystem == null) return;
            var main = particleSystem.main;
            main.startSize = new ParticleSystem.MinMaxCurve(6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(10f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f);
            main.maxParticles = 600;
            var shape = particleSystem.shape; shape.angle = 70f; shape.radius = 1.5f;
            particleSystem.Emit(400);
        }
    }
}
