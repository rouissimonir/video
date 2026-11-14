// SPDX-License-Identifier: MIT
// Imports a static Gaussian splat capture (.spz or .ply) as a SplatCaptureAsset plus a
// ready-to-drag prefab, the same story as .vsplat for characters: drop the file in the project and
// it works.
//
// IT STORES THE FILE, IT DOES NOT CONVERT IT
//
// This importer used to decode the capture and pack it into the GPU layout right here. That was
// the wrong place, and expensively so: the packed layout is chosen for the GPU, not for disk, and
// it stores a fixed number of spherical-harmonic slots whether or not the source has them. A
// 27MB, 1.92M-splat, degree-0 capture came out of it as a 432MB project asset, 370MB of which was
// zeroes standing in for harmonics the file never contained.
//
// So it now does what the character importer does: keep the bytes, emit a prefab, and let
// SplatRenderer build the GPU data on load. The same code path then serves captures downloaded at
// runtime, which is how they actually arrive, and which the import path never covered.
//
// A .ply IS CONVERTED TO .spz BYTES, AND ONLY THAT
//
// A .ply cannot be stored verbatim: it is uncompressed float32 at up to 248 bytes a splat, ten
// times the size of the same .spz, so a 476MB project asset AND a slow load, in both directions at
// once. So it is converted at import - into .spz bytes, NOT into the packed GPU layout. That
// distinction is the whole design: the packed layout is the import-time pipeline that was deleted,
// and reintroducing it would mean a second asset type and a second runtime path. Converting to
// .spz instead means a .ply and an .spz of the same capture produce byte-identical
// SplatCaptureAsset contents and are indistinguishable from here on.
//
// It costs precision on rotation and scale, which .spz stores at 8 bits each against the source's
// float32. Positions are effectively unaffected - 24-bit fixed point is finer than the Norm11
// quantisation the runtime packer applies afterwards - and colour and opacity were already 8-bit
// by the time anything looked at them.
//
// TWO EXTENSIONS, TREATED DIFFERENTLY, FOR THE SAME REASON AS THE CHARACTER IMPORTER
//
// A ScriptedImporter claims an extension GLOBALLY, and most .ply files in the world are somebody's
// LiDAR scan or photogrammetry export rather than a splat capture. So a .ply that is not one is a
// WARNING and an inert asset with no prefab: it is probably not ours, and complaining loudly about
// someone else's mesh is obnoxious. The probe that decides is the header alone - a foreign .ply
// can be hundreds of megabytes and none of it is read to find out it is not a capture.
//
// .spz is the opposite case, and is why the error handling for it is blunt: the format exists only
// to carry Gaussian splats, so anything wrong with one IS an error worth stopping on. Either way
// no prefab is emitted for a file that did not read - a prefab that draws nothing and logs at play
// time is worse than no prefab at all.

using System;
using System.Diagnostics;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Viggle.SplatEngine.Editor
{
    /// <summary>
    /// Imports an <c>.spz</c> or <c>.ply</c> static Gaussian splat capture. Emits a prefab as the
    /// main object, exactly as Unity's model importer emits one for an FBX.
    /// </summary>
    [ScriptedImporter(kVersion, new[] { kSpzExt, kPlyExt })]
    public class SplatCaptureImporter : ScriptedImporter
    {
        // Bump to force Unity to re-run the import on every existing asset. Raised to 4 when the
        // importer stopped converting the capture: an asset imported before that carries packed
        // GPU blobs and a SplatAsset the renderer no longer looks for, so every one of them has
        // to be rebuilt; to 5 when SplatRenderer stopped being a wrapper around a second hidden
        // component, since a prefab emitted before that serialized both of them; to 6 when .ply
        // was claimed again, so any .ply already sitting in a project is looked at; to 7 when the
        // shader references stopped being left to Reset and were resolved explicitly below, since
        // an artifact cached before that carries whatever Reset happened to fill in.
        const int kVersion = 7;

        const string kSpzExt = "spz";
        const string kPlyExt = "ply";

        // The Project-window thumbnail is baked DURING import, so it gets a modest budget.
        const int kThumbnailSamples = 20000;

        // Splats per block on the .ply path. 8192 keeps the staging buffers around 2MB, which is
        // small enough to stay in cache and large enough that the per-block overhead vanishes.
        const int kPlyBlockSplats = 8192;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            bool isPly = Path.GetExtension(ctx.assetPath).Equals("." + kPlyExt,
                                                                 StringComparison.OrdinalIgnoreCase);

            byte[] bytes = isPly ? ConvertPly(ctx) : File.ReadAllBytes(ctx.assetPath);
            if (bytes == null)
            {
                // ConvertPly already said why. An inert asset and no prefab, so a foreign mesh
                // sitting in the project costs one warning rather than a broken GameObject.
                ctx.AddObjectToAsset("main", ScriptableObject.CreateInstance<SplatCaptureAsset>());
                return;
            }

            // The .ply path re-reads its own output here, deliberately. It is one gunzipped header,
            // and it is the only check in the system that the encoder wrote something the runtime
            // reader accepts - nothing downstream validates the layout, it just renders wrong.
            if (!SplatSpzFile.PeekHeader(bytes, out int splatCount, out int shDegree) || splatCount <= 0)
            {
                ctx.LogImportError(isPly
                    ? $"'{Path.GetFileName(ctx.assetPath)}' converted to .spz bytes that the " +
                      "capture reader will not read back. This is a bug in the converter, not in " +
                      "the file."
                    : $"'{Path.GetFileName(ctx.assetPath)}' is not a version 2 .spz " +
                      "Gaussian splat capture, so it was imported as an empty asset.");
                ctx.AddObjectToAsset("main", ScriptableObject.CreateInstance<SplatCaptureAsset>());
                return;
            }

            var asset = ScriptableObject.CreateInstance<SplatCaptureAsset>();
            asset.bytes = bytes;
            asset.name = name;
            ctx.AddObjectToAsset("main", asset);

            // The prefab is the main object, so dragging a capture into a scene draws it. Same
            // shape as the character importer, and as Unity's own model importer: GameObject as
            // main object, data as a sub-asset.
            var root = new GameObject(name);
            var renderer = root.AddComponent<SplatRenderer>();
            renderer.splatFile = asset;
            // Explicitly, rather than trusting AddComponent to fire Reset: the shader references
            // it resolves are the ones a player build reads, and an artifact that came out of
            // here with any of them null is a splat that renders in the editor and draws nothing
            // in a player. Cheap when they are already filled - Resolve returns on the first
            // non-null field.
            renderer.AutoResolve();
            ctx.AddObjectToAsset("root", root, RenderThumbnail(bytes));
            ctx.SetMainObject(root);

            // Not a warning, just the one thing about a capture worth knowing before it is in a
            // scene. A degree-0 file has no view-dependent shading to lose, and costs nothing to
            // store or read for it; anything higher pays 32 bytes a splat.
            if (SplatLog.Info)
                Debug.Log($"[SplatImport] {name}: {splatCount:N0} splats, SH degree {shDegree}");
        }

        // Reads a .ply Gaussian capture and re-encodes it as .spz bytes. Null - having already
        // reported why - when the file is not a capture, which for this extension is the expected
        // case rather than the exceptional one.
        byte[] ConvertPly(AssetImportContext ctx)
        {
            string file = Path.GetFileName(ctx.assetPath);

            // Header only: a foreign .ply is hundreds of megabytes and none of it is read to find
            // out that it has no f_dc_0.
            string why = SplatPlyFile.DescribeUnreadable(ctx.assetPath);
            if (why != null)
            {
                ctx.LogImportWarning($"'{file}' is not a Gaussian splat capture - {why}. Importing " +
                                     "as an empty asset with no prefab. Move it out of the project " +
                                     "if it is somebody else's mesh.");
                return null;
            }

            try
            {
                var timer = Stopwatch.StartNew();
                using var ply = SplatPlyFile.Open(ctx.assetPath);
                var encoder = new SplatSpzEncoder(ply.splatCount, ply.shDegree);
                var block = new float[kPlyBlockSplats * ply.strideFloats];
                int got;
                while ((got = ply.ReadBlock(block, kPlyBlockSplats)) > 0)
                    encoder.AddBlock(block, got);
                byte[] bytes = encoder.Finish(out int fractionalBits);

                if (SplatLog.Info)
                    Debug.Log($"[SplatImport] {file}: converted {ply.splatCount:N0} splats to .spz " +
                              $"in {timer.ElapsedMilliseconds:N0} ms - " +
                              $"{new FileInfo(ctx.assetPath).Length / (1024f * 1024f):0.#} MB source, " +
                              $"{bytes.Length / (1024f * 1024f):0.#} MB stored, SH degree " +
                              $"{ply.shDegree}, {fractionalBits} fractional bits");
                return bytes;
            }
            catch (Exception e)
            {
                // The header said it was a capture, so getting here is a real failure rather than
                // a foreign file, and it is loud.
                ctx.LogImportError($"'{file}' looked like a Gaussian splat capture but could not be " +
                                   $"converted: {e.Message}");
                return null;
            }
        }

        // Project-window icon, handed over explicitly: Unity asks the main object's editor for a
        // preview, and the main object is a GameObject, whose preview renders in an isolated scene
        // with no URP feature and no compute dispatch - so a splat capture would draw nothing
        // there. Unlike before, this costs a decode of its own: the import no longer decodes the
        // capture for any other reason. It is one gunzip and a strided sample, which is cheap
        // beside the packing pass it replaced.
        static Texture2D RenderThumbnail(byte[] bytes)
        {
            const long kMaxBytes = 256L * 1024 * 1024;   // very large captures keep the default icon
            try
            {
                if (bytes.LongLength > kMaxBytes)
                    return null;
                if (!SplatSpzFile.SamplePoints(bytes, kThumbnailSamples, out var positions, out var colors,
                                               out var boundsMin, out var boundsMax))
                    return null;
                var snap = SplatPreviewRenderer.Capture(positions, colors, boundsMin, boundsMax);
                if (snap == null)
                    return null;
                var tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                // A slight yaw off dead-on reads as 3D at icon size; straight-on looks flat.
                SplatPreviewRenderer.Render(tex, snap, Quaternion.Euler(0f, -20f, 0f));
                return tex;
            }
            catch
            {
                // A thumbnail is never worth failing an import over.
                return null;
            }
        }
    }
}
