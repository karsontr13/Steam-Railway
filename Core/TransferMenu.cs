using System;
using Il2Cpp;
using Il2CppTLD.Gear;
using Il2CppTLD.IntBackedUnit;
using UnityEngine;

namespace SteamRailway
{
    /// Water transfers into the tender through the game's liquid transfer panel: the player picks an amount,
    /// game time passes, then the water arrives (water hatch, steam pump or snow hatch).
    internal static class TransferMenu
    {
        const long UnitsPerLitre = 1_000_000_000L;
        const float BaseTransferSeconds = 1f;
        const float MinimumUsableLitres = 0.01f;
        const float InventoryStepInLitres = 0.5f;
        const float InventorySecondsPerLitre = 0.05f;
        const float InventoryMinutesPerLitre = 0.05f;
        const float SteamPumpStepInLitres = 10f;
        const float HandPumpStepInLitres = 5f;
        const float PumpSecondsPerLitre = 0.01f;
        const float SnowStepInKilograms = 5f;
        const float SnowSecondsPerKilogram = 0.04f;
        const float MinimumSnowRoomInKilograms = 1f;

        static Panel_TransferTrainTowerLiquid Panel => InterfaceManager.GetPanel<Panel_TransferTrainTowerLiquid>();

        static float Litres(ItemLiquidVolume volume) => volume.m_Units / (float)UnitsPerLitre;

        static void Show(LiquidType type, string sourceLocId, float available, float space, float delta,
                         float secondsPerLitre, float minutesPerLitre, Action<float> onDone)
        {
            var panel = Panel;
            if (panel == null) { CabControls.Message(Localizer.Translate("Aktarım menüsü açılamadı.", "The transfer menu could not be opened.")); return; }
            var settings = new Panel_TransferTrainTowerLiquid.TransferSettings();
            settings.m_CanTransferToTrain = true;
            settings.m_CanTransferToInventory = false;
            settings.m_LiquidType = type;
            settings.m_SourceLocalizedId = sourceLocId;
            settings.m_SourceIcon = "ico_water_prep";
            settings.m_TotalCapacityLiters = ItemLiquidVolume.FromLiters(available);
            settings.m_MaxTrainCapacityLiters = ItemLiquidVolume.FromLiters(space);
            settings.m_DeltaLiters = ItemLiquidVolume.FromLiters(delta);
            settings.m_TransferDurationBaseSeconds = BaseTransferSeconds;
            settings.m_TransferDurationSecondsPerLiter = secondsPerLitre;
            settings.m_TransferDurationPassTimeMinutesPerLiter = minutesPerLitre;
            settings.m_TransferLiquidAudioEvent = Il2CppHelpers.CreateWwiseEvent();
            Action<ItemLiquidVolume, Panel_TransferTrainTowerLiquid.TransferAction> callback = (volume, _) =>
            {
                try { onDone(Litres(volume)); }
                catch (Exception exception) { SteamRailwayMod.Log.Error($"Transfer failed: {exception}"); }
            };
            settings.m_OnTransferLiquid = callback;
            panel.ShowLiquidTransfer(settings);
        }

        // ------------------------------------------------------------------ inventory water

        public static void PourInventory(SteamSimulation simulation)
        {
            var inventory = GameManager.GetInventoryComponent();
            float nonPotable = Volume(inventory.GetNonPotableWaterSupply());
            float potable = Volume(inventory.GetPotableWaterSupply());
            if (simulation.TenderSpace <= MinimumUsableLitres) { CabControls.Message(Localizer.Translate("Su tankı dolu.", "The water tank is full.")); return; }
            if (nonPotable + potable <= MinimumUsableLitres) { CabControls.Message(Localizer.Translate("Envanterinde su yok.", "You have no water.")); return; }
            var type = nonPotable > MinimumUsableLitres ? LiquidType.GetNonPotableWater() : LiquidType.GetPotableWater();
            Show(type, nonPotable > MinimumUsableLitres ? "GAMEPLAY_WaterNonPotable" : "GAMEPLAY_WaterPotable",
                 nonPotable + potable, simulation.TenderSpace, InventoryStepInLitres, InventorySecondsPerLitre, InventoryMinutesPerLitre, litres =>
            {
                float poured = WaterTransfer.PourInventoryIntoTender(simulation, litres);
                if (poured > 0f) TrainSounds.Play(TrainSounds.PourWater, GameManager.GetPlayerObject());
                CabControls.Message(Localizer.Translate($"Tanka {poured:0.0} L su döküldü.", $"Poured {poured:0.0} L of water into the tank."));
            });
        }

        static float Volume(GearItem supply) =>
            supply != null && supply.m_WaterSupply != null ? Litres(supply.m_WaterSupply.m_VolumeInLiters) : 0f;

        // ------------------------------------------------------------------ steam pump

        public static void Pump(SteamSimulation simulation)
        {
            string error = SteamSimulation.PumpError(simulation);
            if (error != null) { CabControls.Message(error); return; }
            bool steam = simulation.PumpBySteam;
            if (steam) SynthSounds.PlayValve(simulation.Train);
            else CabControls.Message(Localizer.Translate($"Buhar {SteamSimulation.PumpMinPsi:0} PSI'ın altında: pompa elle çalıştırılıyor (yavaş).",
                                         $"Steam is below {SteamSimulation.PumpMinPsi:0} PSI: working the pump by hand (slow)."));
            float flow = steam ? SteamSimulation.PumpFlow : SteamSimulation.HandPumpFlow;
            Show(LiquidType.GetNonPotableWater(), "GAMEPLAY_Water", simulation.TenderSpace, simulation.TenderSpace, steam ? SteamPumpStepInLitres : HandPumpStepInLitres,
                 PumpSecondsPerLitre, 1f / flow, litres =>
            {
                float pumped = simulation.PumpIn(litres, steam);
                TrainSounds.Play(TrainSounds.PumpWater, TrainSounds.Cab(simulation.Train));
                CabControls.Message(steam
                    ? Localizer.Translate($"Pompa tanka {pumped:0} L su çekti.", $"The pump drew {pumped:0} L of water into the tank.")
                    : Localizer.Translate($"El pompasıyla tanka {pumped:0} L su çekildi.", $"Hand-pumped {pumped:0} L of water into the tank."));
            });
        }

        // ------------------------------------------------------------------ snow

        public static void ShovelSnow(SteamSimulation simulation)
        {
            if (GameManager.GetWeatherComponent().IsIndoorEnvironment()) { CabControls.Message(Localizer.Translate("Burada kar yok.", "There is no snow here.")); return; }
            float room = Mathf.Min(SteamSimulation.SnowCapacityKg - simulation.State.SnowKg, simulation.TenderSpace - simulation.State.SnowKg);
            if (room < MinimumSnowRoomInKilograms) { CabControls.Message(Localizer.Translate("Tankta daha fazla kara yer yok.", "There is no room for more snow in the tank.")); return; }
            Show(LiquidType.GetNonPotableWater(), "GAMEPLAY_Snow", room, room, SnowStepInKilograms,
                 SnowSecondsPerKilogram, SteamSimulation.ShovelMinutesPerKg, kilograms =>
            {
                simulation.AddSnow(kilograms);
                TrainSounds.Play(TrainSounds.Snow, GameManager.GetPlayerObject());
                CabControls.Message(Localizer.Translate($"Tanka {kilograms:0} kg kar küreklendi ({simulation.State.SnowKg:0} kg eriyor).",
                                        $"Shovelled {kilograms:0} kg of snow into the tank ({simulation.State.SnowKg:0} kg melting)."));
            });
        }
    }
}
