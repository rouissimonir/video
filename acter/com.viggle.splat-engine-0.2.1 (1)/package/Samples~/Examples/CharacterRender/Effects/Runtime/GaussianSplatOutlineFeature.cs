// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Viggle.SplatEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Viggle.SplatEngine.Samples
{
    // SAMPLE: screen-space outlines for splat characters.
    //
    // Runs after the splats have composited. It re-renders the silhouette of every character
    // marked with SplatOutline into an off-screen mask (through the renderer's public
    // RenderSilhouette API), then a fullscreen pass ring-expands that mask and draws the result
    // over the camera color. Each character's color is written into the mask, so overlapping
    // characters keep distinct outlines.
    //
    // By default the mask has no depth arbitration at all: silhouettes composite in draw order and
    // nothing in the scene is consulted, so a character standing behind another - or behind a wall -
    // still paints its outline color over it. Turn on Occlude By Scene to give the mask a depth
    // buffer, have every splat object write into it, and have the silhouettes test against the
    // camera depth texture as well, so meshes occlude the outline too. See the field's tooltip and
    // the OccluderCache below for what it costs.
    //
    // Setup, once per URP renderer asset:
    //   1. Add Renderer Feature > Gaussian Splat Outline Feature, AFTER SplatRendererFeature.
    //   2. Assign Mask Shader = GaussianSplatOutlineMask, Outline Shader = GaussianSplatOutline.
    // Then add a SplatOutline component to each character you want outlined. Without step 1 the
    // components register but nothing is ever drawn - URP only runs custom passes from a feature.
    //
    // Two implementations follow, picked by Unity version: RenderGraph on Unity 6+ (URP 17+) and
    // the older compatibility path on 2022.3 (URP 14). They draw the same thing.
    public partial class GaussianSplatOutlineFeature : ScriptableRendererFeature
    {
        [Tooltip("Per-character silhouette shader. Assign GaussianSplatOutlineMask.shader.")]
        public Shader m_MaskShader;
        [Tooltip("Fullscreen outline shader. Assign GaussianSplatOutline.shader.")]
        public Shader m_OutlineShader;
        [Range(1f, 16f)] [Tooltip("Outline thickness in pixels.")]
        public float m_Thickness = 4f;
        [Range(0f, 1f)] [Tooltip("Outline opacity over the scene.")]
        public float m_Intensity = 1f;
        [Tooltip("Let whatever stands in front of an outlined character hide its outline, instead " +
                 "of the outline being drawn over everything.\n\n" +
                 "Two halves, both on this one switch:\n" +
                 "  • Splats: every splat object in view, character or static capture, outlined or " +
                 "not, lays depth into the mask, so a character standing behind one no longer " +
                 "paints its outline across it.\n" +
                 "  • Geometry: each silhouette is also tested against the camera depth texture, " +
                 "so ordinary opaque meshes - walls, props, floors - cut the outline off too.\n\n" +
                 "Off (the default) is the original behaviour: every silhouette is composited into " +
                 "the mask in draw order with no depth arbitration at all.\n\n" +
                 "What it costs, honestly: the mask render target gains a full-resolution 24-bit " +
                 "depth buffer. The splat half is one extra depth-only splat draw per splat object " +
                 "per camera, on top of the silhouette draw the outlined ones already pay for - on " +
                 "a mobile tiler that is close to a second full splat pass. The geometry half is " +
                 "far cheaper: one depth-texture sample per mask fragment, plus URP's depth copy " +
                 "if nothing else in the frame already asked for it.\n\n" +
                 "Needs Depth Mask Shader below; without it this does nothing.")]
        public bool m_OccludeByScene;
        [Tooltip("Depth-writing silhouette shader, used only while Occlude By Scene is on. " +
                 "Assign GaussianSplatOutlineMaskDepth.shader.")]
        public Shader m_DepthMaskShader;

        Material m_OutlineMaterial;
        const string k_MaskRTName = "_GaussianOutlineMaskRT";
        const string k_MaskDepthRTName = "_GaussianOutlineMaskDepthRT";
        static readonly int s_MaskRT = Shader.PropertyToID(k_MaskRTName);
        static readonly int s_Thickness = Shader.PropertyToID("_OutlineThickness");
        static readonly int s_Intensity = Shader.PropertyToID("_OutlineIntensity");
        static readonly int s_OutlineColor = Shader.PropertyToID("_OutlineColor");
        // Pass state of GaussianSplatOutlineMaskDepth.shader, driven per material so one shader
        // serves both the depth-only occluder draw and the silhouette draw.
        static readonly int s_MaskDepthOnly = Shader.PropertyToID("_SplatMaskDepthOnly");
        static readonly int s_MaskColorMask = Shader.PropertyToID("_SplatMaskColorMask");
        // Scene-depth inputs of that shader. Resolved by name here rather than reused from
        // SplatRenderer.Props, which is internal to the package - the names are the core's and
        // must stay the core's, since the mask shader runs the core's depth test verbatim.
        static readonly int s_SplatDepthTexParams = Shader.PropertyToID("_SplatDepthTexParams");
#if UNITY_6000_0_OR_NEWER
        // RenderGraph only: there the depth texture has to be bound by name from the graph handle.
        // The compatibility path gets it as a global from URP's own depth prepass / copy pass.
        static readonly int s_CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
#endif
        // Created on first use: Unity forbids constructing a MaterialPropertyBlock in a static
        // field initializer, which runs in the type constructor.
        static MaterialPropertyBlock s_Mpb;
        static bool s_Released;

        void SyncMaterialParams()
        {
            if (m_OutlineMaterial == null)
                return;
            m_OutlineMaterial.SetFloat(s_Thickness, m_Thickness);
            m_OutlineMaterial.SetFloat(s_Intensity, m_Intensity);
        }

        // Renders each marked character's silhouette into the currently-bound mask target.
        static void RenderMasks(CommandBuffer cmd, Camera cam, Shader maskShader)
        {
            if (s_Released)
                return;

            s_Mpb ??= new MaterialPropertyBlock();
            for (int i = 0; i < SplatOutline.ActiveCount; i++)
            {
                var so = SplatOutline.GetActive(i);
                if (so == null || so.Renderer == null)
                    continue;
                // This walks its own registry rather than the renderer's per-camera gather, so it
                // has to repeat the layer cull itself. Drop it and an outline still appears in a
                // camera whose culling mask excludes the character.
                if ((cam.cullingMask & (1 << so.Renderer.gameObject.layer)) == 0)
                    continue;
                var maskMat = so.GetMaskMaterial(maskShader);
                if (maskMat == null)
                    continue;
                // Upload linear: URP composites in linear space.
                s_Mpb.SetColor(s_OutlineColor, so.m_Color.linear);
                maskMat.SetColor(s_OutlineColor, so.m_Color.linear);
                SplatCustomPass.Draw(so.Renderer, cmd, cam, maskMat, s_Mpb);
            }
        }

        // ---- Occlude By Scene ---------------------------------------------------------------
        // Everything below here only runs while m_OccludeByScene is on. The passes decide that
        // once, by whether they were handed an OccluderCache, and then take a wholly separate
        // branch - with the option off, not one line of it executes, no depth is requested from
        // URP, and the mask is byte for byte what it has always been.
        //
        // The cache below is the splat half of the option: the mesh half needs no bookkeeping at
        // all, only the depth-texture sample the mask shader does per fragment.

        OccluderCache m_Occluders;
        bool m_WarnedNoDepthShader;
        // Bumped by InvalidateOccluders(); the cache rescans when its own copy falls behind.
        static int s_OccluderInvalidations;

        // Forces the next frame to re-enumerate the splat objects in the scene. Call it after
        // spawning or destroying one if you cannot wait for the periodic rescan (see OccluderCache).
        public static void InvalidateOccluders()
        {
            s_OccluderInvalidations++;
        }

        // Every splat object in the scene, paired with the materials used to draw it into the mask.
        //
        // Why a scene scan at all: SplatOutline registers only the characters that WANT an outline,
        // but an occluder is any splat object - a plain un-outlined character, or the static
        // capture the characters are standing in, still has to hide the outline behind it. The
        // engine's own registry is internal to the package, so the public SplatRenderer type plus
        // FindObjectsOfType is what is left, and that is far too slow to run per camera per frame.
        // Hence this cache.
        //
        // SplatRenderer, not SkinnedSplatRenderer: a character IS a SplatRenderer, so this one
        // enumeration covers animated characters and static captures alike, and a capture occludes
        // exactly like a character does.
        class OccluderCache
        {
            // Safety-net rescan interval. Everything that changes the OUTLINED set is already
            // caught the same frame (see EnsureUpToDate), so this only governs how long a newly
            // spawned NON-outlined splat object takes to start occluding - and the worst that
            // happens meanwhile is an outline showing through it for a fraction of a second.
            const float k_RescanSeconds = 0.5f;

            public struct Entry
            {
                public SplatRenderer Renderer;
                // Resolved with GetComponent, which finds the component even while it is disabled.
                // So toggling an outline on and off - the usual selection highlight - takes effect
                // immediately, with no rescan: only adding or destroying the component needs one.
                public SplatOutline Outline;
                public Material DepthMaterial; // depth-only occluder draw
                public Material ColorMaterial; // silhouette draw; null if this object gets no outline
                public float SortDepth;        // camera-space depth, refreshed per camera
            }

            Entry[] m_Entries = new Entry[8];
            Entry[] m_Scratch = new Entry[8];
            int m_Count;
            Shader m_Shader;
            int m_LastCheckFrame = -1;
            int m_OutlineCountAtScan = -1;
            int m_InvalidationsAtScan = -1;
            float m_LastScanTime = float.NegativeInfinity;
            readonly Plane[] m_FrustumPlanes = new Plane[6];

            public int Count => m_Count;
            public Entry Get(int index) => m_Entries[index];

            // Rescans when: the shader changed; the number of registered SplatOutlines has moved
            // since the last scan (so an object that was just spawned or just gained an outline
            // is picked up the same frame it appears); something invalidated the cache by hand; a
            // cached renderer has been destroyed; or the safety-net interval has elapsed.
            public void EnsureUpToDate(Shader depthShader, int activeOutlineCount)
            {
                int frame = Time.frameCount;
                if (frame == m_LastCheckFrame)
                    return; // once a frame, however many cameras draw the outline
                m_LastCheckFrame = frame;

                bool stale = m_Shader != depthShader
                             || activeOutlineCount != m_OutlineCountAtScan
                             || m_InvalidationsAtScan != s_OccluderInvalidations
                             || Time.unscaledTime - m_LastScanTime >= k_RescanSeconds
                             || HasDestroyedEntry();
                if (!stale)
                    return;

                Rescan(depthShader);
                m_Shader = depthShader;
                m_OutlineCountAtScan = activeOutlineCount;
                m_InvalidationsAtScan = s_OccluderInvalidations;
                m_LastScanTime = Time.unscaledTime;
            }

            // Per-camera state for the draw loop: the frustum planes IsVisibleTo tests against, and
            // a nearest-first ordering. The order is what the draw loop needs: an object's
            // silhouette has to be tested against the depth of everything in front of it BEFORE it
            // lays down its own.
            public void PrepareForCamera(Camera cam)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, m_FrustumPlanes);

                var camTr = cam.transform;
                Vector3 camPos = camTr.position, camFwd = camTr.forward;
                for (int i = 0; i < m_Count; i++)
                {
                    var r = m_Entries[i].Renderer;
                    // The renderer's origin, not its bounds: this only has to order whole objects,
                    // and the per-fragment depth test still resolves the pixels where the ordering
                    // is arguable (two characters leaning into each other). Getting the object
                    // order wrong for such a pair costs one of them its clipping, which is what the
                    // option off gives you anyway.
                    m_Entries[i].SortDepth = r == null
                        ? float.MaxValue
                        : Vector3.Dot(r.transform.position - camPos, camFwd);
                }

                // Insertion sort: the list is every splat object in the scene, single digits in
                // practice, and it is nearly sorted already from frame to frame.
                for (int i = 1; i < m_Count; i++)
                {
                    var entry = m_Entries[i];
                    int j = i - 1;
                    while (j >= 0 && m_Entries[j].SortDepth > entry.SortDepth)
                    {
                        m_Entries[j + 1] = m_Entries[j];
                        j--;
                    }
                    m_Entries[j + 1] = entry;
                }
            }

            // Whether this object is worth drawing into the mask for the camera PrepareForCamera
            // was last given.
            //
            // This is a frustum test on the object's own world bounds rather than a question put to
            // the renderer, because the renderer's "was I recently visible" flag is internal. It
            // answers what actually matters here, and it is safe in the direction that counts: the
            // engine culls with the SAME bounds PADDED, and a padded box that lies entirely outside
            // a frustum plane contains an unpadded one that does too, so anything the engine culled
            // this frame fails this test as well. That is the important half - a culled object's
            // per-splat view data is left over from an older frame, and drawing it would put stale
            // depth (and a stale silhouette) into the mask. The other half, an object inside the
            // pad margin that the engine did draw and this skips, has no fragments on screen by
            // definition, so skipping it costs nothing.
            public bool IsVisibleTo(SplatRenderer r)
            {
                // No bounds means no asset loaded, so there is nothing to draw either way.
                return r.TryGetWorldBounds(out Bounds b)
                       && GeometryUtility.TestPlanesAABB(m_FrustumPlanes, b);
            }

            public void Dispose(bool immediate)
            {
                DestroyEntries(immediate);
                m_Shader = null;
                m_LastCheckFrame = -1;
                m_OutlineCountAtScan = -1;
                m_InvalidationsAtScan = -1;
                m_LastScanTime = float.NegativeInfinity;
            }

            void Rescan(Shader depthShader)
            {
                if (m_Shader != depthShader)
                    DestroyEntries(false); // a different shader makes every cached material wrong

                var found = Object.FindObjectsOfType<SplatRenderer>();
                if (m_Scratch.Length < found.Length)
                    m_Scratch = new Entry[Mathf.Max(found.Length, m_Scratch.Length * 2)];

                int kept = 0;
                for (int i = 0; i < found.Length; i++)
                {
                    var r = found[i];
                    if (r == null)
                        continue;
                    // Carry the materials over from the previous scan; they are expensive to make
                    // and the scene is usually the same one it was half a second ago.
                    var entry = Detach(r);
                    entry.Renderer = r;
                    entry.Outline = r.GetComponent<SplatOutline>();
                    if (entry.DepthMaterial == null)
                        entry.DepthMaterial = CreateMaterial(depthShader, r.name, true);
                    // Only where the non-occluded path would draw an outline too: SplatOutline
                    // resolves a SkinnedSplatRenderer, so a component sitting on a static capture
                    // outlines nothing either way, and this path must not invent an outline the
                    // other one does not draw.
                    if (entry.Outline != null && entry.Outline.Renderer != null && entry.ColorMaterial == null)
                        entry.ColorMaterial = CreateMaterial(depthShader, r.name, false);
                    m_Scratch[kept++] = entry;
                }

                DestroyEntries(false); // whatever is left in m_Entries did not survive the scan

                var swap = m_Entries;
                m_Entries = m_Scratch;
                m_Scratch = swap;
                m_Count = kept;
            }

            // Pulls an existing entry out of the live list, leaving a blank behind so the sweep
            // afterwards does not destroy the materials we are about to reuse.
            Entry Detach(SplatRenderer r)
            {
                for (int i = 0; i < m_Count; i++)
                {
                    if (m_Entries[i].Renderer != r)
                        continue;
                    var entry = m_Entries[i];
                    m_Entries[i] = default;
                    return entry;
                }
                return default;
            }

            bool HasDestroyedEntry()
            {
                for (int i = 0; i < m_Count; i++)
                {
                    if (m_Entries[i].Renderer == null)
                        return true;
                }
                return false;
            }

            void DestroyEntries(bool immediate)
            {
                for (int i = 0; i < m_Count; i++)
                {
                    DestroyMaterial(m_Entries[i].DepthMaterial, immediate);
                    DestroyMaterial(m_Entries[i].ColorMaterial, immediate);
                    m_Entries[i] = default;
                }
                m_Count = 0;
            }

            // One material per renderer per role, never shared: the splat draw binds the splat
            // buffers on the material itself (property blocks are unreliable for buffers on OpenGL
            // ES), and those bindings are read when the command buffer executes, not when it is
            // recorded. A shared material would leave every draw reading the last one's buffers.
            static Material CreateMaterial(Shader shader, string ownerName, bool depthOnly)
            {
                var mat = new Material(shader)
                {
                    name = depthOnly ? $"SplatOutlineOccluder ({ownerName})" : $"SplatOutlineMaskDepth ({ownerName})",
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (SplatCustomPass.RequiresVertexTextureFetch)
                    mat.EnableKeyword(SplatCustomPass.VertexTextureFetchKeyword);
                // ShaderLab render state, and so material state: a property block cannot override it.
                mat.SetFloat(s_MaskDepthOnly, depthOnly ? 1f : 0f);
                mat.SetFloat(s_MaskColorMask, depthOnly ? 0f : 15f);
                return mat;
            }

            static void DestroyMaterial(Material mat, bool immediate)
            {
                if (mat == null)
                    return;
                if (Application.isPlaying && !immediate)
                    Object.Destroy(mat);
                else
                    Object.DestroyImmediate(mat);
            }
        }

        // Depth-arbitrated version of RenderMasks. Walks every splat object nearest first and, for
        // each one, draws its silhouette (tested against the depth already laid down by the objects
        // in front of it) and then its own depth.
        //
        // The order within an object matters. Laying its depth down first would have its front-most
        // splat reject the rest of its own shell - all those splats sit at slightly different
        // depths - which shreds the mask from the inside, and the composite pass reads mask alpha
        // under 0.5 as "outside the silhouette", so the character would sprout outlines through its
        // own body.
        static void RenderMasksOccluded(CommandBuffer cmd, Camera cam, Shader depthShader, OccluderCache cache)
        {
            if (s_Released)
                return;

            s_Mpb ??= new MaterialPropertyBlock();
            cache.EnsureUpToDate(depthShader, SplatOutline.ActiveCount);
            cache.PrepareForCamera(cam);

            for (int i = 0; i < cache.Count; i++)
            {
                var entry = cache.Get(i);
                var r = entry.Renderer;
                if (r == null || !r.isActiveAndEnabled)
                    continue;
                // Same layer cull as RenderMasks: this walks its own list rather than the engine's
                // per-camera gather, so an object on a layer this camera does not render must not
                // put depth or a silhouette into its mask.
                if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0)
                    continue;
                // Skip what the engine did not draw this frame. The silhouette draw reuses the
                // per-splat view data the main pass computed, so for a frustum-culled object that
                // data is from an older frame and would put stale depth in the mask. It also saves
                // the draw outright for every object nobody can see.
                if (!cache.IsVisibleTo(r))
                    continue;

                var outline = entry.Outline;
                if (outline != null && outline.isActiveAndEnabled && entry.ColorMaterial != null)
                {
                    // Upload linear: URP composites in linear space.
                    s_Mpb.SetColor(s_OutlineColor, outline.m_Color.linear);
                    entry.ColorMaterial.SetColor(s_OutlineColor, outline.m_Color.linear);
                    SplatCustomPass.Draw(r, cmd, cam, entry.ColorMaterial, s_Mpb);
                }

                if (entry.DepthMaterial != null)
                    SplatCustomPass.Draw(r, cmd, cam, entry.DepthMaterial, s_Mpb);
            }
        }

        // Null unless the option is on AND usable. The passes test exactly this to pick their
        // branch, so "off" costs one null check per frame and nothing else.
        OccluderCache EnsureOccluders()
        {
            if (!m_OccludeByScene || m_DepthMaskShader == null)
            {
                if (m_OccludeByScene && m_DepthMaskShader == null && !m_WarnedNoDepthShader)
                {
                    m_WarnedNoDepthShader = true;
                    Debug.LogWarning("GaussianSplatOutlineFeature: 'Occlude By Scene' is on but " +
                                     "'Depth Mask Shader' is not assigned, so outlines are not being " +
                                     "occluded. Assign GaussianSplatOutlineMaskDepth.shader.");
                }
                DisposeOccluders(false);
                return null;
            }

            m_WarnedNoDepthShader = false;
            return m_Occluders ??= new OccluderCache();
        }

        void DisposeOccluders(bool immediate)
        {
            if (m_Occluders == null)
                return;
            m_Occluders.Dispose(immediate);
            m_Occluders = null;
        }

#if UNITY_6000_0_OR_NEWER
        class OutlinePass : ScriptableRenderPass
        {
            const string ProfilerTag = "GaussianSplatOutline";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);

            public Material OutlineMaterial;
            public Shader MaskShader;
            public Shader DepthMaskShader;
            public OccluderCache Occluders; // non-null only while Occlude By Scene is on

            class PassData
            {
                internal Camera Camera;
                internal TextureHandle CameraColor;
                internal TextureHandle MaskRT;
                internal TextureHandle MaskDepthRT;
                internal TextureHandle CameraDepthTexture;
                internal Vector2Int MaskSize;
                internal Material OutlineMaterial;
                internal Shader MaskShader;
                internal Shader DepthMaskShader;
                internal OccluderCache Occluders;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (s_Released || OutlineMaterial == null || MaskShader == null || SplatOutline.ActiveCount == 0)
                    return;

                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                var maskHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, k_MaskRTName, true);

                // Only when occluding: a companion depth target for the mask. RenderGraph textures
                // are one or the other, so this is a second handle rather than depth on the color
                // descriptor the way the compatibility path does it.
                TextureHandle maskDepthHandle = default;
                if (Occluders != null)
                {
                    RenderTextureDescriptor depthDesc = rtDesc;
                    depthDesc.graphicsFormat = GraphicsFormat.None;
                    depthDesc.depthBufferBits = 24;
                    maskDepthHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, k_MaskDepthRTName, true);
                }

                passData.Camera = cameraData.camera;
                passData.CameraColor = resourceData.activeColorTexture;
                passData.MaskRT = maskHandle;
                passData.MaskDepthRT = maskDepthHandle;
                passData.MaskSize = new Vector2Int(rtDesc.width, rtDesc.height);
                passData.OutlineMaterial = OutlineMaterial;
                passData.MaskShader = MaskShader;
                passData.DepthMaskShader = DepthMaskShader;
                passData.Occluders = Occluders;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(maskHandle, AccessFlags.ReadWrite);
                if (Occluders != null)
                {
                    builder.UseTexture(maskDepthHandle, AccessFlags.ReadWrite);
                    // The mesh half: the camera depth texture the mask shader tests against.
                    // Requested via ConfigureInput(Depth) in AddRenderPasses, so it is there; read
                    // it through the graph and bind it by name, because on this path the global
                    // is not set for us the way the compatibility path's copy-depth pass sets it.
                    if (resourceData.cameraDepthTexture.IsValid())
                    {
                        passData.CameraDepthTexture = resourceData.cameraDepthTexture;
                        builder.UseTexture(resourceData.cameraDepthTexture);
                    }
                }
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    if (s_Released)
                        return;

                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(cmd, s_profilingSampler);

                    if (data.Occluders != null)
                    {
                        // Scene-depth inputs for the mask shader's test. Same value the core splat
                        // pass computes for its own target: 1 / the size in pixels of the RT being
                        // drawn into. The core's is ScaledRTSize(...) because its RT follows
                        // SplatRenderScale; the mask RT is always the camera target descriptor's
                        // size, so this is that - the formula is the core's, the scale simply never
                        // applies here.
                        cmd.SetGlobalVector(s_SplatDepthTexParams,
                            new Vector4(1f / data.MaskSize.x, 1f / data.MaskSize.y, 0, 0));
                        if (data.CameraDepthTexture.IsValid())
                            cmd.SetGlobalTexture(s_CameraDepthTexture, data.CameraDepthTexture);
                        CoreUtils.SetRenderTarget(cmd, data.MaskRT, data.MaskDepthRT, ClearFlag.All, Color.clear);
                        RenderMasksOccluded(cmd, data.Camera, data.DepthMaskShader, data.Occluders);
                    }
                    else
                    {
                        CoreUtils.SetRenderTarget(cmd, data.MaskRT, ClearFlag.Color, Color.clear);
                        RenderMasks(cmd, data.Camera, data.MaskShader);
                    }

                    cmd.SetGlobalTexture(s_MaskRT, data.MaskRT);
                    Blitter.BlitCameraTexture(cmd, data.MaskRT, data.CameraColor, data.OutlineMaterial, 0);
                });
            }
        }

        [Tooltip("When the outline is drawn within the URP frame.\n\n" +
                 "AfterRenderingTransparents (default) draws it over everything URP renders - " +
                 "INCLUDING Screen Space - Camera and World Space UI, because those render in the " +
                 "transparent queue. That is why the outline can appear on top of UI.\n\n" +
                 "Move to BeforeRenderingTransparents so UI drawn afterwards covers it. Note this " +
                 "also puts the outline behind other transparents, so check it against your splat " +
                 "renderer's own pass.\n\n" +
                 "Screen Space - Overlay UI is composited by Unity outside URP entirely and always " +
                 "sits on top, whatever this is set to.")]
        public RenderPassEvent outlinePassEvent = RenderPassEvent.AfterRenderingTransparents;

        OutlinePass m_Pass;

        public override void Create()
        {
            s_Released = false;
            EnsureMaterial();
            m_Pass = new OutlinePass { renderPassEvent = outlinePassEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (s_Released || renderingData.cameraData.cameraType == CameraType.Preview || m_OutlineMaterial == null || m_MaskShader == null || m_Pass == null)
                return;
            SyncMaterialParams();
            m_Pass.OutlineMaterial = m_OutlineMaterial;
            m_Pass.MaskShader = m_MaskShader;
            m_Pass.DepthMaskShader = m_DepthMaskShader;
            m_Pass.Occluders = EnsureOccluders();
            // Occluding: ask URP for the camera depth texture the mask shader tests against. This
            // feature has to ask for itself - SplatRendererFeature requests it only when its own
            // manual test needs it, and nothing guarantees that feature ran at all. Not occluding:
            // ask for nothing, so the option off costs URP no depth copy. ConfigureInput assigns
            // rather than accumulates, so passing None here really does clear a previous request.
            m_Pass.ConfigureInput(m_Pass.Occluders != null
                ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
            renderer.EnqueuePass(m_Pass);
        }
#else
        class OutlinePass : ScriptableRenderPass
        {
            const string ProfilerTag = "GaussianSplatOutline";

            public Material OutlineMaterial;
            public Shader MaskShader;
            public Shader DepthMaskShader;
            public OccluderCache Occluders; // non-null only while Occlude By Scene is on

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (s_Released || OutlineMaterial == null || MaskShader == null || SplatOutline.ActiveCount == 0)
                    return;

                if (Occluders != null)
                {
                    ExecuteOccluded(context, ref renderingData);
                    return;
                }

                var cam = renderingData.cameraData.camera;
                CommandBuffer cmb = CommandBufferPool.Get(ProfilerTag);

                RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                cmb.GetTemporaryRT(s_MaskRT, rtDesc, FilterMode.Point);

                cmb.SetRenderTarget(s_MaskRT);
                cmb.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1.0f, 0);
                RenderMasks(cmb, cam, MaskShader);

                // The temp RT is exposed as the global "_GaussianOutlineMaskRT" the shader samples.
                var renderer = renderingData.cameraData.renderer;
                cmb.SetRenderTarget(renderer.cameraColorTargetHandle);
                cmb.DrawProcedural(Matrix4x4.identity, OutlineMaterial, 0, MeshTopology.Triangles, 3, 1);

                cmb.ReleaseTemporaryRT(s_MaskRT);
                context.ExecuteCommandBuffer(cmb);
                CommandBufferPool.Release(cmb);
            }

            // Same shape as Execute above, with a depth buffer on the mask and the depth-arbitrated
            // draw loop. Kept as its own method rather than threaded through that one with flags,
            // so the path above stays exactly what it was.
            void ExecuteOccluded(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cam = renderingData.cameraData.camera;
                CommandBuffer cmb = CommandBufferPool.Get(ProfilerTag);

                RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
                // The one structural difference: the mask gets a depth buffer, so the silhouette
                // draws can be tested against the occluders standing in front of them. 24-bit
                // rather than 16 because the whole point is telling two characters a step apart
                // from each other.
                rtDesc.depthBufferBits = 24;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                cmb.GetTemporaryRT(s_MaskRT, rtDesc, FilterMode.Point);

                // Scene-depth input for the mask shader's test against opaque geometry. Same value
                // the core splat pass computes for its own target: 1 / the size in pixels of the RT
                // being drawn into. The core's is ScaledRTSize(...) because its RT follows
                // SplatRenderScale; the mask RT is always the camera target descriptor's size, so
                // this is that - the formula is the core's, the scale simply never applies here.
                // (_CameraDepthTexture itself is bound as a global by URP's depth prepass / copy,
                // which ConfigureInput(Depth) in AddRenderPasses forces to run.)
                cmb.SetGlobalVector(s_SplatDepthTexParams, new Vector4(1f / rtDesc.width, 1f / rtDesc.height, 0, 0));

                cmb.SetRenderTarget(s_MaskRT);
                cmb.ClearRenderTarget(RTClearFlags.All, Color.clear, 1.0f, 0);
                RenderMasksOccluded(cmb, cam, DepthMaskShader, Occluders);

                // The temp RT is exposed as the global "_GaussianOutlineMaskRT" the shader samples.
                var renderer = renderingData.cameraData.renderer;
                cmb.SetRenderTarget(renderer.cameraColorTargetHandle);
                cmb.DrawProcedural(Matrix4x4.identity, OutlineMaterial, 0, MeshTopology.Triangles, 3, 1);

                cmb.ReleaseTemporaryRT(s_MaskRT);
                context.ExecuteCommandBuffer(cmb);
                CommandBufferPool.Release(cmb);
            }
        }

        [Tooltip("When the outline is drawn within the URP frame.\n\n" +
                 "AfterRenderingTransparents (default) draws it over everything URP renders - " +
                 "INCLUDING Screen Space - Camera and World Space UI, because those render in the " +
                 "transparent queue. That is why the outline can appear on top of UI.\n\n" +
                 "Move to BeforeRenderingTransparents so UI drawn afterwards covers it. Note this " +
                 "also puts the outline behind other transparents, so check it against your splat " +
                 "renderer's own pass.\n\n" +
                 "Screen Space - Overlay UI is composited by Unity outside URP entirely and always " +
                 "sits on top, whatever this is set to.")]
        public RenderPassEvent outlinePassEvent = RenderPassEvent.AfterRenderingTransparents;

        OutlinePass m_Pass;

        public override void Create()
        {
            s_Released = false;
            EnsureMaterial();
            m_Pass = new OutlinePass { renderPassEvent = outlinePassEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (s_Released || renderingData.cameraData.cameraType == CameraType.Preview || m_OutlineMaterial == null || m_MaskShader == null || m_Pass == null)
                return;
            SyncMaterialParams();
            m_Pass.OutlineMaterial = m_OutlineMaterial;
            m_Pass.MaskShader = m_MaskShader;
            m_Pass.DepthMaskShader = m_DepthMaskShader;
            m_Pass.Occluders = EnsureOccluders();
            // Occluding: ask URP for the camera depth texture the mask shader tests against. This
            // feature has to ask for itself - SplatRendererFeature requests it only when its own
            // manual test needs it, and nothing guarantees that feature ran at all. Not occluding:
            // ask for nothing, so the option off costs URP no depth copy. ConfigureInput assigns
            // rather than accumulates, so passing None here really does clear a previous request.
            m_Pass.ConfigureInput(m_Pass.Occluders != null
                ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
            renderer.EnqueuePass(m_Pass);
        }
#endif // UNITY_6000_0_OR_NEWER

        void EnsureMaterial()
        {
            if (m_OutlineMaterial == null && m_OutlineShader != null)
                m_OutlineMaterial = CoreUtils.CreateEngineMaterial(m_OutlineShader);
            SyncMaterialParams();
        }

        protected override void Dispose(bool disposing)
        {
            DisposeInternal(false);
        }

        public void ReleaseResources()
        {
            DisposeInternal(true);
        }

        void DisposeInternal(bool immediate)
        {
            if (m_OutlineMaterial != null)
            {
                if (immediate)
                    Object.DestroyImmediate(m_OutlineMaterial);
                else
                    CoreUtils.Destroy(m_OutlineMaterial);
            }
            m_OutlineMaterial = null;
            DisposeOccluders(immediate);
            m_Pass = null;
        }

        public static void ReleaseAll()
        {
            s_Released = true;
            s_Mpb = null;
            foreach (var feature in Object.FindObjectsOfType<GaussianSplatOutlineFeature>(true))
            {
                if (feature != null)
                    feature.ReleaseResources();
            }
        }
    }
}

#endif // #if GS_ENABLE_URP
