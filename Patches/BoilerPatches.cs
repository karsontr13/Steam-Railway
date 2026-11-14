using HarmonyLib;
using Il2CppTLD.SteamTrain;

namespace SteamRailway.Patches
{
    /// Our locomotives run on SteamSim, not on the stock engine/boiler logic.
    /// SteamTrain.Update calls UpdateEngine every frame right before it moves the train, so the simulation ticks there.
    [HarmonyPatch(typeof(SteamTrainEngine), nameof(SteamTrainEngine.UpdateEngine))]
    internal static class Engine_UpdateEngine
    {
        private static bool Prefix(SteamTrainEngine __instance)
        {
            var simulation = RailwayManager.GetSim(__instance.m_SteamTrain);
            if (simulation == null) return true;
            simulation.Tick();
            return false;
        }
    }

    /// Getting in or out swings the modelled cab door (after a boiler explosion the player may leave whenever they like).
    [HarmonyPatch(typeof(SteamTrainDoor), nameof(SteamTrainDoor.PerformInteraction))]
    internal static class Door_Perform
    {
        private static void Postfix(SteamTrainDoor __instance)
        {
            if (RailwayManager.IsOurTrain(__instance.m_ParentSteamTrain)) LocomotiveAnimator.SwingDoor();
        }
    }

    [HarmonyPatch(typeof(SteamTrainDoor), nameof(SteamTrainDoor.PerformHold))]
    internal static class Door_Hold
    {
        private static void Postfix(SteamTrainDoor __instance)
        {
            if (RailwayManager.IsOurTrain(__instance.m_ParentSteamTrain)) LocomotiveAnimator.SwingDoor();
        }
    }

    [HarmonyPatch(typeof(SteamTrainBoiler), nameof(SteamTrainBoiler.UpdateComponent))]
    internal static class Boiler_UpdateComponent
    {
        private static bool Prefix(SteamTrainBoiler __instance) => !RailwayManager.IsOurTrain(__instance.m_Train);
    }

    [HarmonyPatch(typeof(SteamTrainGearLever), nameof(SteamTrainGearLever.PerformInteraction))]
    internal static class GearLever_Perform
    {
        private static bool Prefix(SteamTrainGearLever __instance, ref bool __result)
        {
            if (!RailwayManager.IsOurTrain(__instance.m_Train)) return true;
            __result = CabControls.Handle(__instance.m_Train, __instance.gameObject, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(SteamTrainGearLever), nameof(SteamTrainGearLever.PerformAlternativeInteraction))]
    internal static class GearLever_PerformAlternative
    {
        private static bool Prefix(SteamTrainGearLever __instance, ref bool __result)
        {
            if (!RailwayManager.IsOurTrain(__instance.m_Train)) return true;
            __result = CabControls.Handle(__instance.m_Train, __instance.gameObject, true);
            return false;
        }
    }

    [HarmonyPatch(typeof(SteamTrainTankIntake), nameof(SteamTrainTankIntake.PerformInteraction))]
    internal static class TankIntake_Perform
    {
        private static bool Prefix(SteamTrainTankIntake __instance, ref bool __result)
        {
            if (!RailwayManager.IsOurTrain(__instance.m_Train)) return true;
            __result = CabControls.Handle(__instance.m_Train, __instance.gameObject, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(SteamTrainBrakeLever), nameof(SteamTrainBrakeLever.PerformInteraction))]
    internal static class BrakeLever_Perform
    {
        private static bool Prefix(SteamTrainBrakeLever __instance, ref bool __result)
        {
            if (!RailwayManager.IsOurTrain(__instance.m_Train)) return true;
            __result = CabControls.Handle(__instance.m_Train, __instance.gameObject, false);
            return false;
        }
    }
}

namespace SteamRailway.Patches
{
    /// The game's train update turns and locks the train's rotation from its own idea of the train's direction;
    /// our engines keep the facing they have (SteamSim.Facing), re-applied after every update.
    [HarmonyPatch(typeof(SteamTrain), nameof(SteamTrain.Update))]
    internal static class SteamTrain_Update_Facing
    {
        private static void Postfix(SteamTrain __instance)
        {
            var simulation = RailwayManager.GetSim(__instance);
            simulation?.EnforceFacing();
        }
    }
}
