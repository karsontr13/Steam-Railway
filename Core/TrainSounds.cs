using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// Locomotive sounds: base-game Wwise events, the mod's recordings and, when the player's installation has
    /// them, the Wintermute episode 5 steam train sounds (StoryTrainBank).
    internal static class TrainSounds
    {
        const int DefaultStopFadeInMilliseconds = 400;
        const int TimedStopFadeInMilliseconds = 300;
        static readonly Vector3 CabEmitterPosition = new Vector3(0f, 2.6f, -2.6f);
        static readonly Vector3 DoorEmitterPosition = new Vector3(1.5f, 2.4f, -2.35f);
        internal static readonly Vector3 BoilerEmitterPosition = BoilerEmitterPosition;

        // One-shots
        public const string PourWater = "PLAY_PUTINPOTWATER";
        public const string Snow = "PLAY_PUTINPOTSNOW";
        public const string PumpWater = "PLAY_LIQUIDGLUGA";
        public const string Repair = "PLAY_REPAIRINGMETAL";
        const string ExplosionRumble = "PLAY_AMBIENTOBJECTS_ROCKFALLSMEDIUM_MINE";
        const string MetalGroan = "PLAY_METALGROANMETALMEDIUM";
        /// Couplers locking together: the story train's lever lock when its sounds are loaded, else a metal latch.
        public static string CouplerLock => StoryTrainBank.Available ? "Play_SteamTrain_GearLeverLocked" : "Play_SndMechDoorMetalLocked01";
        /// Couplers letting go: the same catch as locking.
        public static string CouplerRelease => CouplerLock;
        /// Throwing a switch lever: the story train's heavy lever when its sounds are loaded, else a metal groan.
        public static string SwitchThrow => StoryTrainBank.Available ? "Play_SteamTrain_GearLeverDown" : MetalGroan;
        // Loops (stopped by playing id)
        public const string SteamHiss = "PLAY_STEAMTUNNELS";
        public const string Simmer = "PLAY_BOILINGLIQUIDTHICKLIGHT";

        public static uint Play(string eventName, GameObject targetObject)
        {
            if (targetObject == null) return 0;
            try { return GameAudioManager.PlaySound(eventName, targetObject); }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Sound {eventName} failed: {exception.Message}"); return 0; }
        }

        /// A looping game sound that is played for a while and then faded out.
        public static void PlayFor(string eventName, GameObject targetObject, float seconds)
        {
            uint identifier = Play(eventName, targetObject);
            if (identifier != 0) MelonLoader.MelonCoroutines.Start(StopAfter(identifier, seconds));
        }

        static System.Collections.IEnumerator StopAfter(uint identifier, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Stop(ref identifier, TimedStopFadeInMilliseconds);
        }

        public static void Stop(ref uint identifier, int fadeInMilliseconds = DefaultStopFadeInMilliseconds)
        {
            if (identifier == 0) return;
            try { GameAudioManager.StopPlayingID(ref identifier, fadeInMilliseconds); } catch { }
            identifier = 0;
        }

        /// A sound emitter object on the train (Wwise needs a registered game object to place 3D sounds).
        public static GameObject Emitter(Transform root, string name, Vector3 localPosition)
        {
            var foundChild = root.Find(name);
            if (foundChild != null) return foundChild.gameObject;
            var targetObject = new GameObject(name);
            targetObject.transform.SetParent(root, false);
            targetObject.transform.localPosition = localPosition;
            targetObject.AddComponent<AkGameObj>();
            return targetObject;
        }

        public static GameObject Cab(SteamTrain train) => Emitter(train.transform, "Snd_Cab", CabEmitterPosition);

        // The cab door: the base game's heavy metal door (Wwise events of the base banks)
        public const string DoorOpenEvent = "Play_SndMechDoorMetalOpen1";
        public const string DoorCloseEvent = "Play_SndMechDoorMetalClose1";
        public static GameObject Door(SteamTrain train) => Emitter(train.transform, "Snd_Door", DoorEmitterPosition);

        /// The cab door opening or closing, with the game's metal door sound; false if the game could not play it.
        public static bool PlayDoor(SteamTrain train, bool opening)
        {
            if (train == null) return false;
            return Play(opening ? DoorOpenEvent : DoorCloseEvent, Door(train)) != 0;
        }

        /// A blast heard from a sound emitter object (it needs an AkGameObj) anywhere in the world.
        public static void ExplosionAt(GameObject emitter)
        {
            Play(ExplosionRumble, emitter);
            Play(MetalGroan, emitter);
        }

        public static void Explosion(SteamTrain train)
        {
            if (StoryTrainBank.Post(train, "Play_SteamTrain_Firebox_Explosion", Emitter(train.transform, "Snd_Boiler", BoilerEmitterPosition))) return;
            var boiler = Emitter(train.transform, "Snd_Boiler", BoilerEmitterPosition);
            Play(ExplosionRumble, boiler);
            Play(MetalGroan, boiler);
            Play(SteamHiss, boiler);
        }
    }

    /// Optional steam train sound bank (Wintermute episode 5), loaded at run time from the player's own installation.
    /// If the file is missing or Wwise refuses it, the mod's own sounds are used instead.
    internal static class StoryTrainBank
    {
        const string RelativePath = @"tld_dlc\episode5\tld_Data\StreamingAssets\Audio\GeneratedSoundBanks\Windows\SteamTrain.bnk";
        static bool s_Tried;
        static bool s_BankAvailable;

        public static bool Available => Ensure();

        public static bool Ensure()
        {
            if (s_Tried) return s_BankAvailable;
            s_Tried = true;
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), RelativePath);
                if (!System.IO.File.Exists(path)) { SteamRailwayMod.Log.Msg("Story train sound bank not found, using the mod's sounds."); return false; }
                var result = AkSoundEngine.LoadBank(path, out uint _);
                s_BankAvailable = result == AKRESULT.AK_Success || result == AKRESULT.AK_BankAlreadyLoaded;
                SteamRailwayMod.Log.Msg($"Story train sound bank: {result}.");
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Story train sound bank failed: {exception.Message}"); s_BankAvailable = false; }
            return s_BankAvailable;
        }

        /// Posts one of the train's events (false when the bank is not available).
        public static bool Post(SteamTrain train, string eventName, GameObject targetObject = null)
        {
            if (train == null || !Available) return false;
            return TrainSounds.Play(eventName, targetObject ?? TrainSounds.Cab(train)) != 0;
        }

        static readonly System.Collections.Generic.Dictionary<string, string> s_States = new System.Collections.Generic.Dictionary<string, string>();

        /// Wwise states are global: only send changes.
        public static void State(string group, string state)
        {
            if (s_States.TryGetValue(group, out var current) && current == state) return;
            s_States[group] = state;
            try { AkSoundEngine.SetState(group, state); } catch { }
        }
    }

    /// The running sounds of one locomotive: valve hiss, boiler simmer, the rails and the whistle.
    internal sealed class TrainAudio
    {
        const float RailLength = 12.5f;                 // m between rail joints
        static readonly float[] AxleOffsets = { 0f, 1.65f, 3.3f, 7.6f, 8.35f };   // driving axles, then pony wheels
        const float WhistleVolume = 0.9f;
        static readonly Vector3 ValveEmitterPosition = ValveEmitterPosition;
        static readonly Vector3 WheelsEmitterPosition = new Vector3(0f, 0.6f, 0f);
        static readonly Vector3 WhistleEmitterPosition = WhistleEmitterPosition;
        static readonly Vector3 TrainEmitterPosition = new Vector3(0f, 2.2f, 0f);
        static readonly Vector3 SecondWheelsEmitterPosition = SecondWheelsEmitterPosition;
        const float RumbleMinimumDistanceInMeters = 8f;
        const float RumbleMaximumDistanceInMeters = 140f;
        const float ClackMinimumDistanceInMeters = 4f;
        const float ClackMaximumDistanceInMeters = 90f;
        const float WhistleMinimumDistanceInMeters = 6f;
        const float WhistleMaximumDistanceInMeters = 500f;
        const float SteamMinimumDistanceInMeters = 3f;
        const float SteamMaximumDistanceInMeters = 120f;
        const float BrakeMinimumDistanceInMeters = 6f;
        const float BrakeMaximumDistanceInMeters = 150f;
        const float SecondRumbleSpatialBlend = 0.6f;
        const float FirstRumbleSpatialBlend = 0.8f;
        const float SimmerMinimumPressureInPsi = 20f;
        const float StandingFadeOutSpeedInMetersPerSecond = 3f;
        const float StoryIdleVolumeMinimum = 0.2f;
        const float StoryIdleVolumeMaximum = 0.5f;
        const float IdleVolumeMinimum = 0.05f;
        const float IdleVolumeMaximum = 0.2f;
        const float SafetyValveVolume = 0.95f;
        const float SteamLeakMinimumPressureInPsi = 10f;
        const float SafetyValveFadeRate = 2f;
        const float SteamLeakFadeRate = 0.5f;
        const float SafetyValvePitch = 1.05f;
        const float SteamLeakPitch = 0.9f;
        const float AudibleVolume = 0.001f;
        const float BrakeSquealMinimumPressureInPsi = 20f;
        const float BrakeSquealFullSpeedInMetersPerSecond = 6f;
        const float BrakeSquealMinimumSpeedInMetersPerSecond = 0.4f;
        const float BrakeSquealVolumeMinimum = 0.35f;
        const float BrakeSquealVolumeMaximum = 0.9f;
        const float BrakeSquealRiseRate = 3f;
        const float BrakeSquealFallRate = 1.5f;
        const float BrakeSquealPitchMinimum = 0.85f;
        const float BrakeSquealPitchMaximum = 1.08f;
        const float RollingMinimumSpeedInMetersPerSecond = 0.2f;
        const float RumbleVolumeMinimum = 0.65f;
        const float RecordedRumblePitchMinimum = 0.72f;
        const float RecordedRumblePitchMaximum = 1.15f;
        const float GeneratedRumblePitchMinimum = 0.7f;
        const float GeneratedRumblePitchMaximum = 1.25f;
        const float RumbleFadeRate = 0.8f;
        const float SecondRumbleVolumeFactor = 0.8f;
        const float SecondRumblePitchFactor = 0.97f;
        const float ClackPitchSpreadMinimum = 0.9f;
        const float ClackPitchSpreadMaximum = 1.1f;
        const float ClackPitchBySpeedMinimum = 0.85f;
        const float ClackPitchBySpeedMaximum = 1.1f;
        const float ClackVolumeMinimum = 0.35f;
        const float ClackVolumeMaximum = 0.9f;
        const float ClackVolumeSpreadMinimum = 0.8f;
        const float WhistleAttackRate = 6f;
        const float WhistleReleaseRate = 3f;
        const float WhistlePitchBase = 0.94f;
        const float WhistlePitchRise = 0.06f;

        readonly GameObject m_Boiler;
        readonly GameObject m_Valve;
        readonly GameObject m_CabEmitter;
        readonly AudioSource m_Rumble;
        readonly AudioSource m_Rumble2;
        readonly AudioSource m_Clack;
        readonly AudioSource m_Whistle;
        readonly AudioSource m_Steam;
        readonly AudioSource m_Brake;
        readonly bool m_RealRolling;     // recorded clips from the bundle
        readonly bool m_RealWhistle;
        uint m_HissId, m_SimmerId;
        float m_Travelled;                              // m since the last joint
        readonly System.Collections.Generic.List<float> m_PendingClacks = new System.Collections.Generic.List<float>();
        float m_WhistleLevel;
        bool m_WhistleWasOpen;
        // story (episode 5) train sounds
        readonly bool m_Story;
        readonly GameObject m_TrainEmitter;
        uint m_TrainLoopId, m_BoilerLoopId;
        bool m_WasInside, m_WasBurning;

        public TrainAudio(SteamTrain train)
        {
            var rootTransform = train.transform;
            m_CabEmitter = TrainSounds.Cab(train);
            m_Boiler = TrainSounds.Emitter(rootTransform, "Snd_Boiler", TrainSounds.BoilerEmitterPosition);
            m_Valve = TrainSounds.Emitter(rootTransform, "Snd_Valve", ValveEmitterPosition);
            var wheels = TrainSounds.Emitter(rootTransform, "Snd_Wheels", WheelsEmitterPosition);
            var whistle = TrainSounds.Emitter(rootTransform, "Snd_Whistle", WhistleEmitterPosition);
            m_Story = StoryTrainBank.Ensure();
            m_TrainEmitter = TrainSounds.Emitter(rootTransform, "Snd_Train", TrainEmitterPosition);
            try
            {
                var rolling = LocomotiveModel.Sound("Rolling");
                var toot = LocomotiveModel.Sound("Whistle");
                m_RealRolling = rolling != null;
                m_RealWhistle = toot != null;
                m_Rumble = SynthSounds.Source(wheels, true, RumbleMinimumDistanceInMeters, RumbleMaximumDistanceInMeters);   // full level anywhere in the cab
                m_Rumble.clip = rolling ?? SynthSounds.Rumble;
                m_Clack = SynthSounds.Source(wheels, false, ClackMinimumDistanceInMeters, ClackMaximumDistanceInMeters);
                m_Clack.volume = 1f;
                m_Whistle = SynthSounds.Source(whistle, !m_RealWhistle, WhistleMinimumDistanceInMeters, WhistleMaximumDistanceInMeters);
                m_Whistle.clip = toot ?? SynthSounds.Whistle;
                // Standing sounds: the air pump beating and steam leaking, as long as the boiler has pressure.
                var hiss = LocomotiveModel.Sound("SteamHiss");
                if (hiss != null) { m_Steam = SynthSounds.Source(m_Valve, true, SteamMinimumDistanceInMeters, SteamMaximumDistanceInMeters); m_Steam.clip = hiss; }
                if (m_RealRolling)
                {
                    // A second copy, half a loop out of step, under the first: a fuller, louder running sound.
                    m_Rumble2 = SynthSounds.Source(TrainSounds.Emitter(rootTransform, "Snd_Wheels2", SecondWheelsEmitterPosition), true, RumbleMinimumDistanceInMeters, RumbleMaximumDistanceInMeters);
                    m_Rumble2.clip = rolling;
                    m_Rumble2.spatialBlend = SecondRumbleSpatialBlend;
                    m_Rumble.spatialBlend = FirstRumbleSpatialBlend;
                }
                var squeal = LocomotiveModel.Sound("BrakeSqueal");
                if (squeal != null) { m_Brake = SynthSounds.Source(wheels, true, BrakeMinimumDistanceInMeters, BrakeMaximumDistanceInMeters); m_Brake.clip = squeal; }
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Train audio sources unavailable: {exception.Message}"); }
        }

        public void Update(SteamSimulation simulation)
        {
            float deltaTime = Time.deltaTime;
            float speed = Mathf.Abs(simulation.Velocity);
            if (m_Story)
            {
                UpdateStory(simulation, speed);
                UpdateStanding(simulation, deltaTime, speed);     // the story loop is almost silent at a stand: keep our steam
            }
            else
            {
                if (m_Steam == null) Loop(ref m_HissId, TrainSounds.SteamHiss, m_Valve, simulation.SafetyValveOpen);
                UpdateStanding(simulation, deltaTime);
                Loop(ref m_SimmerId, TrainSounds.Simmer, m_Boiler, simulation.Pressure > SimmerMinimumPressureInPsi && !simulation.State.Exploded);
            }
            UpdateRails(m_Story ? 0f : speed, deltaTime);      // the story train loop has its own running sound
            UpdateBrake(simulation, speed, deltaTime);
            UpdateWhistle(simulation.Train.m_SteamWhistleOpen, deltaTime);
        }

        /// Feeds the sound bank's loops the values it expects: speed, pressure and the boiler, engine, gear and on-board states.
        void UpdateStory(SteamSimulation simulation, float speed)
        {
            var simulationState = simulation.State;
            if (m_TrainLoopId == 0) m_TrainLoopId = TrainSounds.Play("Play_SteamTrain", m_TrainEmitter);
            if (m_BoilerLoopId == 0) m_BoilerLoopId = TrainSounds.Play("Play_SteamTrain_Boiler", m_Boiler);
            // the story train's RTPC curves run 0..1: speed / top speed and pressure / working pressure
            float speedFraction = Mathf.Clamp01(speed / SteamSimulation.MaxSpeed);
            float pressureFraction = Mathf.Clamp01(simulation.Pressure / SteamSimulation.MaxPressure);
            try
            {
                AkSoundEngine.SetRTPCValue("SteamTrain_Velocity", speedFraction, m_TrainEmitter);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", pressureFraction, m_TrainEmitter);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", pressureFraction, m_Boiler);
                AkSoundEngine.SetRTPCValue("SteamTrain_Velocity", speedFraction);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", pressureFraction);
            }
            catch { }
            bool inside = simulation.Train.IsInsideTrain;
            StoryTrainBank.State("SteamTrain_OnBoard", inside ? "stob_True" : "stob_False");
            StoryTrainBank.State("SteamTrain_Boiler", simulation.FireBurning || simulation.Pressure > SimmerMinimumPressureInPsi ? "stb_On" : "stb_Off");
            StoryTrainBank.State("SteamTrain_Engine", simulation.SafetyValveOpen ? "ste_Overfire" : "ste_Normal");
            StoryTrainBank.State("SteamTrain_GearShift", simulationState.Reverser > 0 ? "stgs_Up" : simulationState.Reverser < 0 ? "stgs_Down" : "None");

            var cab = m_CabEmitter;
            if (inside != m_WasInside) TrainSounds.Play(inside ? "Play_SteamTrain_SteamEngineEnter" : "Play_SteamTrain_SteamEngineExit", cab);
            m_WasInside = inside;
            bool burning = simulation.FireBurning;
            if (burning && !m_WasBurning) TrainSounds.Play("Play_SteamTrain_BoilerTurnOnFire", cab);
            else if (!burning && m_WasBurning) TrainSounds.Play("Play_SteamTrain_BoilerTurnOffFire", cab);
            m_WasBurning = burning;
        }

        void UpdateStanding(SteamSimulation simulation, float deltaTime, float speed = 0f)
        {
            bool alive = !simulation.State.Exploded;
            float progress = Mathf.Clamp01(simulation.Pressure / SteamSimulation.MaxPressure);
            if (m_Steam != null)
            {
                // steam leaking from the cocks and glands while standing; fades away once the train is moving
                float standing = 1f - Mathf.Clamp01(speed / StandingFadeOutSpeedInMetersPerSecond);
                float idle = m_Story ? Mathf.Lerp(StoryIdleVolumeMinimum, StoryIdleVolumeMaximum, progress) * standing : Mathf.Lerp(IdleVolumeMinimum, IdleVolumeMaximum, progress);
                float targetVolume = simulation.SafetyValveOpen ? SafetyValveVolume : alive && simulation.Pressure > SteamLeakMinimumPressureInPsi ? idle : 0f;
                Fade(m_Steam, targetVolume, deltaTime * (simulation.SafetyValveOpen ? SafetyValveFadeRate : SteamLeakFadeRate));
                m_Steam.pitch = simulation.SafetyValveOpen ? SafetyValvePitch : SteamLeakPitch;
            }
        }

        static void Fade(AudioSource source, float target, float step)
        {
            source.volume = Mathf.MoveTowards(source.volume, target, step);
            if (source.volume > AudibleVolume && !source.isPlaying) source.Play();
            else if (source.volume <= AudibleVolume && source.isPlaying) source.Stop();
        }

        /// Brake blocks squealing on the tyres while the train is braked and still rolling.
        void UpdateBrake(SteamSimulation simulation, float speed, float deltaTime)
        {
            if (m_Brake == null) return;
            bool braking = (simulation.State.SteamBrake && simulation.Pressure >= BrakeSquealMinimumPressureInPsi) || simulation.State.HandBrake;
            float scale = Mathf.Clamp01(speed / BrakeSquealFullSpeedInMetersPerSecond);
            float target = braking && speed > BrakeSquealMinimumSpeedInMetersPerSecond ? Mathf.Lerp(BrakeSquealVolumeMinimum, BrakeSquealVolumeMaximum, scale) : 0f;
            Fade(m_Brake, target, deltaTime * (target > m_Brake.volume ? BrakeSquealRiseRate : BrakeSquealFallRate));
            m_Brake.pitch = Mathf.Lerp(BrakeSquealPitchMinimum, BrakeSquealPitchMaximum, scale);
        }

        void UpdateRails(float speed, float deltaTime)
        {
            if (m_Rumble == null) return;
            float scale = Mathf.Clamp01(speed / SteamSimulation.MaxSpeed);
            float target = speed > RollingMinimumSpeedInMetersPerSecond ? Mathf.Lerp(RumbleVolumeMinimum, 1f, Mathf.Sqrt(scale)) : 0f;
            float pitch = m_RealRolling ? Mathf.Lerp(RecordedRumblePitchMinimum, RecordedRumblePitchMaximum, scale) : Mathf.Lerp(GeneratedRumblePitchMinimum, GeneratedRumblePitchMaximum, scale);
            Fade(m_Rumble, target, deltaTime * RumbleFadeRate);
            m_Rumble.pitch = pitch;
            if (m_Rumble2 != null)
            {
                bool wasPlaying = m_Rumble2.isPlaying;
                Fade(m_Rumble2, target * SecondRumbleVolumeFactor, deltaTime * RumbleFadeRate);
                m_Rumble2.pitch = pitch * SecondRumblePitchFactor;               // slightly deeper copy
                if (!wasPlaying && m_Rumble2.isPlaying && m_Rumble2.clip != null)
                    m_Rumble2.timeSamples = m_Rumble2.clip.samples / 2;
            }

            if (m_RealRolling) return;                  // the recording already has the rail joints in it
            if (speed > RollingMinimumSpeedInMetersPerSecond)
            {
                m_Travelled += speed * deltaTime;
                if (m_Travelled >= RailLength)
                {
                    m_Travelled -= RailLength;
                    foreach (var axleOffset in AxleOffsets) m_PendingClacks.Add(axleOffset / speed);
                }
            }
            for (int index = m_PendingClacks.Count - 1; index >= 0; index--)
            {
                m_PendingClacks[index] -= deltaTime;
                if (m_PendingClacks[index] > 0f) continue;
                m_PendingClacks.RemoveAt(index);
                m_Clack.pitch = Random.Range(ClackPitchSpreadMinimum, ClackPitchSpreadMaximum) * Mathf.Lerp(ClackPitchBySpeedMinimum, ClackPitchBySpeedMaximum, scale);
                m_Clack.PlayOneShot(SynthSounds.Clack, Mathf.Lerp(ClackVolumeMinimum, ClackVolumeMaximum, scale) * Random.Range(ClackVolumeSpreadMinimum, 1f));
            }
        }

        void UpdateWhistle(bool open, float deltaTime)
        {
            if (m_Whistle == null) return;
            if (m_RealWhistle)
            {
                // The recorded toot is played whole each time the cord is pulled.
                if (open && !m_WhistleWasOpen) { m_Whistle.volume = WhistleVolume; m_Whistle.pitch = 1f; m_Whistle.Play(); }
                m_WhistleWasOpen = open;
                return;
            }
            // Quick attack, slightly slower release, like a real valve letting go.
            m_WhistleLevel = Mathf.MoveTowards(m_WhistleLevel, open ? 1f : 0f, deltaTime * (open ? WhistleAttackRate : WhistleReleaseRate));
            m_Whistle.volume = m_WhistleLevel * WhistleVolume;
            m_Whistle.pitch = WhistlePitchBase + WhistlePitchRise * m_WhistleLevel;   // the pitch rises as the valve opens
            if (m_WhistleLevel > 0f && !m_Whistle.isPlaying) m_Whistle.Play();
            else if (m_WhistleLevel <= 0f && m_Whistle.isPlaying) m_Whistle.Stop();
        }

        static void Loop(ref uint identifier, string eventName, GameObject targetObject, bool enabled)
        {
            if (enabled && identifier == 0) identifier = TrainSounds.Play(eventName, targetObject);
            else if (!enabled && identifier != 0) TrainSounds.Stop(ref identifier);
        }

        public void StopAll()
        {
            if (m_TrainLoopId != 0) { TrainSounds.Stop(ref m_TrainLoopId, 0); TrainSounds.Play("Stop_SteamTrain", m_TrainEmitter); }
            TrainSounds.Stop(ref m_BoilerLoopId, 0);
            TrainSounds.Stop(ref m_HissId, 0);
            TrainSounds.Stop(ref m_SimmerId, 0);
            if (m_Rumble != null) m_Rumble.Stop();
            if (m_Rumble2 != null) m_Rumble2.Stop();
            if (m_Whistle != null) m_Whistle.Stop();
            if (m_Brake != null) m_Brake.Stop();
            if (m_Steam != null) m_Steam.Stop();
        }
    }
}
