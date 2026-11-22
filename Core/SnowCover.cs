using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// Snow that settles on the locomotive: a top cover that grows with the snowfall and a front crust that grows
    /// while the engine runs into it, both 0..1. Snow melts above freezing; LocomotiveAnimator scales the layers.
    internal static class SnowCover
    {
        // Hours of continuous snowfall until a cover is full.
        const float TopLightSnowHours = 14f, TopHeavySnowHours = 6f, TopBlizzardHours = 2.5f;
        // Hours of running at the reference speed through snowfall until the front crust is full.
        const float FrontLightSnowHours = 6f, FrontHeavySnowHours = 3f, FrontBlizzardHours = 1.2f;
        public const float FrontReferenceSpeedMetersPerSecond = 8f;       // about 29 km/h
        // Hours for a full cover to melt at the reference temperature, and the temperature at which melting runs at that pace.
        const float MeltHoursAtReference = 6f, MeltReferenceCelsius = 4f;
        const float FrontBlowOffHours = 0.5f;                              // at the reference speed, without snowfall
        const float MinutesPerHour = 60f;

        /// Advances both covers by `gameMinutes` of game time.
        public static void Advance(SteamSimulationState state, float gameMinutes, float speedMetersPerSecond)
        {
            var weather = GameManager.GetWeatherComponent();
            float topHours, frontHours;
            switch (weather.GetWeatherStage())
            {
                case WeatherStage.LightSnow: topHours = TopLightSnowHours; frontHours = FrontLightSnowHours; break;
                case WeatherStage.HeavySnow: topHours = TopHeavySnowHours; frontHours = FrontHeavySnowHours; break;
                case WeatherStage.Blizzard: topHours = TopBlizzardHours; frontHours = FrontBlizzardHours; break;
                default: topHours = 0f; frontHours = 0f; break;
            }
            bool snowing = topHours > 0f;
            float runFactor = Mathf.Clamp01(Mathf.Abs(speedMetersPerSecond) / FrontReferenceSpeedMetersPerSecond);
            if (snowing)
            {
                state.SnowOnTop += gameMinutes / (topHours * MinutesPerHour);
                state.SnowOnFront += runFactor * gameMinutes / (frontHours * MinutesPerHour);
            }
            else
            {
                state.SnowOnFront -= runFactor * gameMinutes / (FrontBlowOffHours * MinutesPerHour);
            }
            float temperature = weather.GetCurrentTemperatureWithoutHeatSources();
            if (temperature > 0f)
            {
                float melt = gameMinutes * (temperature / MeltReferenceCelsius) / (MeltHoursAtReference * MinutesPerHour);
                state.SnowOnTop -= melt;
                state.SnowOnFront -= melt;
            }
            state.SnowOnTop = Mathf.Clamp01(state.SnowOnTop);
            state.SnowOnFront = Mathf.Clamp01(state.SnowOnFront);
        }
    }
}
