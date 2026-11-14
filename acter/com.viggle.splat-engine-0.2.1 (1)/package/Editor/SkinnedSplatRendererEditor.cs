// SPDX-License-Identifier: MIT
using UnityEditor;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    /// <summary>
    /// SkinnedSplatRenderer's inspector: the default one, plus the entry point into relighting.
    /// Relight parameters live entirely on the SplatRelight component - this editor only offers
    /// to add it, so a character without relighting carries zero relight UI and a character with
    /// it is tuned in one place.
    /// </remarks>
    [CustomEditor(typeof(SkinnedSplatRenderer))]
    [CanEditMultipleObjects]
    public class SkinnedSplatRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

#if GS_ENABLE_URP
            // A character with no splat feature on the renderer loads, reports success and draws
            // nothing, which reads as a broken character file rather than a missing feature.
            SplatUrpWiring.DrawMissingFeature<SplatRendererFeature>(
                "Splat characters will not appear on screen: the component loads the character " +
                "and reports success, and nothing renders.");
#endif

            var renderer = (SkinnedSplatRenderer)target;

            if (renderer.GetComponentInParent<SplatRelight>() != null)
                return;

            EditorGUILayout.Space();
            GUILayout.Label("Relighting", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Light this character with the scene's real-time lights. Adds a SplatRelight " +
                "component, which generates the lighting proxy and holds every relight setting.",
                MessageType.None);
            if (GUILayout.Button("Add Relighting"))
            {
                foreach (var t in targets)
                {
                    var r = t as SkinnedSplatRenderer;
                    if (r != null && r.GetComponentInParent<SplatRelight>() == null)
                        Undo.AddComponent<SplatRelight>(r.gameObject);
                }
            }
        }
    }
}
