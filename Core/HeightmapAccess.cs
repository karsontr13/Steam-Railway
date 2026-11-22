using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Reads and writes blocks of a terrain's heightmap by invoking the game's own TerrainData methods natively,
    /// because the generated wrappers cannot return the two-dimensional height array on older Il2CppInterop versions.
    internal static class HeightmapAccess
    {
        const int GetHeightsParameterCount = 4;
        const int SetHeightsParameterCount = 3;
        const int PositionArgumentCount = 2;
        const int HeightArgumentIndex = 3;

        static IntPtr s_getHeightsMethod;
        static IntPtr s_setHeightsMethod;
        static IntPtr s_setHeightsDelayedLodMethod;

        /// The heights of the block (xBase, zBase, width, height) as a flat array: index = row * width + column.
        public static unsafe Il2CppStructArray<float> Read(TerrainData terrainData, int xBase, int zBase, int width, int height)
        {
            IntPtr method = FindMethod(ref s_getHeightsMethod, "GetHeights", GetHeightsParameterCount);
            int* argumentValues = stackalloc int[GetHeightsParameterCount];
            argumentValues[0] = xBase;
            argumentValues[1] = zBase;
            argumentValues[2] = width;
            argumentValues[HeightArgumentIndex] = height;
            void** arguments = stackalloc void*[GetHeightsParameterCount];
            for (int argumentIndex = 0; argumentIndex < GetHeightsParameterCount; argumentIndex++) arguments[argumentIndex] = &argumentValues[argumentIndex];

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
                ? FindMethod(ref s_setHeightsDelayedLodMethod, "SetHeightsDelayLOD", SetHeightsParameterCount)
                : FindMethod(ref s_setHeightsMethod, "SetHeights", SetHeightsParameterCount);
            int* argumentValues = stackalloc int[PositionArgumentCount];
            argumentValues[0] = xBase;
            argumentValues[1] = zBase;
            void** arguments = stackalloc void*[SetHeightsParameterCount];
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
