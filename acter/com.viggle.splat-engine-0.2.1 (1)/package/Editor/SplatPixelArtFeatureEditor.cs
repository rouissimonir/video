// SPDX-License-Identifier: MIT
//
// Compiles out with SplatPixelArtFeature, which is Unity 6 / URP 17 only - an inspector for a
// type that is not there would take the editor assembly down with it.
#if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
using UnityEditor;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    /// <summary>
    /// The pixel-art feature holds no settings of its own, so its inspector says where they are.
    /// </summary>
    /// <remarks>
    /// URP can only inject render passes from a renderer asset, so the feature has to sit in that
    /// list. Its values do not: pixel size, colour and the character layers are art direction for
    /// the whole project, and keeping them per-renderer is how two renderers quietly end up
    /// disagreeing. They live in Project Settings with the rest of the look.
    /// </remarks>
    [CustomEditor(typeof(SplatPixelArtFeature))]
    public class SplatPixelArtFeatureEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Pixel size, colour and character layers are in\n" +
                "Project Settings > Viggle Splat Engine > Pixel Art.\n\n" +
                "This entry only puts the passes in the frame - URP has no other way to add one.",
                MessageType.Info);
            if (GUILayout.Button("Open Project Settings"))
                SettingsService.OpenProjectSettings("Project/Viggle Splat Engine");
        }
    }
}

#endif // #if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
