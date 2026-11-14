// SPDX-License-Identifier: MIT
// The one place a splat character's bone hierarchy is built.
//
// Three callers need the same hierarchy for different reasons: SkinnedSplatRenderer builds it at
// load to skin against, SplatCharacterImporter builds a throwaway copy at import time to derive the
// Humanoid Avatar from, and SplatBindPoseBaker builds one to sample the bind-pose clip onto. The
// three MUST agree exactly. The Avatar's HumanDescription names its root and every bone, and the
// Animator later resolves those names against the live rig, so a difference as small as the root
// node's name or a missing path-prefix node yields an avatar that silently refuses to bind.

using UnityEngine;

namespace Viggle.SplatEngine
{
    internal static class SplatRigBuilder
    {
        // The rig root's name. It is baked into the Avatar as HumanDescription.skeleton[0], so the
        // live rig root has to carry the same name for the Animator to bind to it.
        public const string RootName = "SplatRig";

        // Generic mode: a node inserted above the root bone so the rig's transform paths match the
        // clips' curve paths ("Armature/root/pelvis/..."). SplatSkeletonBindPose is baked with the
        // same prefix.
        public const string GenericPathPrefix = "Armature";

        /// The hierarchy <see cref="Build"/> produced.
        public struct Rig
        {
            /// The "SplatRig" node. Destroying this GameObject destroys the whole rig.
            public Transform root;
            /// The <see cref="GenericPathPrefix"/> node when one was inserted; null otherwise.
            public Transform prefix;
            /// Bone Transforms, indexed like the skeleton's Names/Parents.
            public Transform[] bones;
        }

        /// <summary>
        /// Builds root -> [prefix] -> bones under <paramref name="parent"/>, which may be null for
        /// a detached hierarchy. <paramref name="hideFlags"/> is applied to every node created;
        /// pass <see cref="HideFlags.HideAndDontSave"/> to build a rig that never touches the open
        /// scene (edit-time callers), and leave it at None for a rig that lives in the scene.
        /// </summary>
        public static Rig Build(Transform parent, SplatSkeletonDef skel, bool genericPrefix,
                                HideFlags hideFlags = HideFlags.None)
        {
            var rig = new Rig { bones = new Transform[skel.NumBones] };
            rig.root = NewNode(RootName, parent, hideFlags);
            // Do not yaw the root node. The +Z forward turn lives in SplatSkeleton's root BONE,
            // below here, so the Avatar is built from it; a yaw on the rig root is invisible to
            // HumanDescription and retargeting re-applies it, landing animated Humanoid characters
            // 90 degrees off their own rest pose.

            Transform boneParent = rig.root;
            if (genericPrefix)
            {
                rig.prefix = NewNode(GenericPathPrefix, rig.root, hideFlags);
                boneParent = rig.prefix;
            }

            for (int i = 0; i < skel.NumBones; i++)
            {
                int p = skel.Parents[i];
                var t = NewNode(skel.Names[i], p >= 0 ? rig.bones[p] : boneParent, hideFlags);
                t.localPosition = skel.LocalPosition(i);
                t.localRotation = skel.LocalRotation(i);
                t.localScale = Vector3.one;
                rig.bones[i] = t;
            }
            return rig;
        }

        static Transform NewNode(string name, Transform parent, HideFlags hideFlags)
        {
            var go = new GameObject(name) { hideFlags = hideFlags };
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
