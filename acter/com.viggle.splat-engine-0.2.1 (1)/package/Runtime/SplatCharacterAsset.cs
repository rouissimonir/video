// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Container for a raw splat character blob (.vsplat, or a legacy .bin).
    /// </summary>
    /// <remarks>
    /// A plain byte[] ScriptableObject rather than a TextAsset: the TextAsset constructors that
    /// take binary data are Unity 6+, and a string-constructed TextAsset corrupts a binary blob
    /// through UTF-8 re-encoding. A byte[] serializes identically on every supported version.
    /// Hot-reload stable ABI: serialized fields must stay stable.
    /// </remarks>
    public class SplatCharacterAsset : ScriptableObject
    {
        // Internal, not public: FromBytes is the way in, and it validates. This used to be a
        // public mutable byte[] on a ScriptableObject the importer owns, so the unvalidated path
        // FromBytes exists to replace was one assignment away from it. The editor assembly writes
        // and reads it through InternalsVisibleTo.
        [SerializeField, HideInInspector] internal byte[] bytes;

        /// <summary>
        /// How this character's clips bind to its bones. Chosen on the importer's Rig Mode, the
        /// same way a mesh chooses Humanoid or Generic on the model importer's Rig tab.
        /// </summary>
        [HideInInspector] public SplatRigMode rigMode = SplatRigMode.Humanoid;

        /// <summary>
        /// The Humanoid Avatar built at import, stored as a sub-asset of the character file so
        /// every instance shares one. Null in Generic and None mode, and on a file created from
        /// raw bytes at runtime rather than imported.
        /// </summary>
        [HideInInspector] public Avatar avatar;

        /// <summary>
        /// Wraps raw <c>.vsplat</c> bytes as a character asset, for content downloaded or generated
        /// at runtime rather than imported. Returns null and logs if the bytes are not a splat
        /// character.
        /// </summary>
        /// <remarks>
        /// Unlike an imported file this carries no baked Avatar, so a Humanoid character built this
        /// way pays to construct one per instance at load. Prefer importing where you can.
        /// </remarks>
        /// <param name="bytes">The complete contents of a <c>.vsplat</c> file.</param>
        /// <param name="rigMode">How clips bind to the bones. The importer would normally decide
        /// this; there is no importer here, so the caller must.</param>
        public static SplatCharacterAsset FromBytes(byte[] bytes,
                                                    SplatRigMode rigMode = SplatRigMode.Humanoid)
        {
            // Validated rather than trusted. Assigning malformed bytes used to be the documented
            // way to load at runtime, and it failed deep inside the parser, on a background of
            // half-initialised GPU state.
            if (!SplatCharacterBin.HasMagic(bytes))
            {
                Debug.LogError("SplatCharacterAsset.FromBytes: these bytes are not a splat " +
                               $"character (expected the file to start with {SplatCharacterMagic.SupportedMagics}, " +
                               $"found '{SplatCharacterMagic.Describe(bytes)}').");
                return null;
            }
            var asset = CreateInstance<SplatCharacterAsset>();
            asset.bytes = bytes;
            asset.rigMode = rigMode;
            asset.name = "SplatCharacter (runtime)";
            return asset;
        }
    }
}
