using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Reads and writes blocks of a terrain's heightmap by calling the game's own TerrainData methods directly.
    ///
    /// TerrainData.GetHeights returns a two-dimensional array (float[,]). The generated Il2Cpp wrappers declare that
    /// return type only as a generic object, and older Il2CppInterop versions (MelonLoader 0.7.2) hand back null for
    /// it, which made every terrain edit fail with a NullReferenceException. Invoking the native methods by hand
    /// gives the array's pointer without going through those wrappers, so it works with every version.
    internal static class HeightmapAccess
    {
        static IntPtr s_getHeightsMethod;
        static IntPtr s_setHeightsMethod;
        static IntPtr s_setHeightsDelayedLodMethod;

        /// The heights of the block (xBase, zBase, width, height) as a flat array: index = row * width + column.
        public static unsafe Il2CppStructArray<float> Read(TerrainData terrainData, int xBase, int zBase, int width, int height)
        {
            IntPtr method = FindMethod(ref s_getHeightsMethod, "GetHeights", 4);
            int* argumentValues = stackalloc int[4];
            argumentValues[0] = xBase;
            argumentValues[1] = zBase;
            argumentValues[2] = width;
            argumentValues[3] = height;
            void** arguments = stackalloc void*[4];
            for (int argumentIndex = 0; argumentIndex < 4; argumentIndex++) arguments[argumentIndex] = &argumentValues[argumentIndex];

            IntPtr exception = IntPtr.Zero;
            IntPtr heightsPointer = IL2CPP.il2cpp_runtime_invoke(method, terrainData.Pointer, arguments, ref exception);
            if (exception != IntPtr.Zero) throw new InvalidOperationException("TerrainData.GetHeights threw an exception.");
            if (heightsPointer == IntPtr.Zero) throw new InvalidOperationException("TerrainData.GetHeights returned nothing.");
            return new Il2CppStructArray<float>(heightsPointer);
        }

        /// Writes a block read with Read back. delayLevelOfDetail leaves the level of detail and the collider to be
        /// brought up to date later (TerrainData.SyncHeightmap), which keeps painting fast.
        public static unsafe void Write(TerrainData terrainData, int xBase, int zBase, Il2CppStructArray<float> heights, bool delayLevelOfDetail = false)
        {
            IntPtr method = delayLevelOfDetail
                ? FindMethod(ref s_setHeightsDelayedLodMethod, "SetHeightsDelayLOD", 3)
                : FindMethod(ref s_setHeightsMethod, "SetHeights", 3);
            int* argumentValues = stackalloc int[2];
            argumentValues[0] = xBase;
            argumentValues[1] = zBase;
            void** arguments = stackalloc void*[3];
            arguments[0] = &argumentValues[0];
            arguments[1] = &argumentValues[1];
            arguments[2] = (void*)heights.Pointer;

            IntPtr exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, terrainData.Pointer, arguments, ref exception);
            if (exception != IntPtr.Zero) throw new InvalidOperationException("TerrainData.SetHeights threw an exception.");
        }

        static IntPtr FindMethod(ref IntPtr cachedMethod, string methodName, int parameterCount)
        {
            if (cachedMethod != IntPtr.Zero) return cachedMethod;
            IntPtr terrainDataClass = Il2CppClassPointerStore<TerrainData>.NativeClassPtr;
            cachedMethod = IL2CPP.il2cpp_class_get_method_from_name(terrainDataClass, methodName, parameterCount);
            if (cachedMethod == IntPtr.Zero) throw new InvalidOperationException($"TerrainData.{methodName} was not found.");
            return cachedMethod;
        }
    }
}
