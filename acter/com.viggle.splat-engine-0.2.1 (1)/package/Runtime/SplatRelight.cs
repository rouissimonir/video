// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// The per-character half of splat relighting: one component that does everything the
    /// character needs. Add it next to the SkinnedSplatRenderer and it
    ///   1. generates the relight proxy at load - voxelizes the character's own splats, welds
    ///      and smooths the surface, skins it to the rig, gives the character a real shadow;
    ///   2. watches every light in the scene and recomputes the splat colors when one changes
    ///      (the renderer otherwise skips its per-splat compute on static frames);
    ///   3. hosts the debug switch, so the SplatRelightingFeature on the renderer asset is
    ///      configure-once and never touched during debugging.
    /// </summary>
    /// <remarks>
    /// The rendering half is SplatRelightingFeature on the URP renderer asset: proxy layer,
    /// texture resolution, reference luminance, self-shadow bias. Set those once.
    ///
    /// Proxy mesh notes: blocky by construction (surface faces of occupied voxels) then welded
    /// and Laplacian-smoothed, so the silhouette and self-shadows are stable while animating.
    /// With a built rig it is a SkinnedMeshRenderer whose voxels take the blended bone weights
    /// of the splats inside them, deforming in lockstep with the splat cloud.
    /// </remarks>
    [AddComponentMenu("Viggle Splat Engine/Splat Relight")]
    [DisallowMultipleComponent]
    public class SplatRelight : MonoBehaviour
    {
        public enum DebugMode
        {
            [Tooltip("Normal rendering.")]
            None = 0,
            [Tooltip("Show the baked lighting texture full screen.")]
            LightingTexture = 1,
            [Tooltip("Show the proxy coverage mask - white where a proxy covers the pixel.")]
            CoverageMask = 2,
            [Tooltip("Draw the proxy as a translucent ghost over the image, for alignment checks.")]
            ProxyGhost = 3,
            [Tooltip("Paint every splat with its own relight sample: magenta = texture not " +
                     "published, yellow = behind camera, red = off screen, blue = no proxy " +
                     "coverage, orange = coverage only at the mirrored row (flip bug), greys = " +
                     "the sampled lighting.")]
            SplatSample = 4,
        }

        [Header("Lighting")]
        [Range(0f, 1f)]
        [Tooltip("How much scene lighting replaces the splats' baked appearance. Splat colors " +
                 "already contain the lighting of the capture, so a full replace double-lights " +
                 "them - 0.3-0.6 is the useful range. 0 turns the whole system off.")]
        public float m_Strength = 0.5f;

        [Tooltip("Tints the light this character receives, without touching its base color.")]
        [ColorUsage(false, true)] public Color m_Tint = Color.white;

        [Range(0f, 1f)]
        [Tooltip("Floor under the light term, so surfaces facing away from every light keep some " +
                 "of their original appearance instead of going black.")]
        public float m_Ambient = 0.2f;

        [Range(1, 16)]
        [Tooltip("Refresh the lighting every Nth frame rather than every frame a light moves. " +
                 "Lighting is low-frequency, so 2-4 is usually invisible and keeps the static-" +
                 "scene compute skip engaging.")]
        public int m_UpdateNthFrame = 1;

        [Header("Debug")]
        [Tooltip("All relight debugging lives here - the renderer feature never needs touching.")]
        public DebugMode m_Debug = DebugMode.None;

        [Header("Proxy generation")]
        [Range(0.02f, 0.2f)]
        [Tooltip("Edge length of one voxel, in meters. 0.05 resolves limbs on a human-sized " +
                 "character; smaller sharpens the silhouette at cubic memory cost.")]
        public float m_VoxelSize = 0.05f;

        [Range(0f, 1f)]
        [Tooltip("Splats fainter than this do not mark voxels. Filters the halo of near-" +
                 "transparent floaters around a capture, which would otherwise inflate the proxy.")]
        public float m_OpacityThreshold = 0.2f;

        [Range(0, 2)]
        [Tooltip("Grow the voxel volume outward this many voxels. One step closes small holes and " +
                 "lets edge splats (hair tips, cloth edges) still land inside the proxy's mask.")]
        public int m_Dilate = 1;

        [Range(0, 8)]
        [Tooltip("Laplacian smoothing passes over the voxel surface. Rounds the blocky mesh so the " +
                 "coverage silhouette and the self-shadows stay stable while the mesh animates. " +
                 "0 = raw cubes.")]
        public int m_SmoothIterations = 3;

        [Tooltip("Layer for the generated proxy (pick ONE; with several checked the lowest wins). " +
                 "The SplatRelightingFeature finds this layer on its own while its Proxy Layer " +
                 "Source is Auto. Keep it CHECKED in the camera's Culling Mask, and excluded from " +
                 "the renderer's Opaque/Transparent masks.")]
        public LayerMask m_ProxyLayer = 1 << 10;

        /// <summary>
        /// The single layer index this character's proxy is put on, or -1 when no layer is
        /// selected. The SplatRelightingFeature unions this across every enabled component to work
        /// out what it has to draw, so the two halves cannot drift apart.
        /// </summary>
        public int ProxyLayerIndex => SplatProxyLayers.Resolve(m_ProxyLayer.value);

        [Tooltip("Skin the proxy to the character's rig so it follows animation. Needs a " +
                 "SplatCharacterSkinner with a built rig; falls back to a static rest-pose mesh " +
                 "without one.")]
        public bool m_Skinned = true;

        [Tooltip("Cast real shadows from the proxy, giving the splat character a shadow in the " +
                 "scene (splats themselves cannot cast). The proxy stays invisible either way.")]
        public bool m_CastShadows = true;

        [Header("Performance")]
        [Tooltip("Beyond this distance from the camera the proxy stops rendering and the character " +
                 "falls back to its baked appearance - lighting differences are not readable at " +
                 "range. 0 = no limit.")]
        public float m_MaxRelightDistance = 0f;

        SkinnedSplatRenderer m_Viewer;
        SplatRenderer m_Renderer;
        GameObject m_ProxyGo;
        Renderer m_Proxy;
        Mesh m_Mesh;
        Material m_ProxyMaterial;
        bool m_Generated;

        // Frame stride bookkeeping, staggered per character so a crowd does not all refresh on
        // the same frame. Mirrors how the depth sort staggers itself (m_SortPhase).
        int m_Phase = -1;
        bool m_RefreshPending;

        void Awake()
        {
            m_Viewer = GetComponentInChildren<SkinnedSplatRenderer>(true);
            m_Renderer = m_Viewer != null ? m_Viewer : GetComponentInChildren<SplatRenderer>(true);
            if (m_Phase < 0)
                m_Phase = GetInstanceID() & 0x7fffffff;
        }

        bool WantsSkinning => m_Skinned && m_Viewer != null;

        void LateUpdate()
        {
            if (m_Renderer == null)
                return;

            PushDebug();

            // This component owns the user-facing parameters; the renderer fields are the
            // runtime state its uniform uploads read.
            m_Renderer.m_RelightStrength = m_Strength;
            m_Renderer.m_RelightTint = m_Tint;
            m_Renderer.m_RelightAmbient = m_Ambient;
            m_Renderer.m_RelightUpdateNthFrame = m_UpdateNthFrame;

            // Relighting off for this character: keep the proxy dark and the compute asleep.
            if (m_Strength <= 0f)
            {
                SetProxyActive(false);
                return;
            }

            // The character bin loads in the viewer's own setup and the rig is built by the
            // skinner, either of which can complete after us. Poll until ready, generate once.
            if (!m_Generated)
            {
                if (m_Viewer == null || m_Viewer.Bin == null)
                    return;
                if (WantsSkinning && !m_Viewer.RigReady)
                    return;
                Regenerate();
                if (!m_Generated)
                    return;
            }

            bool inRange = IsInRelightRange();
            SetProxyActive(inRange);
            if (!inRange)
                return;

            if (AnyLightChanged())
                m_RefreshPending = true;

            // Apply a pending refresh on this character's scheduled frame. Lighting is
            // low-frequency, so spreading refreshes across frames is invisible while keeping the
            // static-scene compute skip engaged on the frames in between.
            if (!m_RefreshPending)
                return;
            int stride = Mathf.Max(1, m_UpdateNthFrame);
            if (stride > 1 && (Time.frameCount + m_Phase) % stride != 0)
                return;

            m_Renderer.InvalidateViewData();
            m_RefreshPending = false;
        }

        // ------------------------------------------------------------------ debug

        void PushDebug()
        {
            bool splatSample = m_Debug == DebugMode.SplatSample;
            if (m_Renderer.m_RelightDebugSplatView != splatSample)
            {
                m_Renderer.m_RelightDebugSplatView = splatSample;
                m_Renderer.InvalidateViewData();
            }
// Matches SplatRelightingFeature's own guard: the feature does not exist below Unity 6, so
// neither does the debug channel into it. The splat-sample view above is ours and stays.
#if GS_ENABLE_URP && UNITY_6000_0_OR_NEWER
            switch (m_Debug)
            {
                case DebugMode.LightingTexture:
                    SplatRelightingFeature.RequestDebug(SplatRelightingFeature.DebugView.RelightRGB);
                    break;
                case DebugMode.CoverageMask:
                    SplatRelightingFeature.RequestDebug(SplatRelightingFeature.DebugView.MaskAlpha);
                    break;
                case DebugMode.ProxyGhost:
                    SplatRelightingFeature.RequestDebug(SplatRelightingFeature.DebugView.ProxyOverlay);
                    break;
            }
#endif
        }

        // ------------------------------------------------------------------ light watching

        // All-lights change signature: a cheap weighted sum over every enabled light's pose,
        // color, intensity and range - any change moves the number and triggers a recompute.
        // This is what makes a light matter on an otherwise static frame: the renderer reuses
        // the previous per-splat compute until something says the world changed.
        Light[] m_AllLights;
        int m_LightsRefreshFrame = -1000;
        float m_LastLightsSignature = float.NaN;

        bool AnyLightChanged()
        {
            if (m_AllLights == null || Time.frameCount - m_LightsRefreshFrame > 120)
            {
                m_AllLights = FindObjectsByType<Light>(FindObjectsSortMode.None);
                m_LightsRefreshFrame = Time.frameCount;
            }
            float sig = 0f;
            for (int i = 0; i < m_AllLights.Length; i++)
            {
                var l = m_AllLights[i];
                if (l == null || !l.isActiveAndEnabled)
                    continue;
                var p = l.transform.position;
                var d = l.transform.forward;
                sig += p.x * 0.731f + p.y * 1.259f + p.z * 2.117f
                     + d.x * 3.001f + d.y * 4.917f + d.z * 6.053f
                     + l.intensity * 7.919f + l.range * 0.371f
                     + l.color.r * 0.913f + l.color.g * 1.731f + l.color.b * 2.633f;
            }
            if (float.IsNaN(m_LastLightsSignature)) { m_LastLightsSignature = sig; return true; }
            if (Mathf.Abs(sig - m_LastLightsSignature) < 1e-3f)
                return false;
            m_LastLightsSignature = sig;
            return true;
        }

        bool IsInRelightRange()
        {
            if (m_MaxRelightDistance <= 0f)
                return true;
            var cam = Camera.main;
            if (cam == null)
                return true;
            float d = Vector3.Distance(cam.transform.position, transform.position);
            return d <= m_MaxRelightDistance;
        }

        void SetProxyActive(bool active)
        {
            if (m_Proxy == null)
                return;
            if (m_Proxy.enabled != active)
                m_Proxy.enabled = active;
        }

        // ------------------------------------------------------------------ proxy generation

        /// <summary>Rebuilds the proxy from the current character data.</summary>
        public void Regenerate()
        {
            var bin = m_Viewer != null ? m_Viewer.Bin : null;
            if (bin == null || bin.numSplats == 0)
                return;

            bool skinned = WantsSkinning && m_Viewer.RigReady &&
                           bin.boneIndices.IsCreated && bin.boneWeights.IsCreated &&
                           m_Viewer.Bones != null && m_Viewer.RestInv != null;

            var mesh = BuildVoxelMesh(bin, skinned, out var boneWeights);
            if (mesh == null)
                return;

            // The renderer type depends on the skinning decision, so a change there rebuilds the
            // holder object rather than trying to swap component types in place.
            if (m_ProxyGo != null)
                Destroy(m_ProxyGo);
            m_ProxyGo = new GameObject("SplatRelight proxy (generated)");
            // Same local space as the splat positions: child of the character with an identity
            // local transform tracks the character wherever it goes.
            m_ProxyGo.transform.SetParent(m_Viewer.transform, false);
            // The same resolution the feature unions over, so what gets drawn is what got put
            // here. With nothing selected the proxy stays on Default, as it always has - visible,
            // because Default is not one of the layers the renderer asset hides.
            int layerIndex = ProxyLayerIndex;
            m_ProxyGo.layer = layerIndex >= 0 ? layerIndex : 0;

            if (m_Mesh != null)
                Destroy(m_Mesh);
            m_Mesh = mesh;

            if (skinned)
            {
                var bones = m_Viewer.Bones;
                var restInv = m_Viewer.RestInv;
                int count = Mathf.Min(bones.Length, restInv.Length);
                var bindposes = new Matrix4x4[count];
                for (int i = 0; i < count; i++)
                    bindposes[i] = restInv[i];
                mesh.boneWeights = boneWeights;
                mesh.bindposes = bindposes;

                var smr = m_ProxyGo.AddComponent<SkinnedMeshRenderer>();
                smr.bones = bones;
                smr.rootBone = m_Viewer.transform;
                smr.sharedMesh = mesh;
                // The proxy is culled from every camera by layer, which would freeze its
                // skinning; the relight pass still needs the current pose.
                smr.updateWhenOffscreen = true;
                m_Proxy = smr;
            }
            else
            {
                m_ProxyGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                m_Proxy = m_ProxyGo.AddComponent<MeshRenderer>();
            }
            // ShadowCastingMode.On, not ShadowsOnly: ShadowsOnly drops the renderer from the
            // normal culled object lists, which would ALSO empty the relight pass's draw list.
            // The color passes never draw it anyway - its layer is excluded on the renderer
            // asset - so On gives shadows plus the relight draw, and Off gives neither shadow.
            m_Proxy.shadowCastingMode = m_CastShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            // The bake pass overrides materials, so this one only matters for the SHADOW pass -
            // any material with a ShadowCaster pass works; URP/Lit is the safe default.
            if (m_ProxyMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null)
                    m_ProxyMaterial = new Material(shader);
                else if (SplatLog.Warning)
                    Debug.LogWarning("SplatRelight: URP/Lit not found (player build?); the proxy " +
                                     "cannot cast shadows without a material.", this);
            }
            m_Proxy.sharedMaterial = m_ProxyMaterial;

            m_Generated = true;
            if (SplatLog.Info)
                Debug.Log($"SplatRelight: {(skinned ? "skinned" : "static")} proxy, " +
                          $"{mesh.vertexCount} verts from {bin.numSplats:N0} splats " +
                          $"at {m_VoxelSize:F3}m voxels", this);
        }

        Mesh BuildVoxelMesh(SplatCharacterBinData bin, bool skinned, out BoneWeight[] outWeights)
        {
            outWeights = null;
            int n = bin.numSplats;
            var positions = bin.positions;
            var opacities = bin.opacities;

            // Bounds over the splats that count, padded so dilation never clips at the border.
            float3 mn = new float3(float.MaxValue), mx = new float3(float.MinValue);
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                if (opacities.IsCreated && opacities[i] < m_OpacityThreshold)
                    continue;
                mn = math.min(mn, positions[i]);
                mx = math.max(mx, positions[i]);
                used++;
            }
            if (used == 0)
                return null;

            float voxel = m_VoxelSize;
            float pad = (m_Dilate + 1) * voxel;
            mn -= pad; mx += pad;
            int3 dims = (int3)math.ceil((mx - mn) / voxel) + 1;
            // Memory guard: a bad voxel size on a large capture would otherwise allocate a
            // pathological grid. Coarsen instead - a coarser proxy still lights correctly.
            const long kMaxVoxels = 4_000_000;
            while ((long)dims.x * dims.y * dims.z > kMaxVoxels)
            {
                voxel *= 1.5f;
                dims = (int3)math.ceil((mx - mn) / voxel) + 1;
            }
            if (SplatLog.Warning && voxel != m_VoxelSize)
                Debug.LogWarning($"SplatRelight: voxel size raised {m_VoxelSize:F3} -> {voxel:F3} " +
                                 $"to keep the grid under {kMaxVoxels:N0} voxels", this);

            int nx = dims.x, ny = dims.y, nz = dims.z;
            int Idx(int x, int y, int z) => (z * ny + y) * nx + x;
            var occ = new bool[nx * ny * nz];
            // Per-voxel accumulated bone weights, keyed by voxel index. Sparse: only occupied
            // voxels (a few thousand) carry an entry.
            var voxelBones = skinned ? new Dictionary<int, Dictionary<int, float>>() : null;

            var boneIndices = bin.boneIndices;
            var binWeights = bin.boneWeights;
            for (int i = 0; i < n; i++)
            {
                if (opacities.IsCreated && opacities[i] < m_OpacityThreshold)
                    continue;
                int3 c = (int3)math.floor((positions[i] - mn) / voxel);
                int vi = Idx(c.x, c.y, c.z);
                occ[vi] = true;
                if (voxelBones == null)
                    continue;
                if (!voxelBones.TryGetValue(vi, out var acc))
                    voxelBones[vi] = acc = new Dictionary<int, float>();
                uint4 bi = boneIndices[i];
                float4 bw = binWeights[i];
                for (int j = 0; j < 4; j++)
                {
                    float w = bw[j];
                    if (w <= 0f)
                        continue;
                    int b = (int)bi[j];
                    acc.TryGetValue(b, out float cur);
                    acc[b] = cur + w;
                }
            }

            // 6-neighbor dilation, run m_Dilate times. Grown voxels inherit the bone weights of
            // the voxel that grew them, so dilated skin still follows the right limb.
            for (int pass = 0; pass < m_Dilate; pass++)
            {
                var grown = (bool[])occ.Clone();
                for (int z = 1; z < nz - 1; z++)
                for (int y = 1; y < ny - 1; y++)
                for (int x = 1; x < nx - 1; x++)
                {
                    int vi = Idx(x, y, z);
                    if (occ[vi]) continue;
                    int src = -1;
                    if (occ[Idx(x - 1, y, z)]) src = Idx(x - 1, y, z);
                    else if (occ[Idx(x + 1, y, z)]) src = Idx(x + 1, y, z);
                    else if (occ[Idx(x, y - 1, z)]) src = Idx(x, y - 1, z);
                    else if (occ[Idx(x, y + 1, z)]) src = Idx(x, y + 1, z);
                    else if (occ[Idx(x, y, z - 1)]) src = Idx(x, y, z - 1);
                    else if (occ[Idx(x, y, z + 1)]) src = Idx(x, y, z + 1);
                    if (src < 0)
                        continue;
                    grown[vi] = true;
                    if (voxelBones != null && !voxelBones.ContainsKey(vi) &&
                        voxelBones.TryGetValue(src, out var srcAcc))
                        voxelBones[vi] = srcAcc; // shared reference is fine, read-only from here
                }
                occ = grown;
            }

            BoneWeight VoxelWeight(int vi)
            {
                var bw = new BoneWeight();
                if (voxelBones == null || !voxelBones.TryGetValue(vi, out var acc) || acc.Count == 0)
                    return bw; // all-zero: bound to bone 0 with weight 0, harmless for lighting
                // Top-4 accumulated bones, normalized.
                int count = 0;
                var top = new KeyValuePair<int, float>[4];
                foreach (var kv in acc)
                {
                    if (count < 4) { top[count++] = kv; continue; }
                    int min = 0;
                    for (int k = 1; k < 4; k++) if (top[k].Value < top[min].Value) min = k;
                    if (kv.Value > top[min].Value) top[min] = kv;
                }
                float total = 0;
                for (int k = 0; k < count; k++) total += top[k].Value;
                if (total <= 0)
                    return bw;
                if (count > 0) { bw.boneIndex0 = top[0].Key; bw.weight0 = top[0].Value / total; }
                if (count > 1) { bw.boneIndex1 = top[1].Key; bw.weight1 = top[1].Value / total; }
                if (count > 2) { bw.boneIndex2 = top[2].Key; bw.weight2 = top[2].Value / total; }
                if (count > 3) { bw.boneIndex3 = top[3].Key; bw.weight3 = top[3].Value / total; }
                return bw;
            }

            // WELDED vertices, keyed by grid corner: shared vertices are what make the
            // Laplacian smoothing below possible (and cut the vertex count roughly in half).
            var verts = new List<Vector3>();
            var weights = skinned ? new List<BoneWeight>() : null;
            var tris = new List<int>();
            var cornerIndex = new Dictionary<long, int>();
            long CornerKey(int x, int y, int z) => ((long)z * (ny + 1) + y) * (nx + 1) + x;

            int GetVert(int cx, int cy, int cz, int sourceVoxel)
            {
                long key = CornerKey(cx, cy, cz);
                if (cornerIndex.TryGetValue(key, out int idx))
                    return idx;
                idx = verts.Count;
                cornerIndex[key] = idx;
                verts.Add((Vector3)(mn + new float3(cx, cy, cz) * voxel));
                weights?.Add(VoxelWeight(sourceVoxel));
                return idx;
            }

            // One quad per voxel face that borders empty space, wound outward.
            void Face(int x, int y, int z, int3 dir, int3 ca, int3 cb, int3 cc, int3 cd)
            {
                int px = x + dir.x, py = y + dir.y, pz = z + dir.z;
                bool open = px < 0 || py < 0 || pz < 0 || px >= nx || py >= ny || pz >= nz
                            || !occ[Idx(px, py, pz)];
                if (!open)
                    return;
                int src = Idx(x, y, z);
                int i0 = GetVert(x + ca.x, y + ca.y, z + ca.z, src);
                int i1 = GetVert(x + cb.x, y + cb.y, z + cb.z, src);
                int i2 = GetVert(x + cc.x, y + cc.y, z + cc.z, src);
                int i3 = GetVert(x + cd.x, y + cd.y, z + cd.z, src);
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                tris.Add(i0); tris.Add(i2); tris.Add(i3);
            }

            for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                if (!occ[Idx(x, y, z)])
                    continue;
                Face(x, y, z, new int3(-1, 0, 0), new int3(0, 0, 0), new int3(0, 0, 1), new int3(0, 1, 1), new int3(0, 1, 0));
                Face(x, y, z, new int3(1, 0, 0),  new int3(1, 0, 0), new int3(1, 1, 0), new int3(1, 1, 1), new int3(1, 0, 1));
                Face(x, y, z, new int3(0, -1, 0), new int3(0, 0, 0), new int3(1, 0, 0), new int3(1, 0, 1), new int3(0, 0, 1));
                Face(x, y, z, new int3(0, 1, 0),  new int3(0, 1, 0), new int3(0, 1, 1), new int3(1, 1, 1), new int3(1, 1, 0));
                Face(x, y, z, new int3(0, 0, -1), new int3(0, 0, 0), new int3(0, 1, 0), new int3(1, 1, 0), new int3(1, 0, 0));
                Face(x, y, z, new int3(0, 0, 1),  new int3(0, 0, 1), new int3(1, 0, 1), new int3(1, 1, 1), new int3(0, 1, 1));
            }

            if (tris.Count == 0)
                return null;

            // Laplacian smoothing: melt the cube staircase into a rounded surface. Plain
            // (shrinking) Laplacian on purpose - the surface was dilated outward first, so the
            // mild shrink pulls it back toward the true body while it rounds off.
            if (m_SmoothIterations > 0)
            {
                var neighbors = new HashSet<int>[verts.Count];
                for (int t = 0; t < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    (neighbors[a] ??= new HashSet<int>()).Add(b); neighbors[a].Add(c);
                    (neighbors[b] ??= new HashSet<int>()).Add(a); neighbors[b].Add(c);
                    (neighbors[c] ??= new HashSet<int>()).Add(a); neighbors[c].Add(b);
                }
                var next = new Vector3[verts.Count];
                for (int pass = 0; pass < m_SmoothIterations; pass++)
                {
                    for (int i = 0; i < verts.Count; i++)
                    {
                        var nb = neighbors[i];
                        if (nb == null || nb.Count == 0) { next[i] = verts[i]; continue; }
                        Vector3 avg = Vector3.zero;
                        foreach (int j in nb) avg += verts[j];
                        avg /= nb.Count;
                        next[i] = Vector3.Lerp(verts[i], avg, 0.5f);
                    }
                    for (int i = 0; i < verts.Count; i++) verts[i] = next[i];
                }
            }

            var mesh = new Mesh { name = "SplatRelight proxy" };
            if (verts.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            // Shared vertices + smoothed geometry give proper smooth normals directly.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (weights != null)
                outWeights = weights.ToArray();
            return mesh;
        }

        void OnValidate()
        {
            // Parameter edits must reach the screen even on a static frame.
            if (m_Renderer != null)
                m_Renderer.InvalidateViewData();
        }

        void OnEnable()
        {
            // How SplatRelightingFeature learns which layer to draw; see SplatProxyLayers.
            SplatProxyLayers.Register(this);
        }

        void OnDisable()
        {
            SplatProxyLayers.Unregister(this);
            SetProxyActive(false);
        }

        void OnDestroy()
        {
            if (m_Mesh != null)
                Destroy(m_Mesh);
            if (m_ProxyGo != null)
                Destroy(m_ProxyGo);
            if (m_ProxyMaterial != null)
                Destroy(m_ProxyMaterial);
        }
    }
}
