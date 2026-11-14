# Viggle Splat Engine

Renders Gaussian splats in Unity: animated characters and static captures.

Point it at a `.vsplat` character file, add an Animator Controller, and the splats deform on the GPU
with linear blend skinning - the same workflow as a skinned mesh, without the mesh. Drop an `.spz`
capture in the project and it imports as a ready-to-drag scene object. The split is the one
Unity already makes: `SplatRenderer` is to `SkinnedSplatRenderer` as `MeshRenderer` is to
`SkinnedMeshRenderer`.

## Requirements

- Unity 2022.3 or newer.
- **URP.** You must add **SplatRendererFeature** to the renderer asset your camera uses, or the
  splats never draw. URP is the only pipeline this package is tested against. Built-in and HDRP
  code paths exist here but are unverified, and nothing in `Runtime/` or `Editor/` registers
  `SplatCustomPassHDRP`, so treat both as unsupported until someone runs them.
- A `.vsplat` character file, or an `.spz` static capture. The Gaussian Splatting renderer
  itself is vendored into this package under `GaussianSplatting/`; you do not install it separately.

## Quick start

1. Create an empty GameObject and add a **Skinned Splat Renderer**.
2. Drag a `.vsplat` file onto **Splat File**.
3. Press **Play**. The character renders in its rest pose.

To animate it:

4. Assign any Humanoid **Animator Controller** to **Animator Controller**, on the same component.
5. Press **Play**.

There is nothing else to wire up. The compute shaders the renderer needs are resolved from the
package automatically.

For a static capture there is even less to do: drag the `.spz` into the project, then drag the
imported asset into the scene. It arrives as a GameObject carrying a **Splat Renderer**, already
pointed at its data.

## Static captures

An `.spz` Gaussian splat capture is imported like any other asset - no conversion window, no output
folder, and no settings, because nothing is converted. The importer stores the compressed file and
emits a prefab; the GPU-ready data is built when the capture loads, exactly as it is for a
character. The inspector reports what the file contains and what it will cost on the GPU.

A `.ply` capture is imported the same way, with one step in front of it: it is converted to the same
packed `.spz` representation at import, and it is those bytes that are stored. A `.ply` and an
`.spz` of the same capture therefore produce identical assets and are indistinguishable from that
point on.

That is a deliberate reversal. Converting at import turned a 27MB capture into a 432MB project
asset, most of it spherical-harmonic slots the file did not contain, and it did nothing at all for
captures fetched from a server at runtime - which is how they usually arrive. Building on load
costs about 16 bytes a splat and serves both.

To show a capture downloaded at runtime:

```csharp
GetComponent<SplatRenderer>().LoadAsync(spzBytes);   // or SplatCaptureAsset.FromBytes
```

> **What a `.ply` costs.** It is converted, not stored: a `.ply` Gaussian capture is uncompressed
> float32 at up to 248 bytes a splat, ten times the size of the same `.spz`, so keeping one in the
> project to decode on load would be worse in both directions at once. The conversion quantises
> **rotation and scale to 8 bits each**, against float32 in the source - that is the precision you
> give up. Positions are effectively unaffected: they become 24-bit fixed point, finer than the
> Norm11 quantisation the runtime packer applies afterwards either way. Colour and opacity were
> already 8-bit by the time anything looked at them.
>
> A `.ply` that is **not** a Gaussian capture - somebody's LiDAR scan or photogrammetry export -
> imports as an empty asset with a warning and no prefab, rather than failing. A ScriptedImporter
> claims an extension for the whole project, and only the file's header is read to find out that it
> is not ours.

## Character files

Characters ship as `.vsplat` - Gaussian splats plus per-splat bone weights and a rest skeleton in
one file. The `.bin` extension is still imported for files that already exist; new exports should
use `.vsplat`.

Two container formats are read, and the package tells them apart by the eight magic bytes the file
starts with, never by its extension - so either format works under either extension:

| Magic | What it is |
| --- | --- |
| `VIGGCHAR` | The current bundle: a JSON block table followed by quantised, component-major blocks. What the character pipeline produces today, and what the web engine and the Python tooling read. |
| `VFCHARB1` | The original bundle: interleaved per-splat records, fp32 or fp16. Still read, unchanged, and it always will be - existing projects have those bytes baked into imported assets. |

Nothing downstream of the reader knows which one arrived.

A file arrives in one of two rigs, and the package picks between them from the file's own bone
count. There is nothing to configure.

| Rig | Bones | Contents |
| --- | --- | --- |
| Body | 86 | Facial splat weights folded onto `head` / `neck_02` at export. |
| Body + facial | 441 | The same 86 body bones, in the same order, plus 355 `FACIAL_*` bones. |

Both behave identically under body animation, because a body clip never touches a facial bone. Use
the 441-bone rig when face and neck weighting must stay exactly as authored, or when facial
animation will be driven; use the 86-bone rig for crowds, where it costs about a fifth of the
per-character rig in bone Transforms and palette entries.

> **Trap:** loading a 441-bone file into a build that only knows the 86-bone rig is not a graceful
> degradation - every facial index collapses onto one shin bone. A character whose face is welded
> to its left leg is a version mismatch, not a corrupt file.

## Public API

The API is deliberately small. Everything below is in the `Viggle.SplatEngine` namespace.

### `SkinnedSplatRenderer` (component)

The one component a splat character needs. It loads the character file, renders it, builds the bone
hierarchy, drives it with an Animator and deforms the splats on the GPU each frame - the splat
analogue of `SkinnedMeshRenderer`.

| Member | Purpose |
| --- | --- |
| `splatFile` | The character asset to render. |
| `baseColor` | HDR brightness / color multiplier for this character. A plain multiply, so `0.7` is 70% as bright. Live-editable in Play mode. |
| `animatorController` | The controller to play. Leave empty to hold the rest pose. How its clips bind to the bones is the file's own Rig Mode, set on the `.vsplat` importer. |
| `poseEveryNFrames` | Re-pose only every Nth frame (1-4). Skipped frames hold the last pose and skip both the skinning dispatch and the re-sort. Phase is staggered per character, so a crowd's cost spreads instead of spiking. |
| `sortEveryNFrames` | Re-sort by depth only every Nth pose frame (1-8). Camera or character motion always re-sorts promptly. |
| `updateWhenOffscreen` | Keep animating while off camera. Off by default. Named after `SkinnedMeshRenderer.updateWhenOffscreen`. |
| `AttachToBone(name, obj, ...)` | Parent a GameObject to a bone - a weapon, a VFX emitter. |
| `GetBoneSocket(name)` | The Transform for a named bone. |
| `ReleaseResources()` | Frees this character's GPU and native buffers and destroys its rig. |

`opacityScale` fades the whole character; `splatScale` and `renderOrder` are settable from code and
are deliberately not drawn in the inspector.

### `SplatRenderer` (component)

The static sibling: it renders one imported capture and nothing else. Same visual controls, no rig
and no animation.

| Member | Purpose |
| --- | --- |
| `splatFile` | The `.spz` capture to render. |
| `baseColor` | HDR brightness / color multiplier. As above. |
| `opacityScale` | Fades the whole capture. |
| `shOrder` | How much of the capture's spherical harmonics to evaluate, 0-3. 3 is as captured (view-dependent); lower is cheaper and flatter. Capped by what the file carries - a degree-0 capture stores no SH at all. Characters have no SH either, which is why only this component has it. |
| `shOnly` | Diagnostic: show only the SH contribution, in grey. |
| `LoadAsync(byte[])` | Show a capture downloaded at runtime. |
| `LoadAsync(SplatCaptureAsset)` | Show a different imported capture. |
| `IsLoaded` / `Loaded` | Ready state, and the event that fires when it becomes ready. |
| `TryGetWorldBounds(out Bounds)` | World-space bounds of the capture. |
| `ApplyVisuals()` | Pushes the fields above onto the renderer after changing them from code. |

### `SplatCaptureAsset` (asset)

One imported `.spz`, stored verbatim. `SplatCaptureAsset.FromBytes(byte[])` wraps a capture
downloaded at runtime as the same thing.

### `SplatQuality` (static)

Global quality knobs. Both apply live to every character, so a game's own quality menu can drive
them per platform or per device tier.

```csharp
SplatQuality.RenderScale = 0.7f;     // offscreen render target scale, 0.25 - 1
SplatQuality.ScreenSizeCull = 1.5f;  // drop splats smaller than this radius in pixels, 0 = off
```

Splat rendering is fill-rate bound, which makes `RenderScale` the most effective single knob on a
GPU-bound device. The composite upsamples bilinearly, so the cost is softness rather than aliasing.
`ScreenSizeCull` is the targeted one: it removes splats too small to contribute a pixel, which is
most of the cost of a distant crowd and almost none of its appearance.

### `SplatSettings` (project settings)

**Edit > Project Settings > Viggle Splat Engine.** No setup is required - the defaults apply with
no asset, and the settings asset is created only when you change something on that page.

| Setting | Purpose |
| --- | --- |
| `logLevel` | How much the package prints. `Warning` (the default) means problems speak and instrumentation stays quiet. `Info` adds load timings; `Verbose` adds per-frame chatter. `Off` silences failures too. |
| `renderInEditor` | Render characters in the Scene view while editing, the way meshes do, so you can place them without entering Play mode. Turn it off for heavy edit-time scenes. |

### `SplatLog.Level`

The same log threshold, settable from code at any time:

```csharp
SplatLog.Level = SplatLogLevel.Info;   // Off, Error, Warning, Info, Verbose
```

### `SplatEngineLifecycle.ReleaseAll()`

```csharp
SplatEngineLifecycle.ReleaseAll();
```

Destroys the package's live components, frees their GPU and native buffers, and resets every
package static to its default. Safe to call more than once; the engine works normally afterwards,
and the next character you spawn re-creates what it needs.

Call it when the engine's lifetime ends rather than the game's - unloading the scene that held the
characters, returning to a main menu, closing a mini-game. Unity does not need it on application
quit.

## Samples

Import via **Package Manager > Viggle Splat Engine > Samples > Examples**, then open a scene under
`Examples/`.

- **CharacterRender** - animated characters with example visual effects: outline, tint and
  color/opacity. See `EFFECTS.md`.

## License

**MIT** - see `LICENSE.md`. Copyright 2026 Viggle / WarpEngine.

In plain terms: use it, modify it, ship it, redistribute it, commercially or not. What MIT asks in
return is that you keep the copyright and permission notices in the copies you distribute.

That applies to the vendored code too. Part of this package derives from the Gaussian Splatting
package by Aras Pranckevičius, which is also MIT, and its notice is preserved in
`GaussianSplatting/LICENSE.md`. Keep both license files in your source tree.
`Third Party Notices.md` records which files derive from that package and how they were modified.
