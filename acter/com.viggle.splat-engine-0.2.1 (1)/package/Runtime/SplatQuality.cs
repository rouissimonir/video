// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Runtime quality knobs. These are decisions a game makes from its own quality menu, per
    /// platform or per device tier, so they are plain statics rather than project settings: a
    /// settings asset could hold only one value and would have to be read at exactly the right
    /// moment to take effect.
    /// </summary>
    public static partial class SplatQuality
    {
        /// <summary>
        /// Resolution scale of the offscreen target the splats render into, 0.25 to 1. Splat
        /// rendering is fill-rate bound, so this is the most effective knob on a GPU-bound device.
        /// The composite upsamples bilinearly, so the cost is softness, not aliasing.
        /// </summary>
        public static float RenderScale
        {
            get => SplatRenderer.SplatRenderScale;
            set => SplatRenderer.SplatRenderScale = Mathf.Clamp(value, 0.25f, 1f);
        }

        /// <summary>
        /// Drop splats whose projected size falls below this radius in pixels. 0 = off.
        /// Removes distant crowd splats that cannot contribute more than a fraction of a pixel.
        /// </summary>
        public static float ScreenSizeCull
        {
            get => SplatRenderer.GlobalScreenSizeCull;
            set => SplatRenderer.GlobalScreenSizeCull = Mathf.Max(0f, value);
        }

        /// <summary>
        /// Test each splat against the depth buffer so opaque geometry hides the splats behind it.
        /// On by default. Turning it off is the single largest mobile saving here, at the cost of
        /// characters drawing through walls, so it suits a scene where nothing occludes them.
        /// </summary>
        public static bool DepthOcclusion
        {
            get => SplatRenderer.SplatDepthOcclusion;
            set => SplatRenderer.SplatDepthOcclusion = value;
        }

        /// <summary>Restores every knob to its default. Call when tearing the engine down.</summary>
        public static void ResetStatics()
        {
            RenderScale = 1f;
            ScreenSizeCull = 0f;
            DepthOcclusion = true;
        }
    }
}
