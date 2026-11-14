// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Project-wide defaults for splat characters, edited under Edit &gt; Project Settings &gt; Viggle
    /// Splat Engine. No setup is required: with no settings asset the code defaults apply, and the
    /// asset is only created once something on the settings page is changed. On load these push
    /// into the runtime API, which a game's own settings menu can still override live.
    /// </summary>
    /// <remarks>
    /// Hot-reload stable ABI: Unity message methods and serialized fields must stay stable.
    /// </remarks>
    public partial class SplatSettings : ScriptableObject
    {
        // Loadable at runtime from a Resources folder; the Project Settings page writes it there.
        internal const string kResourceName = "ViggleSplatSettings";

        /// <summary>
        /// How much this package prints. Warning, the default, lets problems speak while
        /// instrumentation stays quiet. Off silences failures too, so use it deliberately.
        /// </summary>
        [Tooltip("How much this package prints. Warning (the default) means problems speak and " +
                 "instrumentation stays quiet, which is what a shipping build wants. Info adds " +
                 "load timings and the DIAG lines; Verbose adds per-frame and per-character " +
                 "chatter. Off silences failures too, so use it deliberately.")]
        public SplatLogLevel logLevel = SplatLogLevel.Warning;

        /// <summary>
        /// Render splats in the Scene view while editing, so they can be placed without entering
        /// Play mode. Applies to characters and static captures alike. Turn off for heavy
        /// edit-time scenes.
        /// </summary>
        [Tooltip("Render splats in the Scene view while editing (like meshes do), so you can " +
                 "place them without entering Play mode. Turn off for heavy edit-time scenes.")]
        public bool renderInEditor = true;

        /// <summary>Pixel-art block edge for characters, in screen pixels.</summary>
        [Range(1, 16)]
        [Tooltip("Block edge, in screen pixels, for splat characters and anything else drawn " +
                 "after the splat composite. 1 leaves characters at full resolution.")]
        public int pixelCharacterScale = 4;

        /// <summary>Pixel-art block edge for the world, in screen pixels.</summary>
        [Range(1, 32)]
        [Tooltip("Block edge for the world drawn before the splat composite. Rounded up to a " +
                 "whole multiple of Character Scale, or the fine pass would re-chop the coarse " +
                 "blocks. Equal to Character Scale gives one uniform grid.")]
        public int pixelEnvironmentScale = 8;

        /// <summary>Colour steps per channel for the pixel-art filter.</summary>
        [Range(0, 32)]
        [Tooltip("Steps per colour channel. 0 or 1 = off. Low values (4-8) give the flat banded " +
                 "palette that reads as retro.")]
        public int pixelQuantizeLevels = 0;

        /// <summary>Bayer dither amplitude for the pixel-art filter.</summary>
        [Range(0f, 1f)]
        [Tooltip("Bayer 4x4 ordered dither, applied before quantisation, in quant steps. No " +
                 "effect unless Quantize Levels is set.")]
        public float pixelDitherStrength = 0f;

        /// <summary>Dither cell edge in screen pixels. 0 follows Environment Scale.</summary>
        [Range(0, 32)]
        [Tooltip("Dither cell edge in screen pixels. 0 follows Environment Scale, so the dot " +
                 "pattern is the same size as the colour blocks over most of the frame. Set it " +
                 "smaller for a finer dot than the blocks.")]
        public int pixelDitherCellSize = 0;

        /// <summary>Layers rendered on the character pixel grid.</summary>
        [Tooltip("Splat renderers on these layers are masked out of the environment pass and use " +
                 "Character Scale. All other splats use Environment Scale.")]
        public LayerMask pixelCharacterLayers;

        static SplatSettings s_Instance;

        // The active settings. Loads the project's asset if present, otherwise a transient instance with
        // the code defaults - so there is never a required setup step.
        internal static SplatSettings Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;
                s_Instance = Resources.Load<SplatSettings>(kResourceName);
                s_FromAsset = s_Instance != null;
                if (s_Instance == null)
                {
                    // Reaching here means the project's configured values are not in use, whether
                    // because no asset was authored or because one exists that could not be loaded.
                    // AppliedBy reports which of the two is live rather than implying the asset won.
                    s_Instance = CreateInstance<SplatSettings>();
                    s_Instance.hideFlags = HideFlags.HideAndDontSave;
                }
                return s_Instance;
            }
        }

        // Drop the cached instance so the next access re-loads the asset. Called by the editor after
        // the Project Settings asset is created or edited.
        internal static void Invalidate() => s_Instance = null;

        /// <summary>Clears cached settings state. Call when tearing the engine down.</summary>
        public static void ResetStatics()
        {
            var inst = s_Instance;
            if (inst != null && (inst.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave)
            {
                if (Application.isPlaying) Destroy(inst);
                else DestroyImmediate(inst);
            }
            s_Instance = null;
            s_AppliedBy = null;
            s_FromAsset = false;
            s_DefaultsProfile = "desktop";
        }

        static string s_DefaultsProfile = "desktop";

        // Push the runtime-relevant settings into the global API. renderInEditor is read directly by
        // SkinnedSplatRenderer, so it is not pushed here.
        internal void Apply()
        {
            // The vendored GaussianSplatting assembly sits below this one and cannot see SplatLog,
            // so its diagnostics flag is pushed down here rather than read from there.
            SplatLog.Level = logLevel;
            SplatRenderer.s_LogDiagnostics = logLevel >= SplatLogLevel.Info;
            SplatStyle.PixelCharacterScale = pixelCharacterScale;
            SplatStyle.PixelEnvironmentScale = pixelEnvironmentScale;
            SplatStyle.PixelQuantizeLevels = pixelQuantizeLevels;
            SplatStyle.PixelDitherStrength = pixelDitherStrength;
            SplatStyle.PixelDitherCellSize = pixelDitherCellSize;
            SplatStyle.PixelCharacterLayers = pixelCharacterLayers;
            // A host calling Apply itself is a legitimate third route and has to be distinguishable
            // from "never applied". The two callers below overwrite this with a specific label.
            if (s_AppliedBy == null) s_AppliedBy = "explicit";
        }

        // How the settings got applied, or null if they never did. Reported in the diagnostics line,
        // because whether a project's settings are actually in effect is not otherwise observable.
        static string s_AppliedBy;
        static bool s_FromAsset;

        // The second word is the one that matters: "defaults" means the project's configured values
        // are not in effect whatever the settings page shows.
        internal static string AppliedBy =>
            s_AppliedBy == null
                ? "NEVER"
                : $"{s_AppliedBy}/{(s_FromAsset ? "asset" : $"defaults({s_DefaultsProfile})")}";

        // Apply once, from wherever the package is first asked. Idempotent.
        //
        // ApplyOnLoad below cannot be relied on: Unity collects [RuntimeInitializeOnLoadMethod] by
        // scanning assemblies at build time, so an assembly loaded dynamically after
        // BeforeSceneLoad never registers the hook and every setting silently stays at its
        // hardcoded default. Callers therefore invoke this before building any resources, the
        // earliest point the package itself controls.
        internal static void EnsureApplied()
        {
            if (s_AppliedBy != null) return;
            Instance.Apply();
            s_AppliedBy = "lazy";
        }

        // Auto-apply on play so no bootstrap code is needed. Runs before the first scene loads, so
        // the settings are in place before any character loads.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ApplyOnLoad()
        {
            Instance.Apply();
            s_AppliedBy = "attribute";
        }
    }
}
