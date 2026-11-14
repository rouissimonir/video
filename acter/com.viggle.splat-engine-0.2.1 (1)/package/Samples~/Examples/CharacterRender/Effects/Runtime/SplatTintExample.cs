// SPDX-License-Identifier: MIT
using UnityEngine;
using Viggle.SplatEngine;

namespace Viggle.SplatEngine.Samples
{
    // SAMPLE: pulse a character's tint from C# - a damage flash, say - with no shader editing.
    //
    // Add it next to a SkinnedSplatRenderer, then press F or call Flash() from your own code. It
    // only animates the character's baseColor; the renderer feeds that into the per-splat color
    // hook (ModifySplatColor in GaussianSplatColorModifier.hlsl) for you.
    //
    // baseColor is a plain multiply, so the flash is layered over the character's authored color
    // rather than replacing it: baseColor = authored * lerp(white, flashColor, t). The authored
    // value is captured on first use and restored when the flash ends.
    //
    // This is the common extension path: per-character visual changes without writing HLSL. For
    // custom per-splat logic (dissolve, gradients, sparkle), edit ModifySplatColor instead.
    //
    // Deliberately no [RequireComponent]: a character is often spawned and loaded at runtime, so
    // this resolves the renderer lazily and stays idle until it appears.
    [DisallowMultipleComponent]
    public partial class SplatTintExample : MonoBehaviour
    {
        [Tooltip("Color to flash toward.")]
        public Color m_FlashColor = Color.red;
        [Tooltip("Seconds for one flash to fade back to no tint.")]
        public float m_FlashDuration = 0.25f;
        [Tooltip("Flash automatically on this key (set to None to drive Flash() yourself).")]
        public KeyCode m_FlashKey = KeyCode.F;

        SkinnedSplatRenderer m_Renderer;
        // Lazily resolved: the character may be loaded after this component is enabled.
        SkinnedSplatRenderer Renderer => m_Renderer != null ? m_Renderer : (m_Renderer = GetComponent<SkinnedSplatRenderer>());
        float m_FlashTimer;

        bool m_Captured;
        Color m_OrigColor;

        // Call from your own code (damage events, etc.) to trigger a flash.
        public void Flash() => m_FlashTimer = Mathf.Max(m_FlashDuration, 0.0001f);

        void Update()
        {
            if (m_FlashKey != KeyCode.None && Input.GetKeyDown(m_FlashKey))
                Flash();

            var r = Renderer;
            if (r == null)
                return; // character not loaded yet; try again next frame

            if (m_FlashTimer > 0f)
            {
                if (!m_Captured)
                {
                    m_OrigColor = r.baseColor;
                    m_Captured = true;
                }
                m_FlashTimer -= Time.deltaTime;
                float t = Mathf.Clamp01(m_FlashTimer / Mathf.Max(m_FlashDuration, 0.0001f));
                Apply(r, m_OrigColor * Color.Lerp(Color.white, m_FlashColor, t));
            }
            else if (m_Captured)
            {
                Apply(r, m_OrigColor);
                m_Captured = false;
            }
        }

        void OnDisable() => RestoreOriginal();

        public void ReleaseResources()
        {
            RestoreOriginal();
            m_FlashTimer = 0f;
            m_Renderer = null;
        }

        void RestoreOriginal()
        {
            if (!m_Captured)
                return;
            Apply(Renderer, m_OrigColor);
            m_Captured = false;
        }

        // ApplyVisuals invalidates the renderer's cached per-splat view data, which is what lets a
        // still character and a still camera skip the recompute - so push only on a real change.
        static void Apply(SkinnedSplatRenderer r, Color color)
        {
            if (r == null || r.baseColor == color)
                return;
            r.baseColor = color;
            r.ApplyVisuals();
        }
    }
}
