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

        static void Leave(EditorMode m)
        {
            try
            {
                if (m == EditorMode.Objects) ObjectEditor.Leave();
                if (m == EditorMode.Track) TrackEditor.Leave();
                if (m == EditorMode.Terrain) TerrainEditor.Leave();
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
            float w = Screen.width;
            GUI.Label(new Rect(w * 0.5f - 12f, Screen.height * 0.5f - 14f, 24f, 28f), "+", s_Big);
            GUI.Label(new Rect(0f, 30f, w, 28f), head + "  " + info, s_Big);
            if (!string.IsNullOrEmpty(warn)) GUI.Label(new Rect(0f, 58f, w, 24f), warn, s_Warn);
            if (!string.IsNullOrEmpty(keys))
            {
                var lines = keys.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    GUI.Label(new Rect(0f, Screen.height - 60f - (lines.Length - 1 - i) * 20f, w, 22f), lines[i], s_Small);
            }
        }

        static Material s_LineMat;

        /// A plain coloured material for the preview lines (a built-in shader the game always has, else a rail material).
        static Material LineMaterial()
        {
            if (s_LineMat != null) return s_LineMat;
            Shader sh = null;
            foreach (var n in new[] { "Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color", "Legacy Shaders/Particles/Alpha Blended Premultiply" })
            {
                sh = Shader.Find(n);
                if (sh != null) break;
            }
            if (sh != null)
            {
                s_LineMat = new Material(sh);
                try { s_LineMat.SetInt("_ZWrite", 0); } catch { }
            }
            else
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m.name == "GLB_Green_A05") { s_LineMat = new Material(m); break; }
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
                var arr = new Vector3[pts.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = pts[i];
                m_Lr.positionCount = arr.Length;
                m_Lr.SetPositions(new Il2CppStructArray<Vector3>(arr));
                m_Lr.startColor = c; m_Lr.endColor = c;
                if (m_Mat != null) m_Mat.color = c;
            }

            public void Hide()
            {
                if (m_Go != null) m_Go.SetActive(false);
            }
        }

        /// A ring on the ground (brush, snap marker).
        public static List<Vector3> Ring(Vector3 c, float r, int n = 40, float lift = 0.15f)
        {
            var pts = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                var p = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                p.y = H.TerrainHeight(p, c.y) + lift;
                pts.Add(p);
            }
            return pts;
        }

        public static Transform Camera()
        {
            var cam = GameManager.GetVpFPSCamera();
            return cam != null ? cam.transform : null;
        }

        /// Where the crosshair meets the ground or an object (the player, our editor lines and trains ignored).
        public static bool Aim(out Vector3 point, float reach = 400f)
        {
            point = Vector3.zero;
            var cam = Camera();
            if (cam == null) return false;
            var hits = Physics.RaycastAll(cam.position, cam.forward, reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool any = false;
            foreach (var h in hits)
            {
                if (h.distance >= best || IsOurs(h.collider.transform)) continue;
                best = h.distance; point = h.point; any = true;
            }
            return any;
        }

        static bool IsOurs(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name.StartsWith("CHARACTER_FPSPlayer") || x.name.StartsWith("SR_") || x.name.StartsWith(TrainBuilder.NamePrefix)) return true;
            return false;
        }

        public static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        public static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
