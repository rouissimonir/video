// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Container for a raw splat capture blob (<c>.spz</c>). The static-capture counterpart of
    /// <see cref="SplatCharacterAsset"/>: assign it to a <see cref="SplatRenderer"/> and the
    /// GPU-ready data is built from it when the component loads.
    /// </summary>
    /// <remarks>
    /// The compressed source, stored verbatim, rather than the packed GPU layout. Converting at
    /// import instead used to turn a 27MB capture into a 432MB project asset - most of it
    /// spherical-harmonic slots the file does not contain - and captures arrive from a backend at
    /// runtime anyway, so the import path was never the primary case.
    /// <para>
    /// A <c>byte[]</c> ScriptableObject rather than a <see cref="TextAsset"/>, for the same reason
    /// as the character asset: a string-constructed TextAsset corrupts a binary blob through UTF-8
    /// re-encoding.
    /// </para>
    /// Hot-reload stable ABI: serialized fields must stay stable.
    /// </remarks>
    public class SplatCaptureAsset : ScriptableObject
    {
        // Internal, not public: FromBytes is the way in, and it validates. See the same field on
        // SplatCharacterAsset. The editor assembly writes it through InternalsVisibleTo.
        [SerializeField, HideInInspector] internal byte[] bytes;

        /// <summary>
        /// Wraps raw <c>.spz</c> bytes as a capture asset, for content downloaded or generated at
        /// runtime rather than imported. Returns null and logs if the bytes are not an .spz
        /// capture.
        /// </summary>
        /// <param name="bytes">The complete contents of an <c>.spz</c> file.</param>
        public static SplatCaptureAsset FromBytes(byte[] bytes)
        {
            // Validated rather than trusted, so a bad download fails here with a plain message
            // instead of deep inside the decompressor with GPU state half built.
            if (!SplatSpzFile.PeekHeader(bytes, out int splatCount, out _) || splatCount <= 0)
            {
                Debug.LogError("SplatCaptureAsset.FromBytes: these bytes are not a version 2 .spz " +
                               "Gaussian splat capture.");
                return null;
            }
            var asset = CreateInstance<SplatCaptureAsset>();
            asset.bytes = bytes;
            asset.name = "SplatCapture (runtime)";
            return asset;
        }
    }
}
