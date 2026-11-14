// SPDX-License-Identifier: MIT
//
// Unity 6 / URP 17 only, same as SplatRelightingFeature and for the same reason: PixelArtPass
// implements RecordRenderGraph and nothing else, so on URP 14 - where ScriptableRenderPass.Execute
// is still abstract - the pass does not compile. The file compiles out instead of breaking the
// assembly. A compatibility path would go in the #else of the inner guard inside PixelArtPass.
#if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
#endif

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Retro pixel-art filter with independent block sizes for the scene and for splat characters.
    /// Port of the web engine's PixelartEffect (SplatEngine/engine/src/effects/pixelart).
    /// </summary>
    /// <remarks>
    /// Purely additive: with the feature absent from the renderer asset, or Character Scale at 1
    /// with no quantisation, nothing in the frame changes.
    ///
    /// HOW THE TWO SCALES STAY SEPARATE
    ///
    /// The finished splat composite contains both rooms and actors, so draw order alone cannot tell
    /// them apart. The environment pass renders Character Layers into a short-lived mask, then
    /// leaves those pixels untouched while applying the coarse grid to everything else:
    ///
    ///     world + all splats  ->  [coarse pass outside character mask]  ->  [fine pass]
    ///
    /// The fine pass then blocks up the whole frame at the smaller size - and leaves the world
    /// untouched, because
    /// Environment Scale is kept an integer multiple of Character Scale, so every coarse block is
    /// a whole number of fine blocks that already share one colour. Re-snapping a flat block to a
    /// finer grid returns the same flat block.
    ///
    /// Character versus environment is therefore explicit even when both are Gaussian splats.
    /// Non-character transparent objects are already present for the environment pass and receive
    /// its grid; Character-layer meshes and splats receive the character grid.
    ///
    /// PERFORMANCE
    ///
    /// Two full-screen blits and one small RT per stage, plus a character-only splat draw for the
    /// mask when separate grids are enabled. It is not free, and note
    /// it does NOT save any fill rate - the frame is still rendered at full resolution and thrown
    /// away. For an actual saving, drive SplatQuality.RenderScale (characters) and the URP asset's
    /// Render Scale (whole frame) instead; this filter is about the look, and about being able to
    /// separate the two grids, which render scale alone cannot do.
    /// </remarks>
    [DisallowMultipleRendererFeature("Splat Pixel Art")]
    [Tooltip("Retro pixel-art filter with separate block sizes for the scene and splat characters.")]
    public class SplatPixelArtFeature : ScriptableRendererFeature
    {
        const string k_ShaderName = "Hidden/Viggle/Splat Pixel Art";

        // Every value lives in Project Settings > Viggle Splat Engine, pushed into SplatStyle on
        // load - see SplatPixelArtFeatureEditor. Art direction is one decision for the project, and
        // splitting it across renderer assets is how two of them quietly end up disagreeing.
        [SerializeField, HideInInspector] Shader m_Shader;

        Material m_Material;
        PixelArtPass m_EnvironmentPass;
        PixelArtPass m_CharacterPass;

        public override void Create()
        {
            if (m_Shader == null)
                m_Shader = Shader.Find(k_ShaderName);
            if (m_Shader == null)
            {
                if (SplatLog.Warning)
                    Debug.LogWarning($"SplatPixelArtFeature: shader '{k_ShaderName}' not found; the feature is inert.");
                return;
            }

            if (m_Material == null)
                m_Material = CoreUtils.CreateEngineMaterial(m_Shader);

            // With Character Layers configured, the environment pass lands immediately after the
            // unified splat composite so it can mask character splats out. Without classification
            // it falls back to the original early draw-order split; AddRenderPasses sets the event.
            m_EnvironmentPass ??= new PixelArtPass("SplatPixelArt (environment)")
                { renderPassEvent = RenderPassEvent.AfterRenderingSkybox };
            m_CharacterPass ??= new PixelArtPass("SplatPixelArt (character)")
                { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Material == null)
                return;

            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;

            int fine = Mathf.Max(1, SplatStyle.PixelCharacterScale);
            int coarse = Mathf.Max(1, SplatStyle.PixelEnvironmentScale);
            int levels = SplatStyle.PixelQuantizeLevels > 1 ? SplatStyle.PixelQuantizeLevels : 0;
            LayerMask characterLayers = SplatStyle.PixelCharacterLayers;
            bool classified = characterLayers.value != 0;

            if (classified)
            {
                // The two masked passes touch disjoint pixels, so the grids are genuinely
                // independent: neither scale is rounded to a multiple of the other. Quantisation
                // rides both passes and still runs exactly once per classified pixel.
                if (coarse <= 1 && fine <= 1 && levels == 0)
                    return;

                int ditherCell = SplatStyle.PixelDitherCellSize > 0
                    ? SplatStyle.PixelDitherCellSize
                    : coarse;

                m_EnvironmentPass.Layers = characterLayers;
                m_EnvironmentPass.BuildMask = true;
                m_EnvironmentPass.MaskMode = 1; // keep pixels outside the character mask
                m_EnvironmentPass.renderPassEvent =
                    (RenderPassEvent)((int)RenderPassEvent.AfterRenderingTransparents - 1);
                m_EnvironmentPass.ConfigureInput(ScriptableRenderPassInput.Depth);
                m_EnvironmentPass.Setup(m_Material, coarse, levels,
                    SplatStyle.PixelDitherStrength, ditherCell);

                m_CharacterPass.Layers = characterLayers;
                m_CharacterPass.BuildMask = false;
                m_CharacterPass.MaskMode = 2; // keep pixels inside the character mask
                m_CharacterPass.MaskSource = m_EnvironmentPass;
                m_CharacterPass.Setup(m_Material, fine, levels,
                    SplatStyle.PixelDitherStrength, ditherCell);

                renderer.EnqueuePass(m_EnvironmentPass);
                renderer.EnqueuePass(m_CharacterPass);
                return;
            }

            // No semantic classification configured: retain the original draw-order behaviour.
            // The fine pass touches the whole frame, so its grid has to divide the coarse grid or
            // it would re-chop environment blocks when Character Scale changes.
            coarse = Mathf.Max(fine, Mathf.CeilToInt(coarse / (float)fine) * fine);

            if (coarse > fine)
            {
                // Colour work is deliberately left to the character pass so it runs once over the
                // finished frame rather than twice with the second pass re-quantising the first.
                m_EnvironmentPass.Layers = 0;
                m_EnvironmentPass.BuildMask = false;
                m_EnvironmentPass.MaskMode = 0;
                m_EnvironmentPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
                m_EnvironmentPass.ConfigureInput(ScriptableRenderPassInput.None);
                m_EnvironmentPass.Setup(m_Material, coarse, 0, 0f, coarse);
                renderer.EnqueuePass(m_EnvironmentPass);
            }

            if (fine > 1 || levels > 0)
            {
                // 0 means "match the coarse blocks", which is what the dither is breaking up.
                int ditherCell = SplatStyle.PixelDitherCellSize > 0 ? SplatStyle.PixelDitherCellSize : coarse;
                m_CharacterPass.Layers = 0;
                m_CharacterPass.BuildMask = false;
                m_CharacterPass.MaskMode = 0;
                m_CharacterPass.MaskSource = null;
                m_CharacterPass.Setup(m_Material, fine, levels, SplatStyle.PixelDitherStrength, ditherCell);
                renderer.EnqueuePass(m_CharacterPass);
            }
        }

        class PixelArtPass : ScriptableRenderPass
        {
            static readonly int s_BlockId = Shader.PropertyToID("_PixelArtBlock");
            static readonly int s_ColorId = Shader.PropertyToID("_PixelArtColor");
            static readonly int s_MaskId = Shader.PropertyToID("_PixelArtMask");
            static readonly int s_MaskTexId = Shader.PropertyToID("_PixelArtMaskTex");

            readonly string m_Tag;
            readonly ProfilingSampler m_Sampler;

            Material m_Material;
            int m_Block;
            int m_Levels;
            float m_Dither;
            int m_DitherCell;
            internal LayerMask Layers;
            internal bool BuildMask;
            // 0 = no mask, 1 = keep outside, 2 = keep inside.
            internal int MaskMode;
            internal PixelArtPass MaskSource;

            TextureHandle m_FrameMask;
            int m_FrameMaskCamera;
            int m_FrameMaskFrame = -1;
            public PixelArtPass(string tag)
            {
                m_Tag = tag;
                m_Sampler = new ProfilingSampler(tag);
            }

            public void Setup(Material material, int block, int levels, float dither, int ditherCell)
            {
                m_Material = material;
                m_Block = Mathf.Max(1, block);
                m_Levels = levels;
                m_Dither = dither;
                m_DitherCell = Mathf.Max(1, ditherCell);
            }

#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle Source;
                internal TextureHandle LowRes;
                internal Material Material;
                internal ProfilingSampler Sampler;
                internal int Block;
                internal Vector2Int SourceSize;
                internal Vector2Int LowResSize;
                internal int Levels;
                internal float Dither;
                internal int DitherCell;
                internal int Layers;
                internal TextureHandle Mask;
                internal TextureHandle SourceDepth;
                internal TextureHandle CameraDepth;
                internal bool MsaaBlocksDepth;
                internal bool BuildMask;
                internal int MaskMode;
                internal RendererListHandle GeometryMask;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Material == null)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeColorTexture.IsValid())
                    return;

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                int srcW = Mathf.Max(1, desc.width);
                int srcH = Mathf.Max(1, desc.height);
                int lowW = Mathf.Max(1, srcW / m_Block);
                int lowH = Mathf.Max(1, srcH / m_Block);

                RenderTextureDescriptor lowDesc = desc;
                lowDesc.width = lowW;
                lowDesc.height = lowH;
                lowDesc.depthBufferBits = 0;
                lowDesc.msaaSamples = 1;
                var lowHandle = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, lowDesc, "_SplatPixelArtRT", false, FilterMode.Point);

                TextureHandle maskHandle = default;
                int layers = Layers.value;
                if (BuildMask && layers != 0)
                {
                    RenderTextureDescriptor maskDesc = desc;
                    maskDesc.depthBufferBits = 0;
                    maskDesc.msaaSamples = 1;
                    maskHandle = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph, maskDesc, "_SplatPixelArtCharacterMask", true, FilterMode.Point);
                    m_FrameMask = maskHandle;
                    m_FrameMaskCamera = cameraData.camera.GetInstanceID();
                    m_FrameMaskFrame = Time.frameCount;
                }
                else if (MaskSource != null && MaskSource.TryGetFrameMask(cameraData.camera, out var sharedMask))
                {
                    maskHandle = sharedMask;
                }

                using var builder = renderGraph.AddUnsafePass(m_Tag, out PassData data);

                data.CameraData = cameraData;
                data.Source = resourceData.activeColorTexture;
                data.LowRes = lowHandle;
                data.Material = m_Material;
                data.Sampler = m_Sampler;
                data.Block = m_Block;
                data.SourceSize = new Vector2Int(srcW, srcH);
                data.LowResSize = new Vector2Int(lowW, lowH);
                data.Levels = m_Levels;
                data.Dither = m_Dither;
                data.DitherCell = m_DitherCell;
                data.Layers = layers;
                data.Mask = maskHandle;
                data.SourceDepth = resourceData.activeDepthTexture;
                data.MsaaBlocksDepth = SplatRenderer.MsaaBlocksDepthAttachment(desc.msaaSamples);
                data.BuildMask = BuildMask;
                data.MaskMode = MaskMode;
                if (BuildMask && layers != 0 && resourceData.cameraDepthTexture.IsValid())
                    data.CameraDepth = resourceData.cameraDepthTexture;

                if (BuildMask && layers != 0)
                {
                    var renderingData = frameData.Get<UniversalRenderingData>();
                    var lightData = frameData.Get<UniversalLightData>();
                    var drawSettings = RenderingUtils.CreateDrawingSettings(
                        new ShaderTagId("UniversalForward"), renderingData, cameraData, lightData,
                        SortingCriteria.CommonTransparent);
                    drawSettings.SetShaderPassName(1, new ShaderTagId("UniversalForwardOnly"));
                    drawSettings.SetShaderPassName(2, new ShaderTagId("SRPDefaultUnlit"));
                    drawSettings.overrideMaterial = m_Material;
                    drawSettings.overrideMaterialPassIndex = 2;
                    data.GeometryMask = renderGraph.CreateRendererList(new RendererListParams(
                        renderingData.cullResults, drawSettings,
                        new FilteringSettings(RenderQueueRange.all, layers)));
                }

                builder.UseTexture(data.Source, AccessFlags.ReadWrite);
                builder.UseTexture(lowHandle, AccessFlags.ReadWrite);
                if (maskHandle.IsValid())
                {
                    builder.UseTexture(maskHandle, BuildMask ? AccessFlags.ReadWrite : AccessFlags.Read);
                    if (BuildMask)
                    {
                        builder.UseTexture(resourceData.activeDepthTexture);
                        if (data.CameraDepth.IsValid())
                            builder.UseTexture(data.CameraDepth);
                        builder.UseRendererList(data.GeometryMask);
                    }
                }
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext context) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(cmd, d.Sampler);

                    if (d.BuildMask && d.Mask.IsValid())
                    {
                        // Render just the configured character splats to an alpha-bearing target.
                        // The regular all-splat composite has already run, so this extra draw only
                        // supplies classification; it never changes the camera colour or its sort.
                        cmd.SetGlobalTexture(SplatRenderer.Props.GaussianSplatRT, d.Mask);
                        cmd.SetGlobalVector(SplatRenderer.Props.GaussianSplatRTParams,
                            new Vector4(1f / d.SourceSize.x, 1f / d.SourceSize.y, 1f, 0f));
                        bool manualDepth = d.CameraDepth.IsValid();
                        cmd.SetGlobalInteger(SplatRenderer.Props.SplatManualDepthTest, manualDepth ? 1 : 0);
                        cmd.SetGlobalVector(SplatRenderer.Props.SplatDepthTexParams,
                            new Vector4(1f / d.SourceSize.x, 1f / d.SourceSize.y, 0f, 0f));
                        if (manualDepth)
                            cmd.SetGlobalTexture(SplatRenderer.Props.CameraDepthTexture, d.CameraDepth);

                        // Declared as well as bound: the no-compute cross-object path detours the
                        // target to its own view-data textures partway through the pass and has
                        // to put this back.
                        if (manualDepth || SplatRenderer.IsGLDevice || d.MsaaBlocksDepth)
                        {
                            CoreUtils.SetRenderTarget(cmd, d.Mask, ClearFlag.Color, Color.clear);
                            GaussianSplatRenderSystem.instance.DeclareSplatTarget(d.Mask);
                        }
                        else
                        {
                            CoreUtils.SetRenderTarget(cmd, d.Mask, d.SourceDepth, ClearFlag.Color, Color.clear);
                            GaussianSplatRenderSystem.instance.DeclareSplatTarget(d.Mask, d.SourceDepth);
                        }
                        cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1f, 0);
                        // Ordinary MeshRenderer/SkinnedMeshRenderer characters (including loading
                        // placeholders) share the same semantic layer and mask as splat actors.
                        cmd.DrawRendererList(d.GeometryMask);
                        GaussianSplatRenderSystem.instance.SortAndRenderSplats(
                            d.CameraData.camera, cmd, d.Layers);
                    }

                    if (d.Mask.IsValid())
                    {
                        cmd.SetGlobalTexture(s_MaskTexId, d.Mask);
                        cmd.SetGlobalVector(s_MaskId,
                            new Vector4(d.MaskMode, d.SourceSize.x, d.SourceSize.y, 0f));
                    }
                    else
                        cmd.SetGlobalVector(s_MaskId, Vector4.zero);

                    // Globals rather than material properties: both blits share one material, and
                    // a material.SetVector while recording would apply to whichever draw runs
                    // last. Command-buffer globals keep the two sets in submission order.
                    cmd.SetGlobalVector(s_BlockId,
                        new Vector4(d.Block, 1f / d.Block, d.SourceSize.x, d.SourceSize.y));
                    cmd.SetGlobalVector(s_ColorId, Vector4.zero);
                    Blitter.BlitCameraTexture(cmd, d.Source, d.LowRes, d.Material, 0);

                    cmd.SetGlobalVector(s_BlockId,
                        new Vector4(d.Block, 1f / d.Block, d.LowResSize.x, d.LowResSize.y));
                    cmd.SetGlobalVector(s_ColorId, new Vector4(d.Levels, d.Dither, d.DitherCell, 0f));
                    Blitter.BlitCameraTexture(cmd, d.LowRes, d.Source, d.Material, 1);
                });
            }

            bool TryGetFrameMask(Camera camera, out TextureHandle mask)
            {
                bool valid = camera != null
                             && m_FrameMaskFrame == Time.frameCount
                             && m_FrameMaskCamera == camera.GetInstanceID()
                             && m_FrameMask.IsValid();
                mask = valid ? m_FrameMask : default;
                return valid;
            }
#endif
        }
    }
}

#endif // #if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
