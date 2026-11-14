// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using Viggle.SplatEngine;
using UnityEditor.EditorTools;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    abstract class GaussianTool : EditorTool
    {
        protected SplatRenderer GetRenderer()
        {
            var gs = target as SplatRenderer;
            if (!gs || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                return null;
            return gs;
        }

        protected bool CanBeEdited()
        {
            var gs = GetRenderer();
            if (!gs)
                return false;
            return gs.asset.chunkData == null; // need to be lossless / non-chunked for editing
        }

        protected bool HasSelection()
        {
            var gs = GetRenderer();
            if (!gs)
                return false;
            return gs.editSelectedSplats > 0;
        }

        protected Vector3 GetSelectionCenterLocal()
        {
            var gs = GetRenderer();
            if (!gs || gs.editSelectedSplats == 0)
                return Vector3.zero;
            return gs.editSelectedBounds.center;
        }
    }
}
