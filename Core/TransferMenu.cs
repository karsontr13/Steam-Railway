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
            var s = new Panel_TransferTrainTowerLiquid.TransferSettings();
            s.m_CanTransferToTrain = true;
            s.m_CanTransferToInventory = false;
            s.m_LiquidType = type;
            s.m_SourceLocalizedId = sourceLocId;
            s.m_SourceIcon = "ico_water_prep";
            s.m_TotalCapacityLiters = ItemLiquidVolume.FromLiters(available);
            s.m_MaxTrainCapacityLiters = ItemLiquidVolume.FromLiters(space);
            s.m_DeltaLiters = ItemLiquidVolume.FromLiters(delta);
            s.m_TransferDurationBaseSeconds = 1f;
            s.m_TransferDurationSecondsPerLiter = secondsPerLitre;
            s.m_TransferDurationPassTimeMinutesPerLiter = minutesPerLitre;
            s.m_TransferLiquidAudioEvent = H.Ev();
            Action<ItemLiquidVolume, Panel_TransferTrainTowerLiquid.TransferAction> cb = (v, _) =>
            {
                try { onDone(Litres(v)); }
                catch (Exception e) { SteamRailwayMod.Log.Error($"Transfer failed: {e}"); }
            };
            s.m_OnTransferLiquid = cb;
            panel.ShowLiquidTransfer(s);
        }

        // ------------------------------------------------------------------ inventory water

        public static void PourInventory(SteamSim sim)
        {
            var inv = GameManager.GetInventoryComponent();
            float nonPotable = Volume(inv.GetNonPotableWaterSupply());
            float potable = Volume(inv.GetPotableWaterSupply());
            if (sim.TenderSpace <= 0.01f) { CabControls.Message(L.T("Su tankı dolu.", "The water tank is full.")); return; }
            if (nonPotable + potable <= 0.01f) { CabControls.Message(L.T("Envanterinde su yok.", "You have no water.")); return; }
            var type = nonPotable > 0.01f ? LiquidType.GetNonPotableWater() : LiquidType.GetPotableWater();
            Show(type, nonPotable > 0.01f ? "GAMEPLAY_WaterNonPotable" : "GAMEPLAY_WaterPotable",
                 nonPotable + potable, sim.TenderSpace, 0.5f, 0.05f, 0.05f, litres =>
            {
                float poured = WaterTransfer.PourInventoryIntoTender(sim, litres);
                if (poured > 0f) TrainSounds.Play(TrainSounds.PourWater, GameManager.GetPlayerObject());
                CabControls.Message(L.T($"Tanka {poured:0.0} L su döküldü.", $"Poured {poured:0.0} L of water into the tank."));
            });
        }

        static float Volume(GearItem supply) =>
            supply != null && supply.m_WaterSupply != null ? Litres(supply.m_WaterSupply.m_VolumeInLiters) : 0f;

        // ------------------------------------------------------------------ steam pump

        public static void Pump(SteamSim sim)
        {
            string error = SteamSim.PumpError(sim);
            if (error != null) { CabControls.Message(error); return; }
            bool steam = sim.PumpBySteam;
            if (steam) SynthSounds.PlayValve(sim.Train);
            else CabControls.Message(L.T($"Buhar {SteamSim.PumpMinPsi:0} PSI'ın altında: pompa elle çalıştırılıyor (yavaş).",
                                         $"Steam is below {SteamSim.PumpMinPsi:0} PSI: working the pump by hand (slow)."));
            float flow = steam ? SteamSim.PumpFlow : SteamSim.HandPumpFlow;
            Show(LiquidType.GetNonPotableWater(), "GAMEPLAY_Water", sim.TenderSpace, sim.TenderSpace, steam ? 10f : 5f,
                 0.01f, 1f / flow, litres =>
            {
                float pumped = sim.PumpIn(litres, steam);
                TrainSounds.Play(TrainSounds.PumpWater, TrainSounds.Cab(sim.Train));
                CabControls.Message(steam
                    ? L.T($"Pompa tanka {pumped:0} L su çekti.", $"The pump drew {pumped:0} L of water into the tank.")
                    : L.T($"El pompasıyla tanka {pumped:0} L su çekildi.", $"Hand-pumped {pumped:0} L of water into the tank."));
            });
        }

        // ------------------------------------------------------------------ snow

        public static void ShovelSnow(SteamSim sim)
        {
            if (GameManager.GetWeatherComponent().IsIndoorEnvironment()) { CabControls.Message(L.T("Burada kar yok.", "There is no snow here.")); return; }
            float room = Mathf.Min(SteamSim.SnowCapacityKg - sim.S.SnowKg, sim.TenderSpace - sim.S.SnowKg);
            if (room < 1f) { CabControls.Message(L.T("Tankta daha fazla kara yer yok.", "There is no room for more snow in the tank.")); return; }
            Show(LiquidType.GetNonPotableWater(), "GAMEPLAY_Snow", room, room, 5f,
                 0.04f, SteamSim.ShovelMinutesPerKg, kg =>
            {
                sim.AddSnow(kg);
                TrainSounds.Play(TrainSounds.Snow, GameManager.GetPlayerObject());
                CabControls.Message(L.T($"Tanka {kg:0} kg kar küreklendi ({sim.S.SnowKg:0} kg eriyor).",
                                        $"Shovelled {kg:0} kg of snow into the tank ({sim.S.SnowKg:0} kg melting)."));
            });
        }
    }
}
