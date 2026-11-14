using System;
using Il2Cpp;
using Il2CppTLD.Gear;
using Il2CppTLD.IntBackedUnit;
using UnityEngine;

namespace SteamRailway
{
    /// Water transfers into the tender through the game's liquid transfer panel (the same menu the water
    /// towers use): the player picks an amount, a progress bar runs and game time passes, then the water arrives.
    ///   - water hatch: the player's water (non-potable first, then potable; all of it can go)
    ///   - steam pump:  open water near the train (a player-cut ice hole or flowing water)
    ///   - snow hatch:  snow shovelled into the tender (1 kg snow = 1 L water once it melts)
    internal static class TransferMenu
    {
        const long UnitsPerLitre = 1_000_000_000L;

        static Panel_TransferTrainTowerLiquid Panel => InterfaceManager.GetPanel<Panel_TransferTrainTowerLiquid>();

        static float Litres(ItemLiquidVolume v) => v.m_Units / (float)UnitsPerLitre;

        static void Show(LiquidType type, string sourceLocId, float available, float space, float delta,
                         float secondsPerLitre, float minutesPerLitre, Action<float> onDone)
        {
            var panel = Panel;
            if (panel == null) { CabControls.Message(L.T("Aktarım menüsü açılamadı.", "The transfer menu could not be opened.")); return; }
            var settings = new Panel_TransferTrainTowerLiquid.TransferSettings();
            settings.m_CanTransferToTrain = true;
            settings.m_CanTransferToInventory = false;
            settings.m_LiquidType = type;
            settings.m_SourceLocalizedId = sourceLocId;
            settings.m_SourceIcon = "ico_water_prep";
            settings.m_TotalCapacityLiters = ItemLiquidVolume.FromLiters(available);
            settings.m_MaxTrainCapacityLiters = ItemLiquidVolume.FromLiters(space);
            settings.m_DeltaLiters = ItemLiquidVolume.FromLiters(delta);
            settings.m_TransferDurationBaseSeconds = 1f;
            settings.m_TransferDurationSecondsPerLiter = secondsPerLitre;
            settings.m_TransferDurationPassTimeMinutesPerLiter = minutesPerLitre;
            settings.m_TransferLiquidAudioEvent = H.Ev();
            Action<ItemLiquidVolume, Panel_TransferTrainTowerLiquid.TransferAction> callback = (v, _) =>
            {
                try { onDone(Litres(v)); }
                catch (Exception e) { SteamRailwayMod.Log.Error($"Transfer failed: {e}"); }
            };
            settings.m_OnTransferLiquid = callback;
            panel.ShowLiquidTransfer(settings);
        }

        // ------------------------------------------------------------------ inventory water

        public static void PourInventory(SteamSim simulation)
        {
            var inventory = GameManager.GetInventoryComponent();
            float nonPotable = Volume(inventory.GetNonPotableWaterSupply());
            float potable = Volume(inventory.GetPotableWaterSupply());
            if (simulation.TenderSpace <= 0.01f) { CabControls.Message(L.T("Su tankı dolu.", "The water tank is full.")); return; }
            if (nonPotable + potable <= 0.01f) { CabControls.Message(L.T("Envanterinde su yok.", "You have no water.")); return; }
            var type = nonPotable > 0.01f ? LiquidType.GetNonPotableWater() : LiquidType.GetPotableWater();
            Show(type, nonPotable > 0.01f ? "GAMEPLAY_WaterNonPotable" : "GAMEPLAY_WaterPotable",
                 nonPotable + potable, simulation.TenderSpace, 0.5f, 0.05f, 0.05f, litres =>
            {
                float poured = WaterTransfer.PourInventoryIntoTender(simulation, litres);
                if (poured > 0f) TrainSounds.Play(TrainSounds.PourWater, GameManager.GetPlayerObject());
                CabControls.Message(L.T($"Tanka {poured:0.0} L su döküldü.", $"Poured {poured:0.0} L of water into the tank."));
            });
        }

        static float Volume(GearItem supply) =>
            supply != null && supply.m_WaterSupply != null ? Litres(supply.m_WaterSupply.m_VolumeInLiters) : 0f;

        // ------------------------------------------------------------------ steam pump

        public static void Pump(SteamSim simulation)
        {
            string error = SteamSim.PumpError(simulation);
            if (error != null) { CabControls.Message(error); return; }
            bool steam = simulation.PumpBySteam;
            if (steam) SynthSounds.PlayValve(simulation.Train);
            else CabControls.Message(L.T($"Buhar {SteamSim.PumpMinPsi:0} PSI'ın altında: pompa elle çalıştırılıyor (yavaş).",
                                         $"Steam is below {SteamSim.PumpMinPsi:0} PSI: working the pump by hand (slow)."));
            float flow = steam ? SteamSim.PumpFlow : SteamSim.HandPumpFlow;
            Show(LiquidType.GetNonPotableWater(), "GAMEPLAY_Water", simulation.TenderSpace, simulation.TenderSpace, steam ? 10f : 5f,
                 0.01f, 1f / flow, litres =>
            {
                float pumped = simulation.PumpIn(litres, steam);
                TrainSounds.Play(TrainSounds.PumpWater, TrainSounds.Cab(simulation.Train));
                CabControls.Message(steam
                    ? L.T($"Pompa tanka {pumped:0} L su çekti.", $"The pump drew {pumped:0} L of water into the tank.")
                    : L.T($"El pompasıyla tanka {pumped:0} L su çekildi.", $"Hand-pumped {pumped:0} L of water into the tank."));
            });
        }

        // ------------------------------------------------------------------ snow

        public static void ShovelSnow(SteamSim simulation)
        {
            if (GameManager.GetWeatherComponent().IsIndoorEnvironment()) { CabControls.Message(L.T("Burada kar yok.", "There is no snow here.")); return; }
            float room = Mathf.Min(SteamSim.SnowCapacityKg - simulation.S.SnowKg, simulation.TenderSpace - simulation.S.SnowKg);
            if (room < 1f) { CabControls.Message(L.T("Tankta daha fazla kara yer yok.", "There is no room for more snow in the tank.")); return; }
            Show(LiquidType.GetNonPotableWater(), "GAMEPLAY_Snow", room, room, 5f,
                 0.04f, SteamSim.ShovelMinutesPerKg, kilograms =>
            {
                simulation.AddSnow(kilograms);
                TrainSounds.Play(TrainSounds.Snow, GameManager.GetPlayerObject());
                CabControls.Message(L.T($"Tanka {kilograms:0} kg kar küreklendi ({simulation.S.SnowKg:0} kg eriyor).",
                                        $"Shovelled {kilograms:0} kg of snow into the tank ({simulation.S.SnowKg:0} kg melting)."));
            });
        }
    }
}
