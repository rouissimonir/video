// SPDX-License-Identifier: MIT
using UnityEditor;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    /// <summary>
    /// SplatRelight's inspector: the default one, plus the half of relighting that does not live
    /// in the scene.
    /// </summary>
    /// <remarks>
    /// Adding this component generates proxy meshes and nothing else; whether they are ever drawn
    /// is decided by a SplatRelightingFeature on a URP renderer asset, which the component cannot
    /// reach and a build cannot add. That gap used to be silent - proxies on one layer, a feature
    /// listening to another or absent entirely, an empty lighting texture, and characters quietly
    /// on their baked appearance. This says so, once, and only while it is true.
    /// </remarks>
    [CustomEditor(typeof(SplatRelight))]
    public class SplatRelightEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
#if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
            var relight = (SplatRelight)target;
            // The layer note rides along in whichever box is already being drawn rather than
            // earning one of its own: an unnamed layer renders perfectly well, and a box that
            // appears when nothing is wrong is the noise this whole check is trying not to be.
            string layerNote = LayerNote(relight);

            if (SplatUrpWiring.DrawMissingFeature<SplatRelightingFeature>(
                    "Relighting will not run: the proxy meshes are generated, but nothing draws " +
                    "them into the lighting texture and characters keep their baked appearance.",
                    layerNote,
                    // Created here, so it can start on Auto - an existing feature cannot, see the
                    // field's tooltip.
                    feature => feature.m_ProxyLayerSource = SplatRelightingFeature.ProxyLayerSource.Auto))
                return;

            DrawLayerCheck(relight, layerNote);
#endif
        }

#if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
        // The feature is there. It still has to be pointed at this character's proxy layer, which
        // is exactly what Auto exists to stop being a question.
        static void DrawLayerCheck(SplatRelight relight, string layerNote)
        {
            var feature = SplatUrpWiring.FindEnabled<SplatRelightingFeature>();
            int layer = relight.ProxyLayerIndex;
            if (feature == null || layer < 0)
                return;
            if (feature.m_ProxyLayerSource == SplatRelightingFeature.ProxyLayerSource.Auto)
                return;   // it will find this layer on its own
            if ((feature.m_ProxyLayerMask.value & (1 << layer)) != 0)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                Compose($"This proxy goes on layer {layer}, but the Splat Relighting Feature draws " +
                        "a different set of layers, so the lighting texture will come up empty.",
                        layerNote),
                MessageType.Warning);
            if (GUILayout.Button("Set the feature's Proxy Layer Source to Auto"))
            {
                Undo.RecordObject(feature, "Auto proxy layer");
                feature.m_ProxyLayerSource = SplatRelightingFeature.ProxyLayerSource.Auto;
                EditorUtility.SetDirty(feature);
                AssetDatabase.SaveAssetIfDirty(feature);
            }
        }

        // What is worth saying about the proxy layer itself, or null when there is nothing.
        static string LayerNote(SplatRelight relight)
        {
            int layer = relight.ProxyLayerIndex;
            if (layer < 0)
                return "No proxy layer is selected either, so the proxy would be generated on " +
                       "Default, where the camera draws it as a grey shell over the character.";
            if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                return $"Layer {layer} has no name, which is why the entry in Proxy Layer above is " +
                       "blank. It works exactly the same either way - naming it under Project " +
                       "Settings > Tags and Layers only makes the dropdown readable.";
            return null;
        }

        static string Compose(string head, string note) =>
            string.IsNullOrEmpty(note) ? head : head + "\n\n" + note;
#endif
    }
}
