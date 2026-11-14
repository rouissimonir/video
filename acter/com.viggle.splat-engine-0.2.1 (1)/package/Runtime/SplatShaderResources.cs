// SPDX-License-Identifier: MIT
//
// Why this exists: SplatRenderer's nine shader references are resolved by AutoResolve, which
// searches the AssetDatabase and therefore only runs in the editor. A renderer that reaches a
// player as part of a prefab or an import artifact is fine - the references were resolved when
// it was authored and they travel with the serialized object. A renderer created at runtime with
// AddComponent has never been serialized by anything, so in a player every field is null and
// Setup used to find no SplatUtilities.compute and switch the component off. A
// ComputeShader cannot be looked up by name at runtime the way Shader.Find looks a shader up, so
// there is no fallback; that asymmetry is why this fails hard rather than degrading.
//
// The asset next door under Resources/ is the fix, and it ships inside the package: a package
// installed from a tarball or a git URL lives in Library/PackageCache and is immutable, so
// nothing can write this asset into the consumer's project at their edit time and no consumer
// setup step can be required. Authored once, here; the references are GUIDs and the package's
// own .meta GUIDs travel in the tarball, so they resolve on any machine.

using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// The package's own shaders and compute shaders, held as real serialized references in an
    /// asset that ships inside the package under <c>Resources/</c>, so a player build carries
    /// them and a renderer created at runtime can be handed them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Being under Resources does two jobs at once. It makes the asset loadable by name at
    /// runtime, and it puts every shader it points at into the build whether or not any other
    /// serialized object happens to reference the same one - which also heals a renderer whose
    /// own fields are stale, such as an import artifact cached before the no-compute pair
    /// existed.
    /// </para>
    /// <para>
    /// Maintained by SplatShaderResourcesBuilder (Tools &gt; Viggle Splat Engine &gt; Rebuild
    /// Shader Resources), which only runs where the package is writable - this repository. See
    /// its header for why a consumer never runs it.
    /// </para>
    /// </remarks>
    internal class SplatShaderResources : ScriptableObject
    {
        // Not "SplatEngineResources": a host project may already carry an asset of its own under
        // that name - VigmonUnity does, from before this shipped - and Resources.Load resolves by
        // name across every Resources folder in the build. Named after the settings asset instead,
        // which is the package's other Resources-loaded singleton.
        internal const string kResourceName = "ViggleSplatShaders";

        // Spelled exactly as SplatRenderer spells them, so the pairing is checkable by eye.
        public ComputeShader splatUtilities;
        public ComputeShader deviceRadixSort;
        public Shader shaderSplats;
        public Shader shaderComposite;
        public Shader shaderDebugPoints;
        public Shader shaderDebugBoxes;
        public Shader shaderSplatsNoCompute;
        public Shader shaderCompositeNoCompute;

        /// <summary>SkinnedSplatRenderer's own, resolved from "SplatSkinning".</summary>
        public ComputeShader skinningCompute;

        /// <summary>True when every slot is filled. The builder re-runs itself when it is not.</summary>
        internal bool IsComplete =>
            splatUtilities != null && deviceRadixSort != null && shaderSplats != null &&
            shaderComposite != null && shaderDebugPoints != null && shaderDebugBoxes != null &&
            shaderSplatsNoCompute != null && shaderCompositeNoCompute != null &&
            skinningCompute != null;

        static SplatShaderResources s_Instance;
        static bool s_Loaded;      // distinguishes "not looked yet" from "looked, found nothing"
        static bool s_Warned;

        // Loaded once and cached. A miss is cached too: Resources.Load on every renderer that
        // comes up short would be a per-spawn cost paid to re-learn the same absence.
        static SplatShaderResources Load()
        {
            if (s_Loaded) return s_Instance;
            s_Loaded = true;
            s_Instance = Resources.Load<SplatShaderResources>(kResourceName);
            return s_Instance;
        }

        /// <summary>
        /// Fills in whatever shader references <paramref name="renderer"/> is still missing.
        /// Called from TrySetup for every renderer, which for one created at runtime is the only
        /// thing that ever populates those fields.
        /// </summary>
        /// <remarks>
        /// A slot that already holds something is left alone. A renderer that came from a prefab
        /// or an import artifact carries what the editor resolved for that asset, and that is the
        /// authority for it - a project that vendored the package somewhere else has references
        /// pointing at its own copy, and overwriting them with the package's would swap the
        /// shaders under it.
        /// </remarks>
        internal static void Apply(SplatRenderer renderer)
        {
            if (renderer == null) return;
            var res = Load();
            if (res == null) { WarnMissing(renderer); return; }

            if (renderer.splatUtilities == null) renderer.splatUtilities = res.splatUtilities;
            if (renderer.deviceRadixSort == null) renderer.deviceRadixSort = res.deviceRadixSort;
            if (renderer.shaderSplats == null) renderer.shaderSplats = res.shaderSplats;
            if (renderer.shaderComposite == null) renderer.shaderComposite = res.shaderComposite;
            if (renderer.shaderDebugPoints == null) renderer.shaderDebugPoints = res.shaderDebugPoints;
            if (renderer.shaderDebugBoxes == null) renderer.shaderDebugBoxes = res.shaderDebugBoxes;
            if (renderer.shaderSplatsNoCompute == null) renderer.shaderSplatsNoCompute = res.shaderSplatsNoCompute;
            if (renderer.shaderCompositeNoCompute == null) renderer.shaderCompositeNoCompute = res.shaderCompositeNoCompute;
            // Guarded by type: skinningCompute is declared on the skinned subclass.
            if (renderer is SkinnedSplatRenderer skinned && skinned.skinningCompute == null)
                skinned.skinningCompute = res.skinningCompute;
        }

        // Said once, and only when it is actually about to cost something: in the editor
        // AutoResolve has already filled the fields from the AssetDatabase, so a missing asset
        // there is a packaging fault that has not bitten yet and does not deserve a line per
        // spawn.
        static void WarnMissing(SplatRenderer renderer)
        {
            if (s_Warned || renderer.splatUtilities != null || !SplatLog.Warning) return;
            s_Warned = true;
            Debug.LogWarning(
                $"Viggle Splat Engine: Resources/{kResourceName}.asset is missing from the " +
                "package, so a splat renderer created at runtime has no shaders and will refuse " +
                "to draw. The package was copied or repacked without it; reinstall the package " +
                "as shipped.", renderer);
        }

        /// <summary>Drops the cached asset. Call when tearing the engine down.</summary>
        internal static void ResetStatics()
        {
            s_Instance = null;
            s_Loaded = false;
            s_Warned = false;
        }
    }
}
