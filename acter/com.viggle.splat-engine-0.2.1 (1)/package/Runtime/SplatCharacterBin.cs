// SPDX-License-Identifier: MIT
// The way in to a character file, whichever container it is, and the decoded shape they all hand
// back.
//
// TWO CONTAINERS. Parse, HasMagic, PeekNumBones and PeekRest each resolve the file's container
// through SplatCharacterMagic.Identify - the single place the 8 magic bytes are compared - and
// route to the matching reader. Neither reader is here: VIGGCHAR is read by
// SplatCharacterViggChar, VFCHARB1 by Legacy/SplatCharacterVfCharB1. Both produce the same
// SplatCharacterBinData, so nothing downstream knows or asks which arrived.
//
// This file holds only what BOTH sides need - the entry points, the dispatch, and
// SplatCharacterBinData itself. A container's decode never lands here, so retiring one is
// deleting its file and its arm of the switch below, and adding one is adding both.
//
// The EXTENSION decides nothing. A VIGGCHAR bundle named .bin reads as a VIGGCHAR bundle, which is
// what lets the wire path that carries .bin today start carrying the new format without a
// coordinated change at both ends.
//
// VFCHARB1 keeps working exactly as it did. Existing projects have those bytes baked into imported
// assets, and the policy is that consumers stay backward compatible. Adding VIGGCHAR moved that
// reader's body behind the dispatch and took the magic compare off the front of it; moving it into
// Legacy/ took it out of this file. Not one line of the decode itself has changed.

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine
{
    internal class SplatCharacterBinData : IDisposable
    {
        public int numSplats;
        public int numBones;
        public Vector3 boundsMin;
        public Vector3 boundsMax;
        public int weightsTopK;

        // Decoded splat attributes (length == numSplats).
        public NativeArray<float3> positions;     // object-space
        public NativeArray<float3> scales;        // linear
        public NativeArray<quaternion> rotations; // xyzw unit quats
        public NativeArray<Color32> colors;       // rgba8 (alpha = opacity * 255 baked below)
        public NativeArray<float> opacities;      // 0..1

        // Skinning (length == numSplats).
        public NativeArray<uint4> boneIndices;    // top-4 bone indices
        public NativeArray<float4> boneWeights;   // top-4 weights, normalized

        // Bind-pose rest translations from the .bin joints chunk (length == numBones).
        public NativeArray<float3> jointRestPos;

        // Did this bundle go through the character-correction pipeline? Only then are its joints
        // the rig its splats were actually baked against. See SplatCharacterRest.
        public bool corrected;

        /// <summary>
        /// What this file said about its rig, for
        /// <see cref="SplatSkeleton.For(in SplatCharacterRest)"/>. The copy is deliberate: the
        /// skeleton outlives the decode, and this array is native memory Dispose frees.
        /// </summary>
        public SplatCharacterRest Rest() => new SplatCharacterRest
        {
            numBones = numBones,
            jointRestPos = jointRestPos.IsCreated ? jointRestPos.ToArray() : null,
            corrected = corrected,
        };

        public void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (scales.IsCreated) scales.Dispose();
            if (rotations.IsCreated) rotations.Dispose();
            if (colors.IsCreated) colors.Dispose();
            if (opacities.IsCreated) opacities.Dispose();
            if (boneIndices.IsCreated) boneIndices.Dispose();
            if (boneWeights.IsCreated) boneWeights.Dispose();
            if (jointRestPos.IsCreated) jointRestPos.Dispose();
        }
    }

    internal static class SplatCharacterBin
    {
        /// <summary>
        /// Parses a character file of either container into decoded arrays, choosing the reader
        /// from the file's magic. Throws <see cref="InvalidOperationException"/> on malformed
        /// input, including on a magic this build does not read.
        /// </summary>
        public static SplatCharacterBinData Parse(byte[] bytes)
        {
            switch (SplatCharacterMagic.Identify(bytes))
            {
                case SplatCharacterFormat.ViggChar:
                    return SplatCharacterViggChar.Parse(bytes);
                case SplatCharacterFormat.VfCharB1:
                    return SplatCharacterVfCharB1.Parse(bytes);
                default:
                    throw new InvalidOperationException(
                        $"SplatCharacterBin: bad magic '{SplatCharacterMagic.Describe(bytes)}', " +
                        $"expected {SplatCharacterMagic.SupportedMagics}");
            }
        }

        /// <summary>
        /// True when the bytes begin with a character-container magic this build reads - either
        /// VFCHARB1 or VIGGCHAR. It answers "is this one of ours", nothing more; a file can pass
        /// this and still fail to decode.
        /// </summary>
        public static bool HasMagic(byte[] bytes) =>
            SplatCharacterMagic.Identify(bytes) != SplatCharacterFormat.Unknown;

        /// <summary>
        /// The file's bone count, without decoding a single splat. 0 when the bytes are not a
        /// readable character file, or carry no rig.
        /// </summary>
        /// <remarks>
        /// For callers that only need to know whether there is a rig at all. Anything that goes on
        /// to BUILD one wants <see cref="PeekRest"/>, which answers the same question and carries
        /// the file's own rest translations with it.
        ///
        /// Returning 0 costs the caller the skeleton and the Avatar, so both containers are
        /// answered here. A VIGGCHAR file falling through to 0 would import as a character with no
        /// rig, no Animator and no retargeting - and nothing in the log to connect it to a reader
        /// that only knew one magic.
        /// </remarks>
        public static int PeekNumBones(byte[] bytes) => PeekRest(bytes).numBones;

        /// <summary>
        /// The file's rest pose - bone count, joints and correction receipt - without decoding a
        /// single splat. All-zero when the bytes are not a readable character file, or carry no
        /// rig; never throws.
        /// </summary>
        /// <remarks>
        /// The importer's half of the change this exists for: the prefab's bone Transforms and the
        /// Humanoid Avatar are both built at import time, so a character whose rest pose is only
        /// merged at load would still be rigged on the canonical tables. Reading the joints costs
        /// one 12-byte row per bone - a few KB - against the several million splats
        /// <see cref="Parse"/> would decode to answer the same question.
        /// </remarks>
        public static SplatCharacterRest PeekRest(byte[] bytes)
        {
            switch (SplatCharacterMagic.Identify(bytes))
            {
                case SplatCharacterFormat.ViggChar:
                    return SplatCharacterViggChar.PeekRest(bytes);
                case SplatCharacterFormat.VfCharB1:
                    return SplatCharacterVfCharB1.PeekRest(bytes);
                default:
                    return default;
            }
        }
    }
}
