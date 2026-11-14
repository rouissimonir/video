// SPDX-License-Identifier: MIT
// Inspector + 3D preview for splat character files (.vsplat, legacy .bin).
//
// Without this a .vsplat is a featureless icon with an empty inspector: you cannot tell a 200k
// stripped body bin from a 2M full-rig one, or a v1 fp32 export from a v2 fp16 LOD bundle,
// without opening it in a viewer. This shows the header facts and a rotatable point cloud of the
// actual splat centres, both derived from the same parser the runtime uses - so if the inspector
// can read it, the runtime can too, and if it cannot, you see the parse error here instead of at
// play time.
//
// LIFETIME
//
// SplatCharacterBinData holds Allocator.Persistent NativeArrays, which do NOT survive a domain
// reload (and leak loudly if they are collected). The bin is therefore parsed once, distilled
// into a small managed snapshot, and disposed inside the same scope. Nothing native outlives
// OnEnable.
//
// The rasteriser is CPU-only on purpose; see SplatPreviewRenderer for why a preview camera
// cannot run the real splat renderer.

using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    [CustomEditor(typeof(SplatCharacterImporter))]
    class SplatCharacterImporterEditor : ScriptedImporterEditor
    {
        // Interactive preview budget. 40k points saturate a ~400px preview at radius 3 while
        // staying well inside a repaint's time budget while dragging.
        const int kInteractiveSamples = 40000;

        // The Project-window thumbnail runs DURING IMPORT, so it gets a smaller budget.
        const int kThumbnailSamples = 12000;

        // Parsing decodes every splat into managed arrays, which is linear but not free. Past this
        // size the thumbnail is not worth the import stall, and the asset falls back to the default
        // icon. ~64MB is a few hundred thousand fp32 splats.
        const long kMaxThumbnailBytes = 64L * 1024 * 1024;

        const float kDragSensitivity = 0.5f;

        SplatPreviewRenderer.Snapshot m_Snapshot;
        string m_Error;

        int m_NumSplats;
        int m_NumBones;
        string m_RigName;
        string m_Precision;
        Vector3 m_BoundsSize;
        long m_FileBytes;

        Texture2D m_PreviewTex;
        float m_Yaw;
        float m_Pitch;
        float m_RenderedYaw = float.NaN;
        float m_RenderedPitch = float.NaN;

        public override void OnEnable()
        {
            base.OnEnable();
            LoadAsset();
        }

        public override void OnDisable()
        {
            if (m_PreviewTex != null)
            {
                DestroyImmediate(m_PreviewTex);
                m_PreviewTex = null;
            }
            m_Snapshot = null;
            base.OnDisable();
        }

        void LoadAsset()
        {
            m_Snapshot = null;
            m_Error = null;
            m_RigName = null;
            m_Precision = null;

            try
            {
                byte[] bytes = ReadBytes();
                if (bytes == null)
                    throw new InvalidOperationException("Could not read the asset's bytes.");
                m_FileBytes = bytes.LongLength;
                m_Precision = DescribePrecision(bytes);

                // Disposed before this method returns: nothing native is cached on the editor.
                using (var bin = SplatCharacterBin.Parse(bytes))
                {
                    m_NumSplats = bin.numSplats;
                    m_NumBones = bin.numBones;
                    m_BoundsSize = bin.boundsMax - bin.boundsMin;
                    m_RigName = DescribeRig(bin.numBones);
                    m_Snapshot = SplatPreviewRenderer.Capture(bin, kInteractiveSamples);
                }
            }
            catch (Exception e)
            {
                // Includes the "not a splat character" case: SplatCharacterImporter deliberately imports
                // foreign .bin files as inert assets, and those land here.
                m_Error = e.Message;
            }
        }

        byte[] ReadBytes()
        {
            // The imported asset already carries the blob; only fall back to disk if the import
            // failed or the asset has not been produced yet.
            if (assetTarget is SplatCharacterAsset asset && asset.bytes != null && asset.bytes.Length > 0)
                return asset.bytes;
            string path = (target as AssetImporter)?.assetPath;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : File.ReadAllBytes(path);
        }

        // SplatCharacterBinData does not surface which container or numeric encoding a file used
        // (the readers only branch on it internally), so the bytes are peeked at directly rather
        // than adding runtime API for an editor label.
        static string DescribePrecision(byte[] bytes)
        {
            switch (SplatCharacterMagic.Identify(bytes))
            {
                case SplatCharacterFormat.ViggChar:
                    // VIGGCHAR has exactly one numeric encoding - quantised against per-block
                    // bounds - and no precision mode to report. The container's version is the
                    // only thing that varies, so that is what this names.
                    return $"quantised (VIGGCHAR v{BitConverter.ToUInt32(bytes, 8)})";
                case SplatCharacterFormat.VfCharB1:
                    return DescribeVfCharB1Precision(bytes);
                default:
                    return null;
            }
        }

        // The splat chunk stride is the authoritative signal - 26 = fp16 (v2), 48 = fp32 (v1) -
        // with version/encoding.precision as the fallback when stride is absent.
        static string DescribeVfCharB1Precision(byte[] bytes)
        {
            try
            {
                if (bytes.Length < 12)
                    return null;
                int headerLen = BitConverter.ToInt32(bytes, 8);
                if (headerLen <= 0 || 12L + headerLen > bytes.LongLength)
                    return null;
                var root = JsonUtility.FromJson<Peek>(Encoding.UTF8.GetString(bytes, 12, headerLen));
                if (root == null)
                    return null;

                // JsonUtility zero-fills what the header omits, which is exactly the "absent"
                // fallback each of these wants anyway.
                int stride = root.chunks?.splats?.stride ?? 0;
                int version = root.version;
                string precision = root.encoding?.precision;

                if (stride == 26) return "fp16 (v2)";
                if (stride == 48) return "fp32 (v1)";
                if (version >= 2 || precision == "fp16") return "fp16 (v2)";
                if (version == 1 || precision == "fp32") return "fp32 (v1)";
                return stride > 0 ? $"unknown (stride {stride})" : "unknown";
            }
            catch
            {
                return null;
            }
        }

        // The three header fields DescribePrecision needs, shaped for JsonUtility (the package has
        // no JSON dependency by design, so there is no DOM to index into). Kept local rather than
        // reusing the runtime's header mirrors, which stay private to the parser; unknown keys are
        // ignored, so this reads a full header happily.
        [Serializable] class Peek { public int version; public PeekEncoding encoding; public PeekChunks chunks; }
        [Serializable] class PeekEncoding { public string precision; }
        [Serializable] class PeekChunks { public PeekChunk splats; }
        [Serializable] class PeekChunk { public int stride; }

        // Only name the rig for the two counts SplatSkeleton actually has tables for. Calling
        // SplatSkeleton.For on anything else logs a console warning, which an inspector redraw has
        // no business doing.
        static string DescribeRig(int numBones)
        {
            if (numBones <= 0)
                return null;
            if (numBones == SplatSkeleton.Body86.NumBones || numBones == SplatSkeleton.Full441.NumBones)
                return SplatSkeleton.For(numBones).Name;
            return "unrecognised rig";
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (m_Error != null)
            {
                EditorGUILayout.HelpBox($"Not a readable splat character.\n{m_Error}", MessageType.Warning);
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField("Splats", m_NumSplats.ToString("N0"));
                    EditorGUILayout.LabelField("Bones", m_RigName == null
                        ? m_NumBones.ToString("N0")
                        : $"{m_NumBones:N0}  ({m_RigName})");
                    EditorGUILayout.LabelField("Precision", m_Precision ?? "unknown");
                    EditorGUILayout.LabelField("Bounds size",
                        $"{m_BoundsSize.x:0.###} x {m_BoundsSize.y:0.###} x {m_BoundsSize.z:0.###} m");
                    EditorGUILayout.LabelField("File size", $"{m_FileBytes / (1024.0 * 1024.0):0.##} MB");
                }
            }

            EditorGUILayout.Space();

            // The only editable import setting, and the analogue of the model importer's Rig tab.
            var rigModeProp = serializedObject.FindProperty("rigMode");
            if (rigModeProp != null)
            {
                EditorGUILayout.PropertyField(rigModeProp, new GUIContent("Rig Mode"));
                // Generic needs a baked bind pose to place the bones in the clips' own frame;
                // without it the character animates with visibly wrong bone rotations, and the
                // failure only shows up at runtime.
                if (rigModeProp.enumValueIndex == (int)SplatRigMode.Generic &&
                    !SplatSkeletonBindPose.HasData)
                {
                    EditorGUILayout.HelpBox(
                        "Generic mode needs the bind pose baked. Run Assets/Viggle/Bake Generic " +
                        "Bind Pose once, or this character will animate with wrong bone rotations.",
                        MessageType.Warning);
                }
                EditorGUILayout.Space();
            }

            serializedObject.ApplyModifiedProperties();

            // Mandatory for ScriptedImporterEditor - Unity logs errors on every repaint without it.
            ApplyRevertGUI();
        }

        public override bool HasPreviewGUI() => m_Snapshot != null;

        public override GUIContent GetPreviewTitle() => new GUIContent("Splat Character");

        public override void OnInteractivePreviewGUI(Rect r, GUIStyle background)
        {
            if (m_Snapshot == null)
                return;

            HandleDrag(r);

            if (Event.current.type != EventType.Repaint)
                return;

            background.Draw(r, false, false, false, false);

            int w = Mathf.Clamp(Mathf.FloorToInt(r.width), 1, 2048);
            int h = Mathf.Clamp(Mathf.FloorToInt(r.height), 1, 2048);
            if (m_PreviewTex == null || m_PreviewTex.width != w || m_PreviewTex.height != h)
            {
                if (m_PreviewTex != null)
                    DestroyImmediate(m_PreviewTex);
                m_PreviewTex = NewPreviewTexture(w, h, dontSave: true);
                m_RenderedYaw = float.NaN;
            }

            // Re-rasterise only when the view actually moved; a static preview would otherwise
            // redraw the whole cloud on every unrelated inspector repaint.
            if (m_RenderedYaw != m_Yaw || m_RenderedPitch != m_Pitch)
            {
                SplatPreviewRenderer.Render(m_PreviewTex, m_Snapshot, ViewRotation());
                m_RenderedYaw = m_Yaw;
                m_RenderedPitch = m_Pitch;
            }

            GUI.DrawTexture(r, m_PreviewTex, ScaleMode.StretchToFill, true);
        }

        Quaternion ViewRotation() => Rotation(m_Yaw, m_Pitch);

        // Turntable: yaw about world up first, then tilt about the fixed screen-horizontal axis, so
        // the horizon never rolls.
        static Quaternion Rotation(float yaw, float pitch) =>
            Quaternion.AngleAxis(pitch, Vector3.right) * Quaternion.AngleAxis(yaw, Vector3.up);

        void HandleDrag(Rect r)
        {
            int id = GUIUtility.GetControlID("SplatPreviewDrag".GetHashCode(), FocusType.Passive);
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        e.Use();
                        EditorGUIUtility.SetWantsMouseJumping(1);
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        // Direct manipulation: drag right spins the front toward screen-right, drag
                        // down tips the top of the character toward the viewer.
                        m_Yaw -= e.delta.x * kDragSensitivity;
                        m_Pitch = Mathf.Clamp(m_Pitch + e.delta.y * kDragSensitivity, -89f, 89f);
                        e.Use();
                        Repaint();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                        EditorGUIUtility.SetWantsMouseJumping(0);
                    }
                    break;
            }
        }

        // NOTE: RenderStaticPreview does NOT belong here, and does not belong on an editor for the
        // imported object either. Unity asks the editor for the MAIN asset object, which is now the
        // rig prefab - a GameObject, whose preview renders in an isolated scene with no URP feature
        // and no compute dispatch, so a splat character draws nothing there. The importer hands the
        // icon over directly instead, via the AddObjectToAsset thumbnail overload.

        static byte[] FindBytes(UnityEngine.Object[] subAssets)
        {
            if (subAssets == null)
                return null;
            foreach (var o in subAssets)
                if (o is SplatCharacterAsset a && a.bytes != null && a.bytes.Length > 0)
                    return a.bytes;
            return null;
        }

        static Texture2D NewPreviewTexture(int w, int h, bool dontSave)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (dontSave)
                tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
