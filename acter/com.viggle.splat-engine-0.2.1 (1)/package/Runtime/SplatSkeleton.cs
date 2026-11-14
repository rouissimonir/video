// SPDX-License-Identifier: MIT
// The rest skeleton a character is skinned against: local rest-TRS accessors and the
// forward-kinematic world bind pose used for skinning.
//
// Two rigs, picked by the character file's bone count:
//   86  -> Body86, the body-only rig, built by truncating the full rig and folding each facial
//          bone's splat weights onto its nearest body bone.
//   441 -> Full441, the same 86 body bones in the same order plus 355 facial bones. Its splats
//          keep their real facial weights, so indices 86..440 must resolve to real bones; on the
//          body rig every one of them clamps onto the last body bone and the face rides the shin.
//
// SplatSkeleton.For is the only place that choice is made, and everything else takes the
// SplatSkeletonDef it returns. There is deliberately no static "the" skeleton: with two rigs in
// play, a call that silently means the 86-bone one is a bug waiting to happen.
//
// The tables supply the HIERARCHY and the rest ROTATIONS for every character. A corrected file
// also carries its own rest TRANSLATIONS, and For(in SplatCharacterRest) merges those over the
// table it picked; see SplatCharacterRest for what makes a file's joints trustworthy and
// KeepsCanonicalRest for the bones where they are not.

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine
{
    // One rest skeleton: bone names, hierarchy, and local rest TRS.
    internal sealed class SplatSkeletonDef
    {
        public readonly string Name;      // for logs / diagnostics, e.g. "body86"
        public readonly int NumBones;
        public readonly string[] Names;
        public readonly int[] Parents;

        readonly float[] m_RestT;         // NumBones x 3, local (x,y,z)
        readonly float[] m_RestR;         // NumBones x 4, local (qx,qy,qz,qw)
        Dictionary<string, int> m_ByName; // lazily built, see IndexOf

        public SplatSkeletonDef(string name, string[] names, int[] parents, float[] restT, float[] restR)
        {
            Name = name;
            Names = names;
            Parents = parents;
            m_RestT = restT;
            m_RestR = restR;
            NumBones = names.Length;
        }

        // Local rest TRS is mirrored across Z=0 to match the splats' handedness conversion (see
        // SplatCharacterBin.Parse). For a hierarchy, mirroring each local transform reproduces the
        // mirrored world pose.
        //
        // The table is authored facing +X and Unity's Humanoid rig wants +Z, so the ROOT bone is
        // yawed; children inherit it down the chain, so one bone turns the whole skeleton. That
        // yaw must live in the table rather than on a parent node above it: Unity builds the Avatar
        // from these values, and a yaw above the avatar root is applied by the live transform AND
        // re-applied by retargeting, landing the animated character 90 degrees off its rest pose.
        static readonly Quaternion kRootYaw = Quaternion.Euler(0f, SplatConvention.ForwardYawDeg, 0f);
        bool IsRoot(int i) => Parents[i] < 0;

        public Vector3 LocalPosition(int i)
        {
            var p = new Vector3(m_RestT[i * 3], m_RestT[i * 3 + 1], -m_RestT[i * 3 + 2]);
            return IsRoot(i) ? kRootYaw * p : p;
        }

        public Quaternion LocalRotation(int i)
        {
            var q = new Quaternion(-m_RestR[i * 4], -m_RestR[i * 4 + 1], m_RestR[i * 4 + 2], m_RestR[i * 4 + 3]);
            return IsRoot(i) ? kRootYaw * q : q;
        }

        // World-space rest (bind) transform per bone, via FK down the parent chain:
        //   world[i] = world[parent[i]] * TRS(localPos, localRot, 1)
        // Parents always precede their children in index order, so one forward pass is enough.
        public Matrix4x4[] ComputeWorldRest()
        {
            var localRot = new Quaternion[NumBones];
            for (int i = 0; i < NumBones; i++) localRot[i] = LocalRotation(i);
            return ComputeWorld(localRot);
        }

        // FK with arbitrary local rotations (rest positions). Used to visualize alternate
        // poses such as the T-pose-corrected avatar reference.
        public Matrix4x4[] ComputeWorld(Quaternion[] localRot)
        {
            int n = NumBones;
            var world = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                Matrix4x4 local = Matrix4x4.TRS(LocalPosition(i), localRot[i], Vector3.one);
                int p = Parents[i];
                world[i] = (p >= 0 && p < n) ? world[p] * local : local;
            }
            return world;
        }

        // A copy of the local rest translations, in the TABLE's frame - source-authored, before
        // LocalPosition mirrors them and yaws the root. Mutate the copy and hand it back through
        // WithRestTranslations; the shared tables are static and are never written to.
        public float[] CopyRestTranslations() => (float[])m_RestT.Clone();

        // This rig with per-bone rest TRANSLATIONS replaced. Names, parents and rest ROTATIONS are
        // shared with the original, because a character file carries none of them - so a derived
        // def costs one float[] and is the same rig by every other measure, including its Name.
        public SplatSkeletonDef WithRestTranslations(float[] restT) =>
            new SplatSkeletonDef(Name, Names, Parents, restT, m_RestR);

        // Bone index by name, -1 if this rig has no such bone. Dictionary-backed rather than
        // Array.IndexOf: the full rig is 441 bones and most callers look bones up by name.
        public int IndexOf(string boneName)
        {
            if (m_ByName == null)
            {
                m_ByName = new Dictionary<string, int>(NumBones);
                for (int i = 0; i < NumBones; i++) m_ByName[Names[i]] = i;
            }
            return m_ByName.TryGetValue(boneName, out int idx) ? idx : -1;
        }
    }

    internal static class SplatSkeleton
    {
        // The 86-bone body rig.
        public static readonly SplatSkeletonDef Body86 = new SplatSkeletonDef(
            "body86", SplatSkeletonData.BoneNames, SplatSkeletonData.Parents,
            SplatSkeletonData.RestTranslations, SplatSkeletonData.RestRotations);

        // The full 441-bone body + facial rig.
        public static readonly SplatSkeletonDef Full441 = new SplatSkeletonDef(
            "full441", SplatSkeletonData441.BoneNames, SplatSkeletonData441.Parents,
            SplatSkeletonData441.RestTranslations, SplatSkeletonData441.RestRotations);

        // Bone counts already warned about. Callers include OnDrawGizmos and every character in a
        // crowd, so an un-deduped warning would be one line per character per frame.
        static readonly HashSet<int> s_WarnedCounts = new HashSet<int>();

        // The rig a file declaring `numBones` bones is weighted against. Falling back to Body86 for
        // an unrecognised count is safe in the way that matters: bone indices are clamped to the
        // palette size before they reach the GPU, so an unknown rig renders a mangled but present
        // character rather than faulting on an out-of-bounds palette read.
        public static SplatSkeletonDef For(int numBones)
        {
            if (numBones == Full441.NumBones) return Full441;
            if (numBones != Body86.NumBones && numBones > 0 && s_WarnedCounts.Add(numBones))
                Debug.LogWarning($"SplatSkeleton: .bin declares {numBones} bones, which matches neither " +
                                 $"the {Body86.NumBones}-bone stripped rig nor the {Full441.NumBones}-bone " +
                                 "full rig. Falling back to the stripped rig; splats weighted to bones past " +
                                 $"{Body86.NumBones - 1} will collapse onto the last body bone.");
            return Body86;
        }

        /// <summary>
        /// The rig a character file poses on: the same table <see cref="For(int)"/> picks, with
        /// that file's own rest translations merged over it wherever they can be trusted.
        /// </summary>
        /// <remarks>
        /// Translations only - a bundle carries no rotations and no hierarchy - and only when the
        /// file is corrected, carries a joints block, and that block is sized to the rig its bone
        /// count selected. Anything else hands back the shared canonical def untouched, which is
        /// what every character rendered before this existed. In particular a bone count matching
        /// neither rig never merges: For falls back to the body rig, whose size the file's joints
        /// then disagree with.
        /// </remarks>
        public static SplatSkeletonDef For(in SplatCharacterRest rest)
        {
            var skel = For(rest.numBones);
            float3[] joints = rest.jointRestPos;
            if (!rest.corrected || joints == null || joints.Length != skel.NumBones)
                return skel;

            float[] restT = skel.CopyRestTranslations();
            for (int b = 0; b < skel.NumBones; b++)
            {
                if (KeepsCanonicalRest(skel.Names[b])) continue;
                // Back into the table's frame. Every producer stores jointRestPos through
                // SplatConvention.Position, which is a pure X<->Z swap and therefore its own
                // inverse; LocalPosition then re-applies the mirror, and the root yaw on top of
                // it, exactly as it does to the canonical rows. Writing the Unity-frame value
                // straight in would mirror the whole rig's z and yaw the root a second time.
                float3 t = SplatConvention.Position(joints[b]);
                restT[b * 3] = t.x;
                restT[b * 3 + 1] = t.y;
                restT[b * 3 + 2] = t.z;
            }
            return skel.WithRestTranslations(restT);
        }

        // Bones whose per-character rest translation is NOT trustworthy, so they keep the canonical
        // value. Both groups were picked on measurement across four generated characters, not by
        // anatomy, and SplatEngine excludes exactly these two (engine/src/character.ts,
        // keepsCanonicalRest) - the two implementations have to agree or the same character poses
        // differently on web and in Unity.
        //
        // FACIAL_* - 160-181 mm from canonical at worst, against a face only ~200 mm tall. And
        // clips are authored on the canonical rig, so a facial clip poses in canonical space while
        // a per-character facial bind sits somewhere else: 143 mm of facial displacement measured,
        // 7x more than with facial channels removed, and exactly 0 mm at rest.
        //
        // *toe_* - here the error relative to BONE LENGTH is what shows, and absolute numbers hide
        // it. Toes run a median 35% and up to 323% of their own length (indextoe_01_l is 17 mm long
        // and displaced 54.8 mm) against a median 14% for the rest of the body.
        //
        // Everything else keeps the file's value because it IS trustworthy - a plausible 45-60 mm -
        // and because that is where the root shift lives. An empty name trusts the file, matching
        // the engine's "unknown rig, no names" case; both bundled rigs always have names.
        static bool KeepsCanonicalRest(string boneName) =>
            !string.IsNullOrEmpty(boneName) &&
            (boneName.StartsWith("FACIAL_", StringComparison.Ordinal) || boneName.Contains("toe_"));
    }
}
