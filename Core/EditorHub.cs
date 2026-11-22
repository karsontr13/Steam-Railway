using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    internal enum EditorMode { Off, Objects, Track, Terrain, Obstacles }

    /// The mod's in-game editors. F11 goes round: objects -> track -> terrain -> obstacles -> off.
    internal static class EditorHub
    {
        const int LargeFontSize = 18;
        const int SmallFontSize = 14;
        const float InfoTextRed = 0.85f;
        const float InfoTextGreen = 0.9f;
        const float WarningTextGreen = 0.75f;
        const float WarningTextBlue = 0.3f;
        const float CrosshairHalfWidth = 12f;
        const float CrosshairHalfHeight = 14f;
        const float CrosshairWidth = 24f;
        const float CrosshairHeight = 28f;
        const float HeadLineTop = 30f;
        const float HeadLineHeight = 28f;
        const float WarningLineTop = 58f;
        const float WarningLineHeight = 24f;
        const float KeyHelpBottomMargin = 60f;
        const float KeyHelpLineStep = 20f;
        const float KeyHelpLineHeight = 22f;
        const int DefaultRingSegmentCount = 40;
        const float DefaultRingLiftInMeters = 0.15f;
        const float DefaultAimReachInMeters = 400f;
        public static EditorMode Mode { get; private set; } = EditorMode.Off;

        static GUIStyle s_Big, s_Small, s_Warn;

        public static void Update()
        {
            if (Input.GetKeyDown(KeyCode.F11))
            {
                Leave(Mode);
                Mode = Mode switch
                {
                    EditorMode.Off => EditorMode.Objects,
                    EditorMode.Objects => EditorMode.Track,
                    EditorMode.Track => EditorMode.Terrain,
                    EditorMode.Terrain => EditorMode.Obstacles,
                    _ => EditorMode.Off,
                };
                CabControls.Message(Mode switch
                {
                    EditorMode.Objects => Localizer.Translate("Nesne düzenleyici (F11: ray düzenleyici)", "Object editor (F11: track editor)"),
                    EditorMode.Track => Localizer.Translate("Ray düzenleyici (F11: arazi düzenleyici)", "Track editor (F11: terrain editor)"),
                    EditorMode.Terrain => Localizer.Translate("Arazi düzenleyici (F11: engel düzenleyici)", "Terrain editor (F11: obstacle editor)"),
                    EditorMode.Obstacles => Localizer.Translate("Engel düzenleyici (F11: kapat)", "Obstacle editor (F11: off)"),
                    _ => Localizer.Translate("Düzenleyiciler kapalı.", "Editors off."),
                });
            }
            if (RailwayManager.CurrentScene == null) return;
            switch (Mode)
            {
                case EditorMode.Objects: ObjectEditor.Update(); break;
                case EditorMode.Track: TrackEditor.Update(); break;
                case EditorMode.Terrain: TerrainEditor.Update(); break;
                case EditorMode.Obstacles: ObstacleEditor.Update(); break;
            }
        }

        static void Leave(EditorMode mode)
        {
            try
            {
                if (mode == EditorMode.Objects) ObjectEditor.Leave();
                if (mode == EditorMode.Track) TrackEditor.Leave();
                if (mode == EditorMode.Terrain) TerrainEditor.Leave();
                if (mode == EditorMode.Obstacles) ObstacleEditor.Leave();
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Leaving the editor: {exception.Message}"); }
        }

        public static void OnGUI()
        {
            if (Mode == EditorMode.Off) return;
            switch (Mode)
            {
                case EditorMode.Objects: ObjectEditor.OnGUI(); break;
                case EditorMode.Track: TrackEditor.OnGUI(); break;
                case EditorMode.Terrain: TerrainEditor.OnGUI(); break;
                case EditorMode.Obstacles: ObstacleEditor.OnGUI(); break;
            }
        }

        // ------------------------------------------------------------------ shared drawing

        static void Styles()
        {
            if (s_Big != null) return;
            s_Big = new GUIStyle(GUI.skin.label) { fontSize = LargeFontSize, alignment = TextAnchor.UpperCenter };
            s_Big.normal.textColor = Color.white;
            s_Small = new GUIStyle(GUI.skin.label) { fontSize = SmallFontSize, alignment = TextAnchor.UpperCenter };
            s_Small.normal.textColor = new Color(InfoTextRed, InfoTextGreen, 1f);
            s_Warn = new GUIStyle(GUI.skin.label) { fontSize = SmallFontSize, alignment = TextAnchor.UpperCenter };
            s_Warn.normal.textColor = new Color(1f, WarningTextGreen, WarningTextBlue);
        }

        /// The editor's head line, an optional warning and the key help at the bottom.
        public static void Panel(string head, string infoText, string warn, string keys)
        {
            Styles();
            float weight = Screen.width;
            GUI.Label(new Rect(weight * 0.5f - CrosshairHalfWidth, Screen.height * 0.5f - CrosshairHalfHeight, CrosshairWidth, CrosshairHeight), "+", s_Big);
            GUI.Label(new Rect(0f, HeadLineTop, weight, HeadLineHeight), head + "  " + infoText, s_Big);
            if (!string.IsNullOrEmpty(warn)) GUI.Label(new Rect(0f, WarningLineTop, weight, WarningLineHeight), warn, s_Warn);
            if (!string.IsNullOrEmpty(keys))
            {
                var lines = keys.Split('\n');
                for (int index = 0; index < lines.Length; index++)
                    GUI.Label(new Rect(0f, Screen.height - KeyHelpBottomMargin - (lines.Length - 1 - index) * KeyHelpLineStep, weight, KeyHelpLineHeight), lines[index], s_Small);
            }
        }

        static Material s_LineMaterial;

        /// A plain coloured material for the preview lines (a built-in shader the game always has, else a rail material).
        static Material LineMaterial()
        {
            if (s_LineMaterial != null) return s_LineMaterial;
            Shader shader = null;
            foreach (var name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color", "Legacy Shaders/Particles/Alpha Blended Premultiply" })
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }
            if (shader != null)
            {
                s_LineMaterial = new Material(shader);
                try { s_LineMaterial.SetInt("_ZWrite", 0); } catch { }
            }
            else
            {
                foreach (var material in Resources.FindObjectsOfTypeAll<Material>()) if (material.name == "GLB_Green_A05") { s_LineMaterial = new Material(material); break; }
            }
            if (s_LineMaterial != null) s_LineMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s_LineMaterial;
        }

        /// A preview line in the world (one per use; colour per line).
        internal sealed class Line
        {
            GameObject m_GameObject;
            LineRenderer m_LineRenderer;
            Material m_Material;
            readonly string m_Name;
            readonly float m_Width;

            public Line(string name, float width) { m_Name = name; m_Width = width; }

            void Make()
            {
                if (m_GameObject != null) return;
                m_GameObject = new GameObject("SR_Editor_" + m_Name);
                Object.DontDestroyOnLoad(m_GameObject);
                m_LineRenderer = m_GameObject.AddComponent<LineRenderer>();
                m_LineRenderer.useWorldSpace = true;
                m_LineRenderer.widthMultiplier = m_Width;
                m_LineRenderer.numCapVertices = 2;
                m_LineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_LineRenderer.receiveShadows = false;
                var baseMaterial = LineMaterial();
                if (baseMaterial != null) { m_Material = new Material(baseMaterial); m_LineRenderer.sharedMaterial = m_Material; }
            }

            public void Set(IList<Vector3> points, Color color, bool loop = false)
            {
                if (points == null || points.Count < 2) { Hide(); return; }
                Make();
                m_GameObject.SetActive(true);
                m_LineRenderer.loop = loop;
                var pointArray = new Vector3[points.Count];
                for (int index = 0; index < pointArray.Length; index++) pointArray[index] = points[index];
                m_LineRenderer.positionCount = pointArray.Length;
                m_LineRenderer.SetPositions(new Il2CppStructArray<Vector3>(pointArray));
                m_LineRenderer.startColor = color; m_LineRenderer.endColor = color;
                if (m_Material != null) m_Material.color = color;
            }

            public void Hide()
            {
                if (m_GameObject != null) m_GameObject.SetActive(false);
            }
        }

        /// A ring on the ground (brush, snap marker).
        public static List<Vector3> Ring(Vector3 center, float radius, int count = DefaultRingSegmentCount, float lift = DefaultRingLiftInMeters)
        {
            var points = new List<Vector3>(count);
            for (int index = 0; index < count; index++)
            {
                float firstValue = index * Mathf.PI * 2f / count;
                var point = center + new Vector3(Mathf.Cos(firstValue) * radius, 0f, Mathf.Sin(firstValue) * radius);
                point.y = Il2CppHelpers.TerrainHeight(point, center.y) + lift;
                points.Add(point);
            }
            return points;
        }

        public static Transform Camera()
        {
            var firstPersonCamera = GameManager.GetVpFPSCamera();
            return firstPersonCamera != null ? firstPersonCamera.transform : null;
        }

        /// Where the crosshair meets the ground or an object (the player, our editor lines and trains ignored).
        public static bool Aim(out Vector3 point, float reach = DefaultAimReachInMeters)
        {
            point = Vector3.zero;
            var cameraTransform = Camera();
            if (cameraTransform == null) return false;
            var hits = Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool any = false;
            foreach (var raycastHit in hits)
            {
                if (raycastHit.distance >= best || IsOurs(raycastHit.collider.transform)) continue;
                best = raycastHit.distance; point = raycastHit.point; any = true;
            }
            return any;
        }

        static bool IsOurs(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent)
                if (node.name.StartsWith("CHARACTER_FPSPlayer") || node.name.StartsWith("SR_") || node.name.StartsWith(TrainBuilder.NamePrefix)) return true;
            return false;
        }

        public static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        public static bool Control => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
