using UnityEngine;

namespace SteamRailway
{
    /// The visible shaft of light in front of a lamp: an open cone along local +Z, bright at the lens and fading out
    /// along its length and towards its rim (vertex colours), drawn additively with the game's Particles/Additive shader.
    internal static class LampBeam
    {
        const float Length = 30f, LensRadius = 0.16f, EndRadius = 2.4f;
        const int Sides = 24, Rings = 8;
        static readonly Color Tint = new Color(0.075f, 0.068f, 0.055f, 1f);   // a faint haze lit in the snow-laden air

        static Mesh s_Mesh;
        static Shader s_Shader;

        static Mesh BuildMesh()
        {
            // two nested cones: a narrow bright core and a wider soft rim
            var verts = new System.Collections.Generic.List<Vector3>();
            var cols = new System.Collections.Generic.List<Color>();
            var tris = new System.Collections.Generic.List<int>();
            foreach (var (endR, bright) in new[] { (EndRadius * 0.45f, 1f), (EndRadius, 0.35f) })
            {
                int baseIdx = verts.Count;
                for (int r = 0; r <= Rings; r++)
                {
                    float t = r / (float)Rings;
                    float z = Length * t;
                    float rad = Mathf.Lerp(LensRadius, endR, t);
                    float fade = bright * Mathf.Pow(1f - t, 2.2f) * Mathf.Clamp01(t * 12f);   // not a solid disc at the lens
                    for (int s = 0; s < Sides; s++)
                    {
                        float a = Mathf.PI * 2f * s / Sides;
                        verts.Add(new Vector3(Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, z));
                        cols.Add(new Color(fade, fade, fade, fade));
                    }
                }
                for (int r = 0; r < Rings; r++)
                    for (int s = 0; s < Sides; s++)
                    {
                        int a0 = baseIdx + r * Sides + s, a1 = baseIdx + r * Sides + (s + 1) % Sides;
                        int b0 = a0 + Sides, b1 = a1 + Sides;
                        // both windings: the shaft is seen from inside and outside
                        tris.AddRange(new[] { a0, b0, a1, a1, b0, b1, a0, a1, b0, a1, b1, b0 });
                    }
            }
            var m = new Mesh { name = "SR_LampBeam" };
            m.vertices = verts.ToArray();
            m.colors = cols.ToArray();
            m.triangles = tris.ToArray();
            m.RecalculateBounds();
            m.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        /// Adds a beam under `lamp` (its +Z is the light's direction); null when no additive shader exists.
        public static MeshRenderer Create(Transform lamp)
        {
            if (s_Shader == null) s_Shader = Shader.Find("Particles/Additive");
            if (s_Shader == null) return null;
            if (s_Mesh == null) s_Mesh = BuildMesh();
            var go = new GameObject("Beam");
            go.transform.SetParent(lamp, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            go.AddComponent<MeshFilter>().sharedMesh = s_Mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mat = new Material(s_Shader) { name = "SR_LampBeam" };
            mat.mainTexture = Texture2D.whiteTexture;
            mat.SetColor("_TintColor", Color.black);
            mat.renderQueue = 3000;
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return r;
        }

        static float s_Dark, s_NextDark;

        /// 0 by day .. 1 at night: a lamp's shaft is only seen in the dark (dusk 17-20 h, dawn 5-8 h).
        public static float Darkness()
        {
            if (Time.time < s_NextDark) return s_Dark;
            s_NextDark = Time.time + 2f;
            try
            {
                var tod = Il2Cpp.GameManager.GetTimeOfDayComponent();
                if (tod == null) return s_Dark = 1f;
                float h = tod.GetNormalizedTime() * 24f;
                float dusk = Mathf.InverseLerp(17f, 20f, h), dawn = Mathf.InverseLerp(8f, 5f, h);
                s_Dark = Mathf.SmoothStep(0f, 1f, Mathf.Max(dusk, dawn));
            }
            catch { s_Dark = 1f; }
            return s_Dark;
        }

        /// k = 0 (off) .. 1 (full).
        public static void Set(MeshRenderer beam, float k)
        {
            if (beam == null) return;
            k *= Darkness();
            beam.enabled = k > 0.01f;
            if (beam.enabled) beam.sharedMaterial.SetColor("_TintColor", Tint * k);
        }
    }
}
