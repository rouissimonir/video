// SPDX-License-Identifier: MIT
using UnityEngine;
using Viggle.SplatEngine;

namespace Viggle.SplatEngine.Samples
{
    // SAMPLE: marks a splat character for a screen-space outline. Add it next to a
    // SkinnedSplatRenderer and pick a color; enabling and disabling the component toggles that
    // character's outline, and overlapping characters keep their own colors.
    //
    // This component only registers the character. GaussianSplatOutlineFeature does the drawing,
    // and it has to be added once to your URP renderer asset or nothing appears - see EFFECTS.md.
    //
    // Deliberately no [RequireComponent]: a character is often spawned and loaded at runtime, so
    // this resolves the renderer lazily and stays idle (no outline) until it appears. A
    // [RequireComponent] would instead spawn an empty renderer that logs "no asset assigned".
    [DisallowMultipleComponent]
    public partial class SplatOutline : MonoBehaviour
    {
        [Tooltip("Outline color for this character.")]
        [ColorUsage(false, true)] public Color m_Color = Color.white;

        // The characters currently asking for an outline, walked each frame by
        // GaussianSplatOutlineFeature.
        static SplatOutline[] s_Active = new SplatOutline[16];
        static int s_ActiveCount;

        SkinnedSplatRenderer m_Renderer;
        Material m_MaskMaterial;

        // Resolved lazily and re-checked every access, because the character may be added to this
        // GameObject well after this component is enabled.
        public SkinnedSplatRenderer Renderer => m_Renderer != null ? m_Renderer : (m_Renderer = GetComponent<SkinnedSplatRenderer>());

        public static int ActiveCount => s_ActiveCount;

        public static SplatOutline GetActive(int index)
        {
            return index >= 0 && index < s_ActiveCount ? s_Active[index] : null;
        }

        // The mask material for this character, created from the feature's mask shader.
        //
        // One material per character, never a shared one: on OpenGL ES, consecutive silhouette
        // draws through a shared material alias each other's vertex-stage buffers and the outlines
        // come out wrong.
        public Material GetMaskMaterial(Shader maskShader)
        {
            if (maskShader == null)
                return null;
            if (m_MaskMaterial == null || m_MaskMaterial.shader != maskShader)
            {
                if (m_MaskMaterial != null)
                    DestroyImmediate(m_MaskMaterial);
                m_MaskMaterial = new Material(maskShader) { name = $"SplatOutlineMask ({name})" };
                // Mirror the renderer's vertex-stage data path, or the mask draws nothing on the
                // devices that need it.
                if (SplatCustomPass.RequiresVertexTextureFetch)
                    m_MaskMaterial.EnableKeyword(SplatCustomPass.VertexTextureFetchKeyword);
            }
            return m_MaskMaterial;
        }

        // Register unconditionally; the feature skips us each frame until Renderer resolves, so a
        // runtime-added renderer just starts getting an outline once it exists.
        void OnEnable() => RegisterActive(this);
        void OnDisable() => UnregisterActive(this);

        void OnDestroy()
        {
            Dispose(false);
        }

        public void ReleaseResources()
        {
            Dispose(true);
        }

        void Dispose(bool immediate)
        {
            if (m_MaskMaterial != null)
                DestroyUnityObject(m_MaskMaterial, immediate);
            m_MaskMaterial = null;
            m_Renderer = null;
            UnregisterActive(this);
        }

        public static void RegisterActive(SplatOutline outline)
        {
            if (outline == null || IndexOf(outline) >= 0)
                return;

            if (s_ActiveCount == s_Active.Length)
            {
                var expanded = new SplatOutline[s_Active.Length * 2];
                System.Array.Copy(s_Active, expanded, s_Active.Length);
                s_Active = expanded;
            }

            s_Active[s_ActiveCount++] = outline;
        }

        public static void UnregisterActive(SplatOutline outline)
        {
            int index = IndexOf(outline);
            if (index < 0)
                return;

            for (int i = index; i < s_ActiveCount - 1; i++)
                s_Active[i] = s_Active[i + 1];
            s_ActiveCount--;
            s_Active[s_ActiveCount] = null;
        }

        public static void ResetStatics()
        {
            while (s_ActiveCount > 0)
            {
                var outline = s_Active[s_ActiveCount - 1];
                if (outline == null)
                {
                    s_Active[s_ActiveCount - 1] = null;
                    s_ActiveCount--;
                    continue;
                }

                outline.ReleaseResources();
            }

            System.Array.Clear(s_Active, 0, s_Active.Length);
            s_ActiveCount = 0;
        }

        static int IndexOf(SplatOutline outline)
        {
            for (int i = 0; i < s_ActiveCount; i++)
            {
                if (s_Active[i] == outline)
                    return i;
            }
            return -1;
        }

        static void DestroyUnityObject(Object obj, bool immediate)
        {
            if (Application.isPlaying && !immediate)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
    }
}
