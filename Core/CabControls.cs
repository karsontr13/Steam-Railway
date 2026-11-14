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
            var simulation = RailwayManager.GetSim(train);
            if (simulation == null) return false;
            var simState = simulation.S;
            switch (control.name)
            {
                case Regulator:
                {
                    float before = simState.Regulator;
                    simulation.RegulatorStep(alternative ? -0.1f : 0.1f);
                    if (simState.Regulator != before && !StoryTrainBank.Post(train, simState.Regulator > before ? "Play_SteamTrain_GearLeverUp" : "Play_SteamTrain_GearLeverDown"))
                        SynthSounds.PlayLever(train, simState.Regulator > before ? 1.05f : 0.95f, 0.6f);
                    break;
                }
                case Reverser:
                {
                    // vlick moves the lever towards forward, alternative click towards reverse
                    int before = simState.Reverser;
                    simulation.SetReverser(Mathf.Clamp(simState.Reverser + (alternative ? -1 : 1), -1, 1));
                    if (simState.Reverser != before)
                    {
                        if (!StoryTrainBank.Post(train, simState.Reverser > before ? "Play_SteamTrain_GearLeverUp" : "Play_SteamTrain_GearLeverDown"))
                            SynthSounds.PlayLever(train, 0.85f, 0.9f);
                    }
                    else if (Mathf.Abs(simulation.Velocity) > 0.5f)
                    {
                        StoryTrainBank.Post(train, "Play_SteamTrain_GearLeverLocked");
                        Message(L.T("Yön kolu sadece tren dururken değiştirilebilir.", "The reverser can only be thrown while the train is stopped."));
                    }
                    break;
                }
                case Brake:
                    simState.SteamBrake = !simState.SteamBrake;
                    if (!StoryTrainBank.Post(train, simState.SteamBrake ? "Play_SteamTrain_BrakeLeverOn" : "Play_SteamTrain_BrakeLeverOff"))
                        SynthSounds.PlayLever(train, simState.SteamBrake ? 0.92f : 1f, 0.75f);
                    break;
                case HandBrake:
                    simState.HandBrake = !simState.HandBrake;
                    if (!StoryTrainBank.Post(train, simState.HandBrake ? "Play_SteamTrain_BrakeLeverOn" : "Play_SteamTrain_BrakeLeverOff"))
                        SynthSounds.PlayValve(train);
                    break;
                case Injector:
                    simState.Injector = !simState.Injector;
                    SynthSounds.PlayValve(train);
                    break;
                case Blower:
                    simState.Blower = !simState.Blower;
                    SynthSounds.PlayValve(train);
                    break;
                case Damper:
                    Damp(simulation);
                    break;
                case Headlight:
                    simState.Headlight = !simState.Headlight;
                    SynthSounds.PlayLever(train, 1.25f, 0.45f);
                    break;
                case Pump:
                    TransferMenu.Pump(simulation);
                    break;
                case Repair:
                    BoilerRepair.TryRepair(simulation);
                    break;
                case WaterHatch:
                    TransferMenu.PourInventory(simulation);
                    break;
                case SnowHatch:
                    if (SteamTrain.IsInsideSteamTrain) return true;
                    TransferMenu.ShovelSnow(simulation);
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
        public static void UpdateHoverTexts(SteamSim simulation)
        {
            var rootTransform = simulation.Train.transform;
            var simState = simulation.S;
            string onText = L.T("açık", "open"), off = L.T("kapalı", "closed");
            string applied = L.T("ÇEKİLİ", "ON"), released = L.T("serbest", "off");
            Set(rootTransform, Regulator, L.T("Regülatör", "Regulator") + $": {Mathf.RoundToInt(simState.Regulator * 10f)}/10  " +
                L.T("(tık: aç, alternatif: kıs)", "(click: open, alternative: close)"));
            Set(rootTransform, Reverser, L.T("Yön kolu: ", "Reverser: ") +
                (simState.Reverser > 0 ? L.T("İLERİ", "FORWARD") : simState.Reverser < 0 ? L.T("GERİ", "REVERSE") : L.T("BOŞ", "NEUTRAL")) +
                L.T("  (tık: ileri, alternatif: geri)", "  (click: forward, alternative: reverse)"));
            Set(rootTransform, Brake, L.T("Buhar freni: ", "Steam brake: ") + (simState.SteamBrake ? applied : released) +
                (simulation.Pressure < 20f && simState.SteamBrake ? L.T("  (basınç yok, tutmuyor)", "  (no pressure, not holding)") : ""));
            Set(rootTransform, HandBrake, L.T("El freni: ", "Hand brake: ") + (simState.HandBrake ? applied : released));
            Set(rootTransform, SpeedGauge, L.T("Hız: ", "Speed: ") + (L.Turkish
                ? $"{Mathf.RoundToInt(Mathf.Abs(simulation.Velocity) * 3.6f)} km/s"
                : $"{Mathf.RoundToInt(Mathf.Abs(simulation.Velocity) * 3.6f)} km/h"));
            Set(rootTransform, HeatGauge, L.T("Ocak: ", "Firebox: ") + (simulation.FireBurning ? $"{Mathf.RoundToInt(simulation.FireboxTemp)} °C" : L.T("sönük", "out")));
            Set(rootTransform, Injector, L.T("Enjektör: ", "Injector: ") + (simState.Injector
                ? (simulation.InjectorWorking ? L.T("çalışıyor", "feeding") : L.T("açık (basınç/su yok)", "open (no pressure/water)"))
                : off));
            Set(rootTransform, Blower, "Blower: " + (simState.Blower ? onText : off));
            Set(rootTransform, Damper, !simulation.FireBurning
                ? L.T("Ocak sürgüsü: ocak zaten sönük", "Damper: the fire is already out")
                : Time.time < s_DampUntil
                    ? L.T("Ocak sürgüsü: söndürmek için TEKRAR tıkla", "Damper: click AGAIN to put the fire out")
                    : L.T("Ocak sürgüsü: ocağı söndür (kömür boşa yanmaz)", "Damper: put the fire out (no coal burnt in vain)"));
            Set(rootTransform, Headlight, L.T("Farlar: ", "Lamps: ") + (simState.Headlight ? L.T("açık", "on") : L.T("kapalı", "off")) +
                (simState.Headlight && !simulation.LampsLit ? L.T("  (jeneratör için buhar yok)", "  (no steam for the generator)") : ""));
            string state = simState.Exploded ? L.T("  — KAZAN PATLAMIŞ", "  — BOILER BURST")
                : simState.PlugBlown ? L.T("  — TAPA ERİMİŞ", "  — PLUG MELTED")
                : simulation.SafetyValveOpen ? L.T("  (emniyet supabı açık)", "  (safety valve lifting)") : "";
            string wear = SteamSim.Mode == Realism.Medium
                ? L.T($"  |  Kazan %{Mathf.RoundToInt(simState.BoilerCondition)}, supap %{Mathf.RoundToInt(simState.ValveCondition)}",
                      $"  |  Boiler {Mathf.RoundToInt(simState.BoilerCondition)}%, valve {Mathf.RoundToInt(simState.ValveCondition)}%")
                : "";
            Set(rootTransform, PressureGauge, L.T("Basınç: ", "Pressure: ") + $"{Mathf.RoundToInt(simulation.Pressure)} PSI" + state + wear);
            Set(rootTransform, Repair, BoilerRepair.Describe(simulation));
            Set(rootTransform, WaterGlass, L.T($"Kazan suyu: %{Mathf.RoundToInt(simulation.BoilerLevel * 100f)}", $"Boiler water: {Mathf.RoundToInt(simulation.BoilerLevel * 100f)}%") +
                $"  |  {Mathf.RoundToInt(simState.BoilerTemp)} °C");
            Set(rootTransform, TenderGauge, L.T("Su tankı: ", "Water tank: ") + $"{Mathf.RoundToInt(simState.TenderWater)} / {SteamSim.TenderCapacity:0} L" +
                (simState.SnowKg > 0f ? L.T($"  (+{Mathf.RoundToInt(simState.SnowKg)} kg kar eriyor)", $"  (+{Mathf.RoundToInt(simState.SnowKg)} kg snow melting)") : ""));
            Set(rootTransform, Pump, L.T("Su pompası: açık sudan tanka su çek", "Water pump: draw water from open water into the tank"));
            Set(rootTransform, WaterHatch, L.T("Su tankı kapağı: envanterdeki suyu dök", "Water tank filler: pour in your water") +
                $" ({Mathf.RoundToInt(simState.TenderWater)}/{SteamSim.TenderCapacity:0} L)");
            Set(rootTransform, SnowHatch, L.T("Kar küreği: tanka kar at", "Snow shovel: shovel snow into the tank") +
                $" ({Mathf.RoundToInt(simState.SnowKg)}/{SteamSim.SnowCapacityKg:0} kg)");
        }

        static float s_DampUntil;

        /// rwo clicks within three seconds pt the fire out (one stray click must not waste a good fire).
        static void Damp(SteamSim simulation)
        {
            if (!simulation.FireBurning) { Message(L.T("Ocak zaten sönük.", "The fire is already out.")); return; }
            if (Time.time >= s_DampUntil)
            {
                s_DampUntil = Time.time + 3f;
                Message(L.T("Ocağı söndürmek için sürgüye tekrar tıkla.", "Click the damper again to put the fire out."));
                return;
            }
            s_DampUntil = 0f;
            simulation.ExtinguishFire();
            SynthSounds.PlayLever(simulation.Train, 0.8f, 0.7f);
            TrainSounds.PlayFor(TrainSounds.SteamHiss, TrainSounds.Cab(simulation.Train), 1.4f);
            Message(L.T("Sürgü kapatıldı: ocak söndü.", "The damper is shut: the fire is out."));
        }

        internal static void Message(string text) => HUDMessage.AddMessage(text, false, false);

        /// steam from the safety valve (the whistle's particle systems sit on the boiler top).
        public static void UpdateEffects(SteamSim simulation)
        {
            var particleSystem = simulation.Train.m_SteamWhistleLightParticleSystem;
            if (particleSystem == null) return;
            if (simulation.SafetyValveOpen && !particleSystem.isPlaying) particleSystem.Play();
            else if (!simulation.SafetyValveOpen && particleSystem.isPlaying) particleSystem.Stop();
        }

        static void Set(Transform root, string name, string text)
        {
            var transform = root.Find(name);
            if (transform == null) return;
            var interaction = transform.GetComponent<BaseInteraction>();
            if (interaction != null) interaction.HoverText = text;
        }

        public static void ApplyRealism(SteamTrain train)
        {
            bool medium = SteamSim.Mode == Realism.Medium;
            foreach (var name in MediumOnly)
            {
                var namedChild = train.transform.Find(name);
                if (namedChild != null) namedChild.gameObject.SetActive(medium);
            }
        }
    }
}
