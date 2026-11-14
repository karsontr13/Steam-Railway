using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// The fire hole. Behind it sits the game's own stove (hidden), so clicking the fire doors opens the normal
    /// fire menus: start a fire, then add fuel (coal burns best). The doors swing open while a menu is open
    /// (WoodStove.m_Open). This class only adds the hover text and the firelight that falls into the cab.
    internal static class FireboxControl
    {
        public static string HoverText(SteamSim simulation)
        {
            var simState = simulation.S;
            if (simState.Exploded) return L.T("Ocak: kazan patlamış", "Firebox: the boiler has burst");
            if (simState.PlugBlown && SteamSim.Mode == Realism.Medium) return L.T("Ocak: eriyen tapa erimiş, ocak yanmaz", "Firebox: the fusible plug has melted, it won't burn");
            if (!simulation.FireBurning) return L.T("Ocak: sönük", "Firebox: out");
            float hours = simulation.Fire.GetRemainingLifeTimeHours();
            int height = Mathf.FloorToInt(hours), m = Mathf.FloorToInt((hours - height) * 60f);
            return L.T($"Ocak: {height} sa {m} dk", $"Firebox: {height} h {m} min");
        }

        public static void UpdateEffects(SteamSim simulation)
        {
            var heat = simulation.Train.transform.Find("FireHeat");
            if (heat == null) return;
            var light = heat.GetComponent<Light>();
            if (light == null) return;
            float level = simulation.FireBurning ? Mathf.Max(simulation.S.FireLevel, 0.15f) : 0f;
            float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 6f, 0.3f);
            light.intensity = level * (simulation.FireDoorOpen ? 2.2f : 0.5f) * flicker;
            light.enabled = light.intensity > 0.01f;
        }
    }
}
