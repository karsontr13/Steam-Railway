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
                    float fraction = r / (float)Rings;
                    float z = Length * fraction;
                    float radius = Mathf.Lerp(LensRadius, endR, fraction);
                    float fade = bright * Mathf.Pow(1f - fraction, 2.2f) * Mathf.Clamp01(fraction * 12f);   // not a solid disc at the lens
                    for (int s = 0; s < Sides; s++)
                    {
                        float firstValue = Mathf.PI * 2f * s / Sides;
                        verts.Add(new Vector3(Mathf.Cos(firstValue) * radius, Mathf.Sin(firstValue) * radius, z));
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
            var mesh = new Mesh { name = "SR_LampBeam" };
            mesh.vertices = verts.ToArray();
            mesh.colors = cols.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        /// Adds a beam under `lamp` (its +Z is the light's direction); null when no additive shader exists.
        public static MeshRenderer Create(Transform lamp)
        {
            if (s_Shader == null) s_Shader = Shader.Find("Particles/Additive");
            if (s_Shader == null) return null;
            if (s_Mesh == null) s_Mesh = BuildMesh();
            var targetObject = new GameObject("Beam");
            targetObject.transform.SetParent(lamp, false);
            targetObject.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            targetObject.AddComponent<MeshFilter>().sharedMesh = s_Mesh;
            var meshRenderer = targetObject.AddComponent<MeshRenderer>();
            var material = new Material(s_Shader) { name = "SR_LampBeam" };
            material.mainTexture = Texture2D.whiteTexture;
            material.SetColor("_TintColor", Color.black);
            material.renderQueue = 3000;
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;
            return meshRenderer;
        }

        static float s_Dark, s_NextDark;

        /// 0 by day .. 1 at night: a lamp's shaft is only seen in the dark (dusk 17-20 h, dawn 5-8 h).
        public static float Darkness()
        {
            if (Time.time < s_NextDark) return s_Dark;
            s_NextDark = Time.time + 2f;
            try
            {
                var timeOfDay = Il2Cpp.GameManager.GetTimeOfDayComponent();
                if (timeOfDay == null) return s_Dark = 1f;
                float height = timeOfDay.GetNormalizedTime() * 24f;
                float dusk = Mathf.InverseLerp(17f, 20f, height), dawn = Mathf.InverseLerp(8f, 5f, height);
                s_Dark = Mathf.SmoothStep(0f, 1f, Mathf.Max(dusk, dawn));
            }
            catch { s_Dark = 1f; }
            return s_Dark;
        }

        /// k = 0 (off) .. 1 (full).
        public static void Set(MeshRenderer beam, float scale)
        {
            if (beam == null) return;
            scale *= Darkness();
            beam.enabled = scale > 0.01f;
            if (beam.enabled) beam.sharedMaterial.SetColor("_TintColor", Tint * scale);
        }
    }
}
