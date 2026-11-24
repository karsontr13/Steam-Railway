using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Generated fallback sounds, used only when the recorded clips in the bundle are missing:
    /// rumble and whistle loops, rail clack, lever notch and door sounds, played on AudioSources of the train.
    internal static class SynthSounds
    {
        const int SampleRateInHertz = 44100;

        // ---- PlayCab, PlayLever, PlayValve, Source
        const float DefaultCabPitch = 1f;
        const float DefaultCabVolume = 0.8f;
        const float CabSourceMinimumDistanceInMeters = 1.5f;
        const float CabSourceMaximumDistanceInMeters = 25f;
        const float CabPitchSpreadMinimum = 0.96f;
        const float CabPitchSpreadMaximum = 1.04f;
        const float ValveRecordedPitch = 0.8f;
        const float ValveGeneratedPitch = 0.7f;
        const float ValveVolume = 0.45f;

        // ---- notch: a lever dropping into its notch (click of the latch, dull thud of the handle)
        const float NotchDurationInSeconds = 0.18f;
        const float NotchNoiseSmoothing = 0.5f;
        const float NotchClickDecayPerSecond = 260f;
        const float NotchThudFrequencyInHertz = 95f;
        const float NotchThudDecayPerSecond = 38f;
        const float NotchTickFrequencyInHertz = 2300f;
        const float NotchTickDecayPerSecond = 180f;
        const float NotchClickLevel = 0.9f;
        const float NotchThudLevel = 0.8f;
        const float NotchTickLevel = 0.15f;
        const float NotchPeak = 0.85f;

        // ---- door open: a steel latch released, then hinges creaking
        const float DoorOpenDurationInSeconds = 0.9f;
        const float DoorOpenNoiseSmoothing = 0.4f;
        const float DoorOpenLatchDecayPerSecond = 140f;
        const float DoorOpenLatchRingFrequencyInHertz = 1450f;
        const float DoorOpenLatchRingDecayPerSecond = 60f;
        const float DoorOpenCreakBaseFrequencyInHertz = 310f;
        const float DoorOpenCreakWobbleDepthInHertz = 90f;
        const float DoorOpenCreakWobbleRadiansPerSecond = 9f;
        const float DoorOpenCreakRisePerSecondInHertz = 140f;
        const float DoorOpenCreakStartInSeconds = 0.05f;
        const float DoorOpenCreakFadeInPerSecond = 8f;
        const float DoorOpenCreakDecayPerSecond = 3.2f;
        const float DoorOpenCreakAmplitude = 0.5f;
        const float DoorOpenCreakNoiseBase = 0.65f;
        const float DoorOpenCreakNoiseDepth = 0.35f;
        const float DoorOpenLatchLevel = 0.9f;
        const float DoorOpenLatchRingLevel = 0.2f;
        const float DoorOpenCreakLevel = 0.45f;
        const float DoorOpenPeak = 0.8f;

        // ---- door close: a heavy steel door slammed home (thud, ring of the plate, latch dropping)
        const float DoorCloseDurationInSeconds = 0.7f;
        const float DoorCloseNoiseSmoothing = 0.3f;
        const float DoorCloseThudFrequencyInHertz = 70f;
        const float DoorCloseThudDecayPerSecond = 22f;
        const float DoorCloseSlapDecayPerSecond = 75f;
        const float DoorClosePlateLowFrequencyInHertz = 410f;
        const float DoorClosePlateLowWeight = 0.6f;
        const float DoorClosePlateHighFrequencyInHertz = 880f;
        const float DoorClosePlateHighWeight = 0.4f;
        const float DoorClosePlateDecayPerSecond = 14f;
        const float DoorCloseLatchDelayInSeconds = 0.09f;
        const float DoorCloseLatchFrequencyInHertz = 1900f;
        const float DoorCloseLatchDecayPerSecond = 120f;
        const float DoorCloseThudLevel = 1.1f;
        const float DoorCloseSlapLevel = 0.8f;
        const float DoorClosePlateLevel = 0.35f;
        const float DoorCloseLatchLevel = 0.3f;
        const float DoorClosePeak = 0.9f;

        // ---- rumble: wheels rolling on the rails (4 s seamless loop of brown noise and a low hum)
        const int RumbleLengthInSeconds = 4;
        const int RumbleCrossfadeDivisor = 5;
        const float RumbleBrownStep = 0.02f;
        const float RumbleBrownLeak = 0.995f;
        const float RumbleNoiseSmoothing = 0.08f;
        const float RumbleHumLowFrequencyInHertz = 47f;
        const float RumbleHumLowLevel = 0.25f;
        const float RumbleHumHighFrequencyInHertz = 94f;
        const float RumbleHumHighLevel = 0.12f;
        const float RumbleBrownGain = 3.2f;
        const float RumbleNoiseGain = 0.35f;
        const float RumbleHumGain = 0.35f;
        const float RumblePeak = 0.7f;

        // ---- clack: a sharp knock with a short metallic ring (a wheel over a rail joint)
        const int ClackLengthDivisor = 4;
        const float ClackNoiseSmoothing = 0.35f;
        const float ClackKnockDecayPerSecond = 90f;
        const float ClackBodyFrequencyInHertz = 120f;
        const float ClackBodyDecayPerSecond = 30f;
        const float ClackRingLowFrequencyInHertz = 1180f;
        const float ClackRingLowWeight = 0.5f;
        const float ClackRingHighFrequencyInHertz = 1730f;
        const float ClackRingHighWeight = 0.35f;
        const float ClackRingDecayPerSecond = 45f;
        const float ClackKnockLevel = 1.2f;
        const float ClackBodyLevel = 0.8f;
        const float ClackRingLevel = 0.25f;
        const float ClackPeak = 0.9f;

        // ---- whistle: a three-chime steam whistle with a breathy edge (2 s loop)
        const int WhistleLengthInSeconds = 2;
        const int WhistleCrossfadeDivisor = 10;
        const int WhistleSecondHarmonic = 2;
        const int WhistleThirdHarmonic = 3;
        const float WhistleSecondHarmonicLevel = 0.3f;
        const float WhistleThirdHarmonicLevel = 0.12f;
        const float WhistleHissSmoothing = 0.6f;
        const float WhistleWobbleDepth = 0.06f;
        const float WhistleWobbleRate = 3f;
        const float WhistleToneGain = 0.3f;
        const float WhistleHissGain = 0.18f;
        const float WhistlePeak = 0.8f;
        static readonly int[] WhistleChordFrequenciesInHertz = { 349, 440, 523 };

        static AudioClip s_Rumble, s_Clack, s_Whistle, s_Notch, s_DoorOpen, s_DoorClose;
        static readonly System.Random s_Random = new System.Random(1234);

        public static AudioClip Rumble => s_Rumble ??= Make("SR_Rumble", MakeRumble());
        public static AudioClip Clack => s_Clack ??= Make("SR_Clack", MakeClack());
        public static AudioClip Whistle => s_Whistle ??= Make("SR_Whistle", MakeWhistle());
        public static AudioClip Notch => s_Notch ??= Make("SR_Notch", MakeNotch());
        public static AudioClip DoorOpen => s_DoorOpen ??= Make("SR_DoorOpen", MakeDoorOpen());
        public static AudioClip DoorClose => s_DoorClose ??= Make("SR_DoorClose", MakeDoorClose());

        /// Plays a short cab sound (lever notch, valve turn) at the cab, with a little natural variation.
        public static void PlayCab(Il2CppTLD.SteamTrain.SteamTrain train, AudioClip clip, float pitch = DefaultCabPitch, float volume = DefaultCabVolume)
        {
            if (train == null || clip == null) return;
            var targetObject = TrainSounds.Cab(train);
            var source = targetObject.GetComponent<AudioSource>() ?? Source(targetObject, false, CabSourceMinimumDistanceInMeters, CabSourceMaximumDistanceInMeters);
            source.volume = 1f;
            source.pitch = pitch * Random.Range(CabPitchSpreadMinimum, CabPitchSpreadMaximum);
            source.PlayOneShot(clip, volume * GameVolume.Scale);
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
            PlayCab(train, clip ?? Notch, clip != null ? ValveRecordedPitch : ValveGeneratedPitch, ValveVolume);
        }

        static float[] MakeNotch()
        {
            int count = (int)(SampleRateInHertz * NotchDurationInSeconds);
            var samples = new float[count];
            float smoothedNoise = 0f;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)SampleRateInHertz;
                smoothedNoise += (Noise() - smoothedNoise) * NotchNoiseSmoothing;
                float click = smoothedNoise * Mathf.Exp(-fraction * NotchClickDecayPerSecond);
                float thud = Mathf.Sin(2f * Mathf.PI * NotchThudFrequencyInHertz * fraction) * Mathf.Exp(-fraction * NotchThudDecayPerSecond);
                float tick = Mathf.Sin(2f * Mathf.PI * NotchTickFrequencyInHertz * fraction) * Mathf.Exp(-fraction * NotchTickDecayPerSecond);
                samples[index] = click * NotchClickLevel + thud * NotchThudLevel + tick * NotchTickLevel;
            }
            Normalize(samples, NotchPeak);
            return samples;
        }

        static float[] MakeDoorOpen()
        {
            int count = (int)(SampleRateInHertz * DoorOpenDurationInSeconds);
            var samples = new float[count];
            float smoothedNoise = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                smoothedNoise += (Noise() - smoothedNoise) * DoorOpenNoiseSmoothing;
                float latch = smoothedNoise * Mathf.Exp(-seconds * DoorOpenLatchDecayPerSecond);
                float latchRing = Mathf.Sin(2f * Mathf.PI * DoorOpenLatchRingFrequencyInHertz * seconds) * Mathf.Exp(-seconds * DoorOpenLatchRingDecayPerSecond);
                float creakPitch = DoorOpenCreakBaseFrequencyInHertz + DoorOpenCreakWobbleDepthInHertz * Mathf.Sin(seconds * DoorOpenCreakWobbleRadiansPerSecond) + DoorOpenCreakRisePerSecondInHertz * seconds;
                float creakLevel = Mathf.Clamp01((seconds - DoorOpenCreakStartInSeconds) * DoorOpenCreakFadeInPerSecond) * Mathf.Exp(-(seconds - DoorOpenCreakStartInSeconds) * DoorOpenCreakDecayPerSecond);
                float creak = Mathf.Sin(2f * Mathf.PI * creakPitch * seconds) * DoorOpenCreakAmplitude * creakLevel * (DoorOpenCreakNoiseBase + DoorOpenCreakNoiseDepth * Noise());
                samples[index] = latch * DoorOpenLatchLevel + latchRing * DoorOpenLatchRingLevel + creak * DoorOpenCreakLevel;
            }
            Normalize(samples, DoorOpenPeak);
            return samples;
        }

        static float[] MakeDoorClose()
        {
            int count = (int)(SampleRateInHertz * DoorCloseDurationInSeconds);
            var samples = new float[count];
            float smoothedNoise = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                smoothedNoise += (Noise() - smoothedNoise) * DoorCloseNoiseSmoothing;
                float thud = Mathf.Sin(2f * Mathf.PI * DoorCloseThudFrequencyInHertz * seconds) * Mathf.Exp(-seconds * DoorCloseThudDecayPerSecond);
                float slap = smoothedNoise * Mathf.Exp(-seconds * DoorCloseSlapDecayPerSecond);
                float plate = (DoorClosePlateLowWeight * Mathf.Sin(2f * Mathf.PI * DoorClosePlateLowFrequencyInHertz * seconds) + DoorClosePlateHighWeight * Mathf.Sin(2f * Mathf.PI * DoorClosePlateHighFrequencyInHertz * seconds)) * Mathf.Exp(-seconds * DoorClosePlateDecayPerSecond);
                float latchSeconds = seconds - DoorCloseLatchDelayInSeconds;
                float latch = latchSeconds > 0f ? Mathf.Sin(2f * Mathf.PI * DoorCloseLatchFrequencyInHertz * latchSeconds) * Mathf.Exp(-latchSeconds * DoorCloseLatchDecayPerSecond) : 0f;
                samples[index] = thud * DoorCloseThudLevel + slap * DoorCloseSlapLevel + plate * DoorClosePlateLevel + latch * DoorCloseLatchLevel;
            }
            Normalize(samples, DoorClosePeak);
            return samples;
        }

        static AudioClip Make(string clipName, float[] samples)
        {
            var clip = AudioClip.Create(clipName, samples.Length, 1, SampleRateInHertz, false);
            clip.SetData(new Il2CppStructArray<float>(samples), 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(clip);
            return clip;
        }

        static float Noise() => (float)(s_Random.NextDouble() * 2.0 - 1.0);

        static float[] MakeRumble()
        {
            int count = SampleRateInHertz * RumbleLengthInSeconds;
            var samples = new float[count];
            float brown = 0f, smoothedNoise = 0f;
            for (int index = 0; index < count; index++)
            {
                brown = (brown + RumbleBrownStep * Noise()) * RumbleBrownLeak;
                smoothedNoise += (Noise() - smoothedNoise) * RumbleNoiseSmoothing;
                float fraction = index / (float)SampleRateInHertz;
                float hum = RumbleHumLowLevel * Mathf.Sin(2f * Mathf.PI * RumbleHumLowFrequencyInHertz * fraction) + RumbleHumHighLevel * Mathf.Sin(2f * Mathf.PI * RumbleHumHighFrequencyInHertz * fraction);
                samples[index] = brown * RumbleBrownGain + smoothedNoise * RumbleNoiseGain + hum * RumbleHumGain;
            }
            samples = Crossfade(samples, SampleRateInHertz / RumbleCrossfadeDivisor);
            Normalize(samples, RumblePeak);
            return samples;
        }

        static float[] MakeClack()
        {
            int count = SampleRateInHertz / ClackLengthDivisor;
            var samples = new float[count];
            float smoothedNoise = 0f;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)SampleRateInHertz;
                smoothedNoise += (Noise() - smoothedNoise) * ClackNoiseSmoothing;
                float knock = smoothedNoise * Mathf.Exp(-fraction * ClackKnockDecayPerSecond);
                float body = Mathf.Sin(2f * Mathf.PI * ClackBodyFrequencyInHertz * fraction) * Mathf.Exp(-fraction * ClackBodyDecayPerSecond);
                float ring = (ClackRingLowWeight * Mathf.Sin(2f * Mathf.PI * ClackRingLowFrequencyInHertz * fraction) + ClackRingHighWeight * Mathf.Sin(2f * Mathf.PI * ClackRingHighFrequencyInHertz * fraction)) * Mathf.Exp(-fraction * ClackRingDecayPerSecond);
                samples[index] = knock * ClackKnockLevel + body * ClackBodyLevel + ring * ClackRingLevel;
            }
            Normalize(samples, ClackPeak);
            return samples;
        }

        static float[] MakeWhistle()
        {
            int count = SampleRateInHertz * WhistleLengthInSeconds;
            var samples = new float[count];
            float hiss = 0f;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)SampleRateInHertz;
                float tone = 0f;
                foreach (int frequencyInHertz in WhistleChordFrequenciesInHertz)
                    tone += Mathf.Sin(2f * Mathf.PI * frequencyInHertz * fraction) + WhistleSecondHarmonicLevel * Mathf.Sin(2f * Mathf.PI * frequencyInHertz * WhistleSecondHarmonic * fraction) + WhistleThirdHarmonicLevel * Mathf.Sin(2f * Mathf.PI * frequencyInHertz * WhistleThirdHarmonic * fraction);
                hiss += (Noise() - hiss) * WhistleHissSmoothing;
                float wobble = 1f + WhistleWobbleDepth * Mathf.Sin(2f * Mathf.PI * WhistleWobbleRate * fraction);
                samples[index] = tone * WhistleToneGain * wobble + hiss * WhistleHissGain;
            }
            samples = Crossfade(samples, SampleRateInHertz / WhistleCrossfadeDivisor);
            Normalize(samples, WhistlePeak);
            return samples;
        }

        /// Fades the last `fadeLength` samples into the first ones and cuts them off, so a looping clip has no click.
        static float[] Crossfade(float[] samples, int fadeLength)
        {
            int count = samples.Length;
            for (int index = 0; index < fadeLength; index++)
            {
                float blend = index / (float)fadeLength;
                samples[index] = samples[index] * blend + samples[count - fadeLength + index] * (1f - blend);
            }
            System.Array.Resize(ref samples, count - fadeLength);
            return samples;
        }

        static void Normalize(float[] samples, float peak)
        {
            float loudest = 0f;
            foreach (var sample in samples) loudest = Mathf.Max(loudest, Mathf.Abs(sample));
            if (loudest <= 0f) return;
            float scale = peak / loudest;
            for (int index = 0; index < samples.Length; index++) samples[index] *= scale;
        }

        public static AudioSource Source(GameObject targetObject, bool loop, float minimumDistanceInMeters, float maximumDistanceInMeters)
        {
            var source = targetObject.AddComponent<AudioSource>();
            source.loop = loop;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = minimumDistanceInMeters;
            source.maxDistance = maximumDistanceInMeters;
            source.dopplerLevel = 0f;
            source.volume = 0f;
            return source;
        }
    }
}
