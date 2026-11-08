using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Generated fallback sounds (used only when the recorded clips in the bundle are missing; see Sounds/CREDITS.txt):
    ///   Rumble  - loop: wheels rolling on the rails (low noise + body resonance)
    ///   Clack   - one-shot: a wheel crossing a rail joint
    ///   Whistle - loop: a three-chime steam whistle with a breathy edge
    /// Played through Unity AudioSources on the train.
    internal static class SynthSounds
    {
        const int Rate = 44100;
        static AudioClip s_Rumble, s_Clack, s_Whistle, s_Notch;
        static readonly System.Random s_Rng = new System.Random(1234);

        public static AudioClip Rumble => s_Rumble ??= Make("SR_Rumble", MakeRumble());
        public static AudioClip Clack => s_Clack ??= Make("SR_Clack", MakeClack());
        public static AudioClip Whistle => s_Whistle ??= Make("SR_Whistle", MakeWhistle());
        public static AudioClip Notch => s_Notch ??= Make("SR_Notch", MakeNotch());

        /// Plays a short cab sound (lever notch, valve turn) at the cab, with a little natural variation.
        public static void PlayCab(Il2CppTLD.SteamTrain.SteamTrain train, AudioClip clip, float pitch = 1f, float volume = 0.8f)
        {
            if (train == null || clip == null) return;
            var go = TrainSounds.Cab(train);
            var src = go.GetComponent<AudioSource>() ?? Source(go, false, 1.5f, 25f);
            src.volume = 1f;
            src.pitch = pitch * Random.Range(0.96f, 1.04f);
            src.PlayOneShot(clip, volume);
        }

        static readonly string[] s_Levers = { "Lever1", "Lever2", "Lever3" };

        /// A lever thrown into its notch: one of the recorded clanks (generated click as a fallback).
        public static void PlayLever(Il2CppTLD.SteamTrain.SteamTrain train, float pitch, float volume)
        {
            var clip = LocomotiveModel.Sound(s_Levers[Random.Range(0, s_Levers.Length)]);
            PlayCab(train, clip ?? Notch, pitch, volume);
        }

        /// A valve hand wheel turned: a soft, low clank.
        public static void PlayValve(Il2CppTLD.SteamTrain.SteamTrain train)
        {
            var clip = LocomotiveModel.Sound("Valve");
            PlayCab(train, clip ?? Notch, clip != null ? 0.8f : 0.7f, 0.45f);
        }

        /// 0.18 s: a lever dropping into its notch - a click of the latch and a dull thud of the handle.
        static float[] MakeNotch()
        {
            int n = (int)(Rate * 0.18f);
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                lp += (Noise() - lp) * 0.5f;
                float click = lp * Mathf.Exp(-t * 260f);
                float thud = Mathf.Sin(2f * Mathf.PI * 95f * t) * Mathf.Exp(-t * 38f);
                float tick = Mathf.Sin(2f * Mathf.PI * 2300f * t) * Mathf.Exp(-t * 180f);
                d[i] = click * 0.9f + thud * 0.8f + tick * 0.15f;
            }
            Normalize(d, 0.85f);
            return d;
        }

        static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(new Il2CppStructArray<float>(data), 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(clip);
            return clip;
        }

        static float Noise() => (float)(s_Rng.NextDouble() * 2.0 - 1.0);

        /// 4 s seamless loop: brown noise (track roar) + low hum of the frames.
        static float[] MakeRumble()
        {
            int n = Rate * 4;
            var d = new float[n];
            float brown = 0f, lp = 0f;
            for (int i = 0; i < n; i++)
            {
                brown = (brown + 0.02f * Noise()) * 0.995f;
                lp += (Noise() - lp) * 0.08f;
                float t = i / (float)Rate;
                float hum = 0.25f * Mathf.Sin(2f * Mathf.PI * 47f * t) + 0.12f * Mathf.Sin(2f * Mathf.PI * 94f * t);
                d[i] = brown * 3.2f + lp * 0.35f + hum * 0.35f;
            }
            d = Crossfade(d, Rate / 5);
            Normalize(d, 0.7f);
            return d;
        }

        /// 0.25 s: a sharp knock with a short metallic ring (a wheel over a rail joint).
        static float[] MakeClack()
        {
            int n = Rate / 4;
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                lp += (Noise() - lp) * 0.35f;
                float knock = lp * Mathf.Exp(-t * 90f);
                float body = Mathf.Sin(2f * Mathf.PI * 120f * t) * Mathf.Exp(-t * 30f);
                float ring = (0.5f * Mathf.Sin(2f * Mathf.PI * 1180f * t) + 0.35f * Mathf.Sin(2f * Mathf.PI * 1730f * t)) * Mathf.Exp(-t * 45f);
                d[i] = knock * 1.2f + body * 0.8f + ring * 0.25f;
            }
            Normalize(d, 0.9f);
            return d;
        }

        /// 2 s loop: a three-chime steam whistle with a breathy edge.
        static float[] MakeWhistle()
        {
            int n = Rate * 2;
            var d = new float[n];
            int[] f = { 349, 440, 523 };
            float hiss = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float tone = 0f;
                foreach (int fr in f)
                    tone += Mathf.Sin(2f * Mathf.PI * fr * t) + 0.3f * Mathf.Sin(2f * Mathf.PI * fr * 2 * t) + 0.12f * Mathf.Sin(2f * Mathf.PI * fr * 3 * t);
                hiss += (Noise() - hiss) * 0.6f;
                float wobble = 1f + 0.06f * Mathf.Sin(2f * Mathf.PI * 3f * t);
                d[i] = tone * 0.3f * wobble + hiss * 0.18f;
            }
            d = Crossfade(d, Rate / 10);
            Normalize(d, 0.8f);
            return d;
        }

        /// Blends the end into the start so a looping clip has no click.
        /// The last `len` samples are faded into the first ones and cut off: the new end runs straight into the start.
        static float[] Crossfade(float[] d, int len)
        {
            int n = d.Length;
            for (int i = 0; i < len; i++)
            {
                float a = i / (float)len;
                d[i] = d[i] * a + d[n - len + i] * (1f - a);
            }
            System.Array.Resize(ref d, n - len);
            return d;
        }

        static void Normalize(float[] d, float peak)
        {
            float m = 0f;
            foreach (var v in d) m = Mathf.Max(m, Mathf.Abs(v));
            if (m <= 0f) return;
            float k = peak / m;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }

        public static AudioSource Source(GameObject go, bool loop, float minDist, float maxDist)
        {
            var src = go.AddComponent<AudioSource>();
            src.loop = loop;
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = minDist;
            src.maxDistance = maxDist;
            src.dopplerLevel = 0f;
            src.volume = 0f;
            return src;
        }
    }
}
