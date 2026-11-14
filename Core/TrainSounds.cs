using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// Locomotive sounds: base-game Wwise events, the mod's recordings (Sounds/CREDITS.txt) and, when present in the
    /// player's installation, the Wintermute episode 5 steam train sounds (StoryTrainBank).
    internal static class TrainSounds
    {
        // One-shots
        public const string PourWater = "PLAY_PUTINPOTWATER";
        public const string Snow = "PLAY_PUTINPOTSNOW";
        public const string PumpWater = "PLAY_LIQUIDGLUGA";
        public const string Repair = "PLAY_REPAIRINGMETAL";
        const string ExplosionRumble = "PLAY_AMBIENTOBJECTS_ROCKFALLSMEDIUM_MINE";
        const string MetalGroan = "PLAY_METALGROANMETALMEDIUM";
        /// Throwing a switch lever: the story train's heavy lever when its sounds are loaded, else a metal groan.
        public static string SwitchThrow => StoryTrainBank.Available ? "Play_SteamTrain_GearLeverDown" : MetalGroan;
        // Loops (stopped by playing id)
        public const string SteamHiss = "PLAY_STEAMTUNNELS";
        public const string Simmer = "PLAY_BOILINGLIQUIDTHICKLIGHT";

        public static uint Play(string eventName, GameObject targetObject)
        {
            if (targetObject == null) return 0;
            try { return GameAudioManager.PlaySound(eventName, targetObject); }
            catch (System.Exception e) { SteamRailwayMod.Debug($"Sound {eventName} failed: {e.Message}"); return 0; }
        }

        /// A looping game sound that is played for a while and then faded out (the game ends its own repair sounds
        /// when the repair time is over; a one-shot call would keep looping).
        public static void PlayFor(string eventName, GameObject targetObject, float seconds)
        {
            uint identifier = Play(eventName, targetObject);
            if (identifier != 0) MelonLoader.MelonCoroutines.Start(StopAfter(identifier, seconds));
        }

        static System.Collections.IEnumerator StopAfter(uint identifier, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Stop(ref identifier, 300);
        }

        public static void Stop(ref uint identifier, int fadeMs = 400)
        {
            if (identifier == 0) return;
            try { GameAudioManager.StopPlayingID(ref identifier, fadeMs); } catch { }
            identifier = 0;
        }

        /// A sound emitter object on the train (Wwise needs a registered game object to place 3D sounds).
        public static GameObject Emitter(Transform root, string name, Vector3 localPos)
        {
            var foundChild = root.Find(name);
            if (foundChild != null) return foundChild.gameObject;
            var targetObject = new GameObject(name);
            targetObject.transform.SetParent(root, false);
            targetObject.transform.localPosition = localPos;
            targetObject.AddComponent<AkGameObj>();
            return targetObject;
        }

        public static GameObject Cab(SteamTrain train) => Emitter(train.transform, "Snd_Cab", new Vector3(0f, 2.6f, -2.6f));

        public static void Explosion(SteamTrain train)
        {
            if (StoryTrainBank.Post(train, "Play_SteamTrain_Firebox_Explosion", Emitter(train.transform, "Snd_Boiler", new Vector3(0f, 3.2f, 0.5f)))) return;
            var boiler = Emitter(train.transform, "Snd_Boiler", new Vector3(0f, 3.2f, 0.5f));
            Play(ExplosionRumble, boiler);
            Play(MetalGroan, boiler);
            Play(SteamHiss, boiler);
        }
    }

    /// Optional steam train sound bank (Wintermute episode 5), loaded at run time from the player's own installation
    /// (tld_dlc\episode5\...\SteamTrain.bnk). Nothing is copied into the mod; if the file is missing or Wwise
    /// refuses it, the mod's own sounds are used instead.
    internal static class StoryTrainBank
    {
        const string RelPath = @"tld_dlc\episode5\tld_Data\StreamingAssets\Audio\GeneratedSoundBanks\Windows\SteamTrain.bnk";
        static bool s_Tried, s_Ok;

        public static bool Available => Ensure();

        public static bool Ensure()
        {
            if (s_Tried) return s_Ok;
            s_Tried = true;
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), RelPath);
                if (!System.IO.File.Exists(path)) { SteamRailwayMod.Log.Msg("Story train sound bank not found, using the mod's sounds."); return false; }
                var result = AkSoundEngine.LoadBank(path, out uint _);
                s_Ok = result == AKRESULT.AK_Success || result == AKRESULT.AK_BankAlreadyLoaded;
                SteamRailwayMod.Log.Msg($"Story train sound bank: {result}.");
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Story train sound bank failed: {e.Message}"); s_Ok = false; }
            return s_Ok;
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

    /// The running sounds of one locomotive: safety valve hiss and boiler simmer (game sounds), the rails
    /// (rumble loop + a knock per axle at each rail joint) and the whistle (generated, see SynthSounds).
    internal sealed class TrainAudio
    {
        const float RailLength = 12.5f;                 // m between rail joints
        static readonly float[] AxleOffsets = { 0f, 1.65f, 3.3f, 7.6f, 8.35f };   // driving axles, then pony wheels
        const float WhistleVolume = 0.9f;

        readonly GameObject m_Boiler, m_Valve;
        readonly AudioSource m_Rumble, m_Rumble2, m_Clack, m_Whistle, m_Steam, m_Brake;
        readonly bool m_RealRolling, m_RealWhistle;     // recorded clips from the bundle (see Sounds/CREDITS.txt)
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
            m_Boiler = TrainSounds.Emitter(rootTransform, "Snd_Boiler", new Vector3(0f, 3.2f, 0.5f));
            m_Valve = TrainSounds.Emitter(rootTransform, "Snd_Valve", new Vector3(0f, 4.05f, -1.25f));
            var wheels = TrainSounds.Emitter(rootTransform, "Snd_Wheels", new Vector3(0f, 0.6f, 0f));
            var whistle = TrainSounds.Emitter(rootTransform, "Snd_Whistle", new Vector3(0.35f, 4.0f, -1.45f));
            m_Story = StoryTrainBank.Ensure();
            m_TrainEmitter = TrainSounds.Emitter(rootTransform, "Snd_Train", new Vector3(0f, 2.2f, 0f));
            try
            {
                var rolling = LocomotiveModel.Sound("Rolling");
                var toot = LocomotiveModel.Sound("Whistle");
                m_RealRolling = rolling != null;
                m_RealWhistle = toot != null;
                m_Rumble = SynthSounds.Source(wheels, true, 8f, 140f);   // full level anywhere in the cab
                m_Rumble.clip = rolling ?? SynthSounds.Rumble;
                m_Clack = SynthSounds.Source(wheels, false, 4f, 90f);
                m_Clack.volume = 1f;
                m_Whistle = SynthSounds.Source(whistle, !m_RealWhistle, 6f, 500f);
                m_Whistle.clip = toot ?? SynthSounds.Whistle;
                // Standing sounds: the air pump beating and steam leaking, as long as the boiler has pressure.
                var hiss = LocomotiveModel.Sound("SteamHiss");
                if (hiss != null) { m_Steam = SynthSounds.Source(m_Valve, true, 3f, 120f); m_Steam.clip = hiss; }
                if (m_RealRolling)
                {
                    // A second copy, half a loop out of step, under the first: a fuller, louder running sound.
                    m_Rumble2 = SynthSounds.Source(TrainSounds.Emitter(rootTransform, "Snd_Wheels2", new Vector3(0f, 0.6f, -2.5f)), true, 8f, 140f);
                    m_Rumble2.clip = rolling;
                    m_Rumble2.spatialBlend = 0.6f;
                    m_Rumble.spatialBlend = 0.8f;
                }
                var squeal = LocomotiveModel.Sound("BrakeSqueal");
                if (squeal != null) { m_Brake = SynthSounds.Source(wheels, true, 6f, 150f); m_Brake.clip = squeal; }
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Train audio sources unavailable: {e.Message}"); }
        }

        public void Update(SteamSim simulation)
        {
            float dt = Time.deltaTime;
            float speed = Mathf.Abs(simulation.Velocity);
            if (m_Story)
            {
                UpdateStory(simulation, speed);
                UpdateStanding(simulation, dt, speed);     // the story loop is almost silent at a stand: keep our steam
            }
            else
            {
                if (m_Steam == null) Loop(ref m_HissId, TrainSounds.SteamHiss, m_Valve, simulation.SafetyValveOpen);
                UpdateStanding(simulation, dt);
                Loop(ref m_SimmerId, TrainSounds.Simmer, m_Boiler, simulation.Pressure > 20f && !simulation.S.Exploded);
            }
            UpdateRails(m_Story ? 0f : speed, dt);      // the story train loop has its own running sound
            UpdateBrake(simulation, speed, dt);
            UpdateWhistle(simulation.Train.m_SteamWhistleOpen, dt);
        }

        /// Feeds the sound bank's loops the values it expects: speed, pressure,
        /// boiler temperature and the boiler / engine / gear / on-board states.
        void UpdateStory(SteamSim simulation, float speed)
        {
            var simState = simulation.S;
            if (m_TrainLoopId == 0) m_TrainLoopId = TrainSounds.Play("Play_SteamTrain", m_TrainEmitter);
            if (m_BoilerLoopId == 0) m_BoilerLoopId = TrainSounds.Play("Play_SteamTrain_Boiler", m_Boiler);
            // The story train's sound design uses normalised values (its RTPC curves run 0..1):
            // velocity / 15 m/s (top speed) and pressure / working pressure.
            float v01 = Mathf.Clamp01(speed / SteamSim.MaxSpeed);
            float p01 = Mathf.Clamp01(simulation.Pressure / SteamSim.MaxPressure);
            try
            {
                AkSoundEngine.SetRTPCValue("SteamTrain_Velocity", v01, m_TrainEmitter);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", p01, m_TrainEmitter);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", p01, m_Boiler);
                AkSoundEngine.SetRTPCValue("SteamTrain_Velocity", v01);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", p01);
            }
            catch { }
            bool inside = simulation.Train.IsInsideTrain;
            StoryTrainBank.State("SteamTrain_OnBoard", inside ? "stob_True" : "stob_False");
            StoryTrainBank.State("SteamTrain_Boiler", simulation.FireBurning || simulation.Pressure > 20f ? "stb_On" : "stb_Off");
            StoryTrainBank.State("SteamTrain_Engine", simulation.SafetyValveOpen ? "ste_Overfire" : "ste_Normal");
            StoryTrainBank.State("SteamTrain_GearShift", simState.Reverser > 0 ? "stgs_Up" : simState.Reverser < 0 ? "stgs_Down" : "None");

            var cab = TrainSounds.Cab(simulation.Train);
            if (inside != m_WasInside) TrainSounds.Play(inside ? "Play_SteamTrain_SteamEngineEnter" : "Play_SteamTrain_SteamEngineExit", cab);
            m_WasInside = inside;
            bool burning = simulation.FireBurning;
            if (burning && !m_WasBurning) TrainSounds.Play("Play_SteamTrain_BoilerTurnOnFire", cab);
            else if (!burning && m_WasBurning) TrainSounds.Play("Play_SteamTrain_BoilerTurnOffFire", cab);
            m_WasBurning = burning;
        }

        void UpdateStanding(SteamSim simulation, float deltaTime, float speed = 0f)
        {
            bool alive = !simulation.S.Exploded;
            float progress = Mathf.Clamp01(simulation.Pressure / SteamSim.MaxPressure);
            if (m_Steam != null)
            {
                // Steam leaking from the cocks and glands while standing; fades away once the train is moving.
                float standing = 1f - Mathf.Clamp01(speed / 3f);
                float idle = m_Story ? Mathf.Lerp(0.2f, 0.5f, progress) * standing : Mathf.Lerp(0.05f, 0.2f, progress);
                float value = simulation.SafetyValveOpen ? 0.95f : alive && simulation.Pressure > 10f ? idle : 0f;
                Fade(m_Steam, value, deltaTime * (simulation.SafetyValveOpen ? 2f : 0.5f));
                m_Steam.pitch = simulation.SafetyValveOpen ? 1.05f : 0.9f;
            }
        }

        static void Fade(AudioSource source, float target, float step)
        {
            source.volume = Mathf.MoveTowards(source.volume, target, step);
            if (source.volume > 0.001f && !source.isPlaying) source.Play();
            else if (source.volume <= 0.001f && source.isPlaying) source.Stop();
        }

        /// Brake blocks squealing on the tyres while the train is braked and still rolling.
        void UpdateBrake(SteamSim simulation, float speed, float deltaTime)
        {
            if (m_Brake == null) return;
            bool braking = (simulation.S.SteamBrake && simulation.Pressure >= 20f) || simulation.S.HandBrake;
            float scale = Mathf.Clamp01(speed / 6f);
            float target = braking && speed > 0.4f ? Mathf.Lerp(0.35f, 0.9f, scale) : 0f;
            Fade(m_Brake, target, deltaTime * (target > m_Brake.volume ? 3f : 1.5f));
            m_Brake.pitch = Mathf.Lerp(0.85f, 1.08f, scale);
        }

        void UpdateRails(float speed, float deltaTime)
        {
            if (m_Rumble == null) return;
            float scale = Mathf.Clamp01(speed / SteamSim.MaxSpeed);
            float target = speed > 0.2f ? Mathf.Lerp(0.65f, 1f, Mathf.Sqrt(scale)) : 0f;
            float pitch = m_RealRolling ? Mathf.Lerp(0.72f, 1.15f, scale) : Mathf.Lerp(0.7f, 1.25f, scale);
            Fade(m_Rumble, target, deltaTime * 0.8f);
            m_Rumble.pitch = pitch;
            if (m_Rumble2 != null)
            {
                bool wasPlaying = m_Rumble2.isPlaying;
                Fade(m_Rumble2, target * 0.8f, deltaTime * 0.8f);
                m_Rumble2.pitch = pitch * 0.97f;               // slightly deeper copy
                if (!wasPlaying && m_Rumble2.isPlaying && m_Rumble2.clip != null)
                    m_Rumble2.timeSamples = m_Rumble2.clip.samples / 2;
            }

            if (m_RealRolling) return;                  // the recording already has the rail joints in it
            if (speed > 0.2f)
            {
                m_Travelled += speed * deltaTime;
                if (m_Travelled >= RailLength)
                {
                    m_Travelled -= RailLength;
                    foreach (var off in AxleOffsets) m_PendingClacks.Add(off / speed);
                }
            }
            for (int index = m_PendingClacks.Count - 1; index >= 0; index--)
            {
                m_PendingClacks[index] -= deltaTime;
                if (m_PendingClacks[index] > 0f) continue;
                m_PendingClacks.RemoveAt(index);
                m_Clack.pitch = Random.Range(0.9f, 1.1f) * Mathf.Lerp(0.85f, 1.1f, scale);
                m_Clack.PlayOneShot(SynthSounds.Clack, Mathf.Lerp(0.35f, 0.9f, scale) * Random.Range(0.8f, 1f));
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
            m_WhistleLevel = Mathf.MoveTowards(m_WhistleLevel, open ? 1f : 0f, deltaTime * (open ? 6f : 3f));
            m_Whistle.volume = m_WhistleLevel * WhistleVolume;
            m_Whistle.pitch = 0.94f + 0.06f * m_WhistleLevel;   // the pitch rises as the valve opens
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
