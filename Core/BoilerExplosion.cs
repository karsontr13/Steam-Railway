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

        public static void Play(SteamSim sim)
        {
            var train = sim.Train;
            Blast(train);
            TrainSounds.Explosion(train);

            var player = GameManager.GetPlayerObject();
            if (player == null) return;

            float d = Vector3.Distance(player.transform.position, train.transform.position);
            if (d > HearRadius)
            {
                CabControls.Message(L.T("Uzaktan büyük bir patlama sesi geldi: lokomotifin kazanı patladı.",
                                        "A loud bang in the distance: the locomotive's boiler has exploded."));
                return;
            }
            if (d <= BurnsRadius)
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
                var go = Object.Instantiate(prefab);
                go.name = "BoilerBlast";
                go.transform.position = pos;
                go.transform.rotation = train.transform.rotation;
                var blend = LocoSmoke.FindBlendShader();
                var add = Shader.Find("Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive");
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>())
                {
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    bool fire = ps.name == "Fireball" || ps.name == "Sparks" || ps.name == "Flash" || ps.name == "Embers";
                    var shader = fire ? add : blend;
                    if (shader != null)
                    {
                        var mat = new Material(shader) { name = "SteamRailway_Blast_" + ps.name };
                        mat.mainTexture = ps.name == "Sparks" || ps.name == "Embers" ? Texture2D.whiteTexture : r.sharedMaterial != null ? r.sharedMaterial.mainTexture : null;
                        if (mat.HasProperty("_TintColor"))
                            mat.SetColor("_TintColor", fire ? new Color(0.5f, 0.5f, 0.5f, 0.5f) : new Color(0.5f, 0.5f, 0.5f, 0.5f));
                        r.sharedMaterial = mat;
                    }
                    ps.Play();
                }
                Object.Destroy(go, 30f);
            }
            else SteamBurst(train);

            // a flash of light that dies out
            var lg = new GameObject("BoilerBlastLight");
            lg.transform.position = pos + Vector3.up * 0.5f;
            s_Flash = lg.AddComponent<Light>();
            s_Flash.type = LightType.Point;
            s_Flash.color = new Color(1f, 0.62f, 0.28f);
            s_Flash.range = 40f;
            s_Flash.intensity = 8f;
            s_Flash.shadows = LightShadows.None;
            s_FlashStart = Time.time;
            Object.Destroy(lg, FlashTime + 0.5f);
        }

        /// the flash fades.
        public static void Update()
        {
            float t = Time.time - s_FlashStart;
            if (t < 0f || t > FlashTime) return;
            if (s_Flash != null)
            {
                float k = Mathf.Clamp01(1f - t / FlashTime);
                s_Flash.intensity = 8f * k * k * (0.85f + 0.15f * Mathf.Sin(t * 60f));
            }
        }

        /// the whistle's dense steam emitter, enlarged for a moment.
        static void SteamBurst(SteamTrain train)
        {
            var ps = train.m_SteamWhistleDenseParticleSystem;
            if (ps == null) return;
            var main = ps.main;
            main.startSize = new ParticleSystem.MinMaxCurve(6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(10f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f);
            main.maxParticles = 600;
            var shape = ps.shape; shape.angle = 70f; shape.radius = 1.5f;
            ps.Emit(400);
        }
    }
}
