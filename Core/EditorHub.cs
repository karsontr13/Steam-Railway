using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    internal enum EditorMode { Off, Objects, Track, Terrain }

    /// The mod's in-game editors. F11 goes round: objects -> track -> terrain -> off.
    internal static class EditorHub
    {
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
                    _ => EditorMode.Off,
                };
                CabControls.Message(Mode switch
                {
                    EditorMode.Objects => L.T("Nesne düzenleyici (F11: ray düzenleyici)", "Object editor (F11: track editor)"),
                    EditorMode.Track => L.T("Ray düzenleyici (F11: arazi düzenleyici)", "Track editor (F11: terrain editor)"),
                    EditorMode.Terrain => L.T("Arazi düzenleyici (F11: kapat)", "Terrain editor (F11: off)"),
                    _ => L.T("Düzenleyiciler kapalı.", "Editors off."),
                });
            }
            if (RailwayManager.CurrentScene == null) return;
            switch (Mode)
            {
                case EditorMode.Objects: ObjectEditor.Update(); break;
                case EditorMode.Track: TrackEditor.Update(); break;
                case EditorMode.Terrain: TerrainEditor.Update(); break;
            }
        }

        static void Leave(EditorMode mode)
        {
            try
            {
                if (mode == EditorMode.Objects) ObjectEditor.Leave();
                if (mode == EditorMode.Track) TrackEditor.Leave();
                if (mode == EditorMode.Terrain) TerrainEditor.Leave();
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Leaving the editor: {e.Message}"); }
        }

        public static void OnGUI()
        {
            if (Mode == EditorMode.Off) return;
            switch (Mode)
            {
                case EditorMode.Objects: ObjectEditor.OnGUI(); break;
                case EditorMode.Track: TrackEditor.OnGUI(); break;
                case EditorMode.Terrain: TerrainEditor.OnGUI(); break;
            }
        }

        // ------------------------------------------------------------------ shared drawing

        static void Styles()
        {
            if (s_Big != null) return;
            s_Big = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.UpperCenter };
            s_Big.normal.textColor = Color.white;
            s_Small = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.UpperCenter };
            s_Small.normal.textColor = new Color(0.85f, 0.9f, 1f);
            s_Warn = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.UpperCenter };
            s_Warn.normal.textColor = new Color(1f, 0.75f, 0.3f);
        }

        /// The editor's head line, an optional warning and the key help at the bottom.
        public static void Panel(string head, string info, string warn, string keys)
        {
            Styles();
            float weight = Screen.width;
            GUI.Label(new Rect(weight * 0.5f - 12f, Screen.height * 0.5f - 14f, 24f, 28f), "+", s_Big);
            GUI.Label(new Rect(0f, 30f, weight, 28f), head + "  " + info, s_Big);
            if (!string.IsNullOrEmpty(warn)) GUI.Label(new Rect(0f, 58f, weight, 24f), warn, s_Warn);
            if (!string.IsNullOrEmpty(keys))
            {
                var lines = keys.Split('\n');
                for (int index = 0; index < lines.Length; index++)
                    GUI.Label(new Rect(0f, Screen.height - 60f - (lines.Length - 1 - index) * 20f, weight, 22f), lines[index], s_Small);
            }
        }

        static Material s_LineMat;

        /// A plain coloured material for the preview lines (a built-in shader the game always has, else a rail material).
        static Material LineMaterial()
        {
            if (s_LineMat != null) return s_LineMat;
            Shader shader = null;
            foreach (var name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color", "Legacy Shaders/Particles/Alpha Blended Premultiply" })
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }
            if (shader != null)
            {
                s_LineMat = new Material(shader);
                try { s_LineMat.SetInt("_ZWrite", 0); } catch { }
            }
            else
            {
                foreach (var material in Resources.FindObjectsOfTypeAll<Material>()) if (material.name == "GLB_Green_A05") { s_LineMat = new Material(material); break; }
            }
            if (s_LineMat != null) s_LineMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s_LineMat;
        }

        /// A preview line in the world (one per use; colour per line).
        internal sealed class Line
        {
            GameObject m_Go;
            LineRenderer m_Lr;
            Material m_Mat;
            readonly string m_Name;
            readonly float m_Width;

            public Line(string name, float width) { m_Name = name; m_Width = width; }

            void Make()
            {
                if (m_Go != null) return;
                m_Go = new GameObject("SR_Editor_" + m_Name);
                Object.DontDestroyOnLoad(m_Go);
                m_Lr = m_Go.AddComponent<LineRenderer>();
                m_Lr.useWorldSpace = true;
                m_Lr.widthMultiplier = m_Width;
                m_Lr.numCapVertices = 2;
                m_Lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Lr.receiveShadows = false;
                var baseMat = LineMaterial();
                if (baseMat != null) { m_Mat = new Material(baseMat); m_Lr.sharedMaterial = m_Mat; }
            }

            public void Set(IList<Vector3> pts, Color c, bool loop = false)
            {
                if (pts == null || pts.Count < 2) { Hide(); return; }
                Make();
                m_Go.SetActive(true);
                m_Lr.loop = loop;
                var pointArray = new Vector3[pts.Count];
                for (int index = 0; index < pointArray.Length; index++) pointArray[index] = pts[index];
                m_Lr.positionCount = pointArray.Length;
                m_Lr.SetPositions(new Il2CppStructArray<Vector3>(pointArray));
                m_Lr.startColor = c; m_Lr.endColor = c;
                if (m_Mat != null) m_Mat.color = c;
            }

            public void Hide()
            {
                if (m_Go != null) m_Go.SetActive(false);
            }
        }

        /// A ring on the ground (brush, snap marker).
        public static List<Vector3> Ring(Vector3 center, float radius, int count = 40, float lift = 0.15f)
        {
            var points = new List<Vector3>(count);
            for (int index = 0; index < count; index++)
            {
                float firstValue = index * Mathf.PI * 2f / count;
                var point = center + new Vector3(Mathf.Cos(firstValue) * radius, 0f, Mathf.Sin(firstValue) * radius);
                point.y = H.TerrainHeight(point, center.y) + lift;
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
        public static bool Aim(out Vector3 point, float reach = 400f)
        {
            point = Vector3.zero;
            var cameraTransform = Camera();
            if (cameraTransform == null) return false;
            var hits = Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool any = false;
            foreach (var hit in hits)
            {
                if (hit.distance >= best || IsOurs(hit.collider.transform)) continue;
                best = hit.distance; point = hit.point; any = true;
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
        public static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
