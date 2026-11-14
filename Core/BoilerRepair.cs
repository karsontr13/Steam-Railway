using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// Boiler maintenance with base-game materials. Needs a tool box and a cold boiler (no pressure).
    /// One click fixes the most urgent problem: burst boiler > blown plug > worn valve > worn boiler.
    internal static class BoilerRepair
    {
        const string ScrapMetal = "GEAR_ScrapMetal";
        static readonly string[] Tools = { "GEAR_HighQualityTools", "GEAR_SimpleTools" };

        const int ExplosionScrap = 8;
        const int PlugScrap = 1;
        const int ValveScrap = 2;
        const int BoilerScrap = 3;
        const float BoilerRepairAmount = 30f;
        const float MaxRepairTemp = 80f;

        public static string Describe(SteamSim simulation)
        {
            var simState = simulation.S;
            if (simState.Exploded) return L.T($"Kazan bakımı: PATLAMIŞ — onarım {ExplosionScrap} hurda metal + alet kutusu", $"Boiler maintenance: BURST — repair needs {ExplosionScrap} scrap metal + a tool box");
            if (simState.PlugBlown) return L.T($"Kazan bakımı: eriyen tapa erimiş — {PlugScrap} hurda metal + alet kutusu", $"Boiler maintenance: fusible plug melted — {PlugScrap} scrap metal + a tool box");
            if (simState.ValveCondition < 99f) return L.T($"Kazan bakımı: emniyet supabı %{Mathf.RoundToInt(simState.ValveCondition)} — {ValveScrap} hurda metal + alet kutusu", $"Boiler maintenance: safety valve {Mathf.RoundToInt(simState.ValveCondition)}% — {ValveScrap} scrap metal + a tool box");
            if (simState.BoilerCondition < 99f) return L.T($"Kazan bakımı: kazan %{Mathf.RoundToInt(simState.BoilerCondition)} — {BoilerScrap} hurda metal + alet kutusu", $"Boiler maintenance: boiler {Mathf.RoundToInt(simState.BoilerCondition)}% — {BoilerScrap} scrap metal + a tool box");
            return L.T("Kazan bakımı: her şey yolunda", "Boiler maintenance: all good");
        }

        public static void TryRepair(SteamSim simulation)
        {
            var simState = simulation.S;
            int scrap;
            if (simState.Exploded) scrap = ExplosionScrap;
            else if (simState.PlugBlown) scrap = PlugScrap;
            else if (simState.ValveCondition < 99f) scrap = ValveScrap;
            else if (simState.BoilerCondition < 99f) scrap = BoilerScrap;
            else { CabControls.Message(L.T("Kazanın bakıma ihtiyacı yok.", "The boiler needs no maintenance.")); return; }

            if (simulation.Pressure > 1f || simState.BoilerTemp > MaxRepairTemp)
            {
                CabControls.Message(L.T("Kazan çok sıcak. Ateşi söndür ve soğumasını bekle.", "The boiler is too hot. Let the fire die and wait for it to cool."));
                return;
            }

            var inventory = GameManager.GetInventoryComponent();
            bool hasTools = false;
            foreach (var text in Tools) if (inventory.GetBestGearItemWithName(text) != null) { hasTools = true; break; }
            if (!hasTools) { CabControls.Message(L.T("Onarım için alet kutusu gerekiyor.", "You need a tool box to repair it.")); return; }

            int have = inventory.GetNumGearWithName(ScrapMetal);
            if (have < scrap) { CabControls.Message(L.T($"Onarım için {scrap} hurda metal gerekiyor (sende {have} var).", $"The repair needs {scrap} scrap metal (you have {have}).")); return; }

            inventory.RemoveGearFromInventory(ScrapMetal, scrap, false);
            TrainSounds.PlayFor(TrainSounds.Repair, TrainSounds.Cab(simulation.Train), 2.8f);

            if (simState.Exploded)
            {
                simState.Exploded = false;
                simState.PlugBlown = false;
                simState.BoilerCondition = 40f;
                simState.ValveCondition = 50f;
                CabControls.Message(L.T("Kazan kabaca onarıldı (%40). Tam güç için bakıma devam et.", "The boiler is patched up (40%). Keep maintaining it for full strength."));
            }
            else if (simState.PlugBlown)
            {
                simState.PlugBlown = false;
                CabControls.Message(L.T("Yeni eriyen tapa takıldı. Kazanı doldurmadan ateşi yakma!", "A new fusible plug is fitted. Don't light the fire before filling the boiler!"));
            }
            else if (simState.ValveCondition < 99f)
            {
                simState.ValveCondition = 100f;
                CabControls.Message(L.T("Emniyet supabı yenilendi.", "The safety valve has been renewed."));
            }
            else
            {
                simState.BoilerCondition = Mathf.Min(100f, simState.BoilerCondition + BoilerRepairAmount);
                CabControls.Message(L.T($"Kazan onarıldı (%{Mathf.RoundToInt(simState.BoilerCondition)}).", $"Boiler repaired ({Mathf.RoundToInt(simState.BoilerCondition)}%)."));
            }
        }
    }
}
