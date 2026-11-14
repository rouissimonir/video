// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Global state for the occlusion reveal: scene splats between the camera and
    /// <see cref="Focus"/> dissolve in a circle around the view axis — every splat gone
    /// inside <see cref="CoreRadius"/>, fewer and fewer out to <see cref="Radius"/> —
    /// so the focused character stays readable behind scenery. The game drives
    /// <see cref="Focus"/> and <see cref="Strength"/> every frame (a follow camera
    /// focusing its player); with no driver the default strength of 0 keeps the whole
    /// effect off. Per-renderer participation is <see cref="SplatRenderer.m_OcclusionReveal"/>:
    /// scenery defaults in, skinned characters default out. Both render paths upload
    /// these per camera; the math lives in GaussianSplatOcclusionReveal.hlsl.
    /// </summary>
    public static class SplatOcclusionReveal
    {
        /// <summary>The world point to keep visible — aim at the character's chest, not
        /// its feet, so the interval test measures against where the body reads from.</summary>
        public static Vector3 Focus;

        /// <summary>0 = off. Scales the dissolve density, so a driver can fade the
        /// reveal in and out rather than snapping it.</summary>
        public static float Strength;

        /// <summary>Rim radius in metres: dissolve probability reaches zero here.</summary>
        public static float Radius = 1.69f;

        /// <summary>Core radius in metres: every participating splat inside dissolves.</summary>
        public static float CoreRadius = 0.65f;

        /// <summary>How much of the axis just in FRONT of the focus is exempt, in
        /// metres. The floor at the character's feet is closer to the camera than the
        /// focus; without this margin the reveal would eat the ground stood on.</summary>
        public static float FocusMargin = 0.8f;

        internal static Vector4 PosStrength(bool participates) =>
            new Vector4(Focus.x, Focus.y, Focus.z, participates ? Strength : 0f);

        internal static Vector4 Shape => new Vector4(Radius, CoreRadius, FocusMargin, 0f);
    }
}
