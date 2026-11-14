// SPDX-License-Identifier: MIT
// A pure-CPU point-cloud rasteriser used to draw .vsplat/.bin characters in the inspector
// preview and the Project-window thumbnail.
//
// WHY CPU, AND NOT THE REAL RENDERER
//
// Splat rendering in this package runs through a URP ScriptableRendererFeature plus compute
// passes (sort, skinning, chunk build). None of that exists inside a PreviewRenderUtility
// camera - it has no URP renderer data of its own, and the compute passes are driven from the
// feature, not from a MonoBehaviour we could instantiate off-screen. Rather than build a second
// half-working render path just for previews, this draws the splat CENTRES as a depth-tested
// point cloud on the CPU. It is not photoreal, but it identifies a character at a glance, which
// is all a thumbnail owes you, and it works during import (RenderStaticPreview) where no render
// pipeline is running at all.
//
// STABILITY OVER FIDELITY
//
// Sampling is a fixed stride, never random: the same splats are drawn every frame, so dragging
// the preview rotates a stable cloud instead of a shimmering one.

using System;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine.Editor
{
    internal static class SplatPreviewRenderer
    {
        public const int kDefaultMaxSamples = 40000;

        // Fully-transparent splats are noise in a point cloud (they contribute nothing to the real
        // render either), so they are dropped at capture time rather than fighting for the z-buffer.
        const byte kMinAlpha = 8;

        static readonly Color32 kBackground = new Color32(0, 0, 0, 0);

        /// A managed, already-subsampled copy of the parts of a bin the preview needs.
        /// Exists so callers can Dispose the SplatCharacterBinData (and its Persistent
        /// NativeArrays) immediately after parsing instead of holding them across domain reloads.
        internal sealed class Snapshot
        {
            public Vector3[] positions;
            public Color32[] colors;
            public Vector3 boundsMin;
            public Vector3 boundsMax;
        }

        /// Take a stride-sampled managed copy of `bin`. Returns null if there is nothing drawable.
        /// Never throws.
        public static Snapshot Capture(SplatCharacterBinData bin, int maxSamples = kDefaultMaxSamples)
        {
            try
            {
                if (bin == null || !bin.positions.IsCreated || !bin.colors.IsCreated)
                    return null;

                int n = Mathf.Min(bin.numSplats, Mathf.Min(bin.positions.Length, bin.colors.Length));
                if (n <= 0)
                    return null;

                int budget = Mathf.Max(1, maxSamples);
                int stride = Mathf.Max(1, (n + budget - 1) / budget);
                int capacity = (n + stride - 1) / stride;

                var pos = new Vector3[capacity];
                var col = new Color32[capacity];
                int w = 0;
                for (int i = 0; i < n; i += stride)
                {
                    Color32 c = bin.colors[i];
                    if (c.a < kMinAlpha)
                        continue;
                    float3 p = bin.positions[i];
                    if (!IsFinite(p))
                        continue;
                    pos[w] = new Vector3(p.x, p.y, p.z);
                    col[w] = c;
                    w++;
                }
                if (w == 0)
                    return null;
                if (w != capacity)
                {
                    Array.Resize(ref pos, w);
                    Array.Resize(ref col, w);
                }

                var snap = new Snapshot { positions = pos, colors = col, boundsMin = bin.boundsMin, boundsMax = bin.boundsMax };
                if (!IsUsableBounds(snap.boundsMin, snap.boundsMax))
                    ComputeBounds(pos, out snap.boundsMin, out snap.boundsMax);
                return snap;
            }
            catch
            {
                // A preview is never worth an exception; a missing thumbnail is the correct failure.
                return null;
            }
        }

        /// Build a snapshot from points that were already sampled elsewhere. The static-capture
        /// import path decodes a .ply/.spz exactly once, inside the packer, and never produces a
        /// SplatCharacterBinData - so it hands the sample straight over instead. Same filtering as
        /// the overload above, so both thumbnails read the same way. Never throws.
        public static Snapshot Capture(Vector3[] positions, Color32[] colors, Vector3 boundsMin, Vector3 boundsMax)
        {
            try
            {
                if (positions == null || colors == null)
                    return null;
                int n = Mathf.Min(positions.Length, colors.Length);
                if (n <= 0)
                    return null;

                var pos = new Vector3[n];
                var col = new Color32[n];
                int w = 0;
                for (int i = 0; i < n; i++)
                {
                    Color32 c = colors[i];
                    if (c.a < kMinAlpha)
                        continue;
                    if (!IsFinite(positions[i]))
                        continue;
                    pos[w] = positions[i];
                    col[w] = c;
                    w++;
                }
                if (w == 0)
                    return null;
                if (w != n)
                {
                    Array.Resize(ref pos, w);
                    Array.Resize(ref col, w);
                }

                var snap = new Snapshot { positions = pos, colors = col, boundsMin = boundsMin, boundsMax = boundsMax };
                if (!IsUsableBounds(snap.boundsMin, snap.boundsMax))
                    ComputeBounds(pos, out snap.boundsMin, out snap.boundsMax);
                return snap;
            }
            catch
            {
                return null;
            }
        }

        /// Convenience overload: capture and draw in one call. `bin` is NOT disposed.
        public static void Render(Texture2D target, SplatCharacterBinData bin, Quaternion rotation,
                                  int maxSamples = kDefaultMaxSamples)
        {
            Render(target, Capture(bin, maxSamples), rotation);
        }

        /// Draw `snap` into `target`, orthographically, with `rotation` applied about the bounds
        /// centre. A null/empty snapshot clears the texture. Never throws.
        public static void Render(Texture2D target, Snapshot snap, Quaternion rotation)
        {
            if (target == null)
                return;
            int w = target.width, h = target.height;
            if (w <= 0 || h <= 0)
                return;

            Color32[] pixels = GetColorScratch(w * h);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = kBackground;

            try
            {
                Rasterize(pixels, w, h, snap, rotation);
            }
            catch
            {
                // Fall through and present whatever was drawn; a preview must not throw.
            }

            target.SetPixels32(pixels);
            target.Apply(false);
        }

        static void Rasterize(Color32[] pixels, int w, int h, Snapshot snap, Quaternion rotation)
        {
            if (snap?.positions == null || snap.colors == null || snap.positions.Length == 0)
                return;

            Vector3 centre = (snap.boundsMin + snap.boundsMax) * 0.5f;
            Vector3 size = snap.boundsMax - snap.boundsMin;
            float longest = Mathf.Max(Mathf.Abs(size.x), Mathf.Max(Mathf.Abs(size.y), Mathf.Abs(size.z)));
            if (!IsFinite(centre) || float.IsNaN(longest) || float.IsInfinity(longest) || longest <= 1e-6f)
            {
                // Degenerate or nonsense bounds (a single splat, a coincident min/max, NaNs in the
                // header): fall back to a unit frame so we still draw something instead of dividing
                // by ~0 and scattering points to infinity.
                if (!IsFinite(centre)) centre = Vector3.zero;
                longest = 1f;
            }

            // Fit the longest AABB axis into the short screen axis with a 10% margin. Using the
            // axis rather than the projected silhouette keeps the scale rotation-invariant, so the
            // character does not breathe in and out while you drag.
            float ppu = Mathf.Min(w, h) / (longest * 1.1f);
            if (!IsFinite(ppu) || ppu <= 0f)
                return;

            // Blob radius scales with the texture so the cloud reads as a surface at 400px and is
            // still visible at a 64px thumbnail.
            int radius = Mathf.Max(1, Mathf.RoundToInt(h / 128f));
            int r2 = radius * radius;

            float[] depth = GetDepthScratch(w * h);
            for (int i = 0; i < depth.Length; i++)
                depth[i] = float.MaxValue;

            float cx = w * 0.5f, cy = h * 0.5f;
            var pos = snap.positions;
            var col = snap.colors;
            int count = Mathf.Min(pos.Length, col.Length);

            for (int i = 0; i < count; i++)
            {
                Vector3 v = rotation * (pos[i] - centre);
                if (!IsFinite(v))
                    continue;

                // Camera sits on +Z looking down -Z with +Y up, so screen-right is world -X and
                // depth grows away from the viewer. Identity rotation therefore shows the character
                // from the front (bin data faces +Z after SplatConvention) and unmirrored.
                float sxf = cx - v.x * ppu;
                float syf = cy + v.y * ppu;
                float d = -v.z;

                int px = Mathf.FloorToInt(sxf);
                int py = Mathf.FloorToInt(syf);
                if (px < -radius || py < -radius || px > w + radius || py > h + radius)
                    continue;

                Color32 c = col[i];
                c.a = 255;

                int x0 = Mathf.Max(0, px - radius), x1 = Mathf.Min(w - 1, px + radius);
                int y0 = Mathf.Max(0, py - radius), y1 = Mathf.Min(h - 1, py + radius);
                for (int y = y0; y <= y1; y++)
                {
                    int dy = y - py;
                    int row = y * w;
                    for (int x = x0; x <= x1; x++)
                    {
                        int dx = x - px;
                        if (dx * dx + dy * dy > r2)
                            continue;
                        int idx = row + x;
                        if (d >= depth[idx])
                            continue;
                        depth[idx] = d;
                        pixels[idx] = c;
                    }
                }
            }
        }

        static bool IsUsableBounds(Vector3 min, Vector3 max)
        {
            if (!IsFinite(min) || !IsFinite(max))
                return false;
            Vector3 s = max - min;
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))) > 1e-6f;
        }

        static void ComputeBounds(Vector3[] pts, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < pts.Length; i++)
            {
                min = Vector3.Min(min, pts[i]);
                max = Vector3.Max(max, pts[i]);
            }
            if (!IsUsableBounds(min, max))
            {
                min = Vector3.one * -0.5f;
                max = Vector3.one * 0.5f;
            }
        }

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        static bool IsFinite(float3 v) => math.all(math.isfinite(v));

        static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        // Scratch buffers: a drag repaints continuously, and a 400x400 preview would otherwise
        // churn ~1MB of garbage per frame.
        static Color32[] s_Colors;
        static float[] s_Depth;

        static Color32[] GetColorScratch(int n)
        {
            if (s_Colors == null || s_Colors.Length != n)
                s_Colors = new Color32[n];
            return s_Colors;
        }

        static float[] GetDepthScratch(int n)
        {
            if (s_Depth == null || s_Depth.Length < n)
                s_Depth = new float[n];
            return s_Depth;
        }
    }
}
