// SPDX-License-Identifier: MIT
using UnityEngine;
using Viggle.SplatEngine;

namespace Viggle.SplatEngine.Samples
{
    // SAMPLE: a sustained color + opacity state for a splat character - the translucent red
    // "spirit / hit" look. Add it next to a splat character and set Color, Color Strength and
    // Opacity; it drives SkinnedSplatRenderer's baseColor and opacityScale. No shader edits and
    // no renderer feature.
    //
    // For a glowing halo, add URP Bloom to your scene's Volume and push Color into HDR
    // (intensity > 1) so the bright splats bloom.
    //
    // The effect captures the character's authored values the first time it runs and restores them
    // on disable, so toggling it off cleanly reverts the character. Color Strength blends this
    // effect's color in over those authored values rather than replacing them: baseColor is a
    // plain multiply, so multiplying by lerp(white, color, strength) is exactly a strength-weighted
    // tint - which is why this needs no separate strength control on the renderer.
    [DisallowMultipleComponent]
    public partial class SplatColorOpacityEffect : MonoBehaviour
    {
        [Header("Color")]
        [Tooltip("Tint color. Use HDR (intensity > 1) for a glow that Bloom can pick up.")]
        [ColorUsage(false, true)] public Color m_Color = new Color(1.6f, 0.05f, 0.05f, 1f);
        [Range(0f, 1f)] [Tooltip("0 = original color, 1 = fully tinted.")]
        public float m_ColorStrength = 1f;

        [Header("Opacity")]
        [Range(0.05f, 1f)] [Tooltip("Multiplies splat opacity. ~0.4 reads as translucent; 1 = unchanged.")]
        public float m_Opacity = 0.4f;

        [Header("Optional pulse")]
        [Tooltip("Animate opacity for a breathing/flicker feel.")]
        public bool m_Pulse;
        public float m_PulseSpeed = 2f;
        [Range(0.05f, 1f)] public float m_PulseMin = 0.3f;
        [Range(0.05f, 1f)] public float m_PulseMax = 0.55f;

        SkinnedSplatRenderer m_Renderer;
        // Lazily resolved: a character is often spawned and loaded at runtime, after this component
        // is already enabled.
        SkinnedSplatRenderer Renderer => m_Renderer != null ? m_Renderer : (m_Renderer = GetComponent<SkinnedSplatRenderer>());

        bool m_Captured;
        Color m_OrigColor;
        float m_OrigOpacity;

        void CaptureOriginal(SkinnedSplatRenderer r)
        {
            if (m_Captured)
                return;
            m_OrigColor = r.baseColor;
            m_OrigOpacity = r.opacityScale;
            m_Captured = true;
        }

        void LateUpdate()
        {
            var r = Renderer;
            if (r == null)
                return; // character not loaded yet; try again next frame
            CaptureOriginal(r);

            float opacity = m_Opacity;
            if (m_Pulse)
            {
                float t = 0.5f * (1f + Mathf.Sin(Time.time * m_PulseSpeed));
                opacity = Mathf.Lerp(m_PulseMin, m_PulseMax, t);
            }

            Apply(r, m_OrigColor * Color.Lerp(Color.white, m_Color, m_ColorStrength), opacity);
        }

        void OnDisable()
        {
            RestoreOriginal();
        }

        public void ReleaseResources()
        {
            RestoreOriginal();
            m_Renderer = null;
        }

        void RestoreOriginal()
        {
            // Restore so toggling the effect off cleanly reverts the character.
            if (!m_Captured)
                return;
            Apply(Renderer, m_OrigColor, m_OrigOpacity);
            m_Captured = false;
        }

        // ApplyVisuals invalidates the renderer's cached per-splat view data, which is what lets a
        // still character and a still camera skip the recompute. So push only on a real change -
        // this runs every LateUpdate.
        static void Apply(SkinnedSplatRenderer r, Color color, float opacity)
        {
            if (r == null || (r.baseColor == color && r.opacityScale == opacity))
                return;
            r.baseColor = color;
            r.opacityScale = opacity;
            r.ApplyVisuals();
        }
    }
}
