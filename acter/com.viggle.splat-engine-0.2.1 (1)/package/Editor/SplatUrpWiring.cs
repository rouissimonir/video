// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Viggle.SplatEngine.Editor
{
    /// <summary>
    /// Does the URP renderer this project renders with carry the feature a splat component needs?
    /// Reports it in the component's own inspector, and offers to add it.
    /// </summary>
    /// <remarks>
    /// It offers rather than acts, for three reasons. A renderer feature lives on a
    /// UniversalRendererData PROJECT asset, so a component that added one would be writing to a
    /// version-controlled file the user never pointed at, and they would meet it later as a stray
    /// diff. Several quality levels mean several pipeline assets and several renderers, and which
    /// one should take the feature is a genuine question rather than a detail to guess at. And
    /// features are baked into the renderer asset, so adding one at runtime or in a build is not
    /// possible at all - an auto-add could only ever be an editor affordance, which makes it a
    /// worse answer than the component finding its own layer at runtime.
    ///
    /// It draws nothing when the wiring is right. A validation box that is always on screen is
    /// noise, and noise is how people learn to ignore warnings.
    /// </remarks>
    static class SplatUrpWiring
    {
        // Reused across repaints; OnInspectorGUI runs many times a second.
        static readonly List<ScriptableRendererData> s_Candidates = new List<ScriptableRendererData>();

        // The renderer list is not public API on every URP this package supports: 14 keeps it
        // internal, 17 exposes it as rendererDataList. Both SERIALIZE it under the same name, and
        // a serialized field name is the thing Unity can least afford to rename, so the field is
        // the one handle that spans them. Looked up once, not per repaint.
        static readonly FieldInfo k_RendererDataList = typeof(UniversalRenderPipelineAsset)
            .GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);

        // Empty rather than null if a future URP moves it: this inspector then loses its warning
        // box, which is the same as the wiring being right, and nothing throws mid-OnInspectorGUI.
        static ScriptableRendererData[] RendererDataList(UniversalRenderPipelineAsset pipeline)
            => k_RendererDataList?.GetValue(pipeline) as ScriptableRendererData[]
               ?? Array.Empty<ScriptableRendererData>();

        /// <summary>
        /// The first enabled feature of type <typeparamref name="T"/> on any renderer the active
        /// pipeline asset carries, or null.
        /// </summary>
        public static T FindEnabled<T>() where T : ScriptableRendererFeature
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                return null;
            foreach (var data in RendererDataList(pipeline))
            {
                var feature = Find<T>(data);
                if (feature != null && feature.isActive)
                    return feature;
            }
            return null;
        }

        /// <summary>
        /// Draws a HelpBox naming the missing feature, plus a button per renderer that could take
        /// it. Draws nothing when a renderer already carries it enabled.
        /// </summary>
        /// <param name="why">One sentence: what does not work until it is there.</param>
        /// <param name="note">Appended to the box when there is a box; null for none.</param>
        /// <param name="configureNew">Runs on a feature this created, before it is saved.</param>
        /// <returns>Whether anything was drawn, i.e. whether something is wrong.</returns>
        public static bool DrawMissingFeature<T>(string why, string note = null,
                                                 Action<T> configureNew = null)
            where T : ScriptableRendererFeature
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            // Not URP at all: this package's HDRP and built-in paths are wired elsewhere, and a
            // box telling someone on HDRP to add a URP feature is worse than silence.
            if (pipeline == null)
                return false;

            string featureName = ObjectNames.NicifyVariableName(typeof(T).Name);
            T disabled = null;
            s_Candidates.Clear();
            foreach (var data in RendererDataList(pipeline))
            {
                if (data == null)
                    continue;
                var feature = Find<T>(data);
                if (feature == null)
                    s_Candidates.Add(data);
                else if (feature.isActive)
                    return false;   // wired somewhere in the active pipeline; say nothing
                else
                    disabled ??= feature;
            }

            EditorGUILayout.Space();
            if (disabled != null)
            {
                EditorGUILayout.HelpBox(
                    Compose($"{featureName} is on '{pipeline.name}' but switched off. {why}", note),
                    MessageType.Warning);
                if (GUILayout.Button($"Enable {featureName}"))
                {
                    Undo.RecordObject(disabled, $"Enable {featureName}");
                    disabled.SetActive(true);
                    EditorUtility.SetDirty(disabled);
                    AssetDatabase.SaveAssetIfDirty(disabled);
                }
                return true;
            }

            // Which renderer should take it is the user's call: cameras choose a renderer by
            // index, and this component has no camera to ask.
            bool several = s_Candidates.Count > 1;
            string head = $"'{pipeline.name}' has no {featureName}. {why}";
            if (several)
                head += $" It has {s_Candidates.Count} renderers - add it to the one your cameras use.";
            EditorGUILayout.HelpBox(Compose(head, note), MessageType.Warning);

            for (int i = 0; i < s_Candidates.Count; i++)
            {
                var data = s_Candidates[i];
                string label = several
                    ? $"Add to '{data.name}'"
                    : $"Add {featureName} to '{data.name}'";
                if (GUILayout.Button(label))
                    AddFeature(data, configureNew);
            }
            return true;
        }

        static string Compose(string head, string note) =>
            string.IsNullOrEmpty(note) ? head : head + "\n\n" + note;

        static T Find<T>(ScriptableRendererData data) where T : ScriptableRendererFeature
        {
            if (data == null)
                return null;
            // Not TryGetRendererFeature: that compares types exactly, so a project subclassing the
            // feature would be told to add a second one.
            foreach (var feature in data.rendererFeatures)
                if (feature is T match)
                    return match;
            return null;
        }

        /// <summary>
        /// Adds the feature to a renderer data asset, in the same way URP's own Add Renderer
        /// Feature button does: as a sub-asset of the renderer, listed in m_RendererFeatures with
        /// its local file id mirrored into m_RendererFeatureMap.
        /// </summary>
        /// <remarks>
        /// The map is not decoration - URP relinks the feature list through it when a script fails
        /// to compile, so a feature added without one comes back as a null entry and an error on
        /// the renderer asset. Both are internal fields, hence SerializedObject rather than the
        /// public rendererFeatures list.
        ///
        /// Relighting goes in BEFORE the splat feature: the lighting texture has to exist by the
        /// time the splat pass computes its colors.
        /// </remarks>
        static void AddFeature<T>(ScriptableRendererData data, Action<T> configureNew)
            where T : ScriptableRendererFeature
        {
            var so = new SerializedObject(data);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            if (features == null || map == null)
            {
                Debug.LogError($"Could not add {typeof(T).Name} to '{data.name}': this URP version " +
                               "does not store its renderer features where expected. Add it by hand " +
                               "from the renderer asset's Add Renderer Feature button.", data);
                return;
            }

            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = typeof(T).Name;
            configureNew?.Invoke(feature);
            Undo.RegisterCreatedObjectUndo(feature, $"Add {typeof(T).Name}");
            if (EditorUtility.IsPersistent(data))
                AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            so.Update();
            int index = IndexBefore<SplatRendererFeature>(data);
            if (index < 0 || index > features.arraySize)
                index = features.arraySize;
            if (index == features.arraySize)
            {
                features.arraySize++;
                map.arraySize++;
            }
            else
            {
                // Insert duplicates the element at the index; both entries are overwritten below.
                features.InsertArrayElementAtIndex(index);
                map.InsertArrayElementAtIndex(index);
            }
            features.GetArrayElementAtIndex(index).objectReferenceValue = feature;
            map.GetArrayElementAtIndex(index).longValue = localId;
            so.ApplyModifiedProperties();

            EditorUtility.SetDirty(data);
            if (EditorUtility.IsPersistent(data))
                AssetDatabase.SaveAssetIfDirty(data);
        }

        // Where a relight feature has to land relative to the splat feature. -1 when the splat
        // feature is not on this renderer, which means the order does not matter yet.
        static int IndexBefore<TBefore>(ScriptableRendererData data)
            where TBefore : ScriptableRendererFeature
        {
            var list = data.rendererFeatures;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is TBefore)
                    return i;
            return -1;
        }
    }
}

#endif // #if GS_ENABLE_URP
