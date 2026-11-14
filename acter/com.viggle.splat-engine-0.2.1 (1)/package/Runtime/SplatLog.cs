// SPDX-License-Identifier: MIT
//
// One severity gate for everything this package prints. The default is Warning, so problems speak
// while instrumentation stays quiet.
//
// Logging is guarded at the call site rather than inside a helper:
//
//     if (SplatLog.Info) Debug.Log($"[SplatLoad] {name}: {count:N0} splats");
//
// An interpolated string is built before the call, so a SplatLog.Info("...") helper that discarded
// it would still pay for the formatting and the allocation on every character, every frame, in a
// build with logging off. The bool property compiles to a field read.
//
// The vendored GaussianSplatting assembly cannot reference this one, since the dependency runs the
// other way, so it carries its own diagnostics flag that SplatSettings.Apply pushes the level into.

namespace Viggle.SplatEngine
{
    /// <summary>Severity threshold for the package's own logging.</summary>
    public enum SplatLogLevel
    {
        /// <summary>Say nothing at all, including failures.</summary>
        Off = 0,
        /// <summary>Only conditions that stop something working.</summary>
        Error = 1,
        /// <summary>Errors, plus conditions that will probably bite later. The default.</summary>
        Warning = 2,
        /// <summary>Adds one-off instrumentation: load timings, buffer audits, chosen paths.</summary>
        Info = 3,
        /// <summary>Adds per-frame and per-character chatter. Noisy by design.</summary>
        Verbose = 4,
    }

    /// <summary>Severity gate for everything this package prints.</summary>
    public static partial class SplatLog
    {
        /// <summary>
        /// Severity threshold for everything this package prints. Defaults to Warning, so a host
        /// that never touches it still hears about failures and never sees instrumentation.
        /// Settable from code at any time; SplatSettings drives it from the project setting.
        /// </summary>
        public static SplatLogLevel Level = SplatLogLevel.Warning;

        // Call-site guards; see the header. Internal, because a host configures logging through
        // Level and does not need the gates.
        internal static bool Error => Level >= SplatLogLevel.Error;
        internal static bool Warning => Level >= SplatLogLevel.Warning;
        internal static bool Info => Level >= SplatLogLevel.Info;
        internal static bool Verbose => Level >= SplatLogLevel.Verbose;

        /// <summary>Restores the default level. Call when tearing the engine down.</summary>
        public static void ResetStatics() => Level = SplatLogLevel.Warning;
    }
}
