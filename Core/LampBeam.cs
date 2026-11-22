using UnityEngine;

namespace SteamRailway
{
    /// The visible shaft of light in front of a lamp: an open cone along local +Z that fades out along its
    /// length and towards its rim, drawn additively.
    internal static class LampBeam
    {
        const float Length = 45f;
        const float LensRadius = 0.16f;
        const float EndRadius = 4.6f;
        const int Sides = 24;
        const int Rings = 8;
        const float CoreRadiusFactor = 0.45f;
        const float CoreBrightness = 1f;
        const float RimBrightness = 0.35f;
        const float FadePower = 2.2f;
        const float LensFadeInRings = 12f;
        const float BeamOffsetInMeters = 0.05f;
        const int AdditiveRenderQueue = 3000;
        const float DarknessRefreshSeconds = 2f;
        const float DuskStartHour = 17f;
        const float DuskEndHour = 20f;
        const float DawnStartHour = 8f;
        const float DawnEndHour = 5f;
        const float HoursPerDay = 24f;
        const float MinimumVisibleScale = 0.01f;
        const string TintColorPropertyName = "_TintColor";
        static readonly Color Tint = new Color(0.075f, 0.068f, 0.055f, 1f);   // a faint haze lit in the snowy air

        static Mesh s_Mesh;
        static Shader s_Shader;

        static Mesh BuildMesh()
        {
            // two nested cones: a narrow bright core and a wider soft rim
            var vertices = new System.Collections.Generic.List<Vector3>();
            var colors = new System.Collections.Generic.List<Color>();
            var triangles = new System.Collections.Generic.List<int>();
            foreach (var (endRadius, brightness) in new[] { (EndRadius * CoreRadiusFactor, CoreBrightness), (EndRadius, RimBrightness) })
            {
                int baseIndex = vertices.Count;
                for (int ring = 0; ring <= Rings; ring++)
                {
                    float fraction = ring / (float)Rings;
                    float depth = Length * fraction;
                    float radius = Mathf.Lerp(LensRadius, endRadius, fraction);
                    float fade = brightness * Mathf.Pow(1f - fraction, FadePower) * Mathf.Clamp01(fraction * LensFadeInRings);   // not a solid disc at the lens
                    for (int side = 0; side < Sides; side++)
                    {
                        float angle = Mathf.PI * 2f * side / Sides;
                        vertices.Add(new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, depth));
                        colors.Add(new Color(fade, fade, fade, fade));
                    }
                }
                for (int ring = 0; ring < Rings; ring++)
                    for (int side = 0; side < Sides; side++)
                    {
                        int lowerFirstIndex = baseIndex + ring * Sides + side, lowerSecondIndex = baseIndex + ring * Sides + (side + 1) % Sides;
                        int upperFirstIndex = lowerFirstIndex + Sides, upperSecondIndex = lowerSecondIndex + Sides;
                        // both windings: the shaft is seen from inside and outside
                        triangles.AddRange(new[] { lowerFirstIndex, upperFirstIndex, lowerSecondIndex, lowerSecondIndex, upperFirstIndex, upperSecondIndex, lowerFirstIndex, lowerSecondIndex, upperFirstIndex, lowerSecondIndex, upperSecondIndex, upperFirstIndex });
                    }
            }
            var mesh = new Mesh { name = "SR_LampBeam" };
            mesh.vertices = vertices.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = triangles.ToArray();
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
            targetObject.transform.localPosition = new Vector3(0f, 0f, BeamOffsetInMeters);
            targetObject.AddComponent<MeshFilter>().sharedMesh = s_Mesh;
            var meshRenderer = targetObject.AddComponent<MeshRenderer>();
            var material = new Material(s_Shader) { name = "SR_LampBeam" };
            material.mainTexture = Texture2D.whiteTexture;
            material.SetColor(TintColorPropertyName, Color.black);
            material.renderQueue = AdditiveRenderQueue;
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;
            return meshRenderer;
        }

        static float s_Dark, s_NextDark;

        /// 0 by day .. 1 at night: a lamp's shaft is only seen in the dark.
        public static float Darkness()
        {
            if (Time.time < s_NextDark) return s_Dark;
            s_NextDark = Time.time + DarknessRefreshSeconds;
            try
            {
                var timeOfDay = Il2Cpp.GameManager.GetTimeOfDayComponent();
                if (timeOfDay == null) return s_Dark = 1f;
                float height = timeOfDay.GetNormalizedTime() * HoursPerDay;
                float dusk = Mathf.InverseLerp(DuskStartHour, DuskEndHour, height), dawn = Mathf.InverseLerp(DawnStartHour, DawnEndHour, height);
                s_Dark = Mathf.SmoothStep(0f, 1f, Mathf.Max(dusk, dawn));
            }
            catch { s_Dark = 1f; }
            return s_Dark;
        }

        /// `scale` is 0 (off) .. 1 (full).
        public static void Set(MeshRenderer beam, float scale)
        {
            if (beam == null) return;
            scale *= Darkness();
            beam.enabled = scale > MinimumVisibleScale;
            if (beam.enabled) beam.sharedMaterial.SetColor(TintColorPropertyName, Tint * scale);
        }
    }
}
