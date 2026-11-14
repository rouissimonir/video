// SPDX-License-Identifier: MIT
//
// Keeps Resources/ViggleSplatShaders.asset - the references a runtime-added renderer is handed in
// a player build, see SplatShaderResources - pointing at the right nine assets.
//
// This runs for whoever is DEVELOPING the package, not for whoever installs it. A package
// installed from a tarball, a git URL or a registry lives in Library/PackageCache and is
// immutable, so there is nothing to rebuild there and nothing that could write it; the asset is
// authored here and ships already populated. The writable check below is what keeps a consumer
// from ever seeing this fire, and the error it logs when the asset is short in an immutable
// install is the only useful thing left to say: the package arrived incomplete.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Viggle.SplatEngine.Editor
{
    [InitializeOnLoad]
    internal static class SplatShaderResourcesBuilder
    {
        const string kMenu = "Tools/Viggle Splat Engine/Rebuild Shader Resources";

        // Found rather than hard-coded: the package can be vendored under Assets/, embedded, or
        // in PackageCache, and only the asset's own GUID is stable across all three.
        const string kAssetGuid = "764edfe6c61640b9a4a8eac4365464e6";

        // A shader that is renamed or moved breaks this asset, and the breakage only shows up in
        // a player. Checking on editor load costs one asset load and nine null tests.
        static SplatShaderResourcesBuilder() => EditorApplication.delayCall += VerifyOrRebuild;

        /// <summary>Re-resolves every reference from scratch. Run it after moving or renaming a
        /// shader in this package.</summary>
        [MenuItem(kMenu)]
        internal static void Rebuild() => Build(announce: true);

        static void VerifyOrRebuild()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += VerifyOrRebuild;
                return;
            }
            var asset = Load(out string path);
            if (asset != null && asset.IsComplete) return;

            if (IsWritable(path)) { Build(announce: false); return; }

            Debug.LogError(
                $"Viggle Splat Engine: {Describe(path)} is missing or incomplete, and the package " +
                "is installed read-only so it cannot be repaired here. A splat renderer created " +
                "at runtime will have no shaders in a player build. Reinstall the package as " +
                "shipped.", asset);
        }

        static void Build(bool announce)
        {
            var asset = Load(out string path);
            if (asset == null)
            {
                // Only ever reached in this repository or in a copy vendored under Assets/: the
                // shipped package always carries the asset, and IsWritable gated the way here.
                path = string.IsNullOrEmpty(path) ? DefaultPath() : path;
                if (path == null)
                {
                    Debug.LogError("Viggle Splat Engine: cannot find the package's Runtime folder, " +
                                   "so there is nowhere to write the shader resource asset.");
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                asset = ScriptableObject.CreateInstance<SplatShaderResources>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var missing = new List<string>();
            asset.splatUtilities = Find<ComputeShader>("SplatUtilities", missing);
            asset.deviceRadixSort = Find<ComputeShader>("DeviceRadixSort", missing);
            asset.shaderSplats = Find<Shader>("RenderGaussianSplats", missing);
            asset.shaderComposite = Find<Shader>("GaussianComposite", missing);
            asset.shaderDebugPoints = Find<Shader>("GaussianDebugRenderPoints", missing);
            asset.shaderDebugBoxes = Find<Shader>("GaussianDebugRenderBoxes", missing);
            asset.shaderSplatsNoCompute = Find<Shader>("RenderGaussianSplatsNoCompute", missing);
            asset.shaderCompositeNoCompute = Find<Shader>("GaussianCompositeNoCompute", missing);
            asset.skinningCompute = Find<ComputeShader>("SplatSkinning", missing);

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            if (missing.Count > 0)
                Debug.LogError(
                    $"{path}: could not resolve " + string.Join(", ", missing) +
                    ". A package built now ships a renderer that cannot draw when it is created " +
                    "at runtime. Check that these assets are still in the package under those " +
                    "names, then run " + kMenu + " again.", asset);
            else if (announce)
                Debug.Log($"{path}: all nine shader references resolved.", asset);
        }

        static SplatShaderResources Load(out string path)
        {
            path = AssetDatabase.GUIDToAssetPath(kAssetGuid);
            return string.IsNullOrEmpty(path)
                ? null : AssetDatabase.LoadAssetAtPath<SplatShaderResources>(path);
        }

        // Where the asset goes when it does not exist yet, derived from this script's own location
        // so a vendored or renamed package still lands it in the right Runtime/Resources folder.
        static string DefaultPath()
        {
            foreach (string guid in AssetDatabase.FindAssets("SplatShaderResources t:MonoScript"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) != "SplatShaderResources") continue;
                return Path.GetDirectoryName(p).Replace('\\', '/')
                       + "/Resources/" + SplatShaderResources.kResourceName + ".asset";
            }
            return null;
        }

        // Embedded packages and a copy vendored under Assets/ can be written to; everything the
        // Package Manager fetched - registry, git, tarball - is immutable. A local folder
        // dependency (file:../..) is how this repository's own sample projects consume the
        // package, and is the case this whole file exists to serve.
        static bool IsWritable(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;   // nothing there yet; try
            // Fully qualified: UnityEditor has a PackageInfo of its own, from the Asset Store
            // importer, and a `using UnityEditor.PackageManager` makes the name ambiguous.
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if (info == null) return true;                 // under Assets/
            return info.source == UnityEditor.PackageManager.PackageSource.Embedded
                || info.source == UnityEditor.PackageManager.PackageSource.Local;
        }

        static string Describe(string path) =>
            string.IsNullOrEmpty(path)
                ? $"Resources/{SplatShaderResources.kResourceName}.asset" : path;

        // Mirrors SplatRenderer.Resolve: find by name and type, then reject any hit whose file
        // name is not exactly the name asked for, because FindAssets matches substrings and
        // "SplatSkinning" also hits "SplatSkinningJobs".
        static T Find<T>(string assetName, List<string> missing) where T : Object
        {
            foreach (string guid in AssetDatabase.FindAssets($"{assetName} t:{typeof(T).Name}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != assetName) continue;
                var found = AssetDatabase.LoadAssetAtPath<T>(path);
                if (found != null) return found;
            }
            missing.Add($"{assetName} ({typeof(T).Name})");
            return null;
        }
    }
}
