using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.SteamTrain;

namespace SteamRailway.Patches
{
    /// The cab of our locomotive is sealed: while the player is aboard, chemical hazard zones and toxic fog
    /// do not poison them.
    internal static class CabAir
    {
        public static bool Sealed
        {
            get
            {
                var train = CabPlace_HideTrain.Current;
                return train != null && RailwayManager.IsOurTrain(train);
            }
        }
    }

    [HarmonyPatch(typeof(ChemicalPoisoning), nameof(ChemicalPoisoning.Update))]
    internal static class Chemical_Update
    {
        private static void Prefix(ChemicalPoisoning __instance)
        {
            if (CabAir.Sealed) __instance.m_InHazardZone = false;     // the state machine then lets the toxicity wear off
        }
    }

    [HarmonyPatch(typeof(ChemicalPoisoning), nameof(ChemicalPoisoning.IsHazardActive))]
    internal static class Chemical_IsHazardActive
    {
        private static bool Prefix(ref bool __result)
        {
            if (!CabAir.Sealed) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ChemicalPoisoning), nameof(ChemicalPoisoning.AddToxicity))]
    internal static class Chemical_AddToxicity
    {
        private static bool Prefix() => !CabAir.Sealed;
    }

    [HarmonyPatch(typeof(ToxicFogManager), nameof(ToxicFogManager.MaybeReducePlayerCondition))]
    internal static class ToxicFog_Condition
    {
        private static bool Prefix() => !CabAir.Sealed;
    }
}
