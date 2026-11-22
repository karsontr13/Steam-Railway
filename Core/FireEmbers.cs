using UnityEngine;

namespace SteamRailway
{
    /// The mod's own glowing coal bed and low flames inside the fire hole (the game's stove effect is hidden).
    /// Breathing red-orange ember lumps with short yellow tongues while the fire is fed, drawn additively.
    internal sealed class FireEmbers
    {
        const string ObjectName = "FireEmbers";
        const string ModelObjectName = "Model";
        const string FireSocketName = "Socket_FireFX";
        const string TintColorPropertyName = "_TintColor";
        const float FallbackSocketHeightInMeters = 1.86f;
        const float FallbackSocketDepthInMeters = -1.79f;
        static readonly Vector3 FallbackSocketPosition = new Vector3(0f, FallbackSocketHeightInMeters, FallbackSocketDepthInMeters);

        const int EmberGroupCount = 3;
        const int LumpsPerGroup = 6;
        const int FlameCount = 9;
        const int AdditiveRenderQueue = 3000;

        // Bed layout relative to the fire socket (train space, metres).
        const float BedHalfWidth = 0.14f;
        const float BedBackOffset = -0.08f;          // towards the fire door
        const float BedFrontOffset = 0.12f;          // towards the firebox back
        const float BedLiftAboveSocket = 0.035f;
        const float LumpLiftJitterMaximum = 0.025f;
        const float LumpRadiusMin = 0.028f, LumpRadiusMax = 0.05f;
        const float LumpFlatness = 0.55f;            // lumps are squashed: height / width
        const int LumpSegments = 6;
        const int LumpRingCount = 3;
        const float LumpHeatMinimum = 0.55f;
        const float LumpnessMinimum = 0.75f;
        const float LumpnessMaximum = 1.15f;
        const float VertexHeatMinimum = 0.6f;
        const float CrownHeatFactor = 0.7f;
        const float GreenBase = 0.28f;
        const float GreenHeatFactor = 0.3f;
        const float BlueFactor = 0.04f;
        const float CrownRedFactor = 0.55f;
        const float CrownGreenFactor = 0.12f;

        const float FlameBaseRadius = 0.03f;
        const float FlameHeightMax = 0.3f;
        const float FlameSwayDegrees = 11f;
        const float FlameNoiseSpeed = 2.4f;
        const float FlameVisibleLevel = 0.1f;
        const int FlameSides = 6;
        const int FlameRings = 3;
        const float FlameTaper = 0.85f;
        const float FlameFadePower = 1.4f;
        const float FlameGreenBase = 0.55f;
        const float FlameGreenFalloff = 0.25f;
        const float FlameBlueFactor = 0.08f;
        const float FlameBedWidthFactor = 0.8f;
        const float FlameBackFactor = 0.5f;
        const float FlameFrontFactor = 0.9f;
        const float FlameLiftInMeters = 0.01f;
        const float FlameStrengthOffset = 0.3f;
        const float FlameStrengthGain = 1.3f;
        const float FlameHeightBase = 0.35f;
        const float FlameHeightNoiseGain = 0.65f;
        const float FlameWidthBase = 0.8f;
        const float FlameWidthGain = 0.4f;
        const float FlameMinimumScaleHeight = 0.05f;
        const float FlameTintBoost = 1.4f;
        const float FlameSwayNoiseSpeedX = 0.7f;
        const float FlameSwayNoiseSpeedZ = 0.6f;
        const float FlameSwayNoiseSeedX = 11f;
        const float FlameSwayNoiseSeedZ = 23f;
        const float FlameNoiseIndexStep = 5.1f;
        const float FlameHeightNoiseSeedStep = 3.7f;

        const float EmberBreathSpeed = 0.55f;
        const float EmberBreathDepth = 0.35f;
        const float EmberBreathGroupSeedStep = 7.3f;
        const float BrightnessFollowSeconds = 1.6f;
        const float MinimumBurningBrightness = 0.75f;
        const float EmberVisibleBrightness = 0.01f;
        const float EmberBoost = 1.6f;                // overall brightness of the ember bed
        const float AdditiveNeutralTint = 0.5f;      // the additive shader doubles tint * vertex colour
        const int MeshRandomSeed = 41;

        static Shader s_Shader;
        static Mesh s_FlameMesh;

        readonly MeshRenderer[] m_EmberRenderers = new MeshRenderer[EmberGroupCount];
        readonly Material[] m_EmberMaterials = new Material[EmberGroupCount];
        readonly Transform[] m_Flames = new Transform[FlameCount];
        readonly MeshRenderer[] m_FlameRenderers = new MeshRenderer[FlameCount];
        readonly Material[] m_FlameMaterials = new Material[FlameCount];
        float m_Brightness;

        /// Builds the ember bed under the train root at the model's fire socket; null when the additive shader is missing.
        public static FireEmbers Create(Transform trainRoot)
        {
            if (s_Shader == null) s_Shader = Shader.Find("Particles/Additive");
            if (s_Shader == null) return null;

            var socketPosition = FallbackSocketPosition;
            var socket = LocomotiveModel.FindDeep(trainRoot.Find(ModelObjectName), FireSocketName);
            if (socket != null) socketPosition = trainRoot.InverseTransformPoint(socket.position);

            var holder = new GameObject(ObjectName);
            holder.transform.SetParent(trainRoot, false);
            holder.transform.localPosition = socketPosition;

            var embers = new FireEmbers();
            var random = new System.Random(MeshRandomSeed);
            for (int group = 0; group < EmberGroupCount; group++)
                embers.BuildEmberGroup(holder.transform, group, random);
            if (s_FlameMesh == null) s_FlameMesh = BuildFlameMesh();
            for (int flame = 0; flame < FlameCount; flame++)
                embers.BuildFlame(holder.transform, flame, random);
            return embers;
        }

        static float Range(System.Random random, float minimum, float maximum) => minimum + (float)random.NextDouble() * (maximum - minimum);

        Material MakeMaterial(string materialName)
        {
            var material = new Material(s_Shader) { name = materialName };
            material.mainTexture = Texture2D.whiteTexture;
            material.SetColor(TintColorPropertyName, Color.black);
            material.renderQueue = AdditiveRenderQueue;
            material.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return material;
        }

        void BuildEmberGroup(Transform holder, int group, System.Random random)
        {
            var vertices = new System.Collections.Generic.List<Vector3>();
            var colors = new System.Collections.Generic.List<Color>();
            var triangles = new System.Collections.Generic.List<int>();
            for (int lump = 0; lump < LumpsPerGroup; lump++)
            {
                var center = new Vector3(Range(random, -BedHalfWidth, BedHalfWidth), BedLiftAboveSocket + Range(random, 0f, LumpLiftJitterMaximum),
                                         Range(random, BedBackOffset, BedFrontOffset));
                float radius = Range(random, LumpRadiusMin, LumpRadiusMax);
                float heat = Range(random, LumpHeatMinimum, 1f);
                AddLump(vertices, colors, triangles, center, radius, heat, random);
            }
            var mesh = new Mesh { name = "SR_EmberGroup" + group };
            mesh.vertices = vertices.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            var targetObject = new GameObject("Embers" + group);
            targetObject.transform.SetParent(holder, false);
            targetObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = targetObject.AddComponent<MeshRenderer>();
            m_EmberMaterials[group] = MakeMaterial("SR_Embers" + group);
            meshRenderer.sharedMaterial = m_EmberMaterials[group];
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;
            m_EmberRenderers[group] = meshRenderer;
        }

        /// A squashed, lumpy dome (half sphere) with a hot orange rim fading to deep red at its crown seams.
        static void AddLump(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<Color> colors,
                            System.Collections.Generic.List<int> triangles, Vector3 center, float radius, float heat, System.Random random)
        {
            int firstIndex = vertices.Count;
            for (int ring = 0; ring < LumpRingCount; ring++)
            {
                float elevation = ring / (float)LumpRingCount * Mathf.PI * 0.5f;            // 0 = rim, 90 degrees = crown
                for (int segment = 0; segment < LumpSegments; segment++)
                {
                    float angle = Mathf.PI * 2f * segment / LumpSegments;
                    float lumpiness = Range(random, LumpnessMinimum, LumpnessMaximum);
                    float ringRadius = radius * Mathf.Cos(elevation) * lumpiness;
                    vertices.Add(center + new Vector3(Mathf.Cos(angle) * ringRadius, Mathf.Sin(elevation) * radius * LumpFlatness * lumpiness, Mathf.Sin(angle) * ringRadius));
                    float vertexHeat = heat * Range(random, VertexHeatMinimum, 1f) * Mathf.Lerp(1f, CrownHeatFactor, ring / (float)LumpRingCount);
                    colors.Add(new Color(vertexHeat, vertexHeat * (GreenBase + GreenHeatFactor * vertexHeat * vertexHeat), vertexHeat * BlueFactor, vertexHeat));
                }
            }
            int crownIndex = vertices.Count;
            vertices.Add(center + Vector3.up * radius * LumpFlatness);
            colors.Add(new Color(heat * CrownRedFactor, heat * CrownGreenFactor, 0f, heat * CrownRedFactor));
            for (int ring = 0; ring < LumpRingCount - 1; ring++)
                for (int segment = 0; segment < LumpSegments; segment++)
                {
                    int nextSegment = (segment + 1) % LumpSegments;
                    int lowerFirstIndex = firstIndex + ring * LumpSegments + segment, lowerSecondIndex = firstIndex + ring * LumpSegments + nextSegment;
                    int upperFirstIndex = lowerFirstIndex + LumpSegments, upperSecondIndex = lowerSecondIndex + LumpSegments;
                    triangles.AddRange(new[] { lowerFirstIndex, upperFirstIndex, lowerSecondIndex, lowerSecondIndex, upperFirstIndex, upperSecondIndex, lowerFirstIndex, lowerSecondIndex, upperFirstIndex, lowerSecondIndex, upperSecondIndex, upperFirstIndex });
                }
            int topRing = firstIndex + (LumpRingCount - 1) * LumpSegments;
            for (int segment = 0; segment < LumpSegments; segment++)
            {
                int nextSegment = (segment + 1) % LumpSegments;
                triangles.AddRange(new[] { topRing + segment, crownIndex, topRing + nextSegment, topRing + segment, topRing + nextSegment, crownIndex });
            }
        }

        /// A tapered tongue: bright yellow at the base, fading through orange to nothing at the tip.
        static Mesh BuildFlameMesh()
        {
            var vertices = new System.Collections.Generic.List<Vector3>();
            var colors = new System.Collections.Generic.List<Color>();
            var triangles = new System.Collections.Generic.List<int>();
            for (int ring = 0; ring < FlameRings; ring++)
            {
                float fraction = ring / (float)FlameRings;
                float radius = FlameBaseRadius * (1f - fraction * FlameTaper);
                float fade = Mathf.Pow(1f - fraction, FlameFadePower);
                for (int side = 0; side < FlameSides; side++)
                {
                    float angle = Mathf.PI * 2f * side / FlameSides;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radius, fraction * FlameHeightMax, Mathf.Sin(angle) * radius));
                    colors.Add(new Color(fade, fade * (FlameGreenBase - FlameGreenFalloff * fraction), fade * FlameBlueFactor * (1f - fraction), fade));
                }
            }
            int tipIndex = vertices.Count;
            vertices.Add(new Vector3(0f, FlameHeightMax, 0f));
            colors.Add(Color.black);
            for (int ring = 0; ring < FlameRings - 1; ring++)
                for (int side = 0; side < FlameSides; side++)
                {
                    int nextSide = (side + 1) % FlameSides;
                    int lowerFirstIndex = ring * FlameSides + side, lowerSecondIndex = ring * FlameSides + nextSide;
                    int upperFirstIndex = lowerFirstIndex + FlameSides, upperSecondIndex = lowerSecondIndex + FlameSides;
                    triangles.AddRange(new[] { lowerFirstIndex, upperFirstIndex, lowerSecondIndex, lowerSecondIndex, upperFirstIndex, upperSecondIndex, lowerFirstIndex, lowerSecondIndex, upperFirstIndex, lowerSecondIndex, upperSecondIndex, upperFirstIndex });
                }
            int topRing = (FlameRings - 1) * FlameSides;
            for (int side = 0; side < FlameSides; side++)
            {
                int nextSide = (side + 1) % FlameSides;
                triangles.AddRange(new[] { topRing + side, tipIndex, topRing + nextSide, topRing + side, topRing + nextSide, tipIndex });
            }
            var mesh = new Mesh { name = "SR_FlameTongue" };
            mesh.vertices = vertices.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        void BuildFlame(Transform holder, int flame, System.Random random)
        {
            var targetObject = new GameObject("Flame" + flame);
            targetObject.transform.SetParent(holder, false);
            targetObject.transform.localPosition = new Vector3(Range(random, -BedHalfWidth * FlameBedWidthFactor, BedHalfWidth * FlameBedWidthFactor), BedLiftAboveSocket + FlameLiftInMeters,
                                                              Range(random, BedBackOffset * FlameBackFactor, BedFrontOffset * FlameFrontFactor));
            targetObject.AddComponent<MeshFilter>().sharedMesh = s_FlameMesh;
            var meshRenderer = targetObject.AddComponent<MeshRenderer>();
            m_FlameMaterials[flame] = MakeMaterial("SR_Flame" + flame);
            meshRenderer.sharedMaterial = m_FlameMaterials[flame];
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;
            m_Flames[flame] = targetObject.transform;
            m_FlameRenderers[flame] = meshRenderer;
        }

        /// Called every frame. `fireLevel` is 0 .. 1 (how well the fire burns); `burning` is whether it is lit at all.
        public void Update(bool burning, float fireLevel, float deltaTime)
        {
            float target = burning ? Mathf.Lerp(MinimumBurningBrightness, 1f, Mathf.Clamp01(fireLevel)) : 0f;
            m_Brightness = Mathf.MoveTowards(m_Brightness, target, deltaTime / BrightnessFollowSeconds);
            float time = Time.time;

            for (int group = 0; group < EmberGroupCount; group++)
            {
                var meshRenderer = m_EmberRenderers[group];
                if (meshRenderer == null) continue;
                meshRenderer.enabled = m_Brightness > EmberVisibleBrightness;
                if (!meshRenderer.enabled) continue;
                float breath = 1f - EmberBreathDepth + EmberBreathDepth * 2f * Mathf.PerlinNoise(time * EmberBreathSpeed, group * EmberBreathGroupSeedStep);
                float tint = AdditiveNeutralTint * EmberBoost * m_Brightness * breath;
                m_EmberMaterials[group].SetColor(TintColorPropertyName, new Color(tint, tint, tint, tint));
            }

            float flameStrength = m_Brightness > FlameVisibleLevel ? Mathf.Clamp01((fireLevel + FlameStrengthOffset) * FlameStrengthGain) * m_Brightness : 0f;
            for (int flame = 0; flame < FlameCount; flame++)
            {
                var meshRenderer = m_FlameRenderers[flame];
                if (meshRenderer == null) continue;
                meshRenderer.enabled = flameStrength > FlameVisibleLevel;
                if (!meshRenderer.enabled) continue;
                float noiseTime = time * FlameNoiseSpeed + flame * FlameNoiseIndexStep;
                float height = flameStrength * (FlameHeightBase + FlameHeightNoiseGain * Mathf.PerlinNoise(noiseTime, flame * FlameHeightNoiseSeedStep));
                float sway = FlameSwayDegrees;
                m_Flames[flame].localRotation = Quaternion.Euler((Mathf.PerlinNoise(noiseTime * FlameSwayNoiseSpeedX, FlameSwayNoiseSeedX) - 0.5f) * 2f * sway, 0f,
                                                                 (Mathf.PerlinNoise(noiseTime * FlameSwayNoiseSpeedZ, FlameSwayNoiseSeedZ) - 0.5f) * 2f * sway);
                m_Flames[flame].localScale = new Vector3(FlameWidthBase + FlameWidthGain * height, Mathf.Max(FlameMinimumScaleHeight, height), FlameWidthBase + FlameWidthGain * height);
                float tint = AdditiveNeutralTint * FlameTintBoost * (0.5f + 0.5f * height);
                m_FlameMaterials[flame].SetColor(TintColorPropertyName, new Color(tint, tint, tint, tint));
            }
        }
    }
}
