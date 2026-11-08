using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// The fire hole. Behind it sits the game's own stove (hidden), so clicking the fire doors opens the normal
    /// fire menus: start a fire, then add fuel (coal burns best). The doors swing open while a menu is open
    /// (WoodStove.m_Open). This class only adds the hover text and the firelight that falls into the cab.
    internal static class FireboxControl
    {
        public static string HoverText(SteamSim sim)
        {
            var s = sim.S;
            if (s.Exploded) return L.T("Ocak: kazan patlamış", "Firebox: the boiler has burst");
            if (s.PlugBlown && SteamSim.Mode == Realism.Medium) return L.T("Ocak: eriyen tapa erimiş, ocak yanmaz", "Firebox: the fusible plug has melted, it won't burn");
            if (!sim.FireBurning) return L.T("Ocak: sönük", "Firebox: out");
            float hours = sim.Fire.GetRemainingLifeTimeHours();
            int h = Mathf.FloorToInt(hours), m = Mathf.FloorToInt((hours - h) * 60f);
            return L.T($"Ocak: {h} sa {m} dk", $"Firebox: {h} h {m} min");
        }

        public static void UpdateEffects(SteamSim sim)
        {
            var heat = sim.Train.transform.Find("FireHeat");
            if (heat == null) return;
            var light = heat.GetComponent<Light>();
            if (light == null) return;
            float level = sim.FireBurning ? Mathf.Max(sim.S.FireLevel, 0.15f) : 0f;
            float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 6f, 0.3f);
            light.intensity = level * (sim.FireDoorOpen ? 2.2f : 0.5f) * flicker;
            light.enabled = light.intensity > 0.01f;
        }
    }
}
