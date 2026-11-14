// SPDX-License-Identifier: MIT
// Import settings for a static splat capture (.spz or .ply).
//
// There are none, and that is the point: the importer stores the file and the GPU layout is built
// on load, so there is nothing left to choose at import time. What this draws instead is the one
// thing worth knowing before the capture is in a scene - how many splats it has, whether it
// carries spherical harmonics, and what it will therefore cost on the GPU. That number used to be
// the whole reason a custom editor existed here, when it described a file the import was about to
// write; it describes GPU memory now, which is the budget that actually binds.
//
// The .ply case says one more thing, because it is the one case where something IS converted: the
// file becomes .spz bytes at import, and what that costs. A .ply that is not a capture at all gets
// a warning rather than an error - see the importer's header for why that asymmetry exists.

using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    [CustomEditor(typeof(SplatCaptureImporter))]
    [CanEditMultipleObjects]
    class SplatCaptureImporterEditor : ScriptedImporterEditor
    {
        int m_SplatCount;
        int m_ShDegree;
        long m_FileBytes;
        bool m_Readable;
        bool m_IsPly;

        public override void OnEnable()
        {
            base.OnEnable();
            ReadHeader();
        }

        void ReadHeader()
        {
            m_SplatCount = 0;
            m_ShDegree = 0;
            m_FileBytes = 0;
            m_Readable = false;
            m_IsPly = false;
            // Multi-selection has no single source file to describe.
            if (targets.Length != 1)
                return;
            string path = (target as AssetImporter)?.assetPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            m_FileBytes = new FileInfo(path).Length;
            m_IsPly = Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase);

            if (m_IsPly)
            {
                // The PLY header, and none of the body. This runs on every inspector focus, and a
                // foreign .ply in the project can be hundreds of megabytes.
                try
                {
                    using var ply = SplatPlyFile.Open(path);
                    m_SplatCount = ply.splatCount;
                    m_ShDegree = ply.shDegree;
                    m_Readable = true;
                }
                catch
                {
                    m_Readable = false;
                }
                return;
            }
            // The .spz header lives inside the gzip stream, so this decompresses the first 16 bytes
            // and stops. Cheap enough to do on every inspector focus.
            m_Readable = SplatSpzFile.PeekHeader(File.ReadAllBytes(path), out m_SplatCount, out m_ShDegree);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (targets.Length == 1 && !m_Readable)
            {
                // A .ply that is not a capture is somebody else's mesh far more often than it is a
                // broken one, so it does not get red. See the importer's header.
                EditorGUILayout.HelpBox(
                    m_IsPly
                        ? "This .ply is not a Gaussian splat capture - it has no splat properties, " +
                          "or is not binary little-endian. It imports as an empty asset and " +
                          "produces no prefab."
                        : "This file is not a version 2 .spz Gaussian splat capture, so it imports " +
                          "as an empty asset and produces no prefab.",
                    m_IsPly ? MessageType.Warning : MessageType.Error);
            }
            else if (m_SplatCount > 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField("Source",
                        $"{EditorUtility.FormatBytes(m_FileBytes)} - {m_SplatCount:N0} splats");
                    EditorGUILayout.LabelField("Spherical harmonics",
                        m_ShDegree > 0
                            ? $"degree {m_ShDegree} - view-dependent shading"
                            : "none - the capture stores flat colour");
                    EditorGUILayout.LabelField("GPU memory",
                        EditorUtility.FormatBytes(GpuBytes(m_SplatCount, m_ShDegree)));
                }
                EditorGUILayout.HelpBox(
                    m_IsPly
                        ? "This .ply is converted to the packed .spz representation at import, so " +
                          "it is stored and rendered exactly as an .spz capture is. That costs " +
                          "precision on rotation and scale, which are 8 bits each; positions are " +
                          "24-bit fixed point and effectively unaffected. There is nothing to " +
                          "configure."
                        : "The file is stored as-is and unpacked when the capture loads, so nothing " +
                          "is converted here and there is nothing to configure.", MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();

            // Mandatory for ScriptedImporterEditor - Unity logs errors on every repaint without it.
            ApplyRevertGUI();
        }

        // What SplatRenderAssetBuilder will produce for this capture. Same formats, same helpers,
        // so it cannot drift from the thing it predicts.
        static long GpuBytes(int splatCount, int shDegree)
        {
            long sh = shDegree > 0
                ? SplatAsset.CalcSHDataSize(splatCount, SplatAsset.SHFormat.Norm6)
                : 32;   // the stub a degree-0 capture stores instead of 32 bytes a splat
            return SplatAsset.CalcPosDataSize(splatCount, SplatAsset.VectorFormat.Norm11) +
                   SplatAsset.CalcOtherDataSize(splatCount, SplatAsset.VectorFormat.Norm11) +
                   SplatAsset.CalcColorDataSize(splatCount, SplatAsset.ColorFormat.Norm8x4) +
                   SplatAsset.CalcChunkDataSize(splatCount) +
                   sh;
        }
    }
}
