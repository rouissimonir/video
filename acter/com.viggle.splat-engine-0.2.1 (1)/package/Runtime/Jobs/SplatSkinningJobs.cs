// SPDX-License-Identifier: MIT
// Skinning jobs, deliberately isolated in their own always-loaded assembly.
//
// The first time a job type is scheduled, Unity registers reflection data for it in the native
// layer, holding a function pointer to that struct's Execute. There is no API to unregister it, so
// the reference lives for the lifetime of the player. That is harmless for an assembly compiled
// into the app and fatal for one that has to be unloaded: tearing down an assembly the engine still
// holds a job pointer into fails outright. Keeping the job types here, in an assembly a host never
// unloads, means the registered Execute always belongs to code that stays put.
//
// Burst is the second reason. It compiles jobs ahead of time by scanning assemblies at build time,
// so a [BurstCompile] job inside a dynamically loaded assembly is never compiled and its Execute
// falls back to interpretation - slower than the managed loop the job replaced.
//
// The rule this enforces: the calling assembly must never name a job type and never call Schedule.
// It calls the plain static methods below, which register nothing, and everything crossing the
// boundary is a Unity or AOT type.
//
// The trade: this assembly cannot be hot-fixed, so changing the palette math needs a full native
// rebuild. Acceptable only because that math mirrors SplatSkinning.compute's CSBoneSkinning and
// changes when the kernel does.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace Viggle.SplatEngine.Jobs
{
    /// <summary>
    /// One bone's skinning transform. Uploaded verbatim to _BonePose, so the layout is
    /// load-bearing: it must match the BonePose declared in SplatSkinning.compute exactly, 80
    /// bytes of float4x4 plus float4.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct BonePose
    {
        public float4x4 m;
        public float4 dq;
    }

    /// <summary>
    /// The skinning jobs' entire surface. It schedules and completes within the
    /// call: callers need the result in the same frame, and staying synchronous keeps a JobHandle
    /// off the boundary, so no caller can end up holding a job type.
    /// </summary>
    internal static class SplatSkinningJobs
    {
        // Bones per worker batch. The work per bone is a handful of float4x4 ops, so batches this
        // small exist to spread the TransformAccess reads, not the arithmetic.
        const int kPaletteBatch = 32;

        /// <summary>
        /// Rebuild the skinning palette from the current bone poses.
        /// M = worldToLocal * TRS(bonePos, boneRot, 1) * stretch * restInv, plus the rigid
        /// bone delta quaternion used to rotate each Gaussian.
        /// </summary>
        /// <param name="changed">Single element, zeroed by the caller. Set to 1 when any bone's
        /// entry differs from what was already in the palette, so the caller can skip the upload
        /// and the skinning dispatch entirely on a character nothing has moved.</param>
        public static void BuildPalette(float4x4 worldToLocal,
                                        NativeArray<float4x4> restInv,
                                        NativeArray<float4x4> stretch,
                                        NativeArray<BonePose> palette,
                                        TransformAccessArray bones,
                                        NativeArray<int> changed)
        {
            new PaletteJob
            {
                worldToLocal = worldToLocal,
                restInv = restInv,
                stretch = stretch,
                palette = palette,
                changed = changed,
            }.ScheduleReadOnly(bones, kPaletteBatch).Complete();
        }

    }

    // Per-bone skinning-palette rebuild, parallel over the bones.
    // M = charWorldToLocal * TRS(bonePos, boneRot, 1) * restInv, with the delta quaternion taken
    // from M's normalized rotation columns. M is rigid up to character scale, which the
    // normalization removes; the quaternion's hemisphere is irrelevant because the kernel aligns
    // the quats itself.
    [BurstCompile]
    struct PaletteJob : IJobParallelForTransform
    {
        public float4x4 worldToLocal;
        [ReadOnly] public NativeArray<float4x4> restInv;
        [ReadOnly] public NativeArray<float4x4> stretch;
        // Each Execute writes only its own index; the transform-job safety system cannot see that,
        // so the parallel-write restriction is lifted explicitly.
        [NativeDisableParallelForRestriction] public NativeArray<BonePose> palette;
        // Written by any worker that sees a difference. The race is benign: every writer stores the
        // same 1, and the caller only asks whether anything changed, not which bone or how many.
        [NativeDisableParallelForRestriction] public NativeArray<int> changed;

        public void Execute(int i, TransformAccess bone)
        {
            float4x4 boneTransform = float4x4.TRS(bone.position, bone.rotation, new float3(1f));
            float4x4 rigid = math.mul(worldToLocal, math.mul(boneTransform, restInv[i]));
            float4x4 M = math.mul(worldToLocal,
                math.mul(boneTransform, math.mul(stretch[i], restInv[i])));
            // Rotation remains rigid. The current Unity kernel has separate position and rotation
            // outputs, so deriving dq from the non-rigid matrix would turn stretch into skew.
            float3x3 r = new float3x3(rigid.c0.xyz, rigid.c1.xyz, rigid.c2.xyz);
            r.c0 = math.normalizesafe(r.c0);
            r.c1 = math.normalizesafe(r.c1);
            r.c2 = math.normalizesafe(r.c2);
            quaternion q = math.normalize(new quaternion(r));

            // Exact comparison on purpose. This asks "did anything move", not "did it move
            // visibly", and a tolerance would let slow drift accumulate unseen while the character
            // is skipped. Identical transforms produce bit-identical results here.
            var pose = new BonePose { m = M, dq = q.value };
            BonePose prev = palette[i];
            if (changed[0] == 0 && (!prev.m.Equals(pose.m) || !prev.dq.Equals(pose.dq)))
                changed[0] = 1;
            palette[i] = pose;
        }
    }

    // Per-splat rest-pose height, in character-local space. Mirrors the position half of
    // SplatSkinning.compute's CSBoneSkinning exactly - same palette, same top-4 blend, same
    // multiply order - so the measurement matches what the GPU will draw. Rotation cannot affect a
    // center height, so it is skipped.
}
