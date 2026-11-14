# Splat Effects (sample)

Example visual effects for splat characters, built entirely on the renderer's public extension
points. None of this is required to render or animate a character - copy a file, tweak it, or
delete what you don't use.

Three effects are included, demonstrating the two extension points the renderer offers:

| Effect | Extension point | Where it hooks |
| --- | --- | --- |
| **Outline** | `SplatCustomPass.Draw(...)` | a screen-space pass (URP renderer feature) |
| **Tint / flash** | `SkinnedSplatRenderer.baseColor` | per-splat, in the renderer's compute pass |
| **Color + opacity** | `baseColor` / `opacityScale` | per-splat; no shader edits, no renderer feature |

Every one of these is public `Viggle.SplatEngine` API. Write any value on `SkinnedSplatRenderer`
from code and call `ApplyVisuals()` to push it - inspector edits push on their own.

Only the outline needs one-time renderer setup. The other two are drop-on components.

---

## Outline

Renders each character's silhouette into an off-screen mask, then expands that mask into an
outline over the scene. Overlapping characters keep their own colors.

**One-time setup (URP):**

1. Select the URP **Renderer** asset your camera or quality level uses.
2. **Add Renderer Feature > Gaussian Splat Outline Feature**. Add it *after* the
   `SplatRendererFeature` that renders the splats themselves.
3. On the feature, assign **Mask Shader** = `GaussianSplatOutlineMask` and **Outline Shader** =
   `GaussianSplatOutline`, then set **Thickness** (px) and **Intensity** to taste. Assign **Depth
   Mask Shader** = `GaussianSplatOutlineMaskDepth` as well: it is used only by **Occlude By Scene**
   below, but assigning it now means the option is there the moment you want it.

**Per character:** add a **Splat Outline** component next to the `SkinnedSplatRenderer` and pick
its **Color**. Enabling and disabling the component toggles that character's outline.

> Skipping the one-time step is the usual reason nothing appears: URP only runs a custom pass when
> a renderer feature is present, which is the same reason splat rendering itself needs
> `SplatRendererFeature`.

### Occlude By Scene

Off by default. With it off, silhouettes go into the mask in draw order with no depth arbitration
at all, so a character standing behind another - or behind a wall - still paints its outline across
it. Turn it on and the outline is hidden by whatever is in front of it, splats and geometry both:

- **Splats.** The mask gets a depth buffer and every splat object in view lays down depth, whether
  it is outlined or not and whether it is an animated character or a static capture, so each
  silhouette is clipped by whatever stands in front of it.
- **Geometry.** Each silhouette is also depth-tested against the camera depth texture, exactly the
  way the splat pass tests itself against it, so ordinary opaque meshes cut the outline off.

It is off by default because it is not free. The splat half costs a full-resolution depth buffer on
the mask plus one extra depth-only splat draw per splat object per camera; the geometry half is
much cheaper, one depth-texture sample per mask fragment, plus URP's depth copy if nothing else in
the frame already asked for it. Turn it on when your characters overlap each other or the set, and
leave it off when they don't.

> Because outlines are grown outward from the silhouette, an occluded character still shows a few
> pixels of outline spilling onto whatever is in front of it, along the edge where it disappears
> behind. That is the same spill you get against any other neighbour, and it is what keeps the two
> readable as separate.

> Unified sorting (**Unified Sorting** on `SplatRendererFeature`) and outlines are mutually
> exclusive, with or without this option: cross-object sorting moves the per-splat view data into
> one shared buffer that a per-object silhouette draw cannot index, so the renderer declines the
> draw and logs once. Nothing is outlined, and nothing occludes.

The feature's **Pass Event** controls where the outline lands in the frame. The default,
`AfterRenderingTransparents`, draws over Screen Space - Camera and World Space UI, because those
render in the transparent queue. Move it to `BeforeRenderingTransparents` if UI has to cover the
outline. Screen Space - Overlay UI is composited outside URP and always sits on top either way.

## Tint / flash

`SplatTintExample` pulses a character's tint from C# - a hit flash, say - with no shader editing.

Add **Splat Tint Example** next to a `SkinnedSplatRenderer`, then press **F** or call `Flash()`
from your own damage code.

## Color + opacity (translucent "spirit" look)

`SplatColorOpacityEffect` holds a sustained tint plus translucency, the basis for a glowing
translucent character.

- Add **Splat Color Opacity Effect** next to a splat character.
- Set **Color** to an HDR red (intensity > 1), **Color Strength** to 1, **Opacity** to about 0.4.
- For the glowing halo, add **Bloom** to your scene's Volume. The HDR-bright splats bloom, and the
  translucent silhouette already brightens at its edges on its own from premultiplied blending.

The component captures the character's authored `baseColor` and `opacityScale` on first use and
restores them when disabled, so toggling it off cleanly reverts the character. `baseColor` is a
plain multiply, so **Color Strength** layers this effect's color over the authored one rather than
replacing it.

## Going further

For custom per-splat logic - a fresnel rim, dissolve, gradients, sparkle - edit `ModifySplatColor` in
`GaussianSplatting/Shaders/GaussianSplatColorModifier.hlsl` in the package. The splat index, its
world position and its color are all in scope. That file is the documented hook; nothing else in
the color path needs touching.

## Cleaning up

`SplatEffectsLifecycle.ReleaseAll()` tears every effect in this sample down and frees the GPU
resources they hold. Call it alongside `SplatEngineLifecycle.ReleaseAll()` - releasing only the
engine leaves the effects' own materials and render targets behind.
