// SPDX-License-Identifier: MIT
// Runs the no-compute render path in the editor, so the WebGL2 / GLES 3.0 half of the renderer
// can be exercised in Play mode instead of only through a player build.
//
// SplatRenderer.UseNoComputePath normally probes SystemInfo.supportsComputeShaders, which is true
// on every editor platform, so nothing but a Web build ever reaches that code. s_ForceNoComputePath
// has always existed as the override; this is the switch for it.
//
// WHAT THIS DOES AND DOES NOT PROVE. The render half - the no-compute shaders, the baked source
// textures, CpuSplatSorter, the vertex-stage projection - is the same code here as in a player, so
// a fault there reproduces in seconds. The DECODE half is not: DecodeToArrays runs under Mono here
// and under IL2CPP/WebAssembly there. The low-byte-only position bug this was added to chase did
// NOT reproduce in the editor, which is itself the evidence that it is a codegen artifact rather
// than bad arithmetic. So a capture that looks right here is not proof that a Web build is fixed;
// only [SplatDecode]'s reported extent in a player build settles that.
//
// The flag is a static, so a domain reload and ResetStatics both clear it. It is stored in
// EditorPrefs and re-applied on load and on entering Play mode.
using UnityEditor;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    static class SplatRenderPathMenu
    {
        const string kPref = "Viggle.SplatEngine.ForceNoComputePath";
        const string kMenu = "Tools/Viggle Splat Engine/Force No-Compute Path (WebGL2)";

        static bool Enabled => EditorPrefs.GetBool(kPref, false);

        [InitializeOnLoadMethod]
        static void Hook()
        {
            ApplyToRuntime();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        // ResetStatics clears the flag on teardown, so re-arm it as play mode starts rather than
        // only once at load - otherwise the second Play of a session silently uses the compute path.
        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.EnteredPlayMode)
                ApplyToRuntime();
        }

        // Only the force flag is touched. UseNoComputePath short-circuits on it, and when the
        // flag is off it falls through to the probe, which on any editor platform answers
        // "compute available" - so there is no cached state to invalidate in either direction,
        // and SplatRenderer needs no new API for this.
        static void ApplyToRuntime() => SplatRenderer.s_ForceNoComputePath = Enabled;

        [MenuItem(kMenu, true)]
        static bool Validate()
        {
            Menu.SetChecked(kMenu, Enabled);
            return true;
        }

        [MenuItem(kMenu)]
        static void Toggle()
        {
            bool on = !Enabled;
            EditorPrefs.SetBool(kPref, on);
            ApplyToRuntime();
            Debug.Log($"[SplatRenderPath] No-compute path {(on ? "FORCED" : "released")}. " +
                      "Re-enter Play mode (or reload the scene) so loaded renderers rebuild their " +
                      "GPU resources for the other path.");
        }
    }
}
