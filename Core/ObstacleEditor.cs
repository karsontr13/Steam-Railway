using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// Marks the obstacles of a region for hard mode: a circle around a rockfall or the broken bridge.
    /// Keys: left click new, 1/2 kind, wheel radius, U always apply, K test clear, Del delete, P/O place at player/object.
    internal static class ObstacleEditor
    {
        const float WheelDeadZone = 0.01f;
        const float SmallRadiusStep = 2f;
        const float BigRadiusStep = 10f;
        const float ReachBeyondCircle = 30f;
        const int RingSegments = 64;
        static readonly Color PreviewColor = new Color(0.35f, 1f, 0.35f);
        static readonly Color BlockingColor = new Color(1f, 0.45f, 0.2f);
        static readonly Color ClearedColor = new Color(0.4f, 0.8f, 1f);
        static readonly Color AlwaysColor = new Color(1f, 0.9f, 0.2f);

        static readonly EditorHub.Line s_Preview = new EditorHub.Line("ObstacleNew", 0.15f);
        static readonly EditorHub.Line s_Selected = new EditorHub.Line("ObstacleSelected", 0.25f);

        static ObstacleKind s_Kind = ObstacleKind.Rockfall;
        static float s_NewRadius = Obstacles.NewRadius;
        static string s_InfoText = "";
        static string s_Warn = "";

        public static void Leave()
        {
            s_Preview.Hide();
            s_Selected.Hide();
        }

        public static void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) s_Kind = ObstacleKind.Rockfall;
            if (Input.GetKeyDown(KeyCode.Alpha2)) s_Kind = ObstacleKind.BrokenBridge;
            s_Warn = "";

            if (!EditorHub.Aim(out var hitPoint))
            {
                s_InfoText = Localizer.Translate("(zemin yok)", "(no ground)");
                Leave();
                return;
            }

            var selected = Obstacles.NearestTo(hitPoint, out float distance);
            bool inside = selected != null && distance <= selected.Radius;
            if (selected != null && !inside && distance > selected.Radius + ReachBeyondCircle) selected = null;

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > WheelDeadZone)
            {
                float step = (EditorHub.Shift ? BigRadiusStep : SmallRadiusStep) * Mathf.Sign(wheel);
                if (inside) { selected.Radius = Obstacles.ClampRadius(selected.Radius + step); Obstacles.Save(); }
                else s_NewRadius = Obstacles.ClampRadius(s_NewRadius + step);
            }

            PlaceElsewhere(selected, hitPoint);
            if (inside) Edit(selected);
            else
            {
                s_Preview.Set(EditorHub.Ring(hitPoint, s_NewRadius, RingSegments), PreviewColor);
                s_InfoText = NewObstacleInfo(s_Kind, s_NewRadius);
                if (Input.GetMouseButtonDown(0)) AddObstacle(hitPoint);
            }
            if (selected == null) s_Selected.Hide();
            else
            {
                var center = Obstacles.CenterOf(selected);
                s_Selected.Set(EditorHub.Ring(center, selected.Radius, RingSegments), ColorOf(selected));
            }
            if (inside) s_Preview.Hide();
        }

        static ObstacleKind s_CachedInfoKind;
        static float s_CachedInfoRadius = -1f;
        static string s_CachedInfoText = "";

        /// The info line for a new obstacle, rebuilt only when its kind or radius changes.
        static string NewObstacleInfo(ObstacleKind kind, float radius)
        {
            if (kind != s_CachedInfoKind || radius != s_CachedInfoRadius)
            {
                s_CachedInfoKind = kind;
                s_CachedInfoRadius = radius;
                s_CachedInfoText = Localizer.Translate($"yeni engel: {Name(kind)}   yarıçap {radius:F0} m   (sol tık yerleştirir)", $"new obstacle: {Name(kind)}   radius {radius:F0} m   (left click places it)");
            }
            return s_CachedInfoText;
        }

        static void AddObstacle(Vector3 point)
        {
            var created = Obstacles.Add(point, s_Kind, s_NewRadius);
            CabControls.Message(Localizer.Translate($"Engel eklendi: {created.Identifier}", $"Obstacle added: {created.Identifier}"));
        }

        const float ObjectPickReach = 250f;
        const float LargestPickableObject = 120f;

        /// P: where the player stands. O: the centre of the object under the crosshair, found by the outline of its
        /// parts (a trestle or a bridge may have no collision at all, so the crosshair falls through it to the ground).
        static void PlaceElsewhere(ObstacleData selected, Vector3 groundHit)
        {
            bool atPlayer = Input.GetKeyDown(KeyCode.P);
            bool atObject = Input.GetKeyDown(KeyCode.O);
            if (!atPlayer && !atObject) return;

            Vector3 point;
            if (atPlayer)
            {
                var player = GameManager.GetPlayerObject();
                if (player == null) return;
                point = player.transform.position;
            }
            else if (!PickByOutline(out point))
            {
                CabControls.Message(Localizer.Translate("Nişangâhta nesne yok.", "No object in the crosshair."));
                return;
            }

            if (EditorHub.Shift)
            {
                var nearest = Obstacles.NearestTo(point, out _);
                if (nearest == null) { CabControls.Message(Localizer.Translate("Taşınacak engel yok.", "No obstacle to move.")); return; }
                Obstacles.MoveTo(nearest, point);
                CabControls.Message(Localizer.Translate($"{nearest.Identifier} buraya taşındı.", $"{nearest.Identifier} moved here."));
            }
            else
            {
                AddObstacle(point);
            }
        }

        /// The nearest object whose outline the crosshair's ray crosses (big groups such as whole regions are left out).
        static bool PickByOutline(out Vector3 center)
        {
            center = Vector3.zero;
            var cameraTransform = EditorHub.Camera();
            if (cameraTransform == null) return false;
            var ray = new Ray(cameraTransform.position, cameraTransform.forward);
            float best = ObjectPickReach;
            bool found = false;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var bounds = renderer.bounds;
                if (bounds.size.x > LargestPickableObject || bounds.size.z > LargestPickableObject) continue;
                if (renderer.name.StartsWith("SR_") || renderer.transform.root.name.StartsWith("SteamRailway")) continue;
                if (!bounds.IntersectRay(ray, out float distance) || distance >= best) continue;
                best = distance;
                center = bounds.center;
                found = true;
            }
            return found;
        }

        static void Edit(ObstacleData obstacle)
        {
            bool cleared = RailwayManager.IsObstacleCleared(obstacle.Identifier);
            int objectCount = ObjectEdits.CountWithin(obstacle);
            int terrainCount = TerrainEdits.CountWithin(RailwayManager.CurrentScene, obstacle);
            string state = obstacle.AlwaysApplied ? Localizer.Translate("her zaman uygulanır", "always applied")
                         : cleared ? Localizer.Translate("temizlendi", "cleared") : Localizer.Translate("yolu kapatır", "blocks the line");
            s_InfoText = Localizer.Translate($"{obstacle.Identifier} ({Name(Obstacles.KindOf(obstacle))})   yarıçap {obstacle.Radius:F0} m   içinde {objectCount} nesne, {terrainCount} arazi darbesi   {state}",
                         $"{obstacle.Identifier} ({Name(Obstacles.KindOf(obstacle))})   radius {obstacle.Radius:F0} m   inside: {objectCount} objects, {terrainCount} terrain dabs   {state}");
            if (!Obstacles.HardMode) s_Warn = Localizer.Translate("Bu oyun Kolay modda: engeller yalnızca Zor modda işler.", "This game is in Easy mode: obstacles only work in Hard mode.");

            if (Input.GetKeyDown(KeyCode.U))
            {
                obstacle.AlwaysApplied = !obstacle.AlwaysApplied;
                Obstacles.Save();
                CabControls.Message(obstacle.AlwaysApplied ? Localizer.Translate("Bu bölgedeki değişiklikler her zaman uygulanır.", "The changes in this circle are always applied.")
                                                           : Localizer.Translate("Bu bölge artık engele bağlı.", "This circle now depends on the obstacle."));
            }
            if (Input.GetKeyDown(KeyCode.K))
            {
                if (cleared)
                {
                    Obstacles.Reinstate(obstacle);
                    CabControls.Message(Localizer.Translate("Engel, bölge yeniden kurulunca tekrar yolu kapatır.", "The obstacle blocks the line again when the region is rebuilt."));
                }
                else Obstacles.Clear(obstacle);
            }
            if (Input.GetKeyDown(KeyCode.Delete))
            {
                Obstacles.Remove(obstacle);
                CabControls.Message(Localizer.Translate("Engel silindi.", "Obstacle deleted."));
            }
        }

        static Color ColorOf(ObstacleData obstacle) =>
            obstacle.AlwaysApplied ? AlwaysColor : RailwayManager.IsObstacleCleared(obstacle.Identifier) ? ClearedColor : BlockingColor;

        static string Name(ObstacleKind kind) => kind switch
        {
            ObstacleKind.BrokenBridge => Localizer.Translate("yıkık köprü", "broken bridge"),
            _ => Localizer.Translate("kaya/toprak kayması", "rockfall"),
        };

        public static void OnGUI()
        {
            string keys = Localizer.Translate("Sol tık yeni engel   P oyuncunun olduğu yere   O nişandaki nesneye (Shift: en yakın engeli taşı)   1/2 tür   tekerlek yarıçap (Shift: büyük adım)   U her zaman uygula   K test: temizle / geri kapat   Del sil\nF11 sonraki mod",
                              "Left click new obstacle   1/2/3 kind   wheel radius (Shift: big steps)   U always apply   K test: clear / block again   Del delete\nF11 next mode");
            EditorHub.Panel(Localizer.Translate("ENGEL DÜZENLEYİCİ", "OBSTACLE EDITOR"), s_InfoText, s_Warn, keys);
        }
    }
}
