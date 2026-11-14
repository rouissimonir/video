// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Viggle.SplatEngine.Samples
{
    /// <summary>
    /// Tears down every effect in this sample: disables and destroys the effect components,
    /// releases the materials and render targets they own, and resets their statics.
    /// </summary>
    /// <remarks>
    /// The companion to <c>Viggle.SplatEngine.SplatEngineLifecycle.ReleaseAll()</c>. Call it at the
    /// same time you release the engine - the effects hold their own GPU resources, so releasing
    /// only the engine leaves those behind. Safe to call more than once, and the effects work
    /// normally again afterwards.
    /// </remarks>
    public static class SplatEffectsLifecycle
    {
        public static void ReleaseAll()
        {
            // The renderer feature first: it owns the render target the per-character outlines
            // draw into, and it must stop enqueuing passes before those characters go away.
#if GS_ENABLE_URP
            GaussianSplatOutlineFeature.ReleaseAll();
#endif

            foreach (var outline in FindAll<SplatOutline>())
            {
                if (outline == null) continue;
                outline.enabled = false;
                outline.ReleaseResources();
                DestroyObject(outline);
            }

            foreach (var tint in FindAll<SplatTintExample>())
            {
                if (tint == null) continue;
                tint.enabled = false;
                tint.ReleaseResources();
                DestroyObject(tint);
            }

            foreach (var colorOpacity in FindAll<SplatColorOpacityEffect>())
            {
                if (colorOpacity == null) continue;
                colorOpacity.enabled = false;
                colorOpacity.ReleaseResources();
                DestroyObject(colorOpacity);
            }

            // Clears the outline registry, including any entry whose component was already gone.
            SplatOutline.ResetStatics();
        }

        static T[] FindAll<T>() where T : Object
        {
            return Object.FindObjectsOfType<T>(true);
        }

        static void DestroyObject(Object obj)
        {
            Object.DestroyImmediate(obj);
        }
    }
}
