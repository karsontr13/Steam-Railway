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
            var targetObject = TrainSounds.Cab(train);
            var source = targetObject.GetComponent<AudioSource>() ?? Source(targetObject, false, 1.5f, 25f);
            source.volume = 1f;
            source.pitch = pitch * Random.Range(0.96f, 1.04f);
            source.PlayOneShot(clip, volume);
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
            int count = (int)(Rate * 0.18f);
            var d = new float[count];
            float lastProgress = 0f;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)Rate;
                lastProgress += (Noise() - lastProgress) * 0.5f;
                float click = lastProgress * Mathf.Exp(-fraction * 260f);
                float thud = Mathf.Sin(2f * Mathf.PI * 95f * fraction) * Mathf.Exp(-fraction * 38f);
                float tick = Mathf.Sin(2f * Mathf.PI * 2300f * fraction) * Mathf.Exp(-fraction * 180f);
                d[index] = click * 0.9f + thud * 0.8f + tick * 0.15f;
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
            int count = Rate * 4;
            var d = new float[count];
            float brown = 0f, lastProgress = 0f;
            for (int index = 0; index < count; index++)
            {
                brown = (brown + 0.02f * Noise()) * 0.995f;
                lastProgress += (Noise() - lastProgress) * 0.08f;
                float fraction = index / (float)Rate;
                float humidity = 0.25f * Mathf.Sin(2f * Mathf.PI * 47f * fraction) + 0.12f * Mathf.Sin(2f * Mathf.PI * 94f * fraction);
                d[index] = brown * 3.2f + lastProgress * 0.35f + humidity * 0.35f;
            }
            d = Crossfade(d, Rate / 5);
            Normalize(d, 0.7f);
            return d;
        }

        /// 0.25 s: a sharp knock with a short metallic ring (a wheel over a rail joint).
        static float[] MakeClack()
        {
            int count = Rate / 4;
            var d = new float[count];
            float lastProgress = 0f;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)Rate;
                lastProgress += (Noise() - lastProgress) * 0.35f;
                float knock = lastProgress * Mathf.Exp(-fraction * 90f);
                float body = Mathf.Sin(2f * Mathf.PI * 120f * fraction) * Mathf.Exp(-fraction * 30f);
                float ring = (0.5f * Mathf.Sin(2f * Mathf.PI * 1180f * fraction) + 0.35f * Mathf.Sin(2f * Mathf.PI * 1730f * fraction)) * Mathf.Exp(-fraction * 45f);
                d[index] = knock * 1.2f + body * 0.8f + ring * 0.25f;
            }
            Normalize(d, 0.9f);
            return d;
        }

        /// 2 s loop: a three-chime steam whistle with a breathy edge.
        static float[] MakeWhistle()
        {
            int count = Rate * 2;
            var d = new float[count];
            int[] f = { 349, 440, 523 };
            float hiss = 0f;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)Rate;
                float tone = 0f;
                foreach (int fr in f)
                    tone += Mathf.Sin(2f * Mathf.PI * fr * fraction) + 0.3f * Mathf.Sin(2f * Mathf.PI * fr * 2 * fraction) + 0.12f * Mathf.Sin(2f * Mathf.PI * fr * 3 * fraction);
                hiss += (Noise() - hiss) * 0.6f;
                float wobble = 1f + 0.06f * Mathf.Sin(2f * Mathf.PI * 3f * fraction);
                d[index] = tone * 0.3f * wobble + hiss * 0.18f;
            }
            d = Crossfade(d, Rate / 10);
            Normalize(d, 0.8f);
            return d;
        }

        /// Blends the end into the start so a looping clip has no click.
        /// The last `len` samples are faded into the first ones and cut off: the new end runs straight into the start.
        static float[] Crossfade(float[] d, int len)
        {
            int count = d.Length;
            for (int index = 0; index < len; index++)
            {
                float firstValue = index / (float)len;
                d[index] = d[index] * firstValue + d[count - len + index] * (1f - firstValue);
            }
            System.Array.Resize(ref d, count - len);
            return d;
        }

        static void Normalize(float[] d, float peak)
        {
            float meters = 0f;
            foreach (var value in d) meters = Mathf.Max(meters, Mathf.Abs(value));
            if (meters <= 0f) return;
            float scale = peak / meters;
            for (int index = 0; index < d.Length; index++) d[index] *= scale;
        }

        public static AudioSource Source(GameObject targetObject, bool loop, float minDist, float maxDist)
        {
            var source = targetObject.AddComponent<AudioSource>();
            source.loop = loop;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = minDist;
            source.maxDistance = maxDist;
            source.dopplerLevel = 0f;
            source.volume = 0f;
            return source;
        }
    }
}
