using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// The fire hole in front of the game's hidden stove, so clicking the doors opens the normal fire menus.
    /// This class only adds the hover text and the firelight that falls into the cab.
    internal static class FireboxControl
    {
        const float MinutesPerHour = 60f;
        const float MinimumLightFireLevel = 0.15f;
        const float FlickerBase = 0.85f;
        const float FlickerAmplitude = 0.15f;
        const float FlickerSpeed = 6f;
        const float FlickerNoiseSeed = 0.3f;
        const float DoorOpenLightFactor = 3.4f;
        const float MinimumLightIntensity = 0.01f;
        const string HeatLightName = "FireHeat";

        static readonly System.Collections.Generic.Dictionary<SteamSimulation, Light> s_HeatLights = new System.Collections.Generic.Dictionary<SteamSimulation, Light>();

        public static string HoverText(SteamSimulation simulation)
        {
            var simulationState = simulation.State;
            if (simulationState.Exploded) return Localizer.Translate("Ocak: kazan patlamış", "Firebox: the boiler has burst");
            if (simulationState.PlugBlown && SteamSimulation.Mode == Realism.Medium) return Localizer.Translate("Ocak: eriyen tapa erimiş, ocak yanmaz", "Firebox: the fusible plug has melted, it won't burn");
            if (!simulation.FireBurning) return Localizer.Translate("Ocak: sönük", "Firebox: out");
            float hours = simulation.Fire.GetRemainingLifeTimeHours();
            int wholeHours = Mathf.FloorToInt(hours), minutes = Mathf.FloorToInt((hours - wholeHours) * MinutesPerHour);
            return Localizer.Translate($"Ocak: {wholeHours} sa {minutes} dk", $"Firebox: {wholeHours} h {minutes} min");
        }

        public static void UpdateEffects(SteamSimulation simulation)
        {
            if (simulation.Embers == null) simulation.Embers = FireEmbers.Create(simulation.Train.transform);
            simulation.Embers?.Update(simulation.FireBurning, simulation.State.FireLevel, Time.deltaTime);

            if (!s_HeatLights.TryGetValue(simulation, out var light) || light == null)
            {
                var heat = simulation.Train.transform.Find(HeatLightName);
                light = heat != null ? heat.GetComponent<Light>() : null;
                if (light == null) return;
                s_HeatLights[simulation] = light;
            }
            float level = simulation.FireBurning ? Mathf.Max(simulation.State.FireLevel, MinimumLightFireLevel) : 0f;
            float flicker = FlickerBase + FlickerAmplitude * Mathf.PerlinNoise(Time.time * FlickerSpeed, FlickerNoiseSeed);
            light.intensity = level * (simulation.FireDoorOpen ? DoorOpenLightFactor : 1.0f) * flicker;
            light.enabled = light.intensity > MinimumLightIntensity;
        }
    }
}
