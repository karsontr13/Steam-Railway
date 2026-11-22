using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// In-game route authoring: F6 new route, F8 add a point at the player's feet, F7 remove the last point,
    /// F9 rebuild tracks and trains, F10 write routes.json.
    internal static class RouteEditor
    {
        static RouteData s_Active;

        public static void Update()
        {
            string scene = RailwayManager.CurrentScene;
            if (scene == null) return;

            if (Input.GetKeyDown(KeyCode.F6)) NewRoute(scene);
            if (Input.GetKeyDown(KeyCode.F8)) AddPoint(scene);
            if (Input.GetKeyDown(KeyCode.F7)) RemoveLastPoint();
            if (Input.GetKeyDown(KeyCode.F9))
            {
                RouteStore.Save(); // a rebuilt route is always kept, even if F10 is forgotten
                RailwayManager.Rebuild();
            }
            if (Input.GetKeyDown(KeyCode.F10)) RouteStore.Save();
        }

        static void NewRoute(string scene)
        {
            var routes = RouteStore.GetOrCreateRoutes(scene);
            s_Active = new RouteData { Identifier = $"{scene}_{routes.Count + 1}" };
            routes.Add(s_Active);
            SteamRailwayMod.Log.Msg($"New route '{s_Active.Identifier}'. Walk the line and press F8 to drop points.");
        }

        static void AddPoint(string scene)
        {
            if (s_Active == null)
            {
                var routes = RouteStore.GetRoutes(scene);
                if (routes != null && routes.Count > 0) s_Active = routes[routes.Count - 1];
                else NewRoute(scene);
            }
            var player = GameManager.GetPlayerObject();
            if (player == null) return;
            var point = TrackBuilder.SnapToTerrain(player.transform.position);
            s_Active.AddPoint(point);
            SteamRailwayMod.Log.Msg($"'{s_Active.Identifier}' point {s_Active.Points.Count}: {point}");
        }

        static void RemoveLastPoint()
        {
            if (s_Active == null || s_Active.Points.Count == 0) return;
            s_Active.Points.RemoveAt(s_Active.Points.Count - 1);
            SteamRailwayMod.Log.Msg($"'{s_Active.Identifier}' now has {s_Active.Points.Count} point(s).");
        }
    }
}
