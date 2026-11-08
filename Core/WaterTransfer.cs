using Il2Cpp;
using Il2CppTLD.IntBackedUnit;
using UnityEngine;

namespace SteamRailway
{
    /// Pours the player's water supply into the tender: non-potable water first, then potable water.
    internal static class WaterTransfer
    {
        const long UnitsPerLitre = 1_000_000_000L;

        /// Pours up to `litres` (all of it if available). Returns the litres actually poured.
        public static float PourInventoryIntoTender(SteamSim sim, float litres)
        {
            var inv = GameManager.GetInventoryComponent();
            litres = Mathf.Min(litres, sim.TenderSpace);
            float poured = Take(inv.GetNonPotableWaterSupply(), sim, litres);
            if (poured < litres) poured += Take(inv.GetPotableWaterSupply(), sim, litres - poured);
            return poured;
        }

        static float Take(GearItem supplyItem, SteamSim sim, float want)
        {
            if (want <= 0f || supplyItem == null || supplyItem.m_WaterSupply == null) return 0f;
            var ws = supplyItem.m_WaterSupply;
            float have = ws.m_VolumeInLiters.m_Units / (float)UnitsPerLitre;
            float litres = Mathf.Min(have, want);
            if (litres <= 0f) return 0f;

            var left = new ItemLiquidVolume();
            left.m_Units = System.Math.Max(0L, ws.m_VolumeInLiters.m_Units - (long)System.Math.Round(litres * UnitsPerLitre));
            ws.m_VolumeInLiters = left;   // AddToWaterSupply rejects negative amounts
            sim.PourInWater(litres);
            return litres;
        }
    }
}
