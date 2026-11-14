// SPDX-License-Identifier: MIT
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    // Surfaces SplatSettings under Edit > Project Settings > Viggle Splat Engine, the standard place a
    // plugin ships project-wide config. The asset is created lazily the first time this page is opened
    // (in Assets/Resources so it loads at runtime too), so a project that never customizes anything
    // needs zero setup and carries no extra files.
    static class SplatSettingsProvider
    {
        const string kAssetPath = "Assets/Resources/" + SplatSettings.kResourceName + ".asset";

        static SplatSettings GetOrCreate()
        {
            var settings = AssetDatabase.LoadAssetAtPath<SplatSettings>(kAssetPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<SplatSettings>();
                Directory.CreateDirectory("Assets/Resources");
                AssetDatabase.CreateAsset(settings, kAssetPath);
                AssetDatabase.SaveAssets();
                SplatSettings.Invalidate(); // let runtime/edit-mode readers pick up the new asset
            }
            return settings;
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Viggle Splat Engine", SettingsScope.Project)
            {
                label = "Viggle Splat Engine",
                guiHandler = _ =>
                {
                    var settings = GetOrCreate();
                    var so = new SerializedObject(settings);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(so.FindProperty("logLevel"), new GUIContent("Log Level"));

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Editor", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(so.FindProperty("renderInEditor"), new GUIContent("Render Splats In Editor"));

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Pixel Art", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(so.FindProperty("pixelCharacterScale"), new GUIContent("Character Scale"));
                    EditorGUILayout.PropertyField(so.FindProperty("pixelEnvironmentScale"), new GUIContent("Environment Scale"));
                    EditorGUILayout.PropertyField(so.FindProperty("pixelQuantizeLevels"), new GUIContent("Quantize Levels"));
                    using (new EditorGUI.DisabledScope(so.FindProperty("pixelQuantizeLevels").intValue <= 1))
                        EditorGUILayout.PropertyField(so.FindProperty("pixelDitherStrength"), new GUIContent("Dither Strength"));
                        EditorGUILayout.PropertyField(so.FindProperty("pixelDitherCellSize"), new GUIContent("Dither Cell Size"));
                    EditorGUILayout.PropertyField(so.FindProperty("pixelCharacterLayers"), new GUIContent("Character Layers"));
                    EditorGUILayout.HelpBox(
                        "Only does anything with a Splat Pixel Art feature on the renderer - that " +
                        "is what injects the passes; these are its values.", MessageType.None);

                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(
                        "Pixel style is project-wide and a game can override it live through " +
                        "the SplatStyle static API.\n\nRender quality " +
                        "(render scale, screen-size cull) is a runtime decision a game makes from " +
                        "its own quality menu, so it lives on the SplatQuality static API instead " +
                        "of here.", MessageType.None);

                    if (so.ApplyModifiedProperties())
                    {
                        SplatSettings.Invalidate();
                        settings.Apply();
                    }
                },
                keywords = new[] { "splat", "gaussian", "lod", "quality", "cull", "viggle", "render in editor", "style", "pixel art", "dither", "quantize" }
            };
        }
    }
}
