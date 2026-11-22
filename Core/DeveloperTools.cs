using UnityEngine;

namespace SteamRailway
{
    /// Keys for testing (the "DeveloperTools" preference turns them off).
    internal static class DeveloperTools
    {
        const KeyCode PlaceTrainKey = KeyCode.F4;

        public static void Update()
        {
            if (Input.GetKeyDown(PlaceTrainKey))
            {
                try { RailwayManager.DeveloperPlaceTrain(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Placing the train failed: {exception}"); }
            }
        }
    }
}
