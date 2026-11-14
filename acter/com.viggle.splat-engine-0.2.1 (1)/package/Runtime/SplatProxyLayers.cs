// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Which layers the relight proxies are on: written by the components that create them, read
    /// by the renderer feature that has to draw them.
    /// </summary>
    /// <remarks>
    /// The two halves of relighting cannot see each other - one is a component in a scene, the
    /// other a feature on a renderer PROJECT asset - so the proxy layer used to be agreed by two
    /// hardcoded defaults on opposite sides of the package (layer 10 on the component, Nothing on
    /// the feature), and nothing said so when they disagreed: the feature filtered for a layer no
    /// proxy was on, the lighting texture came up empty, and every character silently kept its
    /// baked appearance. This is the one answer both halves read.
    ///
    /// A plain static registry, the same shape the feature already walks for its consumers
    /// (GaussianSplatRenderSystem.GetRegisteredSplats), and deliberately runtime rather than an
    /// editor fixup: features are baked into the renderer asset, so a build cannot repair the
    /// wiring and the two halves have to agree on their own.
    ///
    /// Only enabled components register, and only in play mode: SplatRelight is not
    /// [ExecuteAlways], so OnEnable does not run while the editor is merely sitting in a scene.
    /// That is the right answer rather than a gap - nothing generates proxies then either, so
    /// there is nothing for the feature to draw.
    /// </remarks>
    static class SplatProxyLayers
    {
        static readonly List<SplatRelight> s_Registered = new List<SplatRelight>();

        /// <summary>Called from SplatRelight.OnEnable.</summary>
        public static void Register(SplatRelight relight)
        {
            // Contains() rather than a bare Add: with Reload Domain off, this list outlives a play
            // session while the components re-enable around it, and a duplicate would keep a dead
            // component's layer in the union after its OnDisable ran once.
            if (relight != null && !s_Registered.Contains(relight))
                s_Registered.Add(relight);
        }

        /// <summary>Called from SplatRelight.OnDisable.</summary>
        public static void Unregister(SplatRelight relight)
        {
            s_Registered.Remove(relight);
        }

        /// <summary>How many SplatRelight components are enabled right now.</summary>
        public static int Count => s_Registered.Count;

        /// <summary>
        /// The one layer index a proxy layer MASK means, or -1 for an empty mask. The field is a
        /// mask only so the inspector draws Unity's layer dropdown instead of a raw number; a
        /// GameObject lives on a single layer, and with several bits set the lowest wins.
        /// </summary>
        public static int Resolve(int mask)
        {
            for (int bit = 0; bit < 32; bit++)
                if ((mask & (1 << bit)) != 0)
                    return bit;
            return -1;
        }

        /// <summary>
        /// The layers every enabled SplatRelight puts its proxy on, as a layer mask. 0 when none
        /// are enabled, which reads the same as "no proxy layer set" - nothing to draw either way.
        /// </summary>
        public static int Union()
        {
            int mask = 0;
            for (int i = 0; i < s_Registered.Count; i++)
            {
                var relight = s_Registered[i];
                if (relight == null)
                    continue;
                int layer = relight.ProxyLayerIndex;
                // Below zero is "no layer picked". Folding that in as layer 0 would put Default
                // into the feature's draw filter and bake every opaque object in the scene into
                // the lighting texture, which is a far louder wrong answer than drawing nothing.
                if (layer >= 0)
                    mask |= 1 << layer;
            }
            return mask;
        }

        /// <summary>Drops every registration; see SplatEngineLifecycle.ReleaseAll.</summary>
        public static void ResetStatics()
        {
            s_Registered.Clear();
        }
    }
}
