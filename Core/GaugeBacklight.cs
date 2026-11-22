using UnityEngine;

namespace SteamRailway
{
    /// A faint warm backlight behind the pressure and speed gauge dials: a soft additive disc in front of each
    /// dial face, strongest in the middle, brighter in the dark and nearly invisible by day.
    internal sealed class GaugeBacklight
    {
        const float DiscOffsetTowardsCab = -0.0145f;     // metres along z, between the dial face and the needle
        const float NeedlePivotToFaceOffset = 0.0055f;
        const float PressureDialRadius = 0.135f;
        const float SpeedDialRadius = 0.085f;
        const float WarmRed = 1f;
        const float WarmGreen = 0.66f;
        const float WarmBlue = 0.28f;
        const int AdditiveRenderQueue = 3000;
        const string TintColorPropertyName = "_TintColor";
        const int DiscSegments = 28;
        const int IndicesPerSegment = 6;
        const float DayBrightness = 0.0f;
        const float NightBrightness = 0.26f;   // additive tint (0.5 = neutral)
        static readonly Color WarmColor = new Color(WarmRed, WarmGreen, WarmBlue, 1f);

        static Shader s_Shader;
        static Mesh s_PressureMesh;
        static Mesh s_SpeedMesh;

        readonly Material m_Material;

        GaugeBacklight(Material material) { m_Material = material; }

        /// Creates the two discs from the needles' positions; null when the model or the additive shader is missing.
        public static GaugeBacklight Create(Transform trainRoot, Transform pressureNeedle, Transform speedNeedle)
        {
            if (pressureNeedle == null || speedNeedle == null) return null;
            if (s_Shader == null) s_Shader = Shader.Find("Particles/Additive");
            if (s_Shader == null) return null;
            var material = new Material(s_Shader) { name = "SR_GaugeBacklight" };
            material.mainTexture = Texture2D.whiteTexture;
            material.SetColor(TintColorPropertyName, Color.black);
            material.renderQueue = AdditiveRenderQueue;
            material.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            if (s_PressureMesh == null) s_PressureMesh = BuildDisc(PressureDialRadius, "SR_BacklightPressure");
            if (s_SpeedMesh == null) s_SpeedMesh = BuildDisc(SpeedDialRadius, "SR_BacklightSpeed");
            AddDisc(trainRoot, "Backlight_Pressure", pressureNeedle, s_PressureMesh, material);
            AddDisc(trainRoot, "Backlight_Speed", speedNeedle, s_SpeedMesh, material);
            return new GaugeBacklight(material);
        }

        static void AddDisc(Transform trainRoot, string name, Transform needle, Mesh mesh, Material material)
        {
            var targetObject = new GameObject(name);
            targetObject.transform.SetParent(trainRoot, false);
            targetObject.transform.localPosition = trainRoot.InverseTransformPoint(needle.position) + new Vector3(0f, 0f, DiscOffsetTowardsCab + NeedlePivotToFaceOffset);
            targetObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = targetObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        static Mesh BuildDisc(float radius, string meshName)
        {
            var vertices = new Vector3[DiscSegments + 1];
            var colors = new Color[DiscSegments + 1];
            var triangles = new int[DiscSegments * IndicesPerSegment];
            vertices[0] = Vector3.zero;
            colors[0] = Color.white;
            for (int segment = 0; segment < DiscSegments; segment++)
            {
                float angle = Mathf.PI * 2f * segment / DiscSegments;
                vertices[segment + 1] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
                colors[segment + 1] = Color.black;            // the rim fades to nothing so the light stays on the dial face
                int nextVertex = (segment + 1) % DiscSegments + 1;
                int firstIndex = segment * IndicesPerSegment;
                triangles[firstIndex] = 0; triangles[firstIndex + 1] = segment + 1; triangles[firstIndex + 2] = nextVertex;
                triangles[firstIndex + 3] = 0; triangles[firstIndex + 4] = nextVertex; triangles[firstIndex + 5] = segment + 1;
            }
            var mesh = new Mesh { name = meshName };
            mesh.vertices = vertices; mesh.colors = colors; mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        public void Update()
        {
            float brightness = Mathf.Lerp(DayBrightness, NightBrightness, LampBeam.Darkness());
            m_Material.SetColor(TintColorPropertyName, WarmColor * brightness);
        }
    }
}
