// SPDX-License-Identifier: MIT
// Which character container a run of bytes is - decided once, here, and nowhere else.
//
// The package reads two containers:
//
//   VFCHARB1  the original interleaved per-splat record format. Still shipped, still exported,
//             and still in host projects, so it keeps working unchanged, forever.
//   VIGGCHAR  the current quantised block format, read by the web engine and the Python tooling.
//
// EVERY entry point that has to tell them apart - SplatCharacterBin.Parse, .HasMagic,
// .PeekNumBones, and the editor's ScriptedImporter - resolves the question through Identify and
// acts on the enum. Nothing compares magic bytes on its own. That rule is not stylistic: the
// importer used to carry a duplicate copy of the VFCHARB1 constant and its own byte compare, so a
// change to the runtime reader left editor import checking for a format the runtime no longer
// insisted on, and files imported as inert assets with a warning nobody connected to the cause.
//
// The magic is compared AS BYTES and never decoded as an integer. Loading 8 bytes as a ulong and
// comparing numerically makes the comparison depend on byte order, which is a magic that works on
// one platform and fails on another.
//
// The EXTENSION is not consulted here and must not be. A file is what its bytes say it is: a
// VIGGCHAR bundle named .bin is a VIGGCHAR bundle, and a .vsplat full of something else is not a
// character however it is named. The editor importer branches on the extension for one thing only
// - how loudly to complain about bytes it does not recognise - and never for how to read them.

using System;

namespace Viggle.SplatEngine
{
    /// <summary>Which character container a file's first 8 bytes name.</summary>
    internal enum SplatCharacterFormat
    {
        /// <summary>Not a character container this package reads.</summary>
        Unknown = 0,
        /// <summary>The legacy interleaved per-splat record format (v1 fp32, v2 fp16).</summary>
        VfCharB1,
        /// <summary>The current quantised block format, versioned in its own container field.</summary>
        ViggChar,
    }

    internal static class SplatCharacterMagic
    {
        /// <summary>Every character container's magic is 8 ASCII bytes.</summary>
        public const int kLength = 8;

        /// <summary>
        /// "VFCHARB1": VF (vendor) + CHAR (character) + B1 (bundle v1). The magic identifies the
        /// CONTENT, so it stayed as-is across the .bin to .vsplat extension change and across the
        /// format's own v1/v2 split, which is carried in its header instead.
        /// </summary>
        public const string kVfCharB1 = "VFCHARB1";

        /// <summary>
        /// "VIGGCHAR": a constant type tag, and only that. It never changes when the payload
        /// changes - the container's uint32 `version` field carries generations - so no part of
        /// these bytes may be read as a version number.
        /// </summary>
        public const string kViggChar = "VIGGCHAR";

        static readonly byte[] s_VfCharB1 = Ascii(kVfCharB1);
        static readonly byte[] s_ViggChar = Ascii(kViggChar);

        /// <summary>The container these bytes begin with, or Unknown.</summary>
        public static SplatCharacterFormat Identify(byte[] bytes)
        {
            if (bytes == null || bytes.Length < kLength) return SplatCharacterFormat.Unknown;
            if (StartsWith(bytes, s_ViggChar)) return SplatCharacterFormat.ViggChar;
            if (StartsWith(bytes, s_VfCharB1)) return SplatCharacterFormat.VfCharB1;
            return SplatCharacterFormat.Unknown;
        }

        /// <summary>
        /// The first 8 bytes rendered for an error message: the ASCII if they are printable,
        /// otherwise hex. A binary blob's magic would otherwise arrive in the console as control
        /// characters and tell the reader nothing.
        /// </summary>
        public static string Describe(byte[] bytes)
        {
            if (bytes == null) return "<null>";
            if (bytes.Length < kLength) return $"<{bytes.Length} bytes>";
            bool printable = true;
            for (int i = 0; i < kLength; i++)
                if (bytes[i] < 0x20 || bytes[i] > 0x7e) { printable = false; break; }
            if (printable)
            {
                var chars = new char[kLength];
                for (int i = 0; i < kLength; i++) chars[i] = (char)bytes[i];
                return new string(chars);
            }
            var hex = new System.Text.StringBuilder(kLength * 3);
            for (int i = 0; i < kLength; i++)
            {
                if (i > 0) hex.Append(' ');
                hex.Append(bytes[i].ToString("x2"));
            }
            return hex.ToString();
        }

        /// <summary>The magics this build reads, for "expected one of ..." messages.</summary>
        public static string SupportedMagics => $"'{kVfCharB1}' or '{kViggChar}'";

        static bool StartsWith(byte[] bytes, byte[] magic)
        {
            for (int i = 0; i < magic.Length; i++)
                if (bytes[i] != magic[i]) return false;
            return true;
        }

        static byte[] Ascii(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
            return b;
        }
    }
}
