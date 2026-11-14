// SPDX-License-Identifier: MIT
//
// Deprecated aliases for this sample's teardown API, kept in one file so the rest of the sample
// reads cleanly. Each member forwards to its replacement and carries an [Obsolete] naming it.
//
// Nothing in the sample calls these. If you copy the sample into your own project, delete this
// file (and the `partial` keywords it needs) and use SplatEffectsLifecycle.ReleaseAll,
// ReleaseResources and ResetStatics directly.

using System;

namespace Viggle.SplatEngine.Samples
{
    [Obsolete("Renamed to SplatEffectsLifecycle.")]
    public static class SplatEffectsHotReloadCleanup
    {
        [Obsolete("Renamed to SplatEffectsLifecycle.ReleaseAll().")]
        public static void ReleaseForHotReload() => SplatEffectsLifecycle.ReleaseAll();
    }

    public partial class SplatOutline
    {
        [Obsolete("Renamed to SplatOutline.ReleaseResources().")]
        public void DisposeForHotReload() => ReleaseResources();

        [Obsolete("Renamed to SplatOutline.ResetStatics().")]
        public static void ReleaseStaticsForHotReload() => ResetStatics();
    }

    public partial class SplatTintExample
    {
        [Obsolete("Renamed to SplatTintExample.ReleaseResources().")]
        public void DisposeForHotReload() => ReleaseResources();
    }

    public partial class SplatColorOpacityEffect
    {
        [Obsolete("Renamed to SplatColorOpacityEffect.ReleaseResources().")]
        public void DisposeForHotReload() => ReleaseResources();
    }

#if GS_ENABLE_URP
    public partial class GaussianSplatOutlineFeature
    {
        [Obsolete("Renamed to GaussianSplatOutlineFeature.ReleaseResources().")]
        public void DisposeForHotReload() => ReleaseResources();

        [Obsolete("Renamed to GaussianSplatOutlineFeature.ReleaseAll().")]
        public static void ReleaseForHotReload() => ReleaseAll();
    }
#endif
}
