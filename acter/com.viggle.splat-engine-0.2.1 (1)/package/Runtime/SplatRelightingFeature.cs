// SPDX-License-Identifier: MIT
//
// Unity 6 / URP 17 only, and the whole file compiles out below that. This feature is Render Graph
// only - see the remarks on the class - so on URP 14 the base class's Create and AddRenderPasses
// have no implementation here and the type does not compile at all. Better absent than
// half-present: a host on 2022 gets no relighting, rather than a package that will not build.
// If a compatibility path is ever written, it goes in the #else of the inner guard at
// ProxyLightingPass and this one relaxes back to GS_ENABLE_URP.
#if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Lights Gaussian splat characters with Unity's real-time lights, via a proxy mesh.
    /// </summary>
    /// <remarks>
    /// Splats carry no surface material, so they cannot be lit directly. Instead each character
    /// gets a rough proxy mesh matching its silhouette, on its own layer and hidden from the
    /// camera. This feature renders those proxies with Unity's lighting into a screen-space
    /// texture, and every splat samples the pixel it projects to (see the relight block in
    /// GaussianSplatColorModifier.hlsl) to decide how it is lit.
    ///
    /// Setup:
    ///   1. Add this feature to your URP renderer, BEFORE GaussianSplatURPFeature - the lighting
    ///      texture has to exist before the splat pass computes its colors.
    ///   2. Put proxy meshes on a dedicated layer. With Proxy Layer Source on Auto that is all:
    ///      the feature draws whatever layers the scene's SplatRelight components use. On Manual
    ///      it draws the Proxy Layers mask below, and the two have to be kept in step by hand.
    ///   3. Hide that layer by excluding it from the renderer's Opaque and Transparent layer
    ///      masks - NOT from the camera's Culling Mask. The camera must keep the layer checked:
    ///      this feature draws the proxies out of the camera's culling results, and a layer the
    ///      camera culls never reaches it (the lighting texture silently comes up empty).
    ///      Proxies MAY cast shadows - that is how the character gets one (SplatVoxelProxy
    ///      turns it on) - the bake shader biases its own shadow lookup against acne.
    ///   4. Raise Relight Strength on each character's SplatRenderer (it defaults to 0).
    ///
    /// Requires Unity 6 / URP 17 with Render Graph enabled: there is no compatibility-mode path.
    /// </remarks>
    // ReSharper disable once InconsistentNaming
    public class SplatRelightingFeature : ScriptableRendererFeature
    {
        public enum DebugView
        {
            [Tooltip("Normal operation.")] None = 0,
            [Tooltip("Show the proxy lighting itself, full screen.")] RelightRGB = 1,
            [Tooltip("Show the proxy coverage mask - white where a proxy covers the pixel.")] MaskAlpha = 2,
            [Tooltip("Draw the proxy meshes as a translucent ghost with a bright rim over the " +
                     "final image, for checking how they line up with the splats.")] ProxyOverlay = 3,
        }

        // Debugging is driven from SplatRelight on the character, not from fields here, so the
        // feature never needs touching once configured. A component requests a view every frame
        // it wants one; the stamp expires the request when nobody asks anymore. Debug shaders
        // resolve via Shader.Find - debugging is an editor activity, player builds skip it.
        static DebugView s_DebugRequest = DebugView.None;
        static int s_DebugRequestFrame = -1000;

        /// <summary>Show a debug view this frame (call every frame you want it, e.g. from LateUpdate).</summary>
        public static void RequestDebug(DebugView view)
        {
            s_DebugRequest = view;
            s_DebugRequestFrame = Time.frameCount;
        }

        DebugView ActiveDebug => Time.frameCount - s_DebugRequestFrame <= 2 ? s_DebugRequest : DebugView.None;

        [Tooltip("Master switch. When off, no pass runs and characters render with their baked appearance.")]
        public bool m_RelightEnabled = true;

        /// <summary>Where the layers this feature draws come from.</summary>
        public enum ProxyLayerSource
        {
            [Tooltip("Draw exactly the Proxy Layers mask below.")]
            Manual = 0,
            [Tooltip("Draw whatever layers the scene's SplatRelight components put their proxies " +
                     "on, so the two halves cannot drift apart.")]
            Auto = 1,
        }

        [Tooltip("Auto: draw whatever layers the SplatRelight components in the scene actually put " +
                 "their proxies on - nothing to keep in sync, and it works in a build. Manual: " +
                 "draw exactly the Proxy Layers mask below.\n\n" +
                 "Manual is the serialized default so that adding this option cannot change what " +
                 "an already-configured renderer draws; new features added from a splat " +
                 "component's inspector are created on Auto.")]
        public ProxyLayerSource m_ProxyLayerSource = ProxyLayerSource.Manual;

        [Tooltip("The layer(s) your proxy meshes live on, used when Proxy Layer Source is Manual. " +
                 "Hide them by excluding this layer from the " +
                 "renderer's Opaque/Transparent Layer Masks - but keep it CHECKED in the camera's " +
                 "Culling Mask, or the proxies are culled before this feature can draw them and the " +
                 "lighting texture comes up empty. Turn off Cast Shadows on proxies too.")]
        public LayerMask m_ProxyLayerMask = 0;

        [Range(0.25f, 1f)]
        [Tooltip("Resolution of the lighting texture, as a fraction of the camera target. Lighting is " +
                 "low-frequency and is sampled once per splat rather than per pixel, so half resolution " +
                 "is normally indistinguishable and costs a quarter of the bandwidth.")]
        public float m_RenderScale = 0.5f;

        [Range(0.1f, 4f)]
        [Tooltip("The proxy brightness that should count as 'normally lit'. Sampled lighting is divided " +
                 "by this, so Relight Strength changes lighting CONTRAST instead of overall brightness.\n\n" +
                 "If characters get darker as you raise Relight Strength, lower this; if they wash out, raise it.")]
        public float m_ReferenceLuminance = 1f;

        [Tooltip("When the proxies are rendered. The default runs after opaques, so the main light's " +
                 "shadow map already exists and the proxies can receive shadows, and before the splat " +
                 "pass at BeforeRenderingTransparents.")]
        public RenderPassEvent m_PassEvent = RenderPassEvent.AfterRenderingOpaques;

        // There is deliberately NO material override slot: the bake shader's output contract
        // (light color x shadow, no N.L, alpha = coverage) is what the splat compute assumes.
        // An arbitrary material here - a white URP/Lit, say - bakes the blocky proxy's own
        // surface shading into the texture, which reads as blotches on the character and
        // flickers during animation. A field for this existed and cost a debugging session
        // when a leftover test material sat in it.

        [Range(0f, 0.3f)]
        [Tooltip("Shadow lookup bias for the light bake, in meters. Smaller lets the character's " +
                 "own body cast shadows on itself (arm on torso, head on shoulders); larger " +
                 "suppresses shadow speckle on the proxy surface. With the smoothed voxel proxy, " +
                 "small values are usually clean.")]
        public float m_SelfShadowBias = 0.03f;

        [Tooltip("Shader backing the default proxy light bake (SplatProxyLight.shader from the " +
                 "package). Found automatically in the editor; assign explicitly for player builds.")]
        public Shader m_ProxyLightShader;

        [Tooltip("Shader backing the relight blur (SplatRelightBlur.shader from the package). The " +
                 "blur is what keeps the lighting temporally stable while the character animates. " +
                 "Found automatically in the editor; assign explicitly for player builds.")]
        public Shader m_RelightBlurShader;


        const string k_RelightRTName = "_GsRelightRT";
        const string k_DebugShaderName = "Hidden/Gaussian Splatting/Splat Relight Debug";
        const string k_ProxyDebugShaderName = "Hidden/Gaussian Splatting/Splat Proxy Debug";
        const string k_ProxyLightShaderName = "Hidden/Gaussian Splatting/Splat Proxy Light";
        const string k_RelightBlurShaderName = "Hidden/Gaussian Splatting/Splat Relight Blur";
        static readonly int s_RelightBlurTexel = Shader.PropertyToID("_RelightBlurTexel");
        static readonly int s_ProxyShadowBias = Shader.PropertyToID("_GsProxyShadowBias");
        static readonly int s_DebugMode = Shader.PropertyToID("_RelightDebugMode");
        static readonly int s_ProxyDebugColor = Shader.PropertyToID("_ProxyDebugColor");

        Material m_DebugMaterial;
        Material m_ProxyOverlayMaterial;
        Material m_ProxyLightMaterial;
        Material m_RelightBlurMaterial;
        // Static so the render pass (which must not capture the feature instance) and
        // SplatRenderer can both see the current frame's texture.
        static bool s_HotReloadReleased;

        /// <summary>Every renderer that currently wants relighting, refreshed per frame.</summary>
        static readonly List<SplatRenderer> s_Consumers = new();
        // Scratch for the registry walk, reused so the per-frame gather allocates nothing.
        static readonly List<SplatRenderer> s_ScratchAll = new();

        static void GatherConsumers(Camera cam)
        {
            s_Consumers.Clear();
            GaussianSplatRenderSystem.instance.GetRegisteredSplats(s_ScratchAll);
            for (int i = 0; i < s_ScratchAll.Count; i++)
            {
                var gs = s_ScratchAll[i];
                // The per-splat debug view needs the texture published even at strength 0.
                if (gs == null || (gs.m_RelightStrength <= 0f && !gs.m_RelightDebugSplatView))
                    continue;
                if ((cam.cullingMask & (1 << gs.gameObject.layer)) == 0)
                    continue;
                s_Consumers.Add(gs);
            }
        }

        /// <summary>
        /// Hands this frame's lighting texture to every character that wants it. Called from the
        /// render pass once the proxies have been drawn.
        /// </summary>
        static void PublishToConsumers(Texture tex, Vector2Int size, float referenceLuminance)
        {
            float invRef = 1f / Mathf.Max(referenceLuminance, 1e-4f);
            var p = new Vector4(1f / Mathf.Max(size.x, 1), 1f / Mathf.Max(size.y, 1), invRef, 1f);
            for (int i = 0; i < s_Consumers.Count; i++)
            {
                var gs = s_Consumers[i];
                if (gs == null)
                    continue;
                gs.m_RelightTexture = tex;
                gs.m_RelightParams = p;
            }
        }

        /// <summary>
        /// Clears the published texture so a stale render target is never sampled on a frame where
        /// no proxy pass ran (feature disabled, no proxy layer set, hot reload).
        /// </summary>
        static void ClearConsumers()
        {
            GaussianSplatRenderSystem.instance.GetRegisteredSplats(s_ScratchAll);
            for (int i = 0; i < s_ScratchAll.Count; i++)
            {
                var gs = s_ScratchAll[i];
                if (gs == null)
                    continue;
                gs.m_RelightTexture = null;
                gs.m_RelightParams = Vector4.zero;
            }
        }

#if UNITY_6000_0_OR_NEWER
        class ProxyLightingPass : ScriptableRenderPass
        {
            const string ProfilerTag = "SplatProxyLighting";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);

            public Material OverrideMaterial;
            public Material DebugMaterial;
            public LayerMask ProxyLayerMask;
            public DebugView Debug;
            // The persistent targets the feature allocates (see EnsureRelightTarget). The pass
            // imports them rather than creating transients: transients die when the pass ends.
            // Color and depth are SEPARATE textures because ImportTexture rejects a texture
            // carrying both a color and a depthStencil format.
            public RTHandle TargetRT;
            public RTHandle TargetDepth;
            public RTHandle TargetBlur;
            public Material BlurMaterial;
            public Vector4 BlurTexel;

            class PassData
            {
                internal TextureHandle RelightRT;
                internal TextureHandle RelightDepth;
                internal TextureHandle RelightBlur;
                internal TextureHandle CameraColor;
                internal RendererListHandle Proxies;
                internal DebugView Debug;
                internal Material DebugMaterial;
                internal Material BlurMaterial;
                internal Vector4 BlurTexel;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                // No material check: the default path has no override material at all (the proxy
                // draws with its own), so requiring one here would disable the feature entirely.
                if (s_HotReloadReleased || ProxyLayerMask == 0 || TargetRT == null || TargetDepth == null)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var lightData = frameData.Get<UniversalLightData>();

                // IMPORT the persistent target, never CreateRenderGraphTexture: a graph-created
                // texture only lives for the passes that declare it, and the consumer of this one
                // - SplatRenderer's compute dispatch, recorded inside GaussianSplat-
                // URPFeature's pass - never declares it. With a transient, the graph recycled the
                // memory right after this pass: the debug view (which reads within the pass)
                // looked perfect while every splat sampled a dead resource and came out one
                // uniform value, plus "kTextureUploadUninitialized" errors. An imported RTHandle
                // is owned memory the graph must leave alone.
                var colorHandle = renderGraph.ImportTexture(TargetRT);
                var depthHandle = renderGraph.ImportTexture(TargetDepth);
                var blurHandle = TargetBlur != null ? renderGraph.ImportTexture(TargetBlur) : TextureHandle.nullHandle;

                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                // Opaque queue, proxy layers only, overridden to the white lit material so what
                // lands in the RT is lighting rather than the proxy's own albedo.
                var sortFlags = cameraData.defaultOpaqueSortFlags;
                var drawSettings = RenderingUtils.CreateDrawingSettings(
                    new ShaderTagId("UniversalForward"), renderingData, cameraData, lightData, sortFlags);
                // Every proxy draws with the light-bake material (see SplatProxyLight.shader):
                // what lands in the texture must be light color x shadowing, never the proxy's
                // own appearance. The bake shader declares its own shadow multi_compiles, so it
                // is safe as an override - unlike URP/Lit, whose per-object keyword needs made
                // an earlier override attempt come out flat-shaded.
                drawSettings.overrideMaterial = OverrideMaterial;
                drawSettings.overrideMaterialPassIndex = 0;
                var filterSettings = new FilteringSettings(RenderQueueRange.opaque, ProxyLayerMask);
                passData.Proxies = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawSettings, filterSettings));

                passData.RelightRT = colorHandle;
                passData.RelightDepth = depthHandle;
                passData.RelightBlur = blurHandle;
                passData.CameraColor = resourceData.activeColorTexture;
                passData.Debug = DebugMaterial != null ? Debug : DebugView.None;
                passData.DebugMaterial = DebugMaterial;
                passData.BlurMaterial = BlurMaterial;
                passData.BlurTexel = BlurTexel;

                builder.UseTexture(colorHandle, AccessFlags.ReadWrite);
                builder.UseTexture(depthHandle, AccessFlags.ReadWrite);
                if (blurHandle.IsValid())
                    builder.UseTexture(blurHandle, AccessFlags.ReadWrite);
                builder.UseRendererList(passData.Proxies);
                if (passData.Debug != DebugView.None)
                    builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    if (s_HotReloadReleased)
                        return;

                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(cmd, s_profilingSampler);

                    // Color + depth attachment (same overload GaussianSplatURPFeature uses for
                    // its own RT+depth binding). ClearFlag.All clears both. Colour clears to
                    // black with alpha 0 - alpha is the proxy coverage mask, so "nothing drawn
                    // here" must read as "no proxy", not as "unlit proxy".
                    CoreUtils.SetRenderTarget(cmd, data.RelightRT, data.RelightDepth, ClearFlag.All, Color.clear);
                    cmd.DrawRendererList(data.Proxies);

                    // Blur into the texture the splats sample (see SplatRelightBlur.shader).
                    bool blurred = data.BlurMaterial != null && data.RelightBlur.IsValid();
                    if (blurred)
                    {
                        data.BlurMaterial.SetVector(s_RelightBlurTexel, data.BlurTexel);
                        Blitter.BlitCameraTexture(cmd, data.RelightRT, data.RelightBlur, data.BlurMaterial, 0);
                    }

                    // Debug: draw the lighting texture over the camera image, exactly as the
                    // splats will sample it.
                    if (data.Debug != DebugView.None)
                    {
                        data.DebugMaterial.SetInteger(s_DebugMode, (int)data.Debug);
                        Blitter.BlitCameraTexture(cmd, blurred ? data.RelightBlur : data.RelightRT,
                            data.CameraColor, data.DebugMaterial, 0);
                    }
                });
            }
        }

        /// <summary>
        /// Debug View > Proxy Overlay: draws the proxies again, over the FINISHED image, as a
        /// translucent ghost with a fresnel rim. Runs after transparents so it sits on top of the
        /// splats - that is the whole point, seeing proxy and character in the same place. Depth
        /// is attached read-only, so scene opaques still occlude the ghost correctly while the
        /// splats (which write no depth) never hide it.
        /// </summary>
        class ProxyOverlayPass : ScriptableRenderPass
        {
            const string ProfilerTag = "SplatProxyOverlay";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);

            public Material OverlayMaterial;
            public LayerMask ProxyLayerMask;

            class PassData
            {
                internal TextureHandle Color;
                internal TextureHandle Depth;
                internal RendererListHandle Proxies;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (s_HotReloadReleased || OverlayMaterial == null || ProxyLayerMask == 0)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var lightData = frameData.Get<UniversalLightData>();

                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var drawSettings = RenderingUtils.CreateDrawingSettings(
                    new ShaderTagId("UniversalForward"), renderingData, cameraData, lightData,
                    cameraData.defaultOpaqueSortFlags);
                drawSettings.overrideMaterial = OverlayMaterial;
                drawSettings.overrideMaterialPassIndex = 0;
                var filterSettings = new FilteringSettings(RenderQueueRange.opaque, ProxyLayerMask);
                passData.Proxies = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawSettings, filterSettings));
                passData.Color = resourceData.activeColorTexture;
                passData.Depth = resourceData.activeDepthTexture;

                builder.UseTexture(passData.Color, AccessFlags.ReadWrite);
                builder.UseTexture(passData.Depth, AccessFlags.ReadWrite);
                builder.UseRendererList(passData.Proxies);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    if (s_HotReloadReleased)
                        return;
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(cmd, s_profilingSampler);
                    CoreUtils.SetRenderTarget(cmd, data.Color, data.Depth, ClearFlag.None);
                    cmd.DrawRendererList(data.Proxies);
                });
            }
        }

        ProxyLightingPass m_Pass;
        ProxyOverlayPass m_OverlayPass;

        public override void Create()
        {
            s_HotReloadReleased = false;
            m_Pass = new ProxyLightingPass { renderPassEvent = m_PassEvent };
            m_OverlayPass = new ProxyOverlayPass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (s_HotReloadReleased || m_Pass == null)
                return;
            // Game cameras only. The relight texture is screen-space and sized per camera;
            // serving the scene view, previews and reflection probes too would mean juggling
            // several sizes and per-platform y-origins in one texture for no gameplay payoff.
            // Skipped cameras get ClearConsumers, which is what makes the splat compute fall
            // back cleanly to the baked appearance (params.w == 0) for their dispatches.
            if (renderingData.cameraData.cameraType != CameraType.Game)
            {
                ClearConsumers();
                return;
            }

            var cam = renderingData.cameraData.camera;
            if (!m_RelightEnabled)
            {
                ClearConsumers();
                return;
            }

            // The two halves that used to be kept in step by hand: where the scene's proxies
            // actually are, and what this feature is set to draw.
            int proxyLayers = SplatProxyLayers.Union();
            int drawLayers = m_ProxyLayerSource == ProxyLayerSource.Auto
                ? proxyLayers
                : m_ProxyLayerMask.value;
            if (drawLayers == 0)
            {
                // Silent when nothing wants relighting - that is just a scene with no characters
                // in it. The case worth a line is a scene full of proxies against a feature
                // filtering for no layer at all, which renders an empty lighting texture and
                // leaves every character on its baked appearance with nothing said.
                if (proxyLayers != 0)
                    WarnOnce("SplatRelightingFeature: Proxy Layers is empty, so nothing can be " +
                             $"drawn into the lighting texture, but {SplatProxyLayers.Count} " +
                             $"SplatRelight component(s) are putting proxies on " +
                             $"{DescribeLayers(proxyLayers)}. Set Proxy Layer Source to Auto, or " +
                             "select that layer in Proxy Layers.");
                ClearConsumers();
                return;
            }
            // Same failure, one step further along: a mask that is set but points somewhere else.
            // Not fatal - a project may legitimately draw proxy geometry this package did not
            // generate - so the pass still runs and only the mismatch is reported.
            if (proxyLayers != 0 && (drawLayers & proxyLayers) == 0)
            {
                WarnOnce($"SplatRelightingFeature: Proxy Layers is {DescribeLayers(drawLayers)}, " +
                         $"but the scene's SplatRelight proxies are on " +
                         $"{DescribeLayers(proxyLayers)}, so the lighting texture comes up empty. " +
                         "Set Proxy Layer Source to Auto, or point the two at the same layer.");
            }
            // The proxy draw list is built from this camera's culling results, so a proxy layer
            // the camera culls away never reaches this feature at all - the classic mistake is
            // hiding the proxies by unchecking their layer on the CAMERA, which silently empties
            // the lighting texture. Hide them via the renderer's Opaque/Transparent layer masks
            // instead; those only affect URP's own passes.
            if ((cam.cullingMask & drawLayers) == 0)
            {
                WarnOnce($"SplatRelightingFeature: camera '{cam.name}' culls away every proxy layer, " +
                         "so no proxy can render into the lighting texture. Keep the proxy layer " +
                         "CHECKED in the camera's Culling Mask and hide it via the renderer's " +
                         "Opaque and Transparent Layer Masks instead.");
                ClearConsumers();
                return;
            }

            var debugView = ActiveDebug;
            GatherConsumers(cam);
            // Nothing on screen wants relighting: skip the proxy draw entirely rather than
            // rendering a texture no one reads.
            //
            // Debug View is the exception, and deliberately so: it exists to answer "are my
            // proxies rendering at all", which is the question you ask BEFORE any character has
            // Relight Strength turned up. Skipping the pass here would make the debug view show
            // an empty screen precisely when it is most needed, which reads as "the feature is
            // broken" rather than "no consumers yet".
            if (s_Consumers.Count == 0 && debugView == DebugView.None)
            {
                ClearConsumers();
                return;
            }

            EnsureMaterial();
            var bakeMaterial = m_ProxyLightMaterial;
            if (bakeMaterial == null)
            {
                WarnOnce("SplatRelightingFeature: proxy light-bake shader not found. Assign " +
                         "SplatProxyLight.shader to the Proxy Light Shader field.");
                ClearConsumers();
                return;
            }
            bool wantsBlit = debugView == DebugView.RelightRGB || debugView == DebugView.MaskAlpha;
            if (wantsBlit && m_DebugMaterial == null)
            {
                WarnOnce("SplatRelightingFeature: Debug View is on but its shader was not found. " +
                         "Assign SplatRelightDebug.shader to the Debug Shader field.");
            }
            if (debugView == DebugView.ProxyOverlay && m_ProxyOverlayMaterial == null)
            {
                WarnOnce("SplatRelightingFeature: Proxy Overlay debug view is on but its shader was " +
                         "not found. Assign SplatProxyDebug.shader to the Proxy Debug Shader field.");
            }

            // Persistent target sized for this camera (grow-only; see EnsureRelightTarget), and
            // published here rather than from inside the render pass: the texture is a plain
            // RenderTexture that exists regardless of the graph, so the reference is valid the
            // moment the splat pass's compute wants it, and the params always describe the
            // region THIS camera renders into.
            var camDesc = renderingData.cameraData.cameraTargetDescriptor;
            int w = Mathf.Max(1, Mathf.RoundToInt(camDesc.width * m_RenderScale));
            int h = Mathf.Max(1, Mathf.RoundToInt(camDesc.height * m_RenderScale));
            EnsureRelightTarget(w, h);
            if (m_RelightTex == null || m_RelightDepthTex == null)
            {
                ClearConsumers();
                return;
            }
            PublishToConsumers(
                m_RelightBlurMaterial != null && m_RelightBlurTex != null ? m_RelightBlurTex : (Texture)m_RelightTex,
                new Vector2Int(w, h), m_ReferenceLuminance);

            m_Pass.renderPassEvent = m_PassEvent;
            bakeMaterial.SetFloat(s_ProxyShadowBias, m_SelfShadowBias);
            m_Pass.OverrideMaterial = bakeMaterial;
            m_Pass.DebugMaterial = m_DebugMaterial;
            m_Pass.ProxyLayerMask = drawLayers;
            // The main pass only knows the fullscreen-blit views; ProxyOverlay is its own pass.
            m_Pass.Debug = wantsBlit ? debugView : DebugView.None;
            m_Pass.TargetRT = m_RelightHandle;
            m_Pass.TargetDepth = m_RelightDepthHandle;
            m_Pass.TargetBlur = m_RelightBlurHandle;
            m_Pass.BlurMaterial = m_RelightBlurMaterial;
            m_Pass.BlurTexel = new Vector4(1f / Mathf.Max(w, 1), 1f / Mathf.Max(h, 1), 0, 0);
            renderer.EnqueuePass(m_Pass);

            if (debugView == DebugView.ProxyOverlay && m_ProxyOverlayMaterial != null && m_OverlayPass != null)
            {
                m_OverlayPass.OverlayMaterial = m_ProxyOverlayMaterial;
                m_OverlayPass.ProxyLayerMask = drawLayers;
                renderer.EnqueuePass(m_OverlayPass);
            }
        }
#endif // UNITY_6000_0_OR_NEWER

        // The persistent relight targets. Owned here, imported into the render graph per camera
        // - see the comment in RecordRenderGraph for why they must not be graph transients.
        // Color and depth are separate textures because ImportTexture rejects one that carries
        // both formats. Sized exactly to the (single, Game) camera; reallocates only when that
        // size changes. Two Game cameras with different sizes would thrash this - one texture
        // per feature is an accepted MVP limit.
        RenderTexture m_RelightTex;
        RenderTexture m_RelightDepthTex;
        // Blurred copy of the bake - the texture the splats actually sample. See the blur
        // shader for why it exists (temporal stability of animated silhouettes and shadows).
        RenderTexture m_RelightBlurTex;
        RTHandle m_RelightHandle;
        RTHandle m_RelightDepthHandle;
        RTHandle m_RelightBlurHandle;

        void EnsureRelightTarget(int w, int h)
        {
            if (m_RelightTex != null && m_RelightDepthTex != null &&
                m_RelightTex.width == w && m_RelightTex.height == h)
                return;
            ReleaseRelightTarget();
            // HDR color, no depth on this one: lighting legitimately exceeds 1 (intense or
            // multiple lights), and clamping would flatten exactly the highlights relighting
            // exists for.
            var colorDesc = new RenderTextureDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat, 0)
            {
                msaaSamples = 1,
            };
            m_RelightTex = new RenderTexture(colorDesc)
            {
                name = k_RelightRTName,
                filterMode = FilterMode.Point,
            };
            m_RelightTex.Create();
            m_RelightHandle = RTHandles.Alloc(m_RelightTex);
            // Depth companion: proxies are opaque geometry that must occlude itself (an arm in
            // front of a torso has to win).
            var depthDesc = new RenderTextureDescriptor(w, h, GraphicsFormat.None, 32)
            {
                msaaSamples = 1,
                depthStencilFormat = GraphicsFormat.D32_SFloat,
            };
            m_RelightDepthTex = new RenderTexture(depthDesc)
            {
                name = k_RelightRTName + "_Depth",
                filterMode = FilterMode.Point,
            };
            m_RelightDepthTex.Create();
            m_RelightDepthHandle = RTHandles.Alloc(m_RelightDepthTex);
            var blurDesc = new RenderTextureDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat, 0)
            {
                msaaSamples = 1,
            };
            m_RelightBlurTex = new RenderTexture(blurDesc)
            {
                name = k_RelightRTName + "_Blur",
                filterMode = FilterMode.Bilinear,
            };
            m_RelightBlurTex.Create();
            m_RelightBlurHandle = RTHandles.Alloc(m_RelightBlurTex);
        }

        void ReleaseRelightTarget()
        {
            m_RelightHandle?.Release();
            m_RelightHandle = null;
            m_RelightDepthHandle?.Release();
            m_RelightDepthHandle = null;
            if (m_RelightTex != null)
            {
                m_RelightTex.Release();
                CoreUtils.Destroy(m_RelightTex);
            }
            m_RelightTex = null;
            if (m_RelightDepthTex != null)
            {
                m_RelightDepthTex.Release();
                CoreUtils.Destroy(m_RelightDepthTex);
            }
            m_RelightDepthTex = null;
            m_RelightBlurHandle?.Release();
            m_RelightBlurHandle = null;
            if (m_RelightBlurTex != null)
            {
                m_RelightBlurTex.Release();
                CoreUtils.Destroy(m_RelightBlurTex);
            }
            m_RelightBlurTex = null;
        }

        // One-shot diagnostics: AddRenderPasses runs every frame for every camera, so an
        // unguarded Debug.LogWarning here would print thousands of identical lines.
        static readonly HashSet<string> s_Warned = new();

        static void WarnOnce(string message)
        {
            if (!s_Warned.Add(message))
                return;
            Debug.LogWarning(message);
        }

        // A bare layer number is not enough to act on: user layers are usually unnamed, so the
        // dropdown entry the message is telling you to look at is blank. Name them where they
        // have names and say so where they do not. Only ever reached on a WarnOnce path.
        static string DescribeLayers(int mask)
        {
            var sb = new StringBuilder();
            for (int bit = 0; bit < 32; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(", ");
                string name = LayerMask.LayerToName(bit);
                sb.Append(string.IsNullOrEmpty(name) ? $"layer {bit} (unnamed)" : $"layer {bit} ('{name}')");
            }
            return sb.Length > 0 ? sb.ToString() : "nothing";
        }

        void EnsureMaterial()
        {
            // The light-bake material is not optional: it is what the proxies draw with (unless
            // the user overrides), and without it the pass has nothing to render.
            if (m_ProxyLightMaterial == null)
            {
                var bakeShader = m_ProxyLightShader != null ? m_ProxyLightShader : Shader.Find(k_ProxyLightShaderName);
                if (bakeShader != null)
                    m_ProxyLightMaterial = CoreUtils.CreateEngineMaterial(bakeShader);
            }
            if (m_RelightBlurMaterial == null)
            {
                var blurShader = m_RelightBlurShader != null ? m_RelightBlurShader : Shader.Find(k_RelightBlurShaderName);
                if (blurShader != null)
                    m_RelightBlurMaterial = CoreUtils.CreateEngineMaterial(blurShader);
            }
            // Prefer the serialized references: Shader.Find only sees shaders a build actually
            // included, and nothing references these, so the find would return null in a
            // player. Debug views are authoring tools, so the fallback is enough in-editor.
            var dbg = ActiveDebug;
            if (m_DebugMaterial == null &&
                (dbg == DebugView.RelightRGB || dbg == DebugView.MaskAlpha))
            {
                var dbgShader = Shader.Find(k_DebugShaderName);
                if (dbgShader != null)
                    m_DebugMaterial = CoreUtils.CreateEngineMaterial(dbgShader);
            }
            if (m_ProxyOverlayMaterial == null && dbg == DebugView.ProxyOverlay)
            {
                var shader = Shader.Find(k_ProxyDebugShaderName);
                if (shader != null)
                {
                    m_ProxyOverlayMaterial = CoreUtils.CreateEngineMaterial(shader);
                    m_ProxyOverlayMaterial.SetColor(s_ProxyDebugColor, new Color(0f, 1f, 0.6f, 0.6f));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            DisposeInternal(false);
        }

        void DisposeInternal(bool immediate)
        {
            if (m_DebugMaterial != null)
            {
                if (immediate)
                    Object.DestroyImmediate(m_DebugMaterial);
                else
                    CoreUtils.Destroy(m_DebugMaterial);
            }
            m_DebugMaterial = null;
            if (m_ProxyOverlayMaterial != null)
            {
                if (immediate)
                    Object.DestroyImmediate(m_ProxyOverlayMaterial);
                else
                    CoreUtils.Destroy(m_ProxyOverlayMaterial);
            }
            m_ProxyOverlayMaterial = null;
            if (m_ProxyLightMaterial != null)
            {
                if (immediate)
                    Object.DestroyImmediate(m_ProxyLightMaterial);
                else
                    CoreUtils.Destroy(m_ProxyLightMaterial);
            }
            m_ProxyLightMaterial = null;
            if (m_RelightBlurMaterial != null)
            {
                if (immediate)
                    Object.DestroyImmediate(m_RelightBlurMaterial);
                else
                    CoreUtils.Destroy(m_RelightBlurMaterial);
            }
            m_RelightBlurMaterial = null;
            ReleaseRelightTarget();
            // Consumers hold a reference to the texture just destroyed; clear it so no splat
            // renderer binds a dead RenderTexture on its next CalcViewData.
            ClearConsumers();
#if UNITY_6000_0_OR_NEWER
            m_Pass = null;
            m_OverlayPass = null;
#endif
        }

        /// <summary>
        /// Domain-reload-less hot reload: drop the published textures and stop the pass, matching
        /// how the splat and outline features release their own GPU state.
        /// </summary>
        public static void ReleaseForHotReload()
        {
            s_HotReloadReleased = true;
            s_Consumers.Clear();
            ClearConsumers();
            foreach (var feature in Object.FindObjectsByType<SplatRelightingFeature>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (feature != null)
                    feature.DisposeInternal(true);
            }
        }
    }
}

#endif // #if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
