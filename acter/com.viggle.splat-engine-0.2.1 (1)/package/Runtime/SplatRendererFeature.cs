// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Viggle.SplatEngine
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    /// <summary>
    /// Makes splats render under URP. Add it to your URP renderer asset; without it, splat
    /// components load and report success and nothing appears on screen.
    /// </summary>
    /// <remarks>
    /// Public only so a build script can add it to a renderer asset without hand-editing YAML.
    /// Adding it in the inspector needs no reference to this type, because Unity's Add Renderer
    /// Feature list resolves through TypeCache, which ignores both accessibility and assembly
    /// references.
    /// </remarks>
    public class SplatRendererFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// Sort every splat object together instead of one at a time, so splats from different
        /// objects interleave where they overlap in depth. Off by default.
        /// </summary>
        /// <remarks>
        /// Splats are alpha-blended into a shared target with no depth buffer, so correctness comes
        /// entirely from draw order. Sorting each object on its own means each is drawn wholly in
        /// front of or wholly behind the others: a character standing inside an environment capture
        /// cannot mix with it. This produces one order across all of them instead.
        /// <para>
        /// It is not free. Every frame anything moves, one pass builds a depth key for every visible
        /// splat and one sort orders all of them - so a large static environment that would
        /// otherwise never re-sort now pays for being re-ordered whenever a character inside it
        /// moves. It also allocates roughly 16 more bytes a splat of shared scratch - sort keys, the
        /// merged order, and the sorter's own ping-pong buffers - on top of the per-object sort
        /// buffers, which stay allocated but idle. (The projected splat data itself, 48 bytes a
        /// splat, is relocated into the shared buffer rather than duplicated.) It also turns off
        /// the screen-size cull's
        /// vertex-side compaction, and disables per-object silhouette effects and editor selection
        /// highlighting, neither of which a merged draw can express. On OpenGL ES, where the sort
        /// keys are 16-bit, depth is quantized over the whole scene rather than per object, which is
        /// coarser than the per-object sort it replaces.
        /// </para>
        /// Leave it off unless splat objects actually overlap in depth on screen.
        /// </remarks>
        [SerializeField]
        [Tooltip("Sort all splat objects together so they interleave where they overlap in depth, " +
                 "instead of drawing each object wholly in front of or behind the others.\n\n" +
                 "Cost: one key-build pass and one sort over EVERY visible splat, each frame " +
                 "anything moves - a large static environment no longer sorts for free when a " +
                 "character inside it animates. Also ~16 extra bytes per splat, no screen-size-cull " +
                 "compaction, no per-object outline effects, and coarser depth quantization on " +
                 "OpenGL ES.\n\n" +
                 "Leave off unless splat objects actually overlap in depth.")]
        bool m_UnifiedSorting;

        /// <summary>See the remarks on the serialized field: sorts all splat objects together.</summary>
        public bool unifiedSorting
        {
            get => m_UnifiedSorting;
            set => m_UnifiedSorting = value;
        }

#if UNITY_6000_0_OR_NEWER
        // Unity 6+ (URP 17+): RenderGraph path.
        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                // Full-res camera depth TEXTURE (the copy, not the attachment) for the manual
                // depth test the splat shader runs when the RT is scaled. Invalid handle when
                // not scaled (the full-res path binds SourceDepth as a real attachment instead).
                internal TextureHandle CameraDepthTexture;
                internal Vector2Int RTSize;
                // Camera is multisampled but GaussianSplatRT is not, so the depth attachment
                // cannot be bound alongside it. Captured here because the render func is static
                // and has no access to the camera descriptor.
                internal bool MsaaBlocksDepth;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (SplatRenderer.Released)
                    return;

                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                // Optional reduced-resolution splat target (see SplatRenderer.SplatRenderScale);
                // the composite bilinearly upsamples via _GaussianSplatRTParams (set in the render func).
                bool scaled = SplatRenderer.RenderScaleActive;
                var rtSize = SplatRenderer.ScaledRTSize(rtDesc.width, rtDesc.height);
                rtDesc.width = rtSize.x;
                rtDesc.height = rtSize.y;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true,
                    scaled ? FilterMode.Bilinear : FilterMode.Point);

                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = textureHandle;
                passData.RTSize = rtSize;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(textureHandle, AccessFlags.Write);
                // Scaled path: read the full-res camera depth texture for the shader-side depth
                // test (requested via ConfigureInput(Depth) in AddRenderPasses, so URP schedules
                // the depth copy before this pass).
                bool isGL = SplatRenderer.IsGLDevice;
                // rtDesc.msaaSamples was forced to 1 above, so ask the CAMERA descriptor, not it.
                bool msaa = SplatRenderer.MsaaBlocksDepthAttachment(
                    cameraData.cameraTargetDescriptor.msaaSamples);
                passData.MsaaBlocksDepth = msaa;
                if (SplatRenderer.ManualDepthWanted(isGL, msaa) && resourceData.cameraDepthTexture.IsValid())
                {
                    passData.CameraDepthTexture = resourceData.cameraDepthTexture;
                    builder.UseTexture(resourceData.cameraDepthTexture);
                }
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    if (SplatRenderer.Released)
                        return;

                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    commandBuffer.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);
                    var targetDesc = data.CameraData.cameraTargetDescriptor;
                    commandBuffer.SetGlobalVector(SplatRenderer.Props.GaussianSplatRTParams,
                        new Vector4(1f / targetDesc.width, 1f / targetDesc.height, SplatRenderer.EffectiveRenderScale, 0));
                    // Manual shader-side depth test when the RT is scaled and the camera depth
                    // texture is available (see RenderGaussianSplats.shader); the hardware
                    // attachment test handles the full-res case below.
                    bool manualDepth = data.CameraDepthTexture.IsValid();
                    commandBuffer.SetGlobalInteger(SplatRenderer.Props.SplatManualDepthTest, manualDepth ? 1 : 0);
                    commandBuffer.SetGlobalVector(SplatRenderer.Props.SplatDepthTexParams,
                        new Vector4(1f / data.RTSize.x, 1f / data.RTSize.y, 0, 0));
                    if (manualDepth)
                        commandBuffer.SetGlobalTexture(SplatRenderer.Props.CameraDepthTexture, data.CameraDepthTexture);
                    // On OpenGL ES the camera depth attachment orientation does not match our
                    // off-screen color target (GL render-into-texture y-flip, worse through
                    // ANGLE), so depth-testing splats against it cuts holes through them.
                    // Render without scene depth there: splats won't be occluded by opaque
                    // geometry, but they render correctly. A scaled splat RT can't bind the
                    // full-res camera depth either (attachment sizes must match) - it uses the
                    // manual test above instead.
                    // ...and an MSAA camera depth attachment cannot pair with our single-sample
                    // splat target at all - Metal asserts on it, which crashes on iOS.
                    // Declared as well as bound: the no-compute cross-object path detours the
                    // target to its own view-data textures partway through the pass and has to
                    // put this back (see GaussianSplatRenderSystem.DeclareSplatTarget).
                    var system = GaussianSplatRenderSystem.instance;
                    if (SplatRenderer.RenderScaleActive || SplatRenderer.IsGLDevice
                        || data.MsaaBlocksDepth)
                    {
                        CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear);
                        system.DeclareSplatTarget(data.GaussianSplatRT);
                    }
                    else
                    {
                        CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                        system.DeclareSplatTarget(data.GaussianSplatRT, data.SourceDepth);
                    }
                    // Belt and braces: issue the clear as an explicit command too. Inside an
                    // unsafe render-graph pass the ClearFlag-based clear can be dropped on some
                    // backends (seen on Android GLES via ANGLE), leaving the blend running
                    // against uninitialized memory.
                    commandBuffer.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1.0f, 0);
                    Material matComposite = system.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                    commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                    commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                });
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            if (SplatRenderer.Released)
                return;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (SplatRenderer.Released || !m_HasCamera || m_Pass == null)
                return;
            // Cross-object sorting is a property of the whole splat system rather than of one pass,
            // so it is requested here as a per-frame stamp and read by the render system when the
            // pass executes. Only URP has this switch; the HDRP and built-in paths never stamp it,
            // and no two of those pipelines are ever active at once.
            if (m_UnifiedSorting)
                SplatUnifiedSort.Request();
            // Scaled splat RT: request the camera depth TEXTURE (forces URP's depth copy) for
            // the shader-side depth test; the full-res path binds the depth attachment instead.
            m_Pass.ConfigureInput(SplatRenderer.ManualDepthWanted(
                    SplatRenderer.IsGLDevice,
                    SplatRenderer.MsaaBlocksDepthAttachment(
                        renderingData.cameraData.cameraTargetDescriptor.msaaSamples))
                ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
            m_HasCamera = false;
        }

#else
        // Pre-Unity-6 (URP 14, e.g. 2022.3 LTS): compatibility-mode path. No RenderGraph;
        // we drive an explicit CommandBuffer the same way the built-in pipeline path does
        // (see GaussianSplatRenderSystem.OnPreCullCamera in SplatRenderer.cs).
        class GSRenderPass : ScriptableRenderPass
        {
            const string ProfilerTag = "GaussianSplatRender";
            static readonly int s_gaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (SplatRenderer.Released)
                    return;

                var cam = renderingData.cameraData.camera;
                var system = GaussianSplatRenderSystem.instance;
                if (!system.GatherSplatsForCamera(cam))
                    return;

                CommandBuffer cmb = CommandBufferPool.Get(ProfilerTag);

                // Off-screen color target the splats accumulate into. Matches camera target size
                // (or a reduced size, see SplatRenderer.SplatRenderScale); force
                // single-sample HDR (matches the RenderGraph path above).
                RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
                int targetW = rtDesc.width, targetH = rtDesc.height;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                bool scaled = SplatRenderer.RenderScaleActive;
                var rtSize = SplatRenderer.ScaledRTSize(targetW, targetH);
                rtDesc.width = rtSize.x;
                rtDesc.height = rtSize.y;
                // Temp RT bound to the "_GaussianSplatRT" nameID is exposed as a global
                // texture automatically, which the composite shader samples by that name.
                cmb.GetTemporaryRT(s_gaussianSplatRT, rtDesc, scaled ? FilterMode.Bilinear : FilterMode.Point);
                cmb.SetGlobalVector(SplatRenderer.Props.GaussianSplatRTParams,
                    new Vector4(1f / targetW, 1f / targetH, SplatRenderer.EffectiveRenderScale, 0));

                var renderer = renderingData.cameraData.renderer;
                // On OpenGL ES the camera depth attachment orientation does not match our
                // off-screen target, so depth-testing splats against it cuts holes through
                // them. Render without scene depth there (no occlusion, but correct splats).
                // A scaled splat RT can't bind the full-res camera depth either - it depth-tests
                // manually in the fragment shader against _CameraDepthTexture (bound by URP via
                // the ConfigureInput(Depth) request in AddRenderPasses).
                bool isGL = SplatRenderer.IsGLDevice;
                // The camera target may be multisampled while our splat RT is forced to 1 sample
                // above. Binding the two together is illegal and asserts on Metal (iOS crash).
                bool msaa = SplatRenderer.MsaaBlocksDepthAttachment(
                    renderingData.cameraData.cameraTargetDescriptor.msaaSamples);
                bool manualDepth = SplatRenderer.ManualDepthWanted(isGL, msaa);
                cmb.SetGlobalInteger(SplatRenderer.Props.SplatManualDepthTest, manualDepth ? 1 : 0);
                cmb.SetGlobalVector(SplatRenderer.Props.SplatDepthTexParams,
                    new Vector4(1f / rtSize.x, 1f / rtSize.y, 0, 0));
                // Declared as well as bound: the no-compute cross-object path detours the target
                // to its own view-data textures partway through the pass and has to put this back.
                if (isGL || scaled || msaa)
                {
                    cmb.SetRenderTarget(s_gaussianSplatRT);
                    system.DeclareSplatTarget(s_gaussianSplatRT);
                }
                else
                {
                    cmb.SetRenderTarget(new RenderTargetIdentifier(s_gaussianSplatRT), renderer.cameraDepthTargetHandle);
                    system.DeclareSplatTarget(new RenderTargetIdentifier(s_gaussianSplatRT), renderer.cameraDepthTargetHandle);
                }
                cmb.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1.0f, 0);

                // Sorting, view-data compute and per-splat draws.
                Material matComposite = system.SortAndRenderSplats(cam, cmb);

                // Compose the splat RT back onto the camera color target with the composite
                // material (it reads the global "_GaussianSplatRT" and alpha-blends).
                cmb.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                cmb.SetRenderTarget(renderer.cameraColorTargetHandle);
                cmb.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
                cmb.EndSample(GaussianSplatRenderSystem.s_ProfCompose);

                cmb.ReleaseTemporaryRT(s_gaussianSplatRT);

                context.ExecuteCommandBuffer(cmb);
                CommandBufferPool.Release(cmb);
            }
        }

        GSRenderPass m_Pass;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (SplatRenderer.Released || m_Pass == null)
                return;

            if (renderingData.cameraData.cameraType == CameraType.Preview)
                return;
            // See the note in the RenderGraph path above: a per-frame stamp the render system reads
            // when the pass executes, and a URP-only switch.
            if (m_UnifiedSorting)
                SplatUnifiedSort.Request();
            // Scaled splat RT: request the camera depth texture (forces URP's depth copy) so
            // the shader-side depth test has something to sample.
            m_Pass.ConfigureInput(SplatRenderer.ManualDepthWanted(
                    SplatRenderer.IsGLDevice,
                    SplatRenderer.MsaaBlocksDepthAttachment(
                        renderingData.cameraData.cameraTargetDescriptor.msaaSamples))
                ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }

#endif // UNITY_6000_0_OR_NEWER
    }
}

#endif // #if GS_ENABLE_URP
