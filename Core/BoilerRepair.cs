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

        public static string Describe(SteamSim sim)
        {
            var s = sim.S;
            if (s.Exploded) return L.T($"Kazan bakımı: PATLAMIŞ — onarım {ExplosionScrap} hurda metal + alet kutusu", $"Boiler maintenance: BURST — repair needs {ExplosionScrap} scrap metal + a tool box");
            if (s.PlugBlown) return L.T($"Kazan bakımı: eriyen tapa erimiş — {PlugScrap} hurda metal + alet kutusu", $"Boiler maintenance: fusible plug melted — {PlugScrap} scrap metal + a tool box");
            if (s.ValveCondition < 99f) return L.T($"Kazan bakımı: emniyet supabı %{Mathf.RoundToInt(s.ValveCondition)} — {ValveScrap} hurda metal + alet kutusu", $"Boiler maintenance: safety valve {Mathf.RoundToInt(s.ValveCondition)}% — {ValveScrap} scrap metal + a tool box");
            if (s.BoilerCondition < 99f) return L.T($"Kazan bakımı: kazan %{Mathf.RoundToInt(s.BoilerCondition)} — {BoilerScrap} hurda metal + alet kutusu", $"Boiler maintenance: boiler {Mathf.RoundToInt(s.BoilerCondition)}% — {BoilerScrap} scrap metal + a tool box");
            return L.T("Kazan bakımı: her şey yolunda", "Boiler maintenance: all good");
        }

        public static void TryRepair(SteamSim sim)
        {
            var s = sim.S;
            int scrap;
            if (s.Exploded) scrap = ExplosionScrap;
            else if (s.PlugBlown) scrap = PlugScrap;
            else if (s.ValveCondition < 99f) scrap = ValveScrap;
            else if (s.BoilerCondition < 99f) scrap = BoilerScrap;
            else { CabControls.Message(L.T("Kazanın bakıma ihtiyacı yok.", "The boiler needs no maintenance.")); return; }

            if (sim.Pressure > 1f || s.BoilerTemp > MaxRepairTemp)
            {
                CabControls.Message(L.T("Kazan çok sıcak. Ateşi söndür ve soğumasını bekle.", "The boiler is too hot. Let the fire die and wait for it to cool."));
                return;
            }

            var inv = GameManager.GetInventoryComponent();
            bool hasTools = false;
            foreach (var t in Tools) if (inv.GetBestGearItemWithName(t) != null) { hasTools = true; break; }
            if (!hasTools) { CabControls.Message(L.T("Onarım için alet kutusu gerekiyor.", "You need a tool box to repair it.")); return; }

            int have = inv.GetNumGearWithName(ScrapMetal);
            if (have < scrap) { CabControls.Message(L.T($"Onarım için {scrap} hurda metal gerekiyor (sende {have} var).", $"The repair needs {scrap} scrap metal (you have {have}).")); return; }

            inv.RemoveGearFromInventory(ScrapMetal, scrap, false);
            TrainSounds.PlayFor(TrainSounds.Repair, TrainSounds.Cab(sim.Train), 2.8f);

            if (s.Exploded)
            {
                s.Exploded = false;
                s.PlugBlown = false;
                s.BoilerCondition = 40f;
                s.ValveCondition = 50f;
                CabControls.Message(L.T("Kazan kabaca onarıldı (%40). Tam güç için bakıma devam et.", "The boiler is patched up (40%). Keep maintaining it for full strength."));
            }
            else if (s.PlugBlown)
            {
                s.PlugBlown = false;
                CabControls.Message(L.T("Yeni eriyen tapa takıldı. Kazanı doldurmadan ateşi yakma!", "A new fusible plug is fitted. Don't light the fire before filling the boiler!"));
            }
            else if (s.ValveCondition < 99f)
            {
                s.ValveCondition = 100f;
                CabControls.Message(L.T("Emniyet supabı yenilendi.", "The safety valve has been renewed."));
            }
            else
            {
                s.BoilerCondition = Mathf.Min(100f, s.BoilerCondition + BoilerRepairAmount);
                CabControls.Message(L.T($"Kazan onarıldı (%{Mathf.RoundToInt(s.BoilerCondition)}).", $"Boiler repaired ({Mathf.RoundToInt(s.BoilerCondition)}%)."));
            }
        }
    }
}
