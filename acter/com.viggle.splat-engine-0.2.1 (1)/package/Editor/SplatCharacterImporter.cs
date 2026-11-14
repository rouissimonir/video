// SPDX-License-Identifier: MIT
// Imports Viggle splat character files as a SplatCharacterAsset so they can be dragged into the
// SkinnedSplatRenderer.splatFile slot (Unity's DefaultImporter would otherwise treat them as
// opaque assets). We store the raw bytes in a byte[] ScriptableObject rather than a TextAsset,
// because a binary TextAsset cannot be authored from a ScriptedImporter before Unity 6.
// See SplatCharacterAsset for details.
//
// TWO EXTENSIONS, ON PURPOSE
//
// .vsplat is the canonical one. .bin is still accepted because existing projects and the export
// pipeline are full of them, and breaking those on an upgrade would be worse than the cost of
// carrying a second extension for a while.
//
// They are NOT treated the same, though, and the difference matters. A ScriptedImporter claims an
// extension GLOBALLY: registering "bin" means every unrelated .bin in the host project - a shader
// cache, a model blob, someone's serialized data - gets imported as a splat character. That is why
// a .bin whose magic does not match is only a warning and still imports as an inert asset: it is
// probably not ours and complaining loudly about someone else's file is obnoxious. A .vsplat with
// the wrong magic IS an error, because nothing else in the world writes that extension.
//
// The extension decides how loudly to complain about bytes we do not recognise, and NOTHING else.
// Which container a file is, and therefore which reader decodes it, comes from the magic via
// SplatCharacterBin - so either format imports under either extension. This importer used to carry
// its own copy of the VFCHARB1 magic and its own byte compare, which is exactly how it came to
// reject a format the runtime reader had already learned: a VIGGCHAR bundle landed in the .bin
// warning path above and imported as an inert asset with no rig and no Avatar.

using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    [ScriptedImporter(kVersion, new[] { kCanonicalExt, kLegacyExt })]
    public class SplatCharacterImporter : ScriptedImporter
    {
        // Bump to force Unity to re-run the import on every existing asset. Raised to 3 when
        // .vsplat was added; to 4 when thumbnails arrived, because Unity caches asset previews and
        // will not regenerate them just because a new editor script appeared - already-imported
        // files would keep showing the default icon forever; to 5 when the Humanoid Avatar started
        // being baked here; to 6 when the main object became the rig prefab, since an
        // already-imported file has neither the prefab nor the baked thumbnail; to 7 to rebuild
        // prefabs that captured a DISABLED renderer, because AddComponent fired OnEnable before
        // the character file was assigned and the component switched itself off; to 8 when
        // SkinnedSplatRenderer became a SplatRenderer instead of forwarding to a hidden second
        // component, since a prefab emitted before that serialized both of them; to 9 when the
        // VIGGCHAR container was added, because a .vsplat or .bin already in the project was
        // imported by a build that did not recognise it and is sitting there as an inert asset;
        // to 10 when a corrected file's own joints started driving the rest pose, since the bones
        // and the Avatar are baked into the prefab and an existing one carries the canonical rig;
        // to 11 when the shader references stopped being left to Reset and were resolved
        // explicitly in BuildPrefab, since an artifact cached before that carries whatever Reset
        // happened to fill in.
        const int kVersion = 11;

        const string kCanonicalExt = "vsplat";
        const string kLegacyExt = "bin";

        /// <summary>
        /// How this character's animation clips bind to its bones. The same choice Unity puts on a
        /// model importer's Rig tab, in the same place: on the file, not on a scene component.
        /// </summary>
        [Tooltip("Humanoid: bake an Avatar so any Humanoid clip retargets onto this character " +
                 "(approximate). Generic: play clips authored on this exact skeleton, bone for " +
                 "bone (exact, no retargeting). None: build the bones but add no Animator - you " +
                 "drive them yourself from script, physics or IK. Note this is NOT Unity's Rig-tab " +
                 "None, which means no rig at all; a splat character always keeps its bones.")]
        public SplatRigMode rigMode = SplatRigMode.Humanoid;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            byte[] bytes = File.ReadAllBytes(ctx.assetPath);

            // The shared check, not a local one: whatever SplatCharacterBin.Parse will accept at
            // load is what imports here, so the two can never drift apart again.
            if (!SplatCharacterBin.HasMagic(bytes))
            {
                string ext = Path.GetExtension(ctx.assetPath).TrimStart('.').ToLowerInvariant();
                string msg = $"'{Path.GetFileName(ctx.assetPath)}' is not a Viggle splat character " +
                             $"(expected the file to start with {SplatCharacterMagic.SupportedMagics}, " +
                             $"found '{SplatCharacterMagic.Describe(bytes)}').";
                if (ext == kCanonicalExt)
                    ctx.LogImportError(msg + " A .vsplat file must be a character bundle.");
                else
                    // Almost certainly somebody else's .bin. Import it as an inert asset and say so
                    // once, rather than failing an import the host never asked us to perform.
                    ctx.LogImportWarning(msg + " Importing as an empty asset; rename it or move it " +
                                         "out of the project if it is not a splat character.");
            }

            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);

            var asset = ScriptableObject.CreateInstance<SplatCharacterAsset>();
            asset.bytes = bytes;
            asset.rigMode = rigMode;
            asset.name = name;
            // Keep the identifier "main" even though this is no longer the main object: a
            // ScriptedImporter derives each sub-asset's local file ID from this string, so changing
            // it would break every scene and prefab already referencing the character.
            ctx.AddObjectToAsset("main", asset);

            // The rig this character poses on, read off the file: its bone count picks the bundled
            // table, and a corrected file's own joints are merged over that table's rest
            // translations. Both the prefab's bones and the Avatar are built from it here, so a
            // character whose rest pose was only merged at load would still be rigged canonically.
            var rest = SplatCharacterBin.PeekRest(bytes);
            var skel = rest.numBones > 0 ? SplatSkeleton.For(rest) : null;

            if (rigMode == SplatRigMode.Humanoid && skel != null)
            {
                var avatar = BuildAvatar(ctx, skel);
                if (avatar != null)
                {
                    asset.avatar = avatar;
                    ctx.AddObjectToAsset("avatar", avatar);
                }
            }

            // The prefab is the main object, so dragging a .vsplat into a scene gives a rigged,
            // animatable character - the same thing dragging an FBX gives. Unity's model importer
            // works exactly this way: GameObject as main object, mesh and avatar as sub-assets.
            var root = BuildPrefab(name, asset, skel);
            ctx.AddObjectToAsset("root", root, RenderThumbnail(bytes, out string decodeError));
            // A file whose MAGIC is ours but whose payload will not decode used to import in
            // silence: the thumbnail attempt swallowed the exception, and the only symptom was a
            // character with no bones. The parse has happened either way, so its failure is
            // reported rather than discarded.
            if (decodeError != null)
                ctx.LogImportError($"'{Path.GetFileName(ctx.assetPath)}' is a splat character but does " +
                                   $"not decode, so it will not render: {decodeError}");
            ctx.SetMainObject(root);
        }

        // Assemble what a user would otherwise have to build by hand: the renderer, the bone
        // hierarchy, and an Animator already bound to the baked avatar.
        static GameObject BuildPrefab(string name, SplatCharacterAsset asset, SplatSkeletonDef skel)
        {
            var root = new GameObject(name);
            var renderer = root.AddComponent<SkinnedSplatRenderer>();
            renderer.splatFile = asset;
            // Explicitly, rather than trusting AddComponent to fire Reset: the shader references
            // it resolves - the skinning compute among them - are the ones a player build reads,
            // and an artifact that came out of here with any of them null is a character that
            // renders in the editor and draws nothing in a player.
            renderer.AutoResolve();

            if (skel == null)
                return root;   // unreadable file; ship the component so the error is visible in-scene

            // Same builder the runtime uses. The two hierarchies must agree bone for bone, because
            // the avatar's HumanDescription names its root and every bone and the Animator resolves
            // those names against whichever rig is actually present.
            var rig = SplatRigBuilder.Build(root.transform, skel,
                                            asset.rigMode == SplatRigMode.Generic);
            renderer.rigRoot = rig.root;
            renderer.bones = rig.bones;
            renderer.bonePrefix = rig.prefix;

            if (asset.rigMode != SplatRigMode.None)
            {
                // On the rig root rather than on the character root, and deliberately: the avatar
                // bakes its root entry as the name "SplatRig", which SplatRigBuilder guarantees.
                // A user renaming the character GameObject would otherwise break avatar binding.
                var animator = rig.root.gameObject.AddComponent<Animator>();
                animator.avatar = asset.avatar;              // null in Generic mode
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.keepAnimatorStateOnDisable = true;
            }
            return root;
        }

        // Project-window icon. Handed over explicitly because the thumbnail no longer follows from
        // a custom editor: Unity asks the editor for the MAIN asset object, and that is now a
        // GameObject, whose preview renders in an isolated scene with no URP feature and no compute
        // dispatch - so a splat character would draw nothing. This overload sidesteps that.
        /// <param name="decodeError">
        /// Why the file would not decode, or null. Only the PARSE is reported: a thumbnail that
        /// fails to render for any other reason is never worth a line in the console, let alone
        /// failing an import over.
        /// </param>
        static Texture2D RenderThumbnail(byte[] bytes, out string decodeError)
        {
            const long kMaxBytes = 64L * 1024 * 1024;   // very large characters keep the default icon
            const int kSamples = 20000;
            decodeError = null;
            if (bytes == null || bytes.LongLength == 0 || bytes.LongLength > kMaxBytes)
                return null;
            SplatCharacterBinData bin;
            try
            {
                bin = SplatCharacterBin.Parse(bytes);
            }
            catch (System.Exception e)
            {
                decodeError = e.Message;
                return null;
            }
            try
            {
                var snap = SplatPreviewRenderer.Capture(bin, kSamples);
                if (snap == null) return null;
                var tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                // A slight yaw off dead-on reads as 3D at icon size; straight-on looks flat.
                SplatPreviewRenderer.Render(tex, snap, Quaternion.Euler(0f, -20f, 0f));
                return tex;
            }
            catch
            {
                return null;
            }
            finally
            {
                bin.Dispose();
            }
        }

        // Bake the Humanoid Avatar as a sub-asset, exactly as Unity's model importer stores the
        // Avatar it generates from an FBX. Doing it here rather than at load means one avatar per
        // character file instead of one per instance, and it is why SplatRigBuilder exists: the
        // hierarchy this throws away has to match the live rig bone for bone, or the Animator
        // silently fails to bind.
        static Avatar BuildAvatar(AssetImportContext ctx, SplatSkeletonDef skel)
        {
            // HideAndDontSave keeps the throwaway hierarchy out of the open scene: an importer runs
            // while a scene is loaded, and a plain GameObject would appear in it.
            var rig = SplatRigBuilder.Build(null, skel, genericPrefix: false, HideFlags.HideAndDontSave);
            try
            {
                // The rig root sits at the origin here, which is the state SplatHumanoidAvatar.Build
                // wants: it copies root.localPosition into the HumanDescription as the root's bind
                // pose, and any offset baked there is applied a second time by the live transform.
                var avatar = SplatHumanoidAvatar.Build(rig.root.gameObject, skel);
                if (avatar == null || !avatar.isValid)
                {
                    ctx.LogImportWarning("Could not build a valid Humanoid Avatar from this " +
                                         "character's rig. It will render, but Humanoid clips will " +
                                         "not retarget onto it.");
                    if (avatar != null) DestroyImmediate(avatar);
                    return null;
                }
                avatar.name = "Avatar";
                return avatar;
            }
            finally
            {
                // An importer that leaks a GameObject leaks it into the user's open scene.
                DestroyImmediate(rig.root.gameObject);
            }
        }
    }
}
