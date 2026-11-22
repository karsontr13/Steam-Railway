using Il2Cpp;
using Il2CppTLD.Gear;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// Boiler maintenance through the game's own repair screen; the boiler must be cold.
    /// A part's condition is set from the simulation when the screen opens; a raised condition on close means a repair.
    internal enum BoilerPart { Boiler, FusiblePlug }

    internal static class BoilerRepair
    {
        const string ScrapMetal = "GEAR_ScrapMetal";
        static readonly string[] Tools = { "GEAR_HighQualityTools", "GEAR_SimpleTools" };
        const float ValveRenewCondition = 10f;
        const float ExplosionRepairCondition = 40f;
        const float PlugRepairCondition = 100f;
        const float RepairMinutes = 60;
        const float PlugRepairMinutes = 30;
        const int FramesBeforeCloseCheck = 3;

        const int ExplosionScrap = 8;
        const int PlugScrap = 1;
        const int ValveScrap = 2;
        const int BoilerScrap = 3;
        const float BoilerRepairAmount = 30f;
        const float MaxRepairTemperatureInCelsius = 80f;
        const float RepairableConditionBelowPercentage = 99f;
        const float FullConditionPercentage = 100f;
        const float ValveRenewedStartConditionPercentage = 50f;
        const float RepairDetectionMarginPercentage = 0.5f;
        const float ColdBoilerPressureLimit = 1f;

        /// What this part needs now: the scrap metal it costs (0 = nothing to do) and a description for the menu and the hover text.
        static int Need(SteamSimulation simulation, BoilerPart part, out string description)
        {
            var simulationState = simulation.State;
            if (part == BoilerPart.FusiblePlug)
            {
                if (simulationState.PlugBlown)
                {
                    description = Localizer.Translate($"Eriyen tapa erimiş. Yenisi takılsın mı?\n{PlugScrap} hurda metal + alet kutusu gerekir.", $"The fusible plug has melted. Fit a new one?\nNeeds {PlugScrap} scrap metal + a tool box.");
                    return PlugScrap;
                }
                description = Localizer.Translate("Eriyen tapa sağlam.", "The fusible plug is sound.");
                return 0;
            }
            if (simulationState.Exploded)
            {
                description = Localizer.Translate($"Kazan patlamış. Kabaca onarılsın mı (%40'a kadar)?\n{ExplosionScrap} hurda metal + alet kutusu gerekir.", $"The boiler has burst. Patch it up (to 40%)?\nNeeds {ExplosionScrap} scrap metal + a tool box.");
                return ExplosionScrap;
            }
            if (simulationState.ValveCondition < RepairableConditionBelowPercentage)
            {
                description = Localizer.Translate($"Emniyet supabı %{Mathf.RoundToInt(simulationState.ValveCondition)}. Yenilensin mi?\n{ValveScrap} hurda metal + alet kutusu gerekir.", $"The safety valve is at {Mathf.RoundToInt(simulationState.ValveCondition)}%. Renew it?\nNeeds {ValveScrap} scrap metal + a tool box.");
                return ValveScrap;
            }
            if (simulationState.BoilerCondition < RepairableConditionBelowPercentage)
            {
                description = Localizer.Translate($"Kazan %{Mathf.RoundToInt(simulationState.BoilerCondition)}. Onarılsın mı (+%{BoilerRepairAmount:F0})?\n{BoilerScrap} hurda metal + alet kutusu gerekir.", $"The boiler is at {Mathf.RoundToInt(simulationState.BoilerCondition)}%. Repair it (+{BoilerRepairAmount:F0}%)?\nNeeds {BoilerScrap} scrap metal + a tool box.");
                return BoilerScrap;
            }
            description = Localizer.Translate("Kazan sağlam.", "The boiler is sound.");
            return 0;
        }

        /// Hover text of the clickable part.
        public static string Describe(SteamSimulation simulation, BoilerPart part)
        {
            string title = part == BoilerPart.Boiler ? Localizer.Translate("Kazan", "Boiler") : Localizer.Translate("Eriyen tapa", "Fusible plug");
            int scrap = Need(simulation, part, out string description);
            return scrap > 0 ? title + Localizer.Translate(": onarım menüsünü aç", ": open the repair menu") : description;
        }

        /// What the open repair screen is about; read again when the screen closes.
        sealed class PendingRepair
        {
            public SteamSimulation Simulation;
            public SteamTrainComponent Part;
            public BoilerPart Kind;
            public bool WasExploded, WasValve;
            public float Baseline;
            public int OpenedFrame;
        }

        static PendingRepair s_Pending;
        static readonly System.Collections.Generic.Dictionary<SteamTrain, SteamTrainComponent> s_PlugParts = new System.Collections.Generic.Dictionary<SteamTrain, SteamTrainComponent>();

        static SteamTrainRepair MakeRepair(int scrap, int conditionRepaired, int minutes)
        {
            var repair = new SteamTrainRepair();
            repair.m_ConditionThreshold = 0f;
            repair.m_ConditionRepaired = conditionRepaired;
            repair.m_DurationMinutes = minutes;
            repair.m_RepairAudio = "";
            repair.m_RepairMaterials = new Il2CppSystem.Collections.Generic.List<SteamTrainRepairMaterial>();
            var material = new SteamTrainRepairMaterial();
            material.m_RequiredGearReference = new AssetReferenceGearItem(ScrapMetal);
            material.m_RequiredGear = Il2CppHelpers.LoadAsset<GameObject>(ScrapMetal)?.GetComponent<GearItem>();
            material.m_RequiredGearUnits = scrap;
            repair.m_RepairMaterials.Add(material);
            repair.m_RepairToolChoices = new Il2CppSystem.Collections.Generic.List<Il2CppTLD.AddressableAssets.AssetReferenceWithComponent<ToolsItem>>();
            repair.m_FilteredRepairToolChoices = new Il2CppSystem.Collections.Generic.List<ToolsItem>();
            foreach (var address in Tools)
            {
                var tool = Il2CppHelpers.LoadAsset<GameObject>(address)?.GetComponent<ToolsItem>();
                if (tool != null) repair.m_FilteredRepairToolChoices.Add(tool);
            }
            return repair;
        }

        /// The fusible plug has no part of its own in the game's train: a spare component stands in for it.
        static SteamTrainComponent PlugPart(SteamTrain train)
        {
            if (s_PlugParts.TryGetValue(train, out var existing) && existing != null) return existing;
            var holder = new GameObject("FusiblePlugPart");
            holder.transform.SetParent(train.transform, false);
            var part = holder.AddComponent<SteamTrainComponent>();
            part.m_Train = train;
            part.m_DefaultHoverText = Il2CppHelpers.Loc(Localizer.Translate("Eriyen tapa", "Fusible plug"));
            part.m_Description = Il2CppHelpers.Loc(Localizer.Translate("Kazanın ocak tavanındaki eriyen tapa.", "The fusible plug in the firebox crown."));
            part.m_RepairConfigurations = new Il2CppSystem.Collections.Generic.List<SteamTrainRepair>();
            s_PlugParts[train] = part;
            return part;
        }

        /// The player clicks the part: the game's repair screen opens for it.
        public static void Open(SteamSimulation simulation, BoilerPart part)
        {
            int scrap = Need(simulation, part, out string description);
            if (scrap == 0) { CabControls.Message(description); return; }
            if (simulation.Pressure > ColdBoilerPressureLimit || simulation.State.BoilerTemp > MaxRepairTemperatureInCelsius)
            {
                CabControls.Message(Localizer.Translate("Kazan çok sıcak. Ateşi söndür ve soğumasını bekle.", "The boiler is too hot. Let the fire die and wait for it to cool."));
                return;
            }
            var panel = InterfaceManager.GetPanel<Panel_Inventory_Examine>();
            if (panel == null) return;
            var simulationState = simulation.State;
            var pending = new PendingRepair { Simulation = simulation, Kind = part, OpenedFrame = Time.frameCount };
            SteamTrainComponent component;
            int conditionRepaired;
            if (part == BoilerPart.FusiblePlug)
            {
                component = PlugPart(simulation.Train);
                pending.Baseline = 0f;
                conditionRepaired = (int)PlugRepairCondition;
                component.m_RepairConfigurations.Clear();
                component.m_RepairConfigurations.Add(MakeRepair(scrap, conditionRepaired, (int)PlugRepairMinutes));
            }
            else
            {
                component = simulation.Train.m_Boiler;
                pending.WasExploded = simulationState.Exploded;
                pending.WasValve = !simulationState.Exploded && simulationState.ValveCondition < RepairableConditionBelowPercentage;
                pending.Baseline = pending.WasExploded ? 0f : pending.WasValve ? simulationState.ValveCondition : simulationState.BoilerCondition;
                conditionRepaired = pending.WasExploded ? (int)ExplosionRepairCondition : pending.WasValve ? (int)ValveRenewCondition : (int)BoilerRepairAmount;
                component.m_RepairConfigurations.Clear();
                component.m_RepairConfigurations.Add(MakeRepair(scrap, conditionRepaired, (int)RepairMinutes));
            }
            if (component == null) return;
            pending.Part = component;
            component.SetCondition(pending.Baseline);
            s_Pending = pending;
            panel.SetRepairable(component.TryCast<IRepairable>());
            panel.Enable(true, ComingFromScreenCategory.NotUI);
        }

        /// Every frame: once the repair screen has closed, a part whose condition rose was repaired.
        public static void Poll()
        {
            var pending = s_Pending;
            if (pending == null) return;
            if (pending.Part == null) { s_Pending = null; return; }
            if (Time.frameCount < pending.OpenedFrame + FramesBeforeCloseCheck) return;
            var panel = InterfaceManager.GetPanel<Panel_Inventory_Examine>();
            if (panel != null && (panel.IsEnabled() || panel.IsRepairing())) return;
            s_Pending = null;
            float condition = pending.Part.Condition;
            var simulationState = pending.Simulation.State;
            if (condition > pending.Baseline + RepairDetectionMarginPercentage)
            {
                if (pending.Kind == BoilerPart.FusiblePlug)
                {
                    simulationState.PlugBlown = false;
                    CabControls.Message(Localizer.Translate("Yeni eriyen tapa takıldı. Kazanı doldurmadan ateşi yakma!", "A new fusible plug is fitted. Don't light the fire before filling the boiler!"));
                }
                else if (pending.WasExploded)
                {
                    simulationState.Exploded = false;
                    simulationState.PlugBlown = false;
                    simulationState.BoilerCondition = Mathf.Max(ExplosionRepairCondition, condition);
                    simulationState.ValveCondition = ValveRenewedStartConditionPercentage;
                    CabControls.Message(Localizer.Translate("Kazan kabaca onarıldı (%40). Tam güç için bakıma devam et.", "The boiler is patched up (40%). Keep maintaining it for full strength."));
                }
                else if (pending.WasValve)
                {
                    simulationState.ValveCondition = FullConditionPercentage;
                    CabControls.Message(Localizer.Translate("Emniyet supabı yenilendi.", "The safety valve has been renewed."));
                }
                else
                {
                    simulationState.BoilerCondition = Mathf.Min(FullConditionPercentage, condition);
                    CabControls.Message(Localizer.Translate($"Kazan onarıldı (%{Mathf.RoundToInt(simulationState.BoilerCondition)}).", $"Boiler repaired ({Mathf.RoundToInt(simulationState.BoilerCondition)}%)."));
                }
            }
            if (pending.Kind == BoilerPart.Boiler) pending.Part.SetCondition(FullConditionPercentage);    // the game's boiler logic keeps seeing a sound boiler; the simulation owns the real value
        }
    }
}
