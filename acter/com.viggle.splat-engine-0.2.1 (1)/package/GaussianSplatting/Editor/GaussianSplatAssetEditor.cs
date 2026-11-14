// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using Viggle.SplatEngine;
using Viggle.SplatEngine.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(SplatAsset))]
    [CanEditMultipleObjects]
    internal class GaussianSplatAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var gs = target as SplatAsset;
            if (!gs)
                return;

            using var _ = new EditorGUI.DisabledScope(true);

            if (targets.Length == 1)
                SingleAssetGUI(gs);
            else
            {
                int totalCount = 0;
                foreach (var tgt in targets)
                {
                    var gss = tgt as SplatAsset;
                    if (gss)
                    {
                        totalCount += gss.splatCount;
                    }
                }
                EditorGUILayout.TextField("Total Splats", $"{totalCount:N0}");
            }
        }

        static void SingleAssetGUI(SplatAsset gs)
        {
            var splatCount = gs.splatCount;
            EditorGUILayout.TextField("Splats", $"{splatCount:N0}");
            var prevBackColor = GUI.backgroundColor;
            if (gs.formatVersion != SplatAsset.kCurrentVersion)
                GUI.backgroundColor *= Color.red;
            EditorGUILayout.IntField("Version", gs.formatVersion);
            GUI.backgroundColor = prevBackColor;

            long sizePos = gs.posData != null ? gs.posData.dataSize : 0;
            long sizeOther = gs.otherData != null ? gs.otherData.dataSize : 0;
            long sizeCol = gs.colorData != null ? gs.colorData.dataSize : 0;
            long sizeSH = SplatAsset.CalcSHDataSize(gs.splatCount, gs.shFormat);
            long sizeChunk = gs.chunkData != null ? gs.chunkData.dataSize : 0;

            EditorGUILayout.TextField("Memory", EditorUtility.FormatBytes(sizePos + sizeOther + sizeSH + sizeCol + sizeChunk));
            EditorGUI.indentLevel++;
            EditorGUILayout.TextField("Positions", $"{EditorUtility.FormatBytes(sizePos)}  ({gs.posFormat})");
            EditorGUILayout.TextField("Other", $"{EditorUtility.FormatBytes(sizeOther)}  ({gs.scaleFormat})");
            EditorGUILayout.TextField("Base color", $"{EditorUtility.FormatBytes(sizeCol)}  ({gs.colorFormat})");
            EditorGUILayout.TextField("SHs", $"{EditorUtility.FormatBytes(sizeSH)}  ({gs.shFormat})");
            EditorGUILayout.TextField("Chunks",
                $"{EditorUtility.FormatBytes(sizeChunk)}  ({UnsafeUtility.SizeOf<SplatChunkInfo>()} B/chunk)");
            EditorGUI.indentLevel--;

            EditorGUILayout.Vector3Field("Bounds Min", gs.boundsMin);
            EditorGUILayout.Vector3Field("Bounds Max", gs.boundsMax);

            EditorGUILayout.TextField("Data Hash", gs.dataHash.ToString());
        }
    }
}
