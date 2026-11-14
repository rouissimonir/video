// SPDX-License-Identifier: MIT
// What a character file says about the rig it was baked against - the three answers
// SplatSkeleton.For needs, and nothing else.
//
// A bundle carries no bone names, no parents and no rest ROTATIONS; those can only come from the
// bundled tables in SplatSkeletonData / SplatSkeletonData441. What it does carry is one rest
// TRANSLATION per bone, and that one is per-character: the generation pipeline moves joints to fit
// the body it built, so the shared table is only ever an approximation of any particular character.
//
// WHY IT IS NOT COSMETIC. A corrected bundle has the ankle correction and the ground shift baked
// into its gaussians AND into joint 0. Reading the joints back is the only way to recover that root
// shift - pose such a character from the shared tables and the skin moves while the skeleton stays
// behind, so the splats and the rig disagree. SplatEngine has read the bundle's joints since the
// corrections shipped (engine/src/character.ts); this is the same method.
//
// THE GATE. `corrected` is the question "did this bundle go through the character-correction
// pipeline", and it is answered per container:
//
//   VIGGCHAR v1 - always true. The version means corrected, and the spec forbids a receipt, so
//                 there is nothing to look for. See SplatCharacterViggCharV1's header.
//   VFCHARB1    - only when the header carries a correction receipt. The old writers agree on the
//                 meaning and disagree on the spelling, so both are accepted: `corrections`, from
//                 the production encoder, which writes it ONLY when the corrections ran, and
//                 `ankleCorrection`, from SplatEngine/tools/pkl_to_vsplat.py, which writes the
//                 ankle block at the top level and no `corrections` key at all. `groundOffset` and
//                 `visibleBounds` are deliberately NOT accepted: that writer emits them
//                 unconditionally, so they say "written recently", not "corrected".
//
// A bundle with no receipt has joints no pipeline ever vetted, so it keeps the canonical tables and
// renders exactly as it did before this existed. Every VFCHARB1 file in this repository is in that
// class - none of them carries either spelling.

using Unity.Mathematics;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// A character file's own answer about its rest pose. Produced by both readers, and by the
    /// peek the importer uses; consumed only by <see cref="SplatSkeleton.For(in SplatCharacterRest)"/>.
    /// </summary>
    internal struct SplatCharacterRest
    {
        /// <summary>Bones in the file's rig, or 0 when it declares none.</summary>
        public int numBones;

        /// <summary>
        /// Parent-local rest translation per bone, already in Unity's convention - every producer
        /// runs it through <see cref="SplatConvention.Position"/>, exactly as it does the splats.
        /// Null when the file carries no joints, in which case the canonical tables stand.
        /// </summary>
        public float3[] jointRestPos;

        /// <summary>Did this bundle go through the character-correction pipeline? See the header.</summary>
        public bool corrected;
    }
}
