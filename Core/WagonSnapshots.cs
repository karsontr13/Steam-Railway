using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// A wagon without an addressable prefab (like the crew speeder camp) is recorded part by part while its region is loaded
    /// (transforms, meshes, materials, colliders, doors) and built again from that record in other regions.
    internal static class WagonSnapshots
    {
        public const string KeyPrefix = "snapshot:";
        const int FormatVersion = 2;
        const int MaximumVertexCount = 400000;
        const int ShortIndexLimit = 65535;
        const int MatrixElementCount = 16;
        const string InstanceSuffix = " (Instance)";
        const string MaterialExtension = ".mat";
        const string OpenTweenName = "open";
        const string AmountKey = "amount";
        const string GuidNamespace = "SteamRailway wagon part ";

        static string FolderPath => Path.Combine(RouteStore.FolderPath, "Wagons");

        static string FilePath(string wagonName)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) wagonName = wagonName.Replace(invalid, '_');
            return Path.Combine(FolderPath, wagonName + ".bin");
        }

        public static bool IsSnapshotKey(string key) => key != null && key.StartsWith(KeyPrefix);

        static bool HasCurrentRecord(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path))) return reader.ReadInt32() == FormatVersion;
            }
            catch (IOException) { return false; }
        }

        // ------------------------------------------------------------------ writing

        /// The wagon's record key; the record is written the first time its region is loaded (and again for a new format).
        public static string Capture(Transform root, string wagonName)
        {
            string path = FilePath(wagonName);
            if (HasCurrentRecord(path)) return KeyPrefix + wagonName;
            try
            {
                Directory.CreateDirectory(FolderPath);
                using (var stream = File.Create(path))
                using (var writer = new BinaryWriter(stream)) new Recorder(root).Write(writer);
                SteamRailwayMod.Log.Msg($"Wagon {wagonName}: its parts were recorded to be built in other regions.");
                return KeyPrefix + wagonName;
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Warning($"Wagon {wagonName} could not be recorded: {exception.Message}");
                if (File.Exists(path)) File.Delete(path);
                return null;
            }
        }

        enum ColliderKind { Box, Sphere, Capsule, Mesh }

        sealed class Recorder
        {
            readonly Transform m_Root;
            readonly Matrix4x4 m_RootFrame;
            readonly List<Transform> m_Transforms = new List<Transform>();
            readonly Dictionary<Transform, int> m_TransformIndices = new Dictionary<Transform, int>();
            /// Local translations taken off a transform so an open door is recorded closed.
            readonly Dictionary<Transform, Vector3> m_ClosingOffsets = new Dictionary<Transform, Vector3>();
            readonly List<Mesh> m_Meshes = new List<Mesh>();
            readonly List<MeshRenderer> m_Renderers = new List<MeshRenderer>();
            readonly List<Collider> m_Colliders = new List<Collider>();
            readonly List<OpenClose> m_Doors = new List<OpenClose>();

            public Recorder(Transform root)
            {
                m_Root = root;
                m_RootFrame = Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse;
            }

            int IndexOf(Transform transform)
            {
                if (transform == m_Root) return -1;
                if (m_TransformIndices.TryGetValue(transform, out int index)) return index;
                IndexOf(transform.parent);
                index = m_Transforms.Count;
                m_Transforms.Add(transform);
                m_TransformIndices[transform] = index;
                return index;
            }

            int MeshIndexOf(Mesh mesh)
            {
                int index = m_Meshes.IndexOf(mesh);
                if (index >= 0) return index;
                m_Meshes.Add(mesh);
                return m_Meshes.Count - 1;
            }

            void Collect()
            {
                var skippedRenderers = new HashSet<Renderer>();
                foreach (var group in m_Root.GetComponentsInChildren<LODGroup>(false))
                {
                    var levels = group.GetLODs();
                    for (int level = 1; level < levels.Length; level++)
                        foreach (var renderer in levels[level].renderers) if (renderer != null) skippedRenderers.Add(renderer);
                }
                int vertexTotal = 0;
                foreach (var renderer in m_Root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (!renderer.enabled || skippedRenderers.Contains(renderer)) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable) continue;
                    vertexTotal += filter.sharedMesh.vertexCount;
                    if (vertexTotal > MaximumVertexCount) throw new IOException("the wagon has too many vertices");
                    m_Renderers.Add(renderer);
                    IndexOf(renderer.transform);
                    MeshIndexOf(filter.sharedMesh);
                }
                foreach (var collider in m_Root.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider.enabled) continue;
                    bool door = collider.GetComponent<OpenClose>() != null;
                    if (collider.isTrigger && !door) continue;
                    var meshCollider = collider.TryCast<MeshCollider>();
                    if (meshCollider != null)
                    {
                        if (meshCollider.sharedMesh == null || !meshCollider.sharedMesh.isReadable) continue;
                        MeshIndexOf(meshCollider.sharedMesh);
                    }
                    else if (collider.TryCast<BoxCollider>() == null && collider.TryCast<SphereCollider>() == null && collider.TryCast<CapsuleCollider>() == null) continue;
                    m_Colliders.Add(collider);
                    IndexOf(collider.transform);
                }
                foreach (var door in m_Root.GetComponentsInChildren<OpenClose>(false))
                {
                    var animation = door.GetComponent<ObjectAnim>();
                    if (animation == null || animation.m_Target == null || animation.m_Events == null) continue;
                    m_Doors.Add(door);
                    IndexOf(door.transform);
                    IndexOf(animation.m_Target.transform);
                    foreach (var tweenEvent in animation.m_Events)
                    {
                        if (tweenEvent == null) continue;
                        IndexOf(tweenEvent.transform);
                        if (door.m_IsOpen && tweenEvent.tweenName == OpenTweenName) NoteClosingOffset(tweenEvent);
                    }
                }
            }

            /// An open door's slide is taken back (a MoveBy in the part's own axes), so the copy starts closed.
            void NoteClosingOffset(iTweenEvent tweenEvent)
            {
                if (tweenEvent.type != iTweenEvent.TweenType.MoveBy || tweenEvent.keys == null) return;
                for (int index = 0; index < tweenEvent.keys.Length; index++)
                {
                    if (tweenEvent.keys[index] != AmountKey || tweenEvent.vector3s == null || tweenEvent.indexes[index] >= tweenEvent.vector3s.Length) continue;
                    var moved = tweenEvent.transform;
                    var worldShift = moved.TransformDirection(tweenEvent.vector3s[tweenEvent.indexes[index]]);
                    var localShift = moved.parent == m_Root ? Quaternion.Inverse(m_Root.rotation) * worldShift : moved.parent.InverseTransformVector(worldShift);
                    m_ClosingOffsets[moved] = localShift;
                }
            }

            public void Write(BinaryWriter writer)
            {
                Collect();
                writer.Write(FormatVersion);
                writer.Write(m_Meshes.Count);
                foreach (var mesh in m_Meshes) WriteMesh(writer, mesh);

                writer.Write(m_Transforms.Count);
                foreach (var transform in m_Transforms)
                {
                    writer.Write(transform.name);
                    writer.Write(transform.gameObject.layer);
                    int parentIndex = IndexOf(transform.parent);
                    writer.Write(parentIndex);
                    var local = parentIndex < 0 ? m_RootFrame * transform.localToWorldMatrix : Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale);
                    if (m_ClosingOffsets.TryGetValue(transform, out var offset)) local.SetColumn(3, local.GetColumn(3) - (Vector4)offset);
                    WriteMatrix(writer, local);
                }

                writer.Write(m_Renderers.Count);
                foreach (var renderer in m_Renderers)
                {
                    writer.Write(IndexOf(renderer.transform));
                    writer.Write(m_Meshes.IndexOf(renderer.GetComponent<MeshFilter>().sharedMesh));
                    var materials = renderer.sharedMaterials;
                    writer.Write(materials.Length);
                    foreach (var material in materials) writer.Write(material != null ? material.name.Replace(InstanceSuffix, "") : "");
                }

                writer.Write(m_Colliders.Count);
                foreach (var collider in m_Colliders)
                {
                    writer.Write(IndexOf(collider.transform));
                    writer.Write(collider.isTrigger);
                    WriteCollider(writer, collider);
                }

                writer.Write(m_Doors.Count);
                foreach (var door in m_Doors) WriteDoor(writer, door);
            }

            void WriteCollider(BinaryWriter writer, Collider collider)
            {
                // Il2Cpp objects are told apart with TryCast: C# type tests see only the declared Collider
                var box = collider.TryCast<BoxCollider>();
                var sphere = collider.TryCast<SphereCollider>();
                var capsule = collider.TryCast<CapsuleCollider>();
                if (box != null) { writer.Write((int)ColliderKind.Box); WriteVector(writer, box.center); WriteVector(writer, box.size); }
                else if (sphere != null) { writer.Write((int)ColliderKind.Sphere); WriteVector(writer, sphere.center); writer.Write(sphere.radius); }
                else if (capsule != null)
                {
                    writer.Write((int)ColliderKind.Capsule); WriteVector(writer, capsule.center);
                    writer.Write(capsule.radius); writer.Write(capsule.height); writer.Write(capsule.direction);
                }
                else
                {
                    var meshCollider = collider.TryCast<MeshCollider>();
                    writer.Write((int)ColliderKind.Mesh); writer.Write(m_Meshes.IndexOf(meshCollider.sharedMesh)); writer.Write(meshCollider.convex);
                }
            }

            void WriteDoor(BinaryWriter writer, OpenClose door)
            {
                var animation = door.GetComponent<ObjectAnim>();
                writer.Write(IndexOf(door.transform));
                writer.Write(IndexOf(animation.m_Target.transform));
                writer.Write(door.m_LocalizedDisplayName != null ? door.m_LocalizedDisplayName.m_LocalizationID ?? "" : "");
                writer.Write(AudioName(door.m_OpenAudioEvent, door.m_OpenAudio));
                writer.Write(AudioName(door.m_CloseAudioEvent, door.m_CloseAudio));
                var events = new List<iTweenEvent>();
                foreach (var tweenEvent in animation.m_Events) if (tweenEvent != null) events.Add(tweenEvent);
                writer.Write(events.Count);
                foreach (var tweenEvent in events)
                {
                    writer.Write(IndexOf(tweenEvent.transform));
                    writer.Write(tweenEvent.tweenName ?? "");
                    writer.Write((int)tweenEvent.type);
                    writer.Write(tweenEvent.delay);
                    WriteStrings(writer, tweenEvent.keys);
                    WriteInts(writer, tweenEvent.indexes);
                    WriteStrings(writer, tweenEvent.metadatas);
                    WriteInts(writer, tweenEvent.ints);
                    WriteFloats(writer, tweenEvent.floats);
                    WriteBools(writer, tweenEvent.bools);
                    WriteStrings(writer, tweenEvent.strings);
                    var vectors = tweenEvent.vector3s;
                    writer.Write(vectors != null ? vectors.Length : 0);
                    if (vectors != null) foreach (var vector in vectors) WriteVector(writer, vector);
                    var spaces = tweenEvent.spaces;
                    writer.Write(spaces != null ? spaces.Length : 0);
                    if (spaces != null) foreach (var space in spaces) writer.Write((int)space);
                    var easeTypes = tweenEvent.easeTypes;
                    writer.Write(easeTypes != null ? easeTypes.Length : 0);
                    if (easeTypes != null) foreach (var easeType in easeTypes) writer.Write((int)easeType);
                    var loopTypes = tweenEvent.loopTypes;
                    writer.Write(loopTypes != null ? loopTypes.Length : 0);
                    if (loopTypes != null) foreach (var loopType in loopTypes) writer.Write((int)loopType);
                }
            }
        }

        static string AudioName(Il2CppAK.Wwise.Event audioEvent, string fallback)
        {
            try { if (audioEvent != null && audioEvent.IsValid()) return audioEvent.Name; }
            catch (System.Exception) { }
            return fallback ?? "";
        }

        static void WriteMesh(BinaryWriter writer, Mesh mesh)
        {
            writer.Write(mesh.name);
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var colors = mesh.colors32;
            writer.Write(vertices.Length);
            foreach (var vertex in vertices) WriteVector(writer, vertex);
            writer.Write(normals.Length);
            foreach (var normal in normals) WriteVector(writer, normal);
            writer.Write(tangents.Length);
            foreach (var tangent in tangents) { writer.Write(tangent.x); writer.Write(tangent.y); writer.Write(tangent.z); writer.Write(tangent.w); }
            writer.Write(uvs.Length);
            foreach (var coordinate in uvs) { writer.Write(coordinate.x); writer.Write(coordinate.y); }
            writer.Write(colors.Length);
            foreach (var color in colors) { writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a); }
            writer.Write(mesh.subMeshCount);
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                var triangles = mesh.GetTriangles(subMesh);
                writer.Write(triangles.Length);
                foreach (int vertexIndex in triangles) writer.Write(vertexIndex);
            }
        }

        static void WriteVector(BinaryWriter writer, Vector3 vector) { writer.Write(vector.x); writer.Write(vector.y); writer.Write(vector.z); }

        static void WriteMatrix(BinaryWriter writer, Matrix4x4 matrix)
        {
            for (int index = 0; index < MatrixElementCount; index++) writer.Write(matrix[index]);
        }

        static void WriteStrings(BinaryWriter writer, Il2CppStringArray values)
        {
            writer.Write(values != null ? values.Length : 0);
            if (values != null) foreach (var value in values) writer.Write(value ?? "");
        }

        static void WriteInts(BinaryWriter writer, Il2CppStructArray<int> values)
        {
            writer.Write(values != null ? values.Length : 0);
            if (values != null) foreach (var value in values) writer.Write(value);
        }

        static void WriteFloats(BinaryWriter writer, Il2CppStructArray<float> values)
        {
            writer.Write(values != null ? values.Length : 0);
            if (values != null) foreach (var value in values) writer.Write(value);
        }

        static void WriteBools(BinaryWriter writer, Il2CppStructArray<bool> values)
        {
            writer.Write(values != null ? values.Length : 0);
            if (values != null) foreach (var value in values) writer.Write(value);
        }

        // ------------------------------------------------------------------ building

        static readonly Dictionary<string, GameObject> s_Templates = new Dictionary<string, GameObject>();

        /// An inactive template of the recorded wagon that lives across regions (copied like a prefab), or null without a record.
        public static GameObject Template(string key)
        {
            if (s_Templates.TryGetValue(key, out var cached) && cached != null) return cached;
            string wagonName = key.Substring(KeyPrefix.Length);
            string path = FilePath(wagonName);
            if (!HasCurrentRecord(path)) { SteamRailwayMod.Debug($"Wagon {wagonName}: no record of its parts."); return null; }
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    var template = Build(reader, wagonName);
                    s_Templates[key] = template;
                    return template;
                }
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Warning($"Wagon {wagonName} could not be built from its record: {exception.Message}");
                return null;
            }
        }

        /// A copy just made from a template: its doors get ids of their own (from the wagon's key), so the region's save keeps
        /// whether they stand open.
        public static void PrepareCopy(GameObject copy, string wagonKey)
        {
            foreach (var door in copy.GetComponentsInChildren<OpenClose>(true))
                ObjectGuid.MaybeAttachObjectGuidAddOrReplace(door.gameObject, StableGuid(wagonKey + "/" + door.name));
        }

        static string StableGuid(string text)
        {
            using (var hash = System.Security.Cryptography.MD5.Create())
                return new System.Guid(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(GuidNamespace + text))).ToString();
        }

        static GameObject Build(BinaryReader reader, string wagonName)
        {
            reader.ReadInt32();
            var meshes = new Mesh[reader.ReadInt32()];
            for (int index = 0; index < meshes.Length; index++) meshes[index] = ReadMesh(reader);

            var root = new GameObject(wagonName);
            root.SetActive(false);
            Object.DontDestroyOnLoad(root);
            var transforms = new Transform[reader.ReadInt32()];
            for (int index = 0; index < transforms.Length; index++)
            {
                var part = new GameObject(reader.ReadString());
                part.layer = reader.ReadInt32();
                int parentIndex = reader.ReadInt32();
                part.transform.SetParent(parentIndex >= 0 ? transforms[parentIndex] : root.transform, false);
                ApplyMatrix(part.transform, ReadMatrix(reader));
                transforms[index] = part.transform;
            }
            int rendererCount = reader.ReadInt32();
            for (int index = 0; index < rendererCount; index++)
            {
                var part = PartAt(transforms, root, reader.ReadInt32());
                part.AddComponent<MeshFilter>().sharedMesh = meshes[reader.ReadInt32()];
                var materials = new Material[reader.ReadInt32()];
                for (int slot = 0; slot < materials.Length; slot++) materials[slot] = FindMaterial(reader.ReadString());
                part.AddComponent<MeshRenderer>().sharedMaterials = materials;
            }
            int colliderCount = reader.ReadInt32();
            for (int index = 0; index < colliderCount; index++)
            {
                var part = PartAt(transforms, root, reader.ReadInt32());
                bool trigger = reader.ReadBoolean();
                ReadCollider(reader, part, meshes).isTrigger = trigger;
            }
            int doorCount = reader.ReadInt32();
            for (int index = 0; index < doorCount; index++) ReadDoor(reader, transforms, root);
            return root;
        }

        /// A recorded part by its index; -1 is the wagon's root itself.
        static GameObject PartAt(Transform[] transforms, GameObject root, int index) => index >= 0 ? transforms[index].gameObject : root;

        static Collider ReadCollider(BinaryReader reader, GameObject part, Mesh[] meshes)
        {
            switch ((ColliderKind)reader.ReadInt32())
            {
                case ColliderKind.Box:
                    var box = part.AddComponent<BoxCollider>();
                    box.center = ReadVector(reader); box.size = ReadVector(reader);
                    return box;
                case ColliderKind.Sphere:
                    var sphere = part.AddComponent<SphereCollider>();
                    sphere.center = ReadVector(reader); sphere.radius = reader.ReadSingle();
                    return sphere;
                case ColliderKind.Capsule:
                    var capsule = part.AddComponent<CapsuleCollider>();
                    capsule.center = ReadVector(reader); capsule.radius = reader.ReadSingle(); capsule.height = reader.ReadSingle(); capsule.direction = reader.ReadInt32();
                    return capsule;
                default:
                    int meshIndex = reader.ReadInt32();
                    bool convex = reader.ReadBoolean();
                    var meshCollider = part.AddComponent<MeshCollider>();
                    meshCollider.convex = convex;
                    meshCollider.sharedMesh = meshes[meshIndex];
                    return meshCollider;
            }
        }

        /// The game's own door: OpenClose plays the ObjectAnim's tweens on the moving part.
        static void ReadDoor(BinaryReader reader, Transform[] transforms, GameObject root)
        {
            var host = PartAt(transforms, root, reader.ReadInt32());
            var target = PartAt(transforms, root, reader.ReadInt32());
            string displayName = reader.ReadString();
            string openAudio = reader.ReadString();
            string closeAudio = reader.ReadString();
            var events = new iTweenEvent[reader.ReadInt32()];
            for (int index = 0; index < events.Length; index++)
            {
                var tweenEvent = PartAt(transforms, root, reader.ReadInt32()).AddComponent<iTweenEvent>();
                tweenEvent.tweenName = reader.ReadString();
                tweenEvent.type = (iTweenEvent.TweenType)reader.ReadInt32();
                tweenEvent.delay = reader.ReadSingle();
                tweenEvent.playAutomatically = false;
                tweenEvent.keys = ReadStrings(reader);
                tweenEvent.indexes = ReadInts(reader);
                tweenEvent.metadatas = ReadStrings(reader);
                tweenEvent.ints = ReadInts(reader);
                tweenEvent.floats = ReadFloats(reader);
                tweenEvent.bools = ReadBools(reader);
                tweenEvent.strings = ReadStrings(reader);
                var vectors = new Vector3[reader.ReadInt32()];
                for (int slot = 0; slot < vectors.Length; slot++) vectors[slot] = ReadVector(reader);
                tweenEvent.vector3s = vectors;
                var spaces = new Space[reader.ReadInt32()];
                for (int slot = 0; slot < spaces.Length; slot++) spaces[slot] = (Space)reader.ReadInt32();
                tweenEvent.spaces = spaces;
                var easeTypes = new iTween.EaseType[reader.ReadInt32()];
                for (int slot = 0; slot < easeTypes.Length; slot++) easeTypes[slot] = (iTween.EaseType)reader.ReadInt32();
                tweenEvent.easeTypes = easeTypes;
                var loopTypes = new iTween.LoopType[reader.ReadInt32()];
                for (int slot = 0; slot < loopTypes.Length; slot++) loopTypes[slot] = (iTween.LoopType)reader.ReadInt32();
                tweenEvent.loopTypes = loopTypes;
                tweenEvent.colors = new Il2CppStructArray<Color>(0);
                tweenEvent.gameObjects = new Il2CppReferenceArray<GameObject>(0);
                tweenEvent.transforms = new Il2CppReferenceArray<Transform>(0);
                tweenEvent.audioClips = new Il2CppReferenceArray<AudioClip>(0);
                tweenEvent.audioSources = new Il2CppReferenceArray<AudioSource>(0);
                tweenEvent.vector3Arrays = new Il2CppReferenceArray<ArrayIndexes>(0);
                tweenEvent.transformArrays = new Il2CppReferenceArray<ArrayIndexes>(0);
                tweenEvent.paths = new Il2CppReferenceArray<iTweenPath>(0);
                events[index] = tweenEvent;
            }
            var animation = host.AddComponent<ObjectAnim>();
            animation.m_Target = target;
            animation.m_Events = events;
            var door = host.AddComponent<OpenClose>();
            door.m_ObjectAnim = animation;
            door.m_LocalizedDisplayName = Il2CppHelpers.Loc(displayName);
            door.m_OpenAudioEvent = Il2CppHelpers.CreateWwiseEvent();
            door.m_CloseAudioEvent = Il2CppHelpers.CreateWwiseEvent();
            door.m_OpenAudio = openAudio;
            door.m_CloseAudio = closeAudio;
            door.m_StartOpened = false;
            door.m_LinkedObjects = new Il2CppReferenceArray<OpenClose>(0);
            door.m_Renderers = new Il2CppReferenceArray<Renderer>(0);
        }

        static Il2CppStringArray ReadStrings(BinaryReader reader)
        {
            var values = new string[reader.ReadInt32()];
            for (int index = 0; index < values.Length; index++) values[index] = reader.ReadString();
            return values;
        }

        static Il2CppStructArray<int> ReadInts(BinaryReader reader)
        {
            var values = new int[reader.ReadInt32()];
            for (int index = 0; index < values.Length; index++) values[index] = reader.ReadInt32();
            return values;
        }

        static Il2CppStructArray<float> ReadFloats(BinaryReader reader)
        {
            var values = new float[reader.ReadInt32()];
            for (int index = 0; index < values.Length; index++) values[index] = reader.ReadSingle();
            return values;
        }

        static Il2CppStructArray<bool> ReadBools(BinaryReader reader)
        {
            var values = new bool[reader.ReadInt32()];
            for (int index = 0; index < values.Length; index++) values[index] = reader.ReadBoolean();
            return values;
        }

        static Mesh ReadMesh(BinaryReader reader)
        {
            var mesh = new Mesh { name = reader.ReadString() };
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var vertices = new Vector3[reader.ReadInt32()];
            for (int index = 0; index < vertices.Length; index++) vertices[index] = ReadVector(reader);
            if (vertices.Length > ShortIndexLimit) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            var normals = new Vector3[reader.ReadInt32()];
            for (int index = 0; index < normals.Length; index++) normals[index] = ReadVector(reader);
            if (normals.Length == vertices.Length) mesh.normals = normals;
            var tangents = new Vector4[reader.ReadInt32()];
            for (int index = 0; index < tangents.Length; index++) tangents[index] = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (tangents.Length == vertices.Length) mesh.tangents = tangents;
            var uvs = new Vector2[reader.ReadInt32()];
            for (int index = 0; index < uvs.Length; index++) uvs[index] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            if (uvs.Length == vertices.Length) mesh.uv = uvs;
            var colors = new Color32[reader.ReadInt32()];
            for (int index = 0; index < colors.Length; index++) colors[index] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            if (colors.Length == vertices.Length) mesh.colors32 = colors;
            int subMeshCount = reader.ReadInt32();
            mesh.subMeshCount = subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                var triangles = new int[reader.ReadInt32()];
                for (int index = 0; index < triangles.Length; index++) triangles[index] = reader.ReadInt32();
                mesh.SetTriangles(triangles, subMesh);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector3 ReadVector(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        static Matrix4x4 ReadMatrix(BinaryReader reader)
        {
            var matrix = new Matrix4x4();
            for (int index = 0; index < MatrixElementCount; index++) matrix[index] = reader.ReadSingle();
            return matrix;
        }

        static void ApplyMatrix(Transform transform, Matrix4x4 matrix)
        {
            var scale = new Vector3(matrix.GetColumn(0).magnitude, matrix.GetColumn(1).magnitude, matrix.GetColumn(2).magnitude);
            // Matrix4x4.determinant is broken in the game's interop: a mirrored part is found from its axes
            Vector3 axisX = matrix.GetColumn(0), axisY = matrix.GetColumn(1), axisZ = matrix.GetColumn(2);
            if (Vector3.Dot(axisX, Vector3.Cross(axisY, axisZ)) < 0f) scale.x = -scale.x;
            transform.localPosition = matrix.GetColumn(3);
            transform.localRotation = Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
            transform.localScale = scale;
        }

        // ------------------------------------------------------------------ materials

        static readonly Dictionary<string, Material> s_Materials = new Dictionary<string, Material>();
        static Dictionary<string, string> s_MaterialKeys;

        /// A material by name: one loaded now, else the game's addressable material of that name, else a plain game material.
        static Material FindMaterial(string materialName)
        {
            if (s_Materials.TryGetValue(materialName, out var cached) && cached != null) return cached;
            Material found = null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material.name == materialName) { found = material; break; }
            if (found == null && MaterialKeys().TryGetValue(materialName, out var key))
            {
                try { found = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Material>(key).WaitForCompletion(); }
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Material {key}: {exception.Message}"); }
            }
            if (found == null)
            {
                SteamRailwayMod.Debug($"Wagon material {materialName} not found; a plain one stands in.");
                found = Dynamite.GameShaderMaterial();
            }
            if (found != null) found.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            s_Materials[materialName] = found;
            return found;
        }

        /// The game's addressable material paths by file name, read once from its catalog.
        static Dictionary<string, string> MaterialKeys()
        {
            if (s_MaterialKeys != null) return s_MaterialKeys;
            s_MaterialKeys = new Dictionary<string, string>();
            try
            {
                string catalog = File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json"));
                int end = catalog.IndexOf(MaterialExtension + "\"");
                while (end >= 0)
                {
                    int start = catalog.LastIndexOf('"', end) + 1;
                    string key = catalog.Substring(start, end + MaterialExtension.Length - start);
                    string fileName = Path.GetFileNameWithoutExtension(key);
                    if (!s_MaterialKeys.ContainsKey(fileName)) s_MaterialKeys[fileName] = key;
                    end = catalog.IndexOf(MaterialExtension + "\"", end + 1);
                }
            }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Addressables catalog: {exception.Message}"); }
            return s_MaterialKeys;
        }
    }
}
