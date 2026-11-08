using Il2Cpp;
using Il2CppTLD.Interactions;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// Cab levers and gauges. They reuse the game's SteamTrainGearLever (click + alternative click) and
    /// SteamTrainBrakeLever (single click) components; the patched PerformInteraction calls Handle() instead of
    /// the stock logic. Gauges are levers that do nothing on click and show their value as hover text.
    internal static class CabControls
    {
        public const string Regulator = "Ctl_Regulator";
        public const string Reverser = "Ctl_Reverser";
        public const string Brake = "Ctl_Brake";
        public const string Injector = "Ctl_Injector";
        public const string Blower = "Ctl_Blower";
        public const string PressureGauge = "Gauge_Pressure";
        public const string WaterGlass = "Gauge_WaterGlass";
        public const string TenderGauge = "Gauge_Tender";
        public const string Pump = "Ctl_Pump";
        public const string Repair = "Ctl_Repair";
        public const string HandBrake = "Ctl_HandBrake";
        public const string SpeedGauge = "Gauge_Speed";
        public const string HeatGauge = "Gauge_Heat";
        public const string Headlight = "Ctl_Headlight";
        public const string Damper = "Ctl_Damper";        
        public const string Firebox = "Ctl_Firebox";     
        public const string WaterHatch = "Ext_WaterHatch";
        public const string SnowHatch = "Ext_SnowHatch";

        /// controls that only exist in Medium realism.
        public static readonly string[] MediumOnly = { Injector, Blower, Repair };

        /// returns true when the click was handled 
        public static bool Handle(SteamTrain train, GameObject control, bool alternative)
        {
            var sim = RailwayManager.GetSim(train);
            if (sim == null) return false;
            var s = sim.S;
            switch (control.name)
            {
                case Regulator:
                {
                    float before = s.Regulator;
                    sim.RegulatorStep(alternative ? -0.1f : 0.1f);
                    if (s.Regulator != before && !StoryTrainBank.Post(train, s.Regulator > before ? "Play_SteamTrain_GearLeverUp" : "Play_SteamTrain_GearLeverDown"))
                        SynthSounds.PlayLever(train, s.Regulator > before ? 1.05f : 0.95f, 0.6f);
                    break;
                }
                case Reverser:
                {
                    // vlick moves the lever towards forward, alternative click towards reverse
                    int before = s.Reverser;
                    sim.SetReverser(Mathf.Clamp(s.Reverser + (alternative ? -1 : 1), -1, 1));
                    if (s.Reverser != before)
                    {
                        if (!StoryTrainBank.Post(train, s.Reverser > before ? "Play_SteamTrain_GearLeverUp" : "Play_SteamTrain_GearLeverDown"))
                            SynthSounds.PlayLever(train, 0.85f, 0.9f);
                    }
                    else if (Mathf.Abs(sim.Velocity) > 0.5f)
                    {
                        StoryTrainBank.Post(train, "Play_SteamTrain_GearLeverLocked");
                        Message(L.T("Yön kolu sadece tren dururken değiştirilebilir.", "The reverser can only be thrown while the train is stopped."));
                    }
                    break;
                }
                case Brake:
                    s.SteamBrake = !s.SteamBrake;
                    if (!StoryTrainBank.Post(train, s.SteamBrake ? "Play_SteamTrain_BrakeLeverOn" : "Play_SteamTrain_BrakeLeverOff"))
                        SynthSounds.PlayLever(train, s.SteamBrake ? 0.92f : 1f, 0.75f);
                    break;
                case HandBrake:
                    s.HandBrake = !s.HandBrake;
                    if (!StoryTrainBank.Post(train, s.HandBrake ? "Play_SteamTrain_BrakeLeverOn" : "Play_SteamTrain_BrakeLeverOff"))
                        SynthSounds.PlayValve(train);
                    break;
                case Injector:
                    s.Injector = !s.Injector;
                    SynthSounds.PlayValve(train);
                    break;
                case Blower:
                    s.Blower = !s.Blower;
                    SynthSounds.PlayValve(train);
                    break;
                case Damper:
                    Damp(sim);
                    break;
                case Headlight:
                    s.Headlight = !s.Headlight;
                    SynthSounds.PlayLever(train, 1.25f, 0.45f);
                    break;
                case Pump:
                    TransferMenu.Pump(sim);
                    break;
                case Repair:
                    BoilerRepair.TryRepair(sim);
                    break;
                case WaterHatch:
                    TransferMenu.PourInventory(sim);
                    break;
                case SnowHatch:
                    if (SteamTrain.IsInsideSteamTrain) return true;
                    TransferMenu.ShovelSnow(sim);
                    break;
                case PressureGauge:
                case SpeedGauge:
                case HeatGauge:
                case WaterGlass:
                case TenderGauge:
                    break; 
                default:
                    return false;
            }
            return true;
        }

        /// keeps the hover texts showing the live state.
        public static void UpdateHoverTexts(SteamSim sim)
        {
            var R = sim.Train.transform;
            var s = sim.S;
            string on = L.T("açık", "open"), off = L.T("kapalı", "closed");
            string applied = L.T("ÇEKİLİ", "ON"), released = L.T("serbest", "off");
            Set(R, Regulator, L.T("Regülatör", "Regulator") + $": {Mathf.RoundToInt(s.Regulator * 10f)}/10  " +
                L.T("(tık: aç, alternatif: kıs)", "(click: open, alternative: close)"));
            Set(R, Reverser, L.T("Yön kolu: ", "Reverser: ") +
                (s.Reverser > 0 ? L.T("İLERİ", "FORWARD") : s.Reverser < 0 ? L.T("GERİ", "REVERSE") : L.T("BOŞ", "NEUTRAL")) +
                L.T("  (tık: ileri, alternatif: geri)", "  (click: forward, alternative: reverse)"));
            Set(R, Brake, L.T("Buhar freni: ", "Steam brake: ") + (s.SteamBrake ? applied : released) +
                (sim.Pressure < 20f && s.SteamBrake ? L.T("  (basınç yok, tutmuyor)", "  (no pressure, not holding)") : ""));
            Set(R, HandBrake, L.T("El freni: ", "Hand brake: ") + (s.HandBrake ? applied : released));
            Set(R, SpeedGauge, L.T("Hız: ", "Speed: ") + (L.Turkish
                ? $"{Mathf.RoundToInt(Mathf.Abs(sim.Velocity) * 3.6f)} km/s"
                : $"{Mathf.RoundToInt(Mathf.Abs(sim.Velocity) * 3.6f)} km/h"));
            Set(R, HeatGauge, L.T("Ocak: ", "Firebox: ") + (sim.FireBurning ? $"{Mathf.RoundToInt(sim.FireboxTemp)} °C" : L.T("sönük", "out")));
            Set(R, Injector, L.T("Enjektör: ", "Injector: ") + (s.Injector
                ? (sim.InjectorWorking ? L.T("çalışıyor", "feeding") : L.T("açık (basınç/su yok)", "open (no pressure/water)"))
                : off));
            Set(R, Blower, "Blower: " + (s.Blower ? on : off));
            Set(R, Damper, !sim.FireBurning
                ? L.T("Ocak sürgüsü: ocak zaten sönük", "Damper: the fire is already out")
                : Time.time < s_DampUntil
                    ? L.T("Ocak sürgüsü: söndürmek için TEKRAR tıkla", "Damper: click AGAIN to put the fire out")
                    : L.T("Ocak sürgüsü: ocağı söndür (kömür boşa yanmaz)", "Damper: put the fire out (no coal burnt in vain)"));
            Set(R, Headlight, L.T("Farlar: ", "Lamps: ") + (s.Headlight ? L.T("açık", "on") : L.T("kapalı", "off")) +
                (s.Headlight && !sim.LampsLit ? L.T("  (jeneratör için buhar yok)", "  (no steam for the generator)") : ""));
            string state = s.Exploded ? L.T("  — KAZAN PATLAMIŞ", "  — BOILER BURST")
                : s.PlugBlown ? L.T("  — TAPA ERİMİŞ", "  — PLUG MELTED")
                : sim.SafetyValveOpen ? L.T("  (emniyet supabı açık)", "  (safety valve lifting)") : "";
            string wear = SteamSim.Mode == Realism.Medium
                ? L.T($"  |  Kazan %{Mathf.RoundToInt(s.BoilerCondition)}, supap %{Mathf.RoundToInt(s.ValveCondition)}",
                      $"  |  Boiler {Mathf.RoundToInt(s.BoilerCondition)}%, valve {Mathf.RoundToInt(s.ValveCondition)}%")
                : "";
            Set(R, PressureGauge, L.T("Basınç: ", "Pressure: ") + $"{Mathf.RoundToInt(sim.Pressure)} PSI" + state + wear);
            Set(R, Repair, BoilerRepair.Describe(sim));
            Set(R, WaterGlass, L.T($"Kazan suyu: %{Mathf.RoundToInt(sim.BoilerLevel * 100f)}", $"Boiler water: {Mathf.RoundToInt(sim.BoilerLevel * 100f)}%") +
                $"  |  {Mathf.RoundToInt(s.BoilerTemp)} °C");
            Set(R, TenderGauge, L.T("Su tankı: ", "Water tank: ") + $"{Mathf.RoundToInt(s.TenderWater)} / {SteamSim.TenderCapacity:0} L" +
                (s.SnowKg > 0f ? L.T($"  (+{Mathf.RoundToInt(s.SnowKg)} kg kar eriyor)", $"  (+{Mathf.RoundToInt(s.SnowKg)} kg snow melting)") : ""));
            Set(R, Pump, L.T("Su pompası: açık sudan tanka su çek", "Water pump: draw water from open water into the tank"));
            Set(R, WaterHatch, L.T("Su tankı kapağı: envanterdeki suyu dök", "Water tank filler: pour in your water") +
                $" ({Mathf.RoundToInt(s.TenderWater)}/{SteamSim.TenderCapacity:0} L)");
            Set(R, SnowHatch, L.T("Kar küreği: tanka kar at", "Snow shovel: shovel snow into the tank") +
                $" ({Mathf.RoundToInt(s.SnowKg)}/{SteamSim.SnowCapacityKg:0} kg)");
        }

        static float s_DampUntil;

        /// rwo clicks within three seconds pt the fire out (one stray click must not waste a good fire).
        static void Damp(SteamSim sim)
        {
            if (!sim.FireBurning) { Message(L.T("Ocak zaten sönük.", "The fire is already out.")); return; }
            if (Time.time >= s_DampUntil)
            {
                s_DampUntil = Time.time + 3f;
                Message(L.T("Ocağı söndürmek için sürgüye tekrar tıkla.", "Click the damper again to put the fire out."));
                return;
            }
            s_DampUntil = 0f;
            sim.ExtinguishFire();
            SynthSounds.PlayLever(sim.Train, 0.8f, 0.7f);
            TrainSounds.PlayFor(TrainSounds.SteamHiss, TrainSounds.Cab(sim.Train), 1.4f);
            Message(L.T("Sürgü kapatıldı: ocak söndü.", "The damper is shut: the fire is out."));
        }

        internal static void Message(string text) => HUDMessage.AddMessage(text, false, false);

        /// steam from the safety valve (the whistle's particle systems sit on the boiler top).
        public static void UpdateEffects(SteamSim sim)
        {
            var ps = sim.Train.m_SteamWhistleLightParticleSystem;
            if (ps == null) return;
            if (sim.SafetyValveOpen && !ps.isPlaying) ps.Play();
            else if (!sim.SafetyValveOpen && ps.isPlaying) ps.Stop();
        }

        static void Set(Transform root, string name, string text)
        {
            var t = root.Find(name);
            if (t == null) return;
            var bi = t.GetComponent<BaseInteraction>();
            if (bi != null) bi.HoverText = text;
        }

        public static void ApplyRealism(SteamTrain train)
        {
            bool medium = SteamSim.Mode == Realism.Medium;
            foreach (var n in MediumOnly)
            {
                var t = train.transform.Find(n);
                if (t != null) t.gameObject.SetActive(medium);
            }
        }
    }
}
