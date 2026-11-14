// SPDX-License-Identifier: MIT
// The source -> Unity coordinate-convention conversion, in one place.
//
// Character files are authored right-handed, +Y up, facing +X. Unity is left-handed and its
// Humanoid rig expects the character to face +Z, so two corrections are needed and are applied
// together:
//
//   1. Handedness - reflect across Z=0. Without it the character is mirrored and Humanoid
//      retargeting comes out broken.
//   2. Forward axis - yaw -90 degrees about Y, which maps +X -> +Z.
//
// Composed, the two collapse to a plain X<->Z swap:
//
//      yaw(-90) * mirrorZ(px, py, pz) = yaw(-90) * (px, py, -pz) = (pz, py, px)
//
// which is exact (a pure axis permutation, no trig and no float error) and self-evidently has
// det = -1, so it carries the handedness flip for free.
//
// Where this is applied:
//   * File data (splats, bounds, joint rest) is converted once, in SplatCharacterBin.Parse, so
//     everything downstream sees Unity-convention data and needs no correction of its own.
//   * The rest skeleton is a static table in the same source convention but is not file data,
//     so it cannot be converted here. SplatSkeleton yaws its ROOT BONE instead (see
//     ForwardYawDeg); children inherit it, so one bone turns the whole skeleton. That yaw must
//     live in the bone table and not on a parent node above it: Unity builds the Avatar from
//     these values, so a yaw above the avatar root is applied by the live transform AND
//     re-applied by retargeting, landing animated characters 90 degrees off rest. Generic mode
//     never sees it, since SplatSkeletonBindPose.Apply overwrites bone 0 with the baked bind
//     pose and takes its orientation from there.
//
// Scales are deliberately left untouched. A gaussian's rotation mirrors by conjugation
// (q -> (-x,-y,z,w)), and conjugating by a diagonal +/-1 matrix leaves a diagonal scale
// invariant. The other valid form, conjugating by the X<->Z swap, gives a tidier quaternion but
// requires swapping sx/sz to match.

using Unity.Mathematics;

namespace Viggle.SplatEngine
{
    internal static class SplatConvention
    {
        // Yaw that takes the source frame's +X forward onto Unity's +Z forward. Applied by
        // SplatSkeleton to the rest table's ROOT BONE, the only place it can live (see the header).
        // File data does not use it; Position and Rotation fold it in.
        public const float ForwardYawDeg = -90f;

        static readonly quaternion kForwardYaw = quaternion.RotateY(math.radians(ForwardYawDeg));

        // Source position to Unity: the fused mirror and yaw, i.e. an X<->Z swap.
        public static float3 Position(float px, float py, float pz) => new float3(pz, py, px);

        public static float3 Position(float3 p) => new float3(p.z, p.y, p.x);

        // Source gaussian rotation to Unity: mirror across Z=0 by conjugation, then yaw. Pairs with
        // scales being left as authored; see the file header.
        public static quaternion Rotation(float qx, float qy, float qz, float qw) =>
            math.mul(kForwardYaw, new quaternion(-qx, -qy, qz, qw));

        // Source AABB to Unity. Position is a pure axis permutation with no sign flips, so it maps
        // min onto min and max onto max componentwise; no corner enumeration needed.
        public static void Bounds(ref UnityEngine.Vector3 min, ref UnityEngine.Vector3 max)
        {
            var nmin = Position(min.x, min.y, min.z);
            var nmax = Position(max.x, max.y, max.z);
            min = new UnityEngine.Vector3(nmin.x, nmin.y, nmin.z);
            max = new UnityEngine.Vector3(nmax.x, nmax.y, nmax.z);
        }
    }
}
