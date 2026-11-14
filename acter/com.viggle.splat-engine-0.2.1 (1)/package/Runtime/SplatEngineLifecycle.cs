// SPDX-License-Identifier: MIT

using UnityEngine;
using Object = UnityEngine.Object;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Tears the engine down: releases every native and GPU resource it owns and resets its
    /// statics, so the assembly holding this code can be unloaded and nothing left behind can call
    /// into it. Call it whenever the engine's lifetime ends - unloading the scene the characters
    /// were in, returning to a menu, shutting a mini-game down, or swapping a hot-updated assembly.
    /// </summary>
    public static class SplatEngineLifecycle
    {
        /// <summary>
        /// Release everything this package owns: destroys its live components, frees their
        /// GPU/native buffers, and resets all package statics to their defaults. Safe to call more
        /// than once. After it returns, the assembly can be unloaded.
        /// </summary>
        public static void ReleaseAll()
        {
            // Stop pipeline callbacks first so no render pass can recreate render-system state.
            SplatRenderer.ReleaseAll();

            // One sweep covers both components: a character IS a SplatRenderer, and
            // FindObjectsByType returns derived types. It used to take three - characters,
            // captures, and the hidden renderer each object forwarded to - and in play mode the
            // later sweeps saw objects the earlier ones had already released, because Destroy is
            // deferred to the end of the frame.
            foreach (var splatRenderer in FindAll<SplatRenderer>())
            {
                if (splatRenderer == null) continue;
                splatRenderer.enabled = false;
                splatRenderer.ReleaseResources();
                DestroyObject(splatRenderer);
            }

            foreach (var cutout in FindAll<GaussianCutout>())
            {
                if (cutout == null) continue;
                cutout.enabled = false;
                DestroyObject(cutout);
            }

            SkinnedSplatRenderer.ResetStatics();
            SplatProxyLayers.ResetStatics();
            SplatSettings.ResetStatics();
            SplatShaderResources.ResetStatics();
            SplatQuality.ResetStatics();
            SplatLog.ResetStatics();
            SplatRenderer.ReleaseAll();
        }

        static T[] FindAll<T>() where T : Object
        {
            // Include inactive: teardown has to reach components on disabled GameObjects too.
            return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        static void DestroyObject(Object obj)
        {
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }
    }
}
