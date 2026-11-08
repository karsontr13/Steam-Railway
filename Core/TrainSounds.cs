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

        public static uint Play(string ev, GameObject go)
        {
            if (go == null) return 0;
            try { return GameAudioManager.PlaySound(ev, go); }
            catch (System.Exception e) { SteamRailwayMod.Debug($"Sound {ev} failed: {e.Message}"); return 0; }
        }

        /// A looping game sound that is played for a while and then faded out (the game ends its own repair sounds
        /// when the repair time is over; a one-shot call would keep looping).
        public static void PlayFor(string ev, GameObject go, float seconds)
        {
            uint id = Play(ev, go);
            if (id != 0) MelonLoader.MelonCoroutines.Start(StopAfter(id, seconds));
        }

        static System.Collections.IEnumerator StopAfter(uint id, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Stop(ref id, 300);
        }

        public static void Stop(ref uint id, int fadeMs = 400)
        {
            if (id == 0) return;
            try { GameAudioManager.StopPlayingID(ref id, fadeMs); } catch { }
            id = 0;
        }

        /// A sound emitter object on the train (Wwise needs a registered game object to place 3D sounds).
        public static GameObject Emitter(Transform root, string name, Vector3 localPos)
        {
            var t = root.Find(name);
            if (t != null) return t.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = localPos;
            go.AddComponent<AkGameObj>();
            return go;
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
                var r = AkSoundEngine.LoadBank(path, out uint _);
                s_Ok = r == AKRESULT.AK_Success || r == AKRESULT.AK_BankAlreadyLoaded;
                SteamRailwayMod.Log.Msg($"Story train sound bank: {r}.");
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Story train sound bank failed: {e.Message}"); s_Ok = false; }
            return s_Ok;
        }

        /// Posts one of the train's events (false when the bank is not available).
        public static bool Post(SteamTrain train, string ev, GameObject go = null)
        {
            if (train == null || !Available) return false;
            return TrainSounds.Play(ev, go ?? TrainSounds.Cab(train)) != 0;
        }

        static readonly System.Collections.Generic.Dictionary<string, string> s_States = new System.Collections.Generic.Dictionary<string, string>();

        /// Wwise states are global: only send changes.
        public static void State(string group, string state)
        {
            if (s_States.TryGetValue(group, out var cur) && cur == state) return;
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
            var R = train.transform;
            m_Boiler = TrainSounds.Emitter(R, "Snd_Boiler", new Vector3(0f, 3.2f, 0.5f));
            m_Valve = TrainSounds.Emitter(R, "Snd_Valve", new Vector3(0f, 4.05f, -1.25f));
            var wheels = TrainSounds.Emitter(R, "Snd_Wheels", new Vector3(0f, 0.6f, 0f));
            var whistle = TrainSounds.Emitter(R, "Snd_Whistle", new Vector3(0.35f, 4.0f, -1.45f));
            m_Story = StoryTrainBank.Ensure();
            m_TrainEmitter = TrainSounds.Emitter(R, "Snd_Train", new Vector3(0f, 2.2f, 0f));
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
                    m_Rumble2 = SynthSounds.Source(TrainSounds.Emitter(R, "Snd_Wheels2", new Vector3(0f, 0.6f, -2.5f)), true, 8f, 140f);
                    m_Rumble2.clip = rolling;
                    m_Rumble2.spatialBlend = 0.6f;
                    m_Rumble.spatialBlend = 0.8f;
                }
                var squeal = LocomotiveModel.Sound("BrakeSqueal");
                if (squeal != null) { m_Brake = SynthSounds.Source(wheels, true, 6f, 150f); m_Brake.clip = squeal; }
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Train audio sources unavailable: {e.Message}"); }
        }

        public void Update(SteamSim sim)
        {
            float dt = Time.deltaTime;
            float speed = Mathf.Abs(sim.Velocity);
            if (m_Story)
            {
                UpdateStory(sim, speed);
                UpdateStanding(sim, dt, speed);     // the story loop is almost silent at a stand: keep our steam
            }
            else
            {
                if (m_Steam == null) Loop(ref m_HissId, TrainSounds.SteamHiss, m_Valve, sim.SafetyValveOpen);
                UpdateStanding(sim, dt);
                Loop(ref m_SimmerId, TrainSounds.Simmer, m_Boiler, sim.Pressure > 20f && !sim.S.Exploded);
            }
            UpdateRails(m_Story ? 0f : speed, dt);      // the story train loop has its own running sound
            UpdateBrake(sim, speed, dt);
            UpdateWhistle(sim.Train.m_SteamWhistleOpen, dt);
        }

        /// Feeds the sound bank's loops the values it expects: speed, pressure,
        /// boiler temperature and the boiler / engine / gear / on-board states.
        void UpdateStory(SteamSim sim, float speed)
        {
            var s = sim.S;
            if (m_TrainLoopId == 0) m_TrainLoopId = TrainSounds.Play("Play_SteamTrain", m_TrainEmitter);
            if (m_BoilerLoopId == 0) m_BoilerLoopId = TrainSounds.Play("Play_SteamTrain_Boiler", m_Boiler);
            // The story train's sound design uses normalised values (its RTPC curves run 0..1):
            // velocity / 15 m/s (top speed) and pressure / working pressure.
            float v01 = Mathf.Clamp01(speed / SteamSim.MaxSpeed);
            float p01 = Mathf.Clamp01(sim.Pressure / SteamSim.MaxPressure);
            try
            {
                AkSoundEngine.SetRTPCValue("SteamTrain_Velocity", v01, m_TrainEmitter);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", p01, m_TrainEmitter);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", p01, m_Boiler);
                AkSoundEngine.SetRTPCValue("SteamTrain_Velocity", v01);
                AkSoundEngine.SetRTPCValue("SteamTrain_PSI", p01);
            }
            catch { }
            bool inside = sim.Train.IsInsideTrain;
            StoryTrainBank.State("SteamTrain_OnBoard", inside ? "stob_True" : "stob_False");
            StoryTrainBank.State("SteamTrain_Boiler", sim.FireBurning || sim.Pressure > 20f ? "stb_On" : "stb_Off");
            StoryTrainBank.State("SteamTrain_Engine", sim.SafetyValveOpen ? "ste_Overfire" : "ste_Normal");
            StoryTrainBank.State("SteamTrain_GearShift", s.Reverser > 0 ? "stgs_Up" : s.Reverser < 0 ? "stgs_Down" : "None");

            var cab = TrainSounds.Cab(sim.Train);
            if (inside != m_WasInside) TrainSounds.Play(inside ? "Play_SteamTrain_SteamEngineEnter" : "Play_SteamTrain_SteamEngineExit", cab);
            m_WasInside = inside;
            bool burning = sim.FireBurning;
            if (burning && !m_WasBurning) TrainSounds.Play("Play_SteamTrain_BoilerTurnOnFire", cab);
            else if (!burning && m_WasBurning) TrainSounds.Play("Play_SteamTrain_BoilerTurnOffFire", cab);
            m_WasBurning = burning;
        }

        void UpdateStanding(SteamSim sim, float dt, float speed = 0f)
        {
            bool alive = !sim.S.Exploded;
            float p = Mathf.Clamp01(sim.Pressure / SteamSim.MaxPressure);
            if (m_Steam != null)
            {
                // Steam leaking from the cocks and glands while standing; fades away once the train is moving.
                float standing = 1f - Mathf.Clamp01(speed / 3f);
                float idle = m_Story ? Mathf.Lerp(0.2f, 0.5f, p) * standing : Mathf.Lerp(0.05f, 0.2f, p);
                float v = sim.SafetyValveOpen ? 0.95f : alive && sim.Pressure > 10f ? idle : 0f;
                Fade(m_Steam, v, dt * (sim.SafetyValveOpen ? 2f : 0.5f));
                m_Steam.pitch = sim.SafetyValveOpen ? 1.05f : 0.9f;
            }
        }

        static void Fade(AudioSource src, float target, float step)
        {
            src.volume = Mathf.MoveTowards(src.volume, target, step);
            if (src.volume > 0.001f && !src.isPlaying) src.Play();
            else if (src.volume <= 0.001f && src.isPlaying) src.Stop();
        }

        /// Brake blocks squealing on the tyres while the train is braked and still rolling.
        void UpdateBrake(SteamSim sim, float speed, float dt)
        {
            if (m_Brake == null) return;
            bool braking = (sim.S.SteamBrake && sim.Pressure >= 20f) || sim.S.HandBrake;
            float k = Mathf.Clamp01(speed / 6f);
            float target = braking && speed > 0.4f ? Mathf.Lerp(0.35f, 0.9f, k) : 0f;
            Fade(m_Brake, target, dt * (target > m_Brake.volume ? 3f : 1.5f));
            m_Brake.pitch = Mathf.Lerp(0.85f, 1.08f, k);
        }

        void UpdateRails(float speed, float dt)
        {
            if (m_Rumble == null) return;
            float k = Mathf.Clamp01(speed / SteamSim.MaxSpeed);
            float target = speed > 0.2f ? Mathf.Lerp(0.65f, 1f, Mathf.Sqrt(k)) : 0f;
            float pitch = m_RealRolling ? Mathf.Lerp(0.72f, 1.15f, k) : Mathf.Lerp(0.7f, 1.25f, k);
            Fade(m_Rumble, target, dt * 0.8f);
            m_Rumble.pitch = pitch;
            if (m_Rumble2 != null)
            {
                bool wasPlaying = m_Rumble2.isPlaying;
                Fade(m_Rumble2, target * 0.8f, dt * 0.8f);
                m_Rumble2.pitch = pitch * 0.97f;               // slightly deeper copy
                if (!wasPlaying && m_Rumble2.isPlaying && m_Rumble2.clip != null)
                    m_Rumble2.timeSamples = m_Rumble2.clip.samples / 2;
            }

            if (m_RealRolling) return;                  // the recording already has the rail joints in it
            if (speed > 0.2f)
            {
                m_Travelled += speed * dt;
                if (m_Travelled >= RailLength)
                {
                    m_Travelled -= RailLength;
                    foreach (var off in AxleOffsets) m_PendingClacks.Add(off / speed);
                }
            }
            for (int i = m_PendingClacks.Count - 1; i >= 0; i--)
            {
                m_PendingClacks[i] -= dt;
                if (m_PendingClacks[i] > 0f) continue;
                m_PendingClacks.RemoveAt(i);
                m_Clack.pitch = Random.Range(0.9f, 1.1f) * Mathf.Lerp(0.85f, 1.1f, k);
                m_Clack.PlayOneShot(SynthSounds.Clack, Mathf.Lerp(0.35f, 0.9f, k) * Random.Range(0.8f, 1f));
            }
        }

        void UpdateWhistle(bool open, float dt)
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
            m_WhistleLevel = Mathf.MoveTowards(m_WhistleLevel, open ? 1f : 0f, dt * (open ? 6f : 3f));
            m_Whistle.volume = m_WhistleLevel * WhistleVolume;
            m_Whistle.pitch = 0.94f + 0.06f * m_WhistleLevel;   // the pitch rises as the valve opens
            if (m_WhistleLevel > 0f && !m_Whistle.isPlaying) m_Whistle.Play();
            else if (m_WhistleLevel <= 0f && m_Whistle.isPlaying) m_Whistle.Stop();
        }

        static void Loop(ref uint id, string ev, GameObject go, bool on)
        {
            if (on && id == 0) id = TrainSounds.Play(ev, go);
            else if (!on && id != 0) TrainSounds.Stop(ref id);
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
