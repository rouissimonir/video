// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Project-wide pixel-art look for splats. Set it once and every renderer uses the same screen
    /// grid and palette treatment.
    /// </summary>
    /// <remarks>
    /// The static API is the authority. <see cref="SplatSettings"/> pushes the project's values in
    /// on load, and a game is free to overwrite them later from its own menu - the same split
    /// <see cref="SplatQuality"/> uses.
    ///
    /// Changing anything here bumps <see cref="Revision"/> so runtime integrations can notice live
    /// art-direction changes without polling every value.
    /// </remarks>
    public static class SplatStyle
    {
        static int s_PixelCharacterScale = 4;
        static int s_PixelEnvironmentScale = 8;
        static int s_PixelQuantizeLevels;
        static float s_PixelDitherStrength;
        static LayerMask s_PixelCharacterLayers;
        static int s_PixelDitherCellSize;

        /// <summary>Bumped whenever any value changes, so live characters can notice.</summary>
        internal static int Revision { get; private set; }

        /// <summary>
        /// Pixel-art block edge, in screen pixels, for splat characters and anything else drawn
        /// after the splat composite. 1 leaves characters at full resolution.
        /// </summary>
        public static int PixelCharacterScale
        {
            get => s_PixelCharacterScale;
            set { value = Mathf.Clamp(value, 1, 16); if (s_PixelCharacterScale == value) return; s_PixelCharacterScale = value; Revision++; }
        }

        /// <summary>
        /// Pixel-art block edge for the world drawn before the splat composite. Rounded up to a
        /// whole multiple of <see cref="PixelCharacterScale"/> when used - the two grids have to
        /// line up or the fine pass would re-chop the coarse blocks.
        /// </summary>
        public static int PixelEnvironmentScale
        {
            get => s_PixelEnvironmentScale;
            set { value = Mathf.Clamp(value, 1, 32); if (s_PixelEnvironmentScale == value) return; s_PixelEnvironmentScale = value; Revision++; }
        }

        /// <summary>Colour steps per channel for the pixel-art filter. 0 or 1 = off.</summary>
        public static int PixelQuantizeLevels
        {
            get => s_PixelQuantizeLevels;
            set { value = Mathf.Clamp(value, 0, 32); if (s_PixelQuantizeLevels == value) return; s_PixelQuantizeLevels = value; Revision++; }
        }

        /// <summary>
        /// Dither cell edge in screen pixels. 0 follows <see cref="PixelEnvironmentScale"/>, which
        /// is what makes the dot pattern the same size as the colour blocks over most of the frame.
        /// </summary>
        public static int PixelDitherCellSize
        {
            get => s_PixelDitherCellSize;
            set { value = Mathf.Clamp(value, 0, 32); if (s_PixelDitherCellSize == value) return; s_PixelDitherCellSize = value; Revision++; }
        }

        /// <summary>Bayer dither amplitude, in quant steps. No effect without quantisation.</summary>
        public static float PixelDitherStrength
        {
            get => s_PixelDitherStrength;
            set { value = Mathf.Clamp01(value); if (Mathf.Approximately(s_PixelDitherStrength, value)) return; s_PixelDitherStrength = value; Revision++; }
        }

        /// <summary>
        /// Layers treated as characters by the pixel-art renderer. They are masked out of the
        /// coarse environment pass and receive the fine character grid instead.
        /// </summary>
        public static LayerMask PixelCharacterLayers
        {
            get => s_PixelCharacterLayers;
            set { if (s_PixelCharacterLayers == value) return; s_PixelCharacterLayers = value; Revision++; }
        }

        /// <summary>Restores every knob to its default. Call when tearing the engine down.</summary>
        public static void ResetStatics()
        {
            s_PixelCharacterScale = 4;
            s_PixelEnvironmentScale = 8;
            s_PixelQuantizeLevels = 0;
            s_PixelDitherStrength = 0f;
            s_PixelCharacterLayers = 0;
            s_PixelDitherCellSize = 0;
            Revision++;
        }
    }
}
