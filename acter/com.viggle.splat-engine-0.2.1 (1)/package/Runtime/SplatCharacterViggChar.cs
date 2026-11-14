// SPDX-License-Identifier: MIT
// The "VIGGCHAR" container: the 16 bytes every version shares, and the table that turns the
// version field into the one handler that reads it.
//
//   [0..8)   magic  "VIGGCHAR" (8 ASCII bytes) - checked by SplatCharacterMagic before we get here
//   [8..12)  uint32 version      - the ONLY value anything branches on
//   [12..16) uint32 totalLength  - the whole file's byte length, so a truncated download is caught
//   [16..)   payload             - version-defined; see SplatCharacterViggCharV1
//
// The version is a plain identifier, not a range. A version this build does not implement is a
// hard failure naming what was found and what is supported - never a best-effort read, and never
// "higher, so probably compatible". There is no ordering between versions.
//
// Adding version 2 is one new handler class and one entry in s_Versions; removing a version is
// deleting the same two things. Nothing here grows a branch per version, and no handler knows
// that any other version exists.

using System;
using System.Collections.Generic;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// One VIGGCHAR payload version. Given the whole file, the offset its payload starts at, and
    /// the payload's length; hands back the same decoded arrays every other reader in this package
    /// produces. Total over its own version, and unaware that any other exists.
    /// </summary>
    internal interface ISplatCharacterViggCharVersion
    {
        SplatCharacterBinData Parse(byte[] bytes, int payloadOffset, int payloadLength);

        /// <summary>
        /// The rig's rest pose - bone count, joints and whether this version means corrected -
        /// without decoding a splat. All-zero when this payload carries no rig. Exists for the
        /// importer, which builds the bones and the Avatar and would otherwise pay a full decode of
        /// every character in the project to learn a few hundred numbers.
        /// </summary>
        SplatCharacterRest PeekRest(byte[] bytes, int payloadOffset, int payloadLength);
    }

    internal static class SplatCharacterViggChar
    {
        /// <summary>Magic (8) + version (4) + totalLength (4). Identical in every version, forever.</summary>
        public const int kContainerBytes = 16;

        // The registry. A table lookup on the integer version, and nothing else: no payload
        // sniffing, no trial parsing, no filename, no size heuristic. The only bytes read before
        // the lookup are the 16 container bytes.
        static readonly Dictionary<uint, ISplatCharacterViggCharVersion> s_Versions =
            new Dictionary<uint, ISplatCharacterViggCharVersion>
            {
                { 1u, new SplatCharacterViggCharV1() },
            };

        /// <summary>
        /// Decodes a VIGGCHAR bundle. Throws <see cref="InvalidOperationException"/> on anything
        /// malformed, with the spec's error category in the message.
        /// </summary>
        public static SplatCharacterBinData Parse(byte[] bytes)
        {
            var handler = Resolve(bytes, out int payloadOffset, out int payloadLength);
            return handler.Parse(bytes, payloadOffset, payloadLength);
        }

        /// <summary>
        /// The rig's rest pose, or all-zero when the file carries no rig or is unreadable. Never
        /// throws: callers use it to decide whether a rig can be built at all.
        /// </summary>
        public static SplatCharacterRest PeekRest(byte[] bytes)
        {
            try
            {
                var handler = Resolve(bytes, out int payloadOffset, out int payloadLength);
                return handler.PeekRest(bytes, payloadOffset, payloadLength);
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Reads the container and looks the version up. This is the whole of the dispatch, and
        /// the only place the container's fields are interpreted.
        /// </summary>
        static ISplatCharacterViggCharVersion Resolve(byte[] bytes, out int payloadOffset, out int payloadLength)
        {
            // The magic is SplatCharacterMagic's business and has already been checked; a file
            // reaching here that is too short to hold the rest of the container has not.
            if (bytes == null || bytes.Length < kContainerBytes)
                throw Error("Truncated",
                            $"file is {(bytes == null ? 0 : bytes.Length)} B, below the {kContainerBytes} B container");

            uint version = BitConverter.ToUInt32(bytes, 8);
            uint totalLength = BitConverter.ToUInt32(bytes, 12);

            // Carried rather than taken from the transport: a truncated download otherwise arrives
            // with an intact header and a short last block, and renders as a broken character
            // instead of failing. Trailing bytes past totalLength are legal and ignored, so the
            // container can sit inside a larger stream.
            if (totalLength < kContainerBytes || totalLength > int.MaxValue)
                throw Error("Truncated", $"header declares totalLength {totalLength}, which is not a usable file size");
            if (bytes.LongLength < totalLength)
                throw Error("Truncated",
                            $"header declares {totalLength} B but the file is {bytes.LongLength} B - a truncated download");

            if (!s_Versions.TryGetValue(version, out var handler))
                throw Error("UnknownVersion",
                            $"payload version {version}; this build reads {SupportedVersions()}");

            payloadOffset = kContainerBytes;
            payloadLength = (int)totalLength - kContainerBytes;
            return handler;
        }

        static string SupportedVersions()
        {
            var versions = new List<uint>(s_Versions.Keys);
            versions.Sort();
            return versions.Count == 1 ? $"version {versions[0]}" : "versions " + string.Join(", ", versions);
        }

        /// <summary>
        /// Every failure in this reader, tagged with the spec's error category. The category is in
        /// the message rather than in an exception subclass because Parse's contract - which every
        /// caller in this package and in host projects is written against - is that it throws
        /// InvalidOperationException and nothing else.
        /// </summary>
        internal static InvalidOperationException Error(string category, string message) =>
            new InvalidOperationException($"SplatCharacterBin: VIGGCHAR [{category}] {message}");
    }
}
