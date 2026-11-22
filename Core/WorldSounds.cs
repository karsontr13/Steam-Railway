using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Generated sounds for the world around the railway: a dynamite blast, the crash of the train against an
    /// obstacle or a wagon, a fallen tree, bridge work and a rolling wagon's rail joints, plus the camera shake of a crash.
    internal static class WorldSounds
    {
        const int SampleRateInHertz = 44100;

        // ---- playing
        const float ExplosionMinimumDistanceInMeters = 25f;
        const float ExplosionMaximumDistanceInMeters = 450f;
        const float CrashMinimumDistanceInMeters = 8f;
        const float CrashMaximumDistanceInMeters = 120f;
        const float OneShotMinimumDistanceInMeters = 3f;
        const float OneShotMaximumDistanceInMeters = 60f;
        const float CrashVolumeBase = 0.55f;
        const float CrashVolumeGain = 0.45f;
        const float CrashPitchBase = 1.15f;
        const float CrashPitchDrop = 0.35f;
        const float TreeHitVolumeBase = 0.5f;
        const float TreeHitVolumeGain = 0.5f;
        const float TreeHitPitchBase = 1.1f;
        const float TreeHitPitchDrop = 0.25f;
        const float MinimumPitchForLifetime = 0.3f;
        const float OneShotLifetimeMarginInSeconds = 0.5f;

        // ---- camera shake
        const float MinimumShakeStrength = 0.02f;
        const float CameraKickScale = 0.12f;
        const float CameraRollScale = 2.5f;
        const float CameraKickSidewaysRange = 0.5f;
        const float CameraKickDownwards = -0.8f;
        const float CameraKickForwardMinimum = 0.4f;
        const float CameraKickForwardMaximum = 1f;
        const float CameraRollRange = 1f;

        // ---- explosion: crack of the charge, a deep boom with a falling pitch, a rolling rumble, rocks falling back
        const float ExplosionDurationInSeconds = 4f;
        const float ExplosionCrackDecayPerSecond = 55f;
        const float ExplosionCrackLevel = 1.4f;
        const float ExplosionBoomEndFrequencyInHertz = 30f;
        const float ExplosionBoomSweepInHertz = 50f;
        const float ExplosionBoomSweepDecayPerSecond = 3.5f;
        const float ExplosionBoomDecayPerSecond = 1.6f;
        const float ExplosionBoomLevel = 1.1f;
        const float ExplosionBodyFirstSmoothing = 0.12f;
        const float ExplosionBodySecondSmoothing = 0.2f;
        const float ExplosionBodyDecayPerSecond = 1.1f;
        const float ExplosionBodyLevel = 3.2f;
        const int ExplosionRockCount = 70;
        const float ExplosionRockEarliestInSeconds = 0.35f;
        const float ExplosionRockSpreadInSeconds = 3.2f;
        const float ExplosionRockLevelBase = 0.12f;
        const float ExplosionRockLevelRange = 0.3f;
        const float ExplosionRockFrequencyBaseInHertz = 70f;
        const float ExplosionRockFrequencyRangeInHertz = 220f;
        const int ExplosionRockLengthDivisor = 8;
        const float ExplosionRockThudDecayPerSecond = 40f;
        const float ExplosionRockNoiseLevel = 0.4f;
        const float ExplosionRockNoiseDecayPerSecond = 90f;
        const float ExplosionRockFadeWithStart = 0.6f;
        const float ExplosionTailStartInSeconds = 3.6f;
        const float ExplosionTailLengthInSeconds = 0.4f;
        const float ExplosionPeak = 0.97f;

        // ---- bridge work: hammer blows on timber, two stretches of sawing and a metallic clink (a 3.2 s loop)
        const float BridgeWorkDurationInSeconds = 3.2f;
        const float BridgeWorkFirstBlowInSeconds = 0.1f;
        const float BridgeWorkLastBlowMarginInSeconds = 0.4f;
        const float BridgeWorkBlowSpacingInSeconds = 0.4f;
        const float BridgeWorkBlowSpacingJitterInSeconds = 0.15f;
        const float BridgeWorkBlowLevelBase = 0.75f;
        const float BridgeWorkBlowLevelRange = 0.25f;
        const float BridgeWorkBlowFrequencyBaseInHertz = 150f;
        const float BridgeWorkBlowFrequencyRangeInHertz = 60f;
        const int BridgeWorkBlowLengthDivisor = 3;
        const float BridgeWorkKnockDecayPerSecond = 38f;
        const float BridgeWorkRingHarmonic = 3.1f;
        const float BridgeWorkRingDecayPerSecond = 70f;
        const float BridgeWorkRingLevel = 0.35f;
        const float BridgeWorkTickDecayPerSecond = 260f;
        const float BridgeWorkTickLevel = 0.6f;
        const float BridgeWorkSawFirstStartInSeconds = 0.9f;
        const float BridgeWorkSawFirstEndInSeconds = 1.5f;
        const float BridgeWorkSawSecondStartInSeconds = 2.3f;
        const float BridgeWorkSawSecondEndInSeconds = 2.9f;
        const float BridgeWorkSawSmoothing = 0.55f;
        const float BridgeWorkSawStrokesPerSecond = 7f;
        const float BridgeWorkSawLevel = 0.22f;
        const float BridgeWorkClinkStartInSeconds = 1.95f;
        const int BridgeWorkClinkLengthDivisor = 2;
        const float BridgeWorkClinkLowFrequencyInHertz = 1850f;
        const float BridgeWorkClinkHighFrequencyInHertz = 2710f;
        const float BridgeWorkClinkHighWeight = 0.6f;
        const float BridgeWorkClinkDecayPerSecond = 22f;
        const float BridgeWorkClinkLevel = 0.18f;
        const float BridgeWorkPeak = 0.9f;
        const int BridgeWorkFadeSamples = 800;

        // ---- crash: metal slamming into metal and rock (a heavy thud, a burst of noise, a ringing of the frame)
        const float CrashDurationInSeconds = 0.9f;
        const float CrashNoiseSmoothing = 0.3f;
        const float CrashThudBaseFrequencyInHertz = 70f;
        const float CrashThudSweepInHertz = 40f;
        const float CrashThudSweepDecayPerSecond = 30f;
        const float CrashThudDecayPerSecond = 14f;
        const float CrashThudLevel = 1.3f;
        const float CrashBurstDecayPerSecond = 40f;
        const float CrashBurstLevel = 1.5f;
        const float CrashPartialDecayBase = 7f;
        const float CrashPartialDecayStep = 3f;
        const float CrashRingLevel = 0.45f;
        const float CrashPeak = 0.95f;
        static readonly float[] CrashPartialFrequenciesInHertz = { 210f, 337f, 521f, 760f, 1140f };

        // ---- tree hit: a heavy trunk struck and splintering (thump, wood cracks, snapping branches, a dump of snow)
        const float TreeHitDurationInSeconds = 1.4f;
        const float TreeHitBodyFirstSmoothing = 0.18f;
        const float TreeHitBodySecondSmoothing = 0.25f;
        const float TreeHitHighpassSmoothing = 0.6f;
        const float TreeHitThumpBaseFrequencyInHertz = 58f;
        const float TreeHitThumpDropInHertz = 20f;
        const float TreeHitThumpDropRatePerSecond = 6f;
        const float TreeHitThumpDecayPerSecond = 8f;
        const float TreeHitThumpLevel = 0.9f;
        const float TreeHitSnowDecayPerSecond = 2.6f;
        const float TreeHitSnowStartInSeconds = 0.05f;
        const float TreeHitSnowRampPerSecond = 12f;
        const float TreeHitSnowLevel = 1.6f;
        const float TreeHitRustleSmoothing = 0.5f;
        const float TreeHitBranchDecayPerSecond = 3.2f;
        const float TreeHitBranchBase = 0.4f;
        const float TreeHitBranchGain = 0.6f;
        const float TreeHitBranchRadiansPerSecond = 53f;
        const float TreeHitBranchLevel = 0.45f;
        const float TreeHitCrackWindowInSeconds = 0.12f;
        const float TreeHitCrackStartFrequencyInHertz = 1500f;
        const float TreeHitCrackDropInHertz = 1100f;
        const float TreeHitCrackDropRatePerSecond = 14f;
        const float TreeHitCrackNoiseWeight = 0.9f;
        const float TreeHitCrackToneWeight = 0.5f;
        const float TreeHitCrackDecayBase = 90f;
        const float TreeHitCrackDecayStep = 25f;
        const float TreeHitCracksLevel = 0.9f;
        const float TreeHitTailStartInSeconds = 1.1f;
        const float TreeHitTailLengthInSeconds = 0.3f;
        const float TreeHitPeak = 0.92f;
        static readonly float[] TreeHitCrackTimesInSeconds = { 0.012f, 0.05f, 0.11f, 0.19f, 0.31f, 0.47f };
        static readonly float[] TreeHitCrackLevels = { 1.0f, 0.8f, 0.7f, 0.55f, 0.4f, 0.28f };

        // ---- wagon joint: a heavy freight wheel crossing a rail joint (two thumps and a short creak)
        const float WagonJointDurationInSeconds = 0.4f;
        const float WagonJointSecondWheelDelayInSeconds = 0.045f;
        const float WagonJointNoiseSmoothing = 0.25f;
        const float WagonJointFirstFrequencyInHertz = 95f;
        const float WagonJointFirstDecayPerSecond = 26f;
        const float WagonJointFirstNoiseDecayPerSecond = 80f;
        const float WagonJointFirstNoiseLevel = 0.8f;
        const float WagonJointSecondFrequencyInHertz = 80f;
        const float WagonJointSecondDecayPerSecond = 30f;
        const float WagonJointSecondNoiseDecayPerSecond = 90f;
        const float WagonJointSecondNoiseLevel = 0.7f;
        const float WagonJointSecondLevel = 0.8f;
        const float WagonJointRingLowWeight = 0.3f;
        const float WagonJointRingLowFrequencyInHertz = 640f;
        const float WagonJointRingHighWeight = 0.2f;
        const float WagonJointRingHighFrequencyInHertz = 910f;
        const float WagonJointRingDecayPerSecond = 38f;
        const float WagonJointRingLevel = 0.25f;
        const float WagonJointPeak = 0.85f;

        static AudioClip s_Explosion, s_Crash, s_WagonJoint, s_TreeHit, s_BridgeWork, s_Tinnitus;
        static readonly System.Random s_Random = new System.Random(4711);

        public static AudioClip Explosion => s_Explosion ??= Make("SR_Explosion", MakeExplosion());
        public static AudioClip Crash => s_Crash ??= Make("SR_Crash", MakeCrash());
        public static AudioClip TreeHit => s_TreeHit ??= Make("SR_TreeHit", MakeTreeHit());
        /// A looping workshop sound: hammer blows on timber with sawing and the clink of metal in between.
        public static AudioClip BridgeWork => s_BridgeWork ??= Make("SR_BridgeWork", MakeBridgeWork());
        /// The ringing in the ears after a blast: two high thin tones with a slow wavering, fading out.
        public static AudioClip Tinnitus => s_Tinnitus ??= Make("SR_Tinnitus", MakeTinnitus());
        public static AudioClip WagonJoint => s_WagonJoint ??= Make("SR_WagonJoint", MakeWagonJoint());

        /// A blast heard from a place in the world (a one-shot source that removes itself).
        public static void PlayExplosion(Vector3 position)
        {
            PlayAt(Explosion, position, 1f, ExplosionMinimumDistanceInMeters, ExplosionMaximumDistanceInMeters, 1f);
        }

        /// The crash of a hard knock at a place; `strength` 0..1 sets volume and pitch.
        public static void PlayCrash(Vector3 position, float strength)
        {
            strength = Mathf.Clamp01(strength);
            PlayAt(Crash, position, CrashVolumeBase + CrashVolumeGain * strength, CrashMinimumDistanceInMeters, CrashMaximumDistanceInMeters, CrashPitchBase - CrashPitchDrop * strength);
        }

        /// The train breaking a fallen tree out of the way; `strength` 0..1 sets volume and pitch.
        public static void PlayTreeHit(Vector3 position, float strength)
        {
            strength = Mathf.Clamp01(strength);
            PlayAt(TreeHit, position, TreeHitVolumeBase + TreeHitVolumeGain * strength, CrashMinimumDistanceInMeters, CrashMaximumDistanceInMeters, TreeHitPitchBase - TreeHitPitchDrop * strength);
        }

        const float TinnitusFullVolume = 0.55f;
        const float TinnitusMinimumVolume = 0.12f;

        /// The ringing in the player's ears (a flat, non-spatial sound); `strength` 0..1 sets how loud it starts.
        public static void PlayTinnitus(float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= 0.01f) return;
            var holder = new GameObject("SR_Tinnitus");
            var source = holder.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.PlayOneShot(Tinnitus, Mathf.Lerp(TinnitusMinimumVolume, TinnitusFullVolume, strength));
            Object.Destroy(holder, Tinnitus.length + 0.5f);
        }

        /// Any clip as a short 3D sound at a place.
        public static void PlayOneShotAt(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            PlayAt(clip, position, volume, OneShotMinimumDistanceInMeters, OneShotMaximumDistanceInMeters, pitch);
        }

        static void PlayAt(AudioClip clip, Vector3 position, float volume, float minimumDistance, float maximumDistance, float pitch)
        {
            if (clip == null) return;
            var holder = new GameObject("SR_OneShotSound");
            holder.transform.position = position;
            var source = SynthSounds.Source(holder, false, minimumDistance, maximumDistance);
            source.volume = 1f;
            source.pitch = pitch;
            source.PlayOneShot(clip, volume);
            Object.Destroy(holder, clip.length / Mathf.Max(MinimumPitchForLifetime, pitch) + OneShotLifetimeMarginInSeconds);
        }

        // ------------------------------------------------------------------ camera shake

        /// The player's view is shaken by a crash: a kick along the view and a roll, scaled by `strength` (0..1).
        public static void ShakeCamera(float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= MinimumShakeStrength) return;
            try
            {
                var camera = Il2Cpp.GameManager.GetVpFPSCamera();
                if (camera == null) return;
                float kick = CameraKickScale * strength;
                camera.AddForce2(Random.Range(-CameraKickSidewaysRange, CameraKickSidewaysRange) * kick, CameraKickDownwards * kick, Random.Range(CameraKickForwardMinimum, CameraKickForwardMaximum) * kick);
                camera.AddRollForce(Random.Range(-CameraRollRange, CameraRollRange) * CameraRollScale * strength);
            }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Camera shake: {exception.Message}"); }
        }

        // ------------------------------------------------------------------ the clips

        static AudioClip Make(string clipName, float[] samples)
        {
            var clip = AudioClip.Create(clipName, samples.Length, 1, SampleRateInHertz, false);
            clip.SetData(new Il2CppStructArray<float>(samples), 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(clip);
            return clip;
        }

        static float Noise() => (float)(s_Random.NextDouble() * 2.0 - 1.0);

        static void Normalize(float[] samples, float peak)
        {
            float highest = 0f;
            foreach (var sample in samples) highest = Mathf.Max(highest, Mathf.Abs(sample));
            if (highest <= 0f) return;
            float scale = peak / highest;
            for (int index = 0; index < samples.Length; index++) samples[index] *= scale;
        }

        static float[] MakeExplosion()
        {
            int count = (int)(SampleRateInHertz * ExplosionDurationInSeconds);
            var samples = new float[count];
            float lowpassed = 0f, rumble = 0f;
            float sweepPhase = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                float noise = Noise();
                // crack: a few milliseconds of loud, bright noise
                float crack = noise * Mathf.Exp(-seconds * ExplosionCrackDecayPerSecond) * ExplosionCrackLevel;
                // boom: the pitch falls from 80 to 30 Hz while it dies away
                float frequency = ExplosionBoomEndFrequencyInHertz + ExplosionBoomSweepInHertz * Mathf.Exp(-seconds * ExplosionBoomSweepDecayPerSecond);
                sweepPhase += 2f * Mathf.PI * frequency / SampleRateInHertz;
                float boom = Mathf.Sin(sweepPhase) * Mathf.Exp(-seconds * ExplosionBoomDecayPerSecond) * ExplosionBoomLevel;
                // body: noise softened twice, a thick blast of air
                lowpassed += (noise - lowpassed) * ExplosionBodyFirstSmoothing;
                rumble += (lowpassed - rumble) * ExplosionBodySecondSmoothing;
                float body = rumble * Mathf.Exp(-seconds * ExplosionBodyDecayPerSecond) * ExplosionBodyLevel;
                samples[index] = crack + boom + body;
            }
            // rocks falling back: sparse clicks and thuds that thin out
            for (int rock = 0; rock < ExplosionRockCount; rock++)
            {
                float start = ExplosionRockEarliestInSeconds + (float)s_Random.NextDouble() * (float)s_Random.NextDouble() * ExplosionRockSpreadInSeconds;
                int startIndex = (int)(start * SampleRateInHertz);
                float level = ExplosionRockLevelBase + ExplosionRockLevelRange * (float)s_Random.NextDouble();
                float pitch = ExplosionRockFrequencyBaseInHertz + ExplosionRockFrequencyRangeInHertz * (float)s_Random.NextDouble();
                for (int offset = 0; offset < SampleRateInHertz / ExplosionRockLengthDivisor && startIndex + offset < count; offset++)
                {
                    float fraction = offset / (float)SampleRateInHertz;
                    float thud = Mathf.Sin(2f * Mathf.PI * pitch * fraction) * Mathf.Exp(-fraction * ExplosionRockThudDecayPerSecond);
                    samples[startIndex + offset] += level * (thud + ExplosionRockNoiseLevel * Noise() * Mathf.Exp(-fraction * ExplosionRockNoiseDecayPerSecond)) * Mathf.Exp(-start * ExplosionRockFadeWithStart);
                }
            }
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                if (seconds > ExplosionTailStartInSeconds) samples[index] *= Mathf.Clamp01((ExplosionDurationInSeconds - seconds) / ExplosionTailLengthInSeconds);
            }
            Normalize(samples, ExplosionPeak);
            return samples;
        }

        static float[] MakeBridgeWork()
        {
            int count = (int)(SampleRateInHertz * BridgeWorkDurationInSeconds);
            var samples = new float[count];
            float time = BridgeWorkFirstBlowInSeconds;
            while (time < BridgeWorkDurationInSeconds - BridgeWorkLastBlowMarginInSeconds)
            {
                int startIndex = (int)(time * SampleRateInHertz);
                float level = BridgeWorkBlowLevelBase + BridgeWorkBlowLevelRange * (float)s_Random.NextDouble();
                float pitch = BridgeWorkBlowFrequencyBaseInHertz + BridgeWorkBlowFrequencyRangeInHertz * (float)s_Random.NextDouble();
                for (int offset = 0; offset < SampleRateInHertz / BridgeWorkBlowLengthDivisor && startIndex + offset < count; offset++)
                {
                    float fraction = offset / (float)SampleRateInHertz;
                    float knock = Mathf.Sin(2f * Mathf.PI * pitch * fraction) * Mathf.Exp(-fraction * BridgeWorkKnockDecayPerSecond);
                    float ring = Mathf.Sin(2f * Mathf.PI * pitch * BridgeWorkRingHarmonic * fraction) * Mathf.Exp(-fraction * BridgeWorkRingDecayPerSecond) * BridgeWorkRingLevel;
                    float tick = Noise() * Mathf.Exp(-fraction * BridgeWorkTickDecayPerSecond) * BridgeWorkTickLevel;
                    samples[startIndex + offset] += level * (knock + ring + tick);
                }
                time += BridgeWorkBlowSpacingInSeconds + BridgeWorkBlowSpacingJitterInSeconds * (float)s_Random.NextDouble();
            }
            // sawing: noise whose loudness swings back and forth about 7 times a second
            float lowpassed = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                bool sawing = (seconds > BridgeWorkSawFirstStartInSeconds && seconds < BridgeWorkSawFirstEndInSeconds) || (seconds > BridgeWorkSawSecondStartInSeconds && seconds < BridgeWorkSawSecondEndInSeconds);
                if (!sawing) continue;
                lowpassed += (Noise() - lowpassed) * BridgeWorkSawSmoothing;
                float stroke = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * BridgeWorkSawStrokesPerSecond * seconds);
                samples[index] += lowpassed * stroke * BridgeWorkSawLevel;
            }
            // a metallic clink
            int clinkIndex = (int)(BridgeWorkClinkStartInSeconds * SampleRateInHertz);
            for (int offset = 0; offset < SampleRateInHertz / BridgeWorkClinkLengthDivisor && clinkIndex + offset < count; offset++)
            {
                float fraction = offset / (float)SampleRateInHertz;
                samples[clinkIndex + offset] += (Mathf.Sin(2f * Mathf.PI * BridgeWorkClinkLowFrequencyInHertz * fraction) + BridgeWorkClinkHighWeight * Mathf.Sin(2f * Mathf.PI * BridgeWorkClinkHighFrequencyInHertz * fraction)) * Mathf.Exp(-fraction * BridgeWorkClinkDecayPerSecond) * BridgeWorkClinkLevel;
            }
            Normalize(samples, BridgeWorkPeak);
            for (int index = 0; index < BridgeWorkFadeSamples; index++)
            {
                float fade = index / (float)BridgeWorkFadeSamples;
                samples[index] *= fade;
                samples[count - 1 - index] *= fade;
            }
            return samples;
        }

        /// 9 s: a 4.1 kHz and a 6.3 kHz sine with a slow wavering in loudness; a quick rise, a long steady stretch, then a slow fade.
        static float[] MakeTinnitus()
        {
            const float Seconds = 9f, RiseSeconds = 0.15f, HoldSeconds = 3.5f;
            int count = (int)(SampleRateInHertz * Seconds);
            var data = new float[count];
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                float envelope = seconds < RiseSeconds ? seconds / RiseSeconds
                    : seconds < HoldSeconds ? 1f
                    : Mathf.Pow(1f - (seconds - HoldSeconds) / (Seconds - HoldSeconds), 2f);
                float wavering = 0.85f + 0.15f * Mathf.Sin(2f * Mathf.PI * 0.7f * seconds);
                float tone = Mathf.Sin(2f * Mathf.PI * 4100f * seconds) + 0.45f * Mathf.Sin(2f * Mathf.PI * 6300f * seconds);
                data[index] = tone * envelope * wavering;
            }
            Normalize(data, 0.8f);
            return data;
        }

        static float[] MakeCrash()
        {
            int count = (int)(SampleRateInHertz * CrashDurationInSeconds);
            var samples = new float[count];
            float lowpassed = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                lowpassed += (Noise() - lowpassed) * CrashNoiseSmoothing;
                float thud = Mathf.Sin(2f * Mathf.PI * (CrashThudBaseFrequencyInHertz + CrashThudSweepInHertz * Mathf.Exp(-seconds * CrashThudSweepDecayPerSecond)) * seconds) * Mathf.Exp(-seconds * CrashThudDecayPerSecond) * CrashThudLevel;
                float burst = lowpassed * Mathf.Exp(-seconds * CrashBurstDecayPerSecond) * CrashBurstLevel;
                float ring = 0f;
                for (int partial = 0; partial < CrashPartialFrequenciesInHertz.Length; partial++)
                    ring += Mathf.Sin(2f * Mathf.PI * CrashPartialFrequenciesInHertz[partial] * seconds) * Mathf.Exp(-seconds * (CrashPartialDecayBase + CrashPartialDecayStep * partial)) / (1f + partial);
                samples[index] = thud + burst + ring * CrashRingLevel;
            }
            Normalize(samples, CrashPeak);
            return samples;
        }

        static float[] MakeTreeHit()
        {
            int count = (int)(SampleRateInHertz * TreeHitDurationInSeconds);
            var samples = new float[count];
            float lowpassed = 0f, softened = 0f, rustle = 0f, highpassBase = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                float noise = Noise();
                lowpassed += (noise - lowpassed) * TreeHitBodyFirstSmoothing;
                softened += (lowpassed - softened) * TreeHitBodySecondSmoothing;
                highpassBase += (noise - highpassBase) * TreeHitHighpassSmoothing;
                float bright = noise - highpassBase;
                // the body of the blow: a dull thump
                float thump = Mathf.Sin(2f * Mathf.PI * (TreeHitThumpBaseFrequencyInHertz - TreeHitThumpDropInHertz * Mathf.Min(1f, seconds * TreeHitThumpDropRatePerSecond)) * seconds) * Mathf.Exp(-seconds * TreeHitThumpDecayPerSecond) * TreeHitThumpLevel;
                // snow and splinters falling: a soft, rushing noise
                float snow = softened * Mathf.Exp(-seconds * TreeHitSnowDecayPerSecond) * (seconds > TreeHitSnowStartInSeconds ? Mathf.Min(1f, (seconds - TreeHitSnowStartInSeconds) * TreeHitSnowRampPerSecond) : 0f) * TreeHitSnowLevel;
                // thin branches whipping and snapping
                rustle += (bright - rustle) * TreeHitRustleSmoothing;
                float branches = rustle * Mathf.Exp(-seconds * TreeHitBranchDecayPerSecond) * (TreeHitBranchBase + TreeHitBranchGain * Mathf.Abs(Mathf.Sin(seconds * TreeHitBranchRadiansPerSecond))) * TreeHitBranchLevel;
                // wood cracks: short, bright, each with a falling pitch
                float cracks = 0f;
                for (int crack = 0; crack < TreeHitCrackTimesInSeconds.Length; crack++)
                {
                    float later = seconds - TreeHitCrackTimesInSeconds[crack];
                    if (later < 0f || later > TreeHitCrackWindowInSeconds) continue;
                    float pitch = TreeHitCrackStartFrequencyInHertz - TreeHitCrackDropInHertz * Mathf.Min(1f, later * TreeHitCrackDropRatePerSecond);
                    cracks += (bright * TreeHitCrackNoiseWeight + Mathf.Sin(2f * Mathf.PI * pitch * later) * TreeHitCrackToneWeight) * Mathf.Exp(-later * (TreeHitCrackDecayBase + TreeHitCrackDecayStep * crack)) * TreeHitCrackLevels[crack];
                }
                samples[index] = thump + snow + branches + cracks * TreeHitCracksLevel;
            }
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                if (seconds > TreeHitTailStartInSeconds) samples[index] *= Mathf.Clamp01((TreeHitDurationInSeconds - seconds) / TreeHitTailLengthInSeconds);
            }
            Normalize(samples, TreeHitPeak);
            return samples;
        }

        static float[] MakeWagonJoint()
        {
            int count = (int)(SampleRateInHertz * WagonJointDurationInSeconds);
            var samples = new float[count];
            float lowpassed = 0f;
            for (int index = 0; index < count; index++)
            {
                float seconds = index / (float)SampleRateInHertz;
                lowpassed += (Noise() - lowpassed) * WagonJointNoiseSmoothing;
                float first = Mathf.Sin(2f * Mathf.PI * WagonJointFirstFrequencyInHertz * seconds) * Mathf.Exp(-seconds * WagonJointFirstDecayPerSecond) + lowpassed * Mathf.Exp(-seconds * WagonJointFirstNoiseDecayPerSecond) * WagonJointFirstNoiseLevel;
                float later = seconds - WagonJointSecondWheelDelayInSeconds;
                float second = later > 0f ? (Mathf.Sin(2f * Mathf.PI * WagonJointSecondFrequencyInHertz * later) * Mathf.Exp(-later * WagonJointSecondDecayPerSecond) + lowpassed * Mathf.Exp(-later * WagonJointSecondNoiseDecayPerSecond) * WagonJointSecondNoiseLevel) * WagonJointSecondLevel : 0f;
                float ring = (WagonJointRingLowWeight * Mathf.Sin(2f * Mathf.PI * WagonJointRingLowFrequencyInHertz * seconds) + WagonJointRingHighWeight * Mathf.Sin(2f * Mathf.PI * WagonJointRingHighFrequencyInHertz * seconds)) * Mathf.Exp(-seconds * WagonJointRingDecayPerSecond);
                samples[index] = first + second + ring * WagonJointRingLevel;
            }
            Normalize(samples, WagonJointPeak);
            return samples;
        }
    }
}
