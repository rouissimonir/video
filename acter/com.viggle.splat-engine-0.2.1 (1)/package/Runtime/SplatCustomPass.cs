// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Rendering;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Draws a splat object with your own material, from your own render pass. The splat
    /// equivalent of issuing a draw for a mesh with a material of your choosing, and the building
    /// block for anything that needs the object's shape on screen - outlines, glows, stencil
    /// masks, custom depth, selection highlights - without the package shipping each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists as public API because everything it needs is not: the per-splat view data, the
    /// sorted order buffer and the projection state all belong to a renderer a host cannot reach.
    /// Without it there is no way to write a custom splat pass at all.
    /// </para>
    /// <para>
    /// The material's vertex stage must read the same per-splat view data as the package's
    /// RenderGaussianSplats.shader. Start from the GaussianSplatOutlineMask shader in the Effects
    /// sample rather than writing one from scratch.
    /// </para>
    /// </remarks>
    public static class SplatCustomPass
    {
        /// <summary>
        /// The shader keyword that switches a splat material's vertex stage to read view data
        /// from a texture instead of from StructuredBuffers. Enable it on your material when
        /// <see cref="RequiresVertexTextureFetch"/> is true, and declare the matching
        /// <c>#pragma multi_compile</c> in the shader.
        /// </summary>
        public static string VertexTextureFetchKeyword => SplatRenderer.VertexTextureFetchKeyword;

        /// <summary>
        /// True on devices whose vertex stage cannot bind the StructuredBuffers the splat shader
        /// normally uses - several mobile GLES drivers report a limit of zero - so the renderer
        /// feeds view data through a texture instead. Probed once from
        /// <see cref="SystemInfo"/>; constant for the lifetime of the process.
        /// </summary>
        public static bool RequiresVertexTextureFetch => SplatRenderer.RequiresVertexTextureFetch;

        /// <summary>
        /// Draws a splat object's splats with <paramref name="material"/> into the currently
        /// bound render target.
        /// </summary>
        /// <param name="splat">The object to draw - a static capture or an animated character,
        /// which is a <see cref="SplatRenderer"/> too. Nothing is drawn if it has not loaded.</param>
        /// <param name="cmd">The command buffer to record the draw into.</param>
        /// <param name="camera">The camera being rendered. Supplies the screen size the splat
        /// projection needs.</param>
        /// <param name="material">Your material. One instance per object: on OpenGL ES,
        /// consecutive draws through a shared material alias each other's vertex-stage buffers.</param>
        /// <param name="properties">Per-draw overrides, e.g. an outline color. Required, because
        /// the per-splat buffers are bound onto it.</param>
        /// <returns>False when nothing was drawn, i.e. the object is not ready.</returns>
        /// <remarks>
        /// Reuses the per-splat view data the main splat pass already computed for this camera
        /// this frame, so schedule your pass after the splats have rendered. Drawing before that
        /// reads the previous frame's projection.
        /// </remarks>
        /// <remarks>
        /// One overload, not one per component: a character IS a <see cref="SplatRenderer"/>. The
        /// two differ in what they load and how they pose it, not in how their splats reach the
        /// screen, so a custom pass has no reason to care which one it was handed.
        /// </remarks>
        public static bool Draw(SplatRenderer splat, CommandBuffer cmd, Camera camera,
                                Material material, MaterialPropertyBlock properties)
        {
            if (splat == null || cmd == null || camera == null)
                return false;
            return splat.RenderSilhouette(cmd, camera, material, properties);
        }
    }
}
