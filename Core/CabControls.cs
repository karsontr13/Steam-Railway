using Il2Cpp;
using Il2CppTLD.Interactions;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// Cab levers and gauges built on the game's gear-lever and brake-lever components; the patched
    /// PerformInteraction calls Handle(). Gauges do nothing on click and show their value as hover text.
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
        public const string HandBrake = "Ctl_HandBrake";
        public const string SpeedGauge = "Gauge_Speed";
        public const string HeatGauge = "Gauge_Heat";
        public const string Headlight = "Ctl_Headlight";
        public const string CabLight = "Ctl_CabLight";
        public const string Bed = "Ctl_Bed";
        public const string Damper = "Ctl_Damper";
        public const string Firebox = "Ctl_Firebox";
        public const string BoilerRepairLeft = "Ext_BoilerRepairLeft";
        public const string BoilerRepairRight = "Ext_BoilerRepairRight";
        public const string PlugRepair = "Ext_PlugRepair";
        public const string WaterHatch = "Ext_WaterHatch";
        public const string SnowHatch = "Ext_SnowHatch";

        const float RegulatorStepFraction = 0.1f;
        const float RegulatorRaisedPitch = 1.05f;
        const float RegulatorLoweredPitch = 0.95f;
        const float RegulatorLeverVolume = 0.6f;
        const float ReverserLeverPitch = 0.85f;
        const float ReverserLeverVolume = 0.9f;
        const float ReverserLockedSpeedInMetersPerSecond = 0.5f;
        const float SteamBrakeOnPitch = 0.92f;
        const float SteamBrakeLeverVolume = 0.75f;
        const float HeadlightLeverPitch = 1.25f;
        const float HeadlightLeverVolume = 0.45f;
        const float CabLightLeverPitch = 1.4f;
        const float CabLightLeverVolume = 0.4f;
        const float BedOpenPitch = 0.7f;
        const float BedFoldedPitch = 0.6f;
        const float BedLeverVolume = 0.8f;
        const float DamperLeverPitch = 0.8f;
        const float DamperLeverVolume = 0.7f;
        const float DamperHissVolume = 1.4f;
        const float DamperConfirmWindowInSeconds = 3f;
        const float RegulatorDisplayStepsPerFull = 10f;
        const float MetersPerSecondToKilometersPerHour = 3.6f;
        const float PercentPerFraction = 100f;
        const float BrakeHoldingMinimumPressureInPsi = 20f;

        /// controls that only exist in Medium realism.
        public static readonly string[] MediumOnly = { Injector, Blower };

        /// Returns true when the click was handled.
        public static bool Handle(SteamTrain train, GameObject control, bool alternative)
        {
            var simulation = RailwayManager.GetSimulation(train);
            if (simulation == null) return false;
            var simulationState = simulation.State;
            switch (control.name)
            {
                case Regulator:
                {
                    float before = simulationState.Regulator;
                    simulation.RegulatorStep(alternative ? -RegulatorStepFraction : RegulatorStepFraction);
                    if (simulationState.Regulator != before && !StoryTrainBank.Post(train, simulationState.Regulator > before ? "Play_SteamTrain_GearLeverUp" : "Play_SteamTrain_GearLeverDown"))
                        SynthSounds.PlayLever(train, simulationState.Regulator > before ? RegulatorRaisedPitch : RegulatorLoweredPitch, RegulatorLeverVolume);
                    break;
                }
                case Reverser:
                {
                    // click moves the lever towards forward, alternative click towards reverse
                    int before = simulationState.Reverser;
                    simulation.SetReverser(Mathf.Clamp(simulationState.Reverser + (alternative ? -1 : 1), -1, 1));
                    if (simulationState.Reverser != before)
                    {
                        if (!StoryTrainBank.Post(train, simulationState.Reverser > before ? "Play_SteamTrain_GearLeverUp" : "Play_SteamTrain_GearLeverDown"))
                            SynthSounds.PlayLever(train, ReverserLeverPitch, ReverserLeverVolume);
                    }
                    else if (Mathf.Abs(simulation.Velocity) > ReverserLockedSpeedInMetersPerSecond)
                    {
                        StoryTrainBank.Post(train, "Play_SteamTrain_GearLeverLocked");
                        Message(Localizer.Translate("Yön kolu sadece tren dururken değiştirilebilir.", "The reverser can only be thrown while the train is stopped."));
                    }
                    break;
                }
                case Brake:
                    simulationState.SteamBrake = !simulationState.SteamBrake;
                    if (!StoryTrainBank.Post(train, simulationState.SteamBrake ? "Play_SteamTrain_BrakeLeverOn" : "Play_SteamTrain_BrakeLeverOff"))
                        SynthSounds.PlayLever(train, simulationState.SteamBrake ? SteamBrakeOnPitch : 1f, SteamBrakeLeverVolume);
                    break;
                case HandBrake:
                    simulationState.HandBrake = !simulationState.HandBrake;
                    if (!StoryTrainBank.Post(train, simulationState.HandBrake ? "Play_SteamTrain_BrakeLeverOn" : "Play_SteamTrain_BrakeLeverOff"))
                        SynthSounds.PlayValve(train);
                    break;
                case Injector:
                    simulationState.Injector = !simulationState.Injector;
                    SynthSounds.PlayValve(train);
                    break;
                case Blower:
                    simulationState.Blower = !simulationState.Blower;
                    SynthSounds.PlayValve(train);
                    break;
                case Damper:
                    Damp(simulation);
                    break;
                case Headlight:
                    simulationState.Headlight = !simulationState.Headlight;
                    SynthSounds.PlayLever(train, HeadlightLeverPitch, HeadlightLeverVolume);
                    break;
                case CabLight:
                    simulationState.CabLight = !simulationState.CabLight;
                    SynthSounds.PlayLever(train, CabLightLeverPitch, CabLightLeverVolume);
                    break;
                case Bed:
                    simulationState.BedOpen = !simulationState.BedOpen;
                    SynthSounds.PlayLever(train, simulationState.BedOpen ? BedOpenPitch : BedFoldedPitch, BedLeverVolume);
                    break;
                case Pump:
                    TransferMenu.Pump(simulation);
                    break;
                case BoilerRepairLeft:
                case BoilerRepairRight:
                    BoilerRepair.Open(simulation, BoilerPart.Boiler);
                    break;
                case PlugRepair:
                    BoilerRepair.Open(simulation, BoilerPart.FusiblePlug);
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

        /// Keeps the hover texts showing the live state.
        public static void UpdateHoverTexts(SteamSimulation simulation)
        {
            var rootTransform = simulation.Train.transform;
            var simulationState = simulation.State;
            string onText = Localizer.Translate("açık", "open"), off = Localizer.Translate("kapalı", "closed");
            string applied = Localizer.Translate("ÇEKİLİ", "ON"), released = Localizer.Translate("serbest", "off");
            Set(rootTransform, Regulator, Localizer.Translate("Regülatör", "Regulator") + $": {Mathf.RoundToInt(simulationState.Regulator * RegulatorDisplayStepsPerFull)}/10  " +
                Localizer.Translate("(tık: aç, alternatif: kıs)", "(click: open, alternative: close)"));
            Set(rootTransform, Reverser, Localizer.Translate("Yön kolu: ", "Reverser: ") +
                (simulationState.Reverser > 0 ? Localizer.Translate("İLERİ", "FORWARD") : simulationState.Reverser < 0 ? Localizer.Translate("GERİ", "REVERSE") : Localizer.Translate("BOŞ", "NEUTRAL")) +
                Localizer.Translate("  (tık: ileri, alternatif: geri)", "  (click: forward, alternative: reverse)"));
            Set(rootTransform, Brake, Localizer.Translate("Buhar freni: ", "Steam brake: ") + (simulationState.SteamBrake ? applied : released) +
                (simulation.Pressure < BrakeHoldingMinimumPressureInPsi && simulationState.SteamBrake ? Localizer.Translate("  (basınç yok, tutmuyor)", "  (no pressure, not holding)") : ""));
            Set(rootTransform, HandBrake, Localizer.Translate("El freni: ", "Hand brake: ") + (simulationState.HandBrake ? applied : released));
            Set(rootTransform, SpeedGauge, Localizer.Translate("Hız: ", "Speed: ") + (Localizer.Turkish
                ? $"{Mathf.RoundToInt(Mathf.Abs(simulation.Velocity) * MetersPerSecondToKilometersPerHour)} km/s"
                : $"{Mathf.RoundToInt(Mathf.Abs(simulation.Velocity) * MetersPerSecondToKilometersPerHour)} km/h"));
            Set(rootTransform, HeatGauge, Localizer.Translate("Ocak: ", "Firebox: ") + (simulation.FireBurning ? $"{Mathf.RoundToInt(simulation.FireboxTemp)} °C" : Localizer.Translate("sönük", "out")));
            Set(rootTransform, Injector, Localizer.Translate("Enjektör: ", "Injector: ") + (simulationState.Injector
                ? (simulation.InjectorWorking ? Localizer.Translate("çalışıyor", "feeding") : Localizer.Translate("açık (basınç/su yok)", "open (no pressure/water)"))
                : off));
            Set(rootTransform, Blower, "Blower: " + (simulationState.Blower ? onText : off));
            Set(rootTransform, Damper, !simulation.FireBurning
                ? Localizer.Translate("Ocak sürgüsü: ocak zaten sönük", "Damper: the fire is already out")
                : Time.time < s_DampUntil
                    ? Localizer.Translate("Ocak sürgüsü: söndürmek için TEKRAR tıkla", "Damper: click AGAIN to put the fire out")
                    : Localizer.Translate("Ocak sürgüsü: ocağı söndür (kömür boşa yanmaz)", "Damper: put the fire out (no coal burnt in vain)"));
            Set(rootTransform, Headlight, Localizer.Translate("Farlar: ", "Lamps: ") + (simulationState.Headlight ? Localizer.Translate("açık", "on") : Localizer.Translate("kapalı", "off")) +
                (simulationState.Headlight && !simulation.LampsLit ? Localizer.Translate("  (jeneratör için buhar yok)", "  (no steam for the generator)") : ""));
            Set(rootTransform, CabLight, Localizer.Translate("Kabin lambası: ", "Cab light: ") + (simulationState.CabLight ? Localizer.Translate("açık", "on") : Localizer.Translate("kapalı", "off")) +
                (simulationState.CabLight && simulation.Pressure <= SteamSimulation.LampPressure ? Localizer.Translate("  (lamba için buhar yok)", "  (no steam for the lamp)") : ""));
            Set(rootTransform, Bed, simulationState.BedOpen ? Localizer.Translate("Portatif yatak: katla", "Folding bed: fold it away") : Localizer.Translate("Portatif yatak: aç", "Folding bed: unfold"));
            string state = simulationState.Exploded ? Localizer.Translate("  — KAZAN PATLAMIŞ", "  — BOILER BURST")
                : simulationState.PlugBlown ? Localizer.Translate("  — TAPA ERİMİŞ", "  — PLUG MELTED")
                : simulation.SafetyValveOpen ? Localizer.Translate("  (emniyet supabı açık)", "  (safety valve lifting)") : "";
            string wear = SteamSimulation.Mode == Realism.Medium
                ? Localizer.Translate($"  |  Kazan %{Mathf.RoundToInt(simulationState.BoilerCondition)}, supap %{Mathf.RoundToInt(simulationState.ValveCondition)}",
                      $"  |  Boiler {Mathf.RoundToInt(simulationState.BoilerCondition)}%, valve {Mathf.RoundToInt(simulationState.ValveCondition)}%")
                : "";
            Set(rootTransform, PressureGauge, Localizer.Translate("Basınç: ", "Pressure: ") + $"{Mathf.RoundToInt(simulation.Pressure)} PSI" + state + wear);
            Set(rootTransform, BoilerRepairLeft, BoilerRepair.Describe(simulation, BoilerPart.Boiler));
            Set(rootTransform, BoilerRepairRight, BoilerRepair.Describe(simulation, BoilerPart.Boiler));
            Set(rootTransform, PlugRepair, BoilerRepair.Describe(simulation, BoilerPart.FusiblePlug));
            Set(rootTransform, WaterGlass, Localizer.Translate($"Kazan suyu: %{Mathf.RoundToInt(simulation.BoilerLevel * PercentPerFraction)}", $"Boiler water: {Mathf.RoundToInt(simulation.BoilerLevel * PercentPerFraction)}%") +
                $"  |  {Mathf.RoundToInt(simulationState.BoilerTemp)} °C");
            Set(rootTransform, TenderGauge, Localizer.Translate("Su tankı: ", "Water tank: ") + $"{Mathf.RoundToInt(simulationState.TenderWater)} / {SteamSimulation.TenderCapacity:0} L" +
                (simulationState.SnowKg > 0f ? Localizer.Translate($"  (+{Mathf.RoundToInt(simulationState.SnowKg)} kg kar eriyor)", $"  (+{Mathf.RoundToInt(simulationState.SnowKg)} kg snow melting)") : ""));
            Set(rootTransform, Pump, Localizer.Translate("Su pompası: açık sudan tanka su çek", "Water pump: draw water from open water into the tank"));
            Set(rootTransform, WaterHatch, Localizer.Translate("Su tankı kapağı: envanterdeki suyu dök", "Water tank filler: pour in your water") +
                $" ({Mathf.RoundToInt(simulationState.TenderWater)}/{SteamSimulation.TenderCapacity:0} L)");
            Set(rootTransform, SnowHatch, Localizer.Translate("Kar küreği: tanka kar at", "Snow shovel: shovel snow into the tank") +
                $" ({Mathf.RoundToInt(simulationState.SnowKg)}/{SteamSimulation.SnowCapacityKg:0} kg)");
        }

        static float s_DampUntil;
        const float MinimumKeptHoursToMention = 0.05f;

        /// Two clicks within a few seconds put the fire out; one stray click must not waste a good fire.
        static void Damp(SteamSimulation simulation)
        {
            if (!simulation.FireBurning) { Message(Localizer.Translate("Ocak zaten sönük.", "The fire is already out.")); return; }
            if (Time.time >= s_DampUntil)
            {
                s_DampUntil = Time.time + DamperConfirmWindowInSeconds;
                Message(Localizer.Translate("Ocağı söndürmek için sürgüye tekrar tıkla.", "Click the damper again to put the fire out."));
                return;
            }
            s_DampUntil = 0f;
            float keptHours = simulation.PutOutFire();
            SynthSounds.PlayLever(simulation.Train, DamperLeverPitch, DamperLeverVolume);
            TrainSounds.PlayFor(TrainSounds.SteamHiss, TrainSounds.Cab(simulation.Train), DamperHissVolume);
            Message(keptHours >= MinimumKeptHoursToMention
                ? Localizer.Translate($"Sürgü kapatıldı: ocak söndü ({keptHours:0.0} saatlik kömür saklandı).", $"The damper is shut: the fire is out ({keptHours:0.0} h of coal kept).")
                : Localizer.Translate("Sürgü kapatıldı: ocak söndü.", "The damper is shut: the fire is out."));
        }

        internal static void Message(string text) => HUDMessage.AddMessage(text, false, false);

        /// Steam from the safety valve, using the whistle's particle system on the boiler top.
        public static void UpdateEffects(SteamSimulation simulation)
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
            bool medium = SteamSimulation.Mode == Realism.Medium;
            foreach (var name in MediumOnly)
            {
                var namedChild = train.transform.Find(name);
                if (namedChild != null) namedChild.gameObject.SetActive(medium);
            }
        }
    }
}
