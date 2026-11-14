// SPDX-License-Identifier: MIT
// Builds a Unity Humanoid Avatar from a splat rig, so any Humanoid AnimationClip retargets onto it
// and thereby drives the splats.
//
// The body bones map onto Unity's HumanBodyBones; twist and corrective bones that are not mapped
// simply ride their parent. Works for either rig (see SplatSkeleton): the human map is body-only,
// so a full 441-bone rig contributes its facial bones to the HumanDescription skeleton as unmapped
// bones. Unity leaves those alone during retargeting, which is what is wanted - a facial bone's
// local never changes, so its skinning matrix resolves to its body ancestor's and the face rides
// the head.

using System.Collections.Generic;
using UnityEngine;

namespace Viggle.SplatEngine
{
    internal static class SplatHumanoidAvatar
    {
        // (Unity human bone name, rig bone name). Order is not significant.
        static readonly (string human, string bone)[] kBodyMap =
        {
            ("Hips", "pelvis"),
            ("Spine", "spine_01"),
            ("Chest", "spine_03"),
            ("UpperChest", "spine_05"),
            ("Neck", "neck_01"),
            ("Head", "head"),

            ("LeftShoulder", "clavicle_l"),
            ("LeftUpperArm", "upperarm_l"),
            ("LeftLowerArm", "lowerarm_l"),
            ("LeftHand", "hand_l"),
            ("RightShoulder", "clavicle_r"),
            ("RightUpperArm", "upperarm_r"),
            ("RightLowerArm", "lowerarm_r"),
            ("RightHand", "hand_r"),

            ("LeftUpperLeg", "thigh_l"),
            ("LeftLowerLeg", "calf_l"),
            ("LeftFoot", "foot_l"),
            ("LeftToes", "ball_l"),
            ("RightUpperLeg", "thigh_r"),
            ("RightLowerLeg", "calf_r"),
            ("RightFoot", "foot_r"),
            ("RightToes", "ball_r"),

            // Fingers: rig _01/_02/_03 -> Unity Proximal/Intermediate/Distal. The metacarpal bones
            // stay unmapped, since Unity drives the chain from Proximal.
            ("Left Thumb Proximal", "thumb_01_l"),  ("Left Thumb Intermediate", "thumb_02_l"),  ("Left Thumb Distal", "thumb_03_l"),
            ("Left Index Proximal", "index_01_l"),  ("Left Index Intermediate", "index_02_l"),  ("Left Index Distal", "index_03_l"),
            ("Left Middle Proximal", "middle_01_l"),("Left Middle Intermediate", "middle_02_l"),("Left Middle Distal", "middle_03_l"),
            ("Left Ring Proximal", "ring_01_l"),    ("Left Ring Intermediate", "ring_02_l"),    ("Left Ring Distal", "ring_03_l"),
            ("Left Little Proximal", "pinky_01_l"), ("Left Little Intermediate", "pinky_02_l"), ("Left Little Distal", "pinky_03_l"),

            ("Right Thumb Proximal", "thumb_01_r"),  ("Right Thumb Intermediate", "thumb_02_r"),  ("Right Thumb Distal", "thumb_03_r"),
            ("Right Index Proximal", "index_01_r"),  ("Right Index Intermediate", "index_02_r"),  ("Right Index Distal", "index_03_r"),
            ("Right Middle Proximal", "middle_01_r"),("Right Middle Intermediate", "middle_02_r"),("Right Middle Distal", "middle_03_r"),
            ("Right Ring Proximal", "ring_01_r"),    ("Right Ring Intermediate", "ring_02_r"),    ("Right Ring Distal", "ring_03_r"),
            ("Right Little Proximal", "pinky_01_r"), ("Right Little Intermediate", "pinky_02_r"), ("Right Little Distal", "pinky_03_r"),
        };

        // Builds the avatar. `root` is the rig root GameObject whose children are bone Transforms
        // named exactly as skel.Names, and `skel` must be the rig those Transforms were built from.
        //
        // tPoseCorrection flattens the arm bones to horizontal in the avatar's bind pose, so
        // muscle-zero is a T-pose, which is what Unity Humanoid clips assume. The live rig stays in
        // its A-pose, where the splats are bound; only this avatar reference changes.
        public static Avatar Build(GameObject root, SplatSkeletonDef skel, bool tPoseCorrection = true)
        {
            var human = new List<HumanBone>(kBodyMap.Length);
            foreach (var (humanName, boneName) in kBodyMap)
            {
                if (skel.IndexOf(boneName) < 0) continue;
                human.Add(new HumanBone
                {
                    humanName = humanName,
                    boneName = boneName,
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            // Skeleton must include the root + every bone, with LOCAL rest TRS.
            // Root bind pose must match the live rig root (it carries the +Z forward yaw).
            var skeleton = new List<SkeletonBone>(skel.NumBones + 1)
            {
                new SkeletonBone { name = root.name, position = root.transform.localPosition, rotation = root.transform.localRotation, scale = Vector3.one },
            };
            Quaternion[] localRot = tPoseCorrection
                ? TPoseLocalRotations(skel)
                : DefaultLocalRotations(skel);
            for (int i = 0; i < skel.NumBones; i++)
            {
                skeleton.Add(new SkeletonBone
                {
                    name = skel.Names[i],
                    // Corrected, not raw: the avatar's declared bone lengths must match the
                    // live rig or retargeting reverts the correction mid-animation.
                    position = skel.LocalPosition(i),
                    rotation = localRot[i],
                    scale = Vector3.one,
                });
            }

            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                // Translation DoF lets clips authored on this skeleton keep their per-bone
                // translation keys through the humanoid retarget; without it those keys are
                // dropped and the feet drift.
                hasTranslationDoF = true,
            };

            return AvatarBuilder.BuildHumanAvatar(root, desc);
        }

        static Quaternion[] DefaultLocalRotations(SplatSkeletonDef skel)
        {
            var r = new Quaternion[skel.NumBones];
            for (int i = 0; i < r.Length; i++) r[i] = skel.LocalRotation(i);
            return r;
        }

        // Arm bone -> its primary child. Flattening each of these directions to horizontal
        // turns the A-pose arms into a T-pose for the avatar's muscle reference.
        static readonly (string bone, string child)[] kArmChains =
        {
            ("upperarm_l", "lowerarm_l"), ("lowerarm_l", "hand_l"),
            ("upperarm_r", "lowerarm_r"), ("lowerarm_r", "hand_r"),
        };

        public static Quaternion[] TPoseLocalRotations(SplatSkeletonDef skel)
        {
            int n = skel.NumBones;
            int[] parents = skel.Parents;
            var localRot = DefaultLocalRotations(skel);
            var localPos = new Vector3[n];
            for (int i = 0; i < n; i++) localPos[i] = skel.LocalPosition(i);

            var worldRot = new Quaternion[n];
            var worldPos = new Vector3[n];
            void FK()
            {
                for (int i = 0; i < n; i++)
                {
                    int p = parents[i];
                    if (p >= 0)
                    {
                        worldRot[i] = worldRot[p] * localRot[i];
                        worldPos[i] = worldPos[p] + worldRot[p] * localPos[i];
                    }
                    else { worldRot[i] = localRot[i]; worldPos[i] = localPos[i]; }
                }
            }
            FK();

            // The lateral (shoulder-to-shoulder) axis, measured from the rig rather than written as
            // a literal. Which world axis is lateral depends on the frame the rest table is turned
            // into (see SplatConvention), and a literal that goes stale flattens the arms into the
            // character's forward/back plane instead, wrecking muscle-zero and every retargeted
            // elbow with it. Shoulder positions are fixed by their parents, so this stays valid
            // across the FK refreshes below.
            int la = skel.IndexOf("upperarm_l"), ra = skel.IndexOf("upperarm_r");
            if (la < 0 || ra < 0) return localRot;
            Vector3 lateral = worldPos[la] - worldPos[ra];
            lateral.y = 0f;
            if (lateral.sqrMagnitude < 1e-8f) return localRot;
            lateral.Normalize();

            foreach (var (boneName, childName) in kArmChains)
            {
                int b = skel.IndexOf(boneName);
                int c = skel.IndexOf(childName);
                if (b < 0 || c < 0) continue;

                Vector3 dCur = (worldPos[c] - worldPos[b]).normalized;
                if (dCur.sqrMagnitude < 1e-8f) continue;
                // Point the bone straight out along the lateral axis, on whichever side it already
                // leans toward. Targeting pure lateral rather than merely flattening to horizontal
                // also straightens the elbow, which an A-pose forearm leans strongly forward from.
                float side = Vector3.Dot(dCur, lateral);
                if (Mathf.Abs(side) < 1e-6f) continue;
                Vector3 dTgt = lateral * Mathf.Sign(side);

                Quaternion delta = Quaternion.FromToRotation(dCur, dTgt);
                worldRot[b] = delta * worldRot[b];
                int par = parents[b];
                localRot[b] = par >= 0 ? Quaternion.Inverse(worldRot[par]) * worldRot[b] : worldRot[b];
                FK(); // refresh child positions before correcting the next bone down the chain
            }
            return localRot;
        }
    }
}
