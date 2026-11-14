# Public API surface

`com.viggle.splat-engine` renders Gaussian splats in Unity: animated `.vsplat` characters and static
`.spz` captures. This file is the complete reference for what a host project can name,
established by reading declarations rather than by reading older docs. `README.md` is the tutorial;
this is the inventory.

## The organising idea

The package is shaped after the workflow Unity already has for meshes, one step down:

| Meshes | Splats |
| --- | --- |
| `MeshRenderer` + `Mesh` | `SplatRenderer` + `SplatCaptureAsset` |
| `SkinnedMeshRenderer` + rig + `Animator` | `SkinnedSplatRenderer` + rig + `Animator` |
| Model importer, FBX in, prefab out | `SplatCharacterImporter` / `SplatCaptureImporter`, prefab out |
| Rig tab: Humanoid / Generic / None | Rig Mode on the `.vsplat` importer: `SplatRigMode` |
| Avatar baked as a sub-asset of the FBX | Avatar baked as a sub-asset of the `.vsplat` |

If you know how to put an animated FBX on screen, you already know this package. Drag a file into
the project, drag the imported prefab into the scene, assign an Animator Controller, press Play.

**What you actually touch: two components.** `SkinnedSplatRenderer` for characters and
`SplatRenderer` for captures. Everything else in the surface is either the data those two consume,
a static settings class, or an extension point almost nobody needs.

## I want X, I use Y

| I want to | I use |
| --- | --- |
| Put an animated character in a scene | Drag the `.vsplat` in and drag the prefab it produces into the scene |
| Animate that character | `SkinnedSplatRenderer.animatorController` |
| Choose how its clips bind to its bones | Rig Mode on the `.vsplat` importer (`SplatRigMode`) |
| Put a static capture in a scene | Drag the `.spz` in and drag the prefab it produces into the scene |
| Show a character downloaded at runtime | `SkinnedSplatRenderer.LoadAsync(byte[])`, or `SplatCharacterAsset.FromBytes` |
| Show a capture downloaded at runtime | `SplatRenderer.LoadAsync(byte[])`, or `SplatCaptureAsset.FromBytes` |
| Swap the capture on a live component | `SplatRenderer.LoadAsync(SplatCaptureAsset)` |
| Know when either is ready to touch | `IsLoaded`, or subscribe to `Loaded` |
| Fade a character or capture out | `opacityScale`, then `ApplyVisuals()` |
| Correct a character that reads too bright | `baseColor`, then `ApplyVisuals()` |
| Frame a camera on it, or ground it | `TryGetWorldBounds(out Bounds)` |
| Attach a prop to a bone | `BoneNames` / `HasBone` / `AttachToBone` / `GetBoneSocket` |
| Trade quality for frame rate per device tier | `SplatQuality` |
| Turn the package's logging up or down | `SplatLog.Level`, or the project setting |
| See splats in the Scene view while editing | `SplatSettings.renderInEditor` (Project Settings) |
| Draw a character's shape from my own pass | `SplatCustomPass` |
| Release everything and unload the assembly | `SplatEngineLifecycle.ReleaseAll()` |
| Make splats draw at all under URP | Add **SplatRendererFeature** to your URP renderer asset (inspector only; see the review) |

## Assemblies, and what a host sees by default

| Assembly | Auto-referenced | Public types | What a host sees with no setup |
| --- | --- | --- | --- |
| `Viggle.SplatEngine` | yes | 12 | The whole API |
| `Viggle.SplatEngine.Editor` (Editor only) | yes | 2 | The two importers (the `.ply` reader and `.spz` encoder behind one of them are internal) |
| `GaussianSplatting` (vendored) | yes | 1 | `SplatAsset` |
| `GaussianSplattingEditor` (vendored, Editor only) | no | 0 | Nothing |
| `Viggle.SplatEngine.Jobs` | no | 0 | Nothing |

Auto-referenced means user code in `Assembly-CSharp` (and `Assembly-CSharp-Editor`) can name those
types with no asmdef reference added. Verified by compiling a probe into `Assembly-CSharp`: every
member listed below resolves, and `GaussianCutout`, `SkinnedSplatRenderer.Bin`,
`SplatRenderer.m_Asset`, `SplatLog.Info` and `SplatSettings.Instance` all fail as `CS0122` /
`CS1061` / `CS0117`.

`InternalsVisibleTo` grants, in full - every one of them points inside the package, so nothing
outside it can see in:

| From | To | Why |
| --- | --- | --- |
| `Viggle.SplatEngine` | `Viggle.SplatEngine.Editor` | The importers need the parser, the skeleton tables, the rig builder and the `.spz` reader the `.ply` encoder writes for |
| `Viggle.SplatEngine.Jobs` | `Viggle.SplatEngine` | The renderer schedules the skinning and capture-packing jobs |
| `GaussianSplatting` | `Viggle.SplatEngine` | Both components drive the internal renderer |
| `GaussianSplatting` | `GaussianSplattingEditor` | The vendored inspectors edit now-internal types |
| `GaussianSplatting` | `Viggle.SplatEngine.Editor` | The capture importer reports what a capture will cost on the GPU, and the `.ply` encoder shares the sigmoid and quaternion helpers with the decoder |
| `GaussianSplatting` | `Viggle.SplatEngine.Jobs` | The packing jobs write the `SplatAsset` chunk record and format layouts |
| `GaussianSplattingEditor` | `Viggle.SplatEngine.Editor` | Historical: nothing reaches through it since the conversion code was deleted |

No grant reaches a Samples assembly, deliberately: sample code is user-facing example code, so
anything a sample needs is something a user needs and belongs in the public API. Both sample
assemblies reference `Viggle.SplatEngine` and nothing else in the package, which is the test that
the surface is sufficient.

---

# Assembly `Viggle.SplatEngine`

Namespace `Viggle.SplatEngine`. Eleven public types.

## class `SkinnedSplatRenderer` : MonoBehaviour

The splat analogue of `SkinnedMeshRenderer`. Holds the character file, owns the bone hierarchy, and
draws the GPU-skinned result. `[ExecuteAlways]`, `[DisallowMultipleComponent]`, menu **Viggle Splat
Engine / Skinned Splat Renderer**. Declared across `SkinnedSplatRenderer.cs` (load and render) and
`SkinnedSplatRenderer.Skinning.cs` (rig, animation, per-frame skin).

Serialized fields, in inspector order:

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public SplatCharacterAsset splatFile` | shown | The character file to render. `[FormerlySerializedAs("binFile")]`. |
| `public Color baseColor` | shown | HDR colour multiplier for this character's splats; white leaves them unchanged. |
| `public float opacityScale` | shown, `[Range(0,1)]` | Opacity multiplier for the whole character. Fade in and out. |
| `public float splatScale` **Code-only**, not drawn in the inspector. | **`[HideInInspector]`** | Size multiplier on every splat. Above 1 closes gaps on a sparse character at the cost of sharpness. Code-only: a per-asset correction, not a per-instance tuning knob. |
| `public int renderOrder` **Code-only**, not drawn in the inspector. | **`[HideInInspector]`** | Draw order against other splat objects; higher draws on top, equal orders sort by distance. Code-only. |
| `public RuntimeAnimatorController animatorController` | shown | Controller driving the character. Empty leaves it at rest. |
| `public int poseEveryNFrames` **Code-only**, not drawn in the inspector. | shown, `[Range(1,4)]` | Re-pose only every Nth frame. Skips both the skinning dispatch and the renderer's re-sort. Per-character phase is staggered. |
| `public int sortEveryNFrames` **Code-only**, not drawn in the inspector. | shown, `[Range(1,8)]` | Re-sort by depth only every Nth pose frame. Camera or character motion always re-sorts promptly. |
| `public bool updateWhenOffscreen` **Code-only**, not drawn in the inspector. | shown | Keep the Animator and the skin running while frustum-culled by every camera. Named after and means the same as `SkinnedMeshRenderer.updateWhenOffscreen`. `[FormerlySerializedAs("animateWhenOffscreen")]`. |

Lifecycle and state:

| Member | Purpose |
| --- | --- |
| `public bool IsLoaded { get; }` | True once the character is parsed and its GPU resources exist. Everything that reaches into the rig needs this first. |
| `public event Action<SkinnedSplatRenderer> Loaded` | Raised on the main thread once loaded. A handler added after the load finished is invoked immediately, so subscribing can never race the load. |
| `public void LoadAsync(byte[] vsplat, SplatRigMode rigMode = SplatRigMode.Humanoid)` | Load from raw `.vsplat` bytes - downloaded or generated content. |
| `public void LoadAsync(SplatCharacterAsset character)` | Load an imported character, replacing whatever was showing. |
| `public void ReleaseResources()` | Unload the character, destroy its rig, release every GPU and native resource it owns. |
| `public static void ResetStatics()` | Clear process-wide shader globals and cached skinning state. |

Both `LoadAsync` overloads complete before they return today. They are named for what they will be:
the load is a single main-thread burst of several hundred milliseconds on a large character, and
splitting it across frames is planned. Write calling code as if it were already asynchronous and
wait on `Loaded`.

Visuals and queries:

| Member | Purpose |
| --- | --- |
| `public void ApplyVisuals()` | Push every visual field onto the renderer. Call after changing any of them from code; inspector edits already trigger it. |
| `public bool TryGetWorldBounds(out Bounds bounds)` | World-space rest-pose bounds. False until loaded. Rest pose, not the animated silhouette: reading back skinned positions per frame is not worth what it buys. |

Bones and attachment:

| Member | Purpose |
| --- | --- |
| `public IReadOnlyList<string> BoneNames { get; }` | Every bone name on this character's rig, in skeleton order. Empty until loaded. The only way to discover a valid key for the three members below, since every skeleton type is internal. |
| `public bool HasBone(string boneName)` | True when this rig has that bone. Cheaper than provoking a warning from `GetBoneSocket`. |
| `public bool AttachToBone(string boneName, Transform obj, Vector3 localOffset = default, Quaternion localRotation = default)` | Attach `obj` so it keeps its authored size and orientation and then rigidly follows the bone. False if the rig is not built or the name is unknown. |
| `public Transform GetBoneSocket(string boneName)` | The socket Transform for a bone, created on first request. Null on the same two failures. |

Prefer `AttachToBone` over parenting to the bone Transform directly. Bone frames follow the source
rig's conventions, and in Generic mode the armature node carries the FBX unit conversion as a scale
of 100, paired with 1/100 bone translations. Splats never notice, because the skinning palette
discards scale; anything parented to a bone inherits both. The socket cancels them.

## class `SplatRenderer` : MonoBehaviour

The splat analogue of `MeshRenderer`, and the sibling of `SkinnedSplatRenderer`: the same visual
controls, no rig and no animation. `[ExecuteAlways]`, `[DisallowMultipleComponent]`, menu **Viggle
Splat Engine / Splat Renderer**.

The GPU-ready data is built when the component loads, from the compressed `.spz` bytes, exactly as
a character's is. Nothing is converted at import.

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public SplatCaptureAsset splatFile` | shown | The capture to draw. Swapping it from code or the inspector reloads on the next `Update`. |
| `public Color baseColor` | shown | HDR colour multiplier, as on the character component. |
| `public float opacityScale` | shown, `[Range(0,1)]` | Opacity multiplier for the whole capture. |
| `public float splatScale` **Code-only**, not drawn in the inspector. | **`[HideInInspector]`** | Per-splat size multiplier. Code-only, same reasoning as on the character component. |
| `public int renderOrder` **Code-only**, not drawn in the inspector. | **`[HideInInspector]`** | Draw order against other splat objects. Code-only. |
| `public int shOrder` | shown, `[Range(0,3)]` | How much of the capture's spherical harmonics to evaluate. 3 is as captured; 0 is unlit base colour. Capped by what the file carries. |
| `public bool shOnly` | shown | Diagnostic: show only the SH contribution, in grey. |

`shOrder` and `shOnly` have no equivalent on `SkinnedSplatRenderer` because a `.vsplat` carries no
spherical harmonics at all. Plenty of captures carry none either - a degree-0 `.spz`, which is what
most object scans are, stores no SH and `shOrder` does nothing on one whatever it is set to. That is
not a limitation, it is the point: SH costs 32 bytes a splat and a degree-0 capture pays none of it.

| Member | Purpose |
| --- | --- |
| `public bool IsLoaded { get; }` | True once the capture's GPU resources exist and it is drawing. |
| `public event Action<SplatRenderer> Loaded` | Raised once it is on the GPU. Same immediate-invoke rule as on the character component. |
| `public void LoadAsync(byte[] spz)` | Show a capture downloaded or generated at runtime. Named async for the same reason as the character overload. |
| `public void LoadAsync(SplatCaptureAsset capture)` | Show a different imported capture. |
| `public void ApplyVisuals()` | Push every visual field onto the renderer. |
| `public bool TryGetWorldBounds(out Bounds bounds)` | World-space bounds. False until loaded. |

## class `SplatCharacterAsset` : ScriptableObject

The imported `.vsplat` (or legacy `.bin`), as a raw byte blob plus how it should be rigged. A
`byte[]` ScriptableObject rather than a `TextAsset`, because the binary `TextAsset` constructors are
Unity 6+ and a string-constructed one corrupts a binary blob through UTF-8 re-encoding.

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public SplatRigMode rigMode` | `[HideInInspector]` | How this character's clips bind to its bones. Set by the importer's Rig Mode. |
| `public Avatar avatar` | `[HideInInspector]` | The Humanoid Avatar baked at import, stored as a sub-asset so every instance shares one. Null in Generic and None mode, and on an asset built from raw bytes. |
| `public static SplatCharacterAsset FromBytes(byte[] bytes, SplatRigMode rigMode = SplatRigMode.Humanoid)` | - | Wrap raw `.vsplat` bytes as a character asset. Checks for either character magic - `VIGGCHAR` or `VFCHARB1` - and returns null with a plain message rather than throwing from inside the parser. Carries no baked Avatar, so a Humanoid character built this way pays to construct one per instance. |

## class `SplatCaptureAsset` : ScriptableObject

The imported `.spz`, as a raw byte blob - the compressed source, stored verbatim, not the packed GPU
layout. The capture-side twin of `SplatCharacterAsset`, and a `byte[]` ScriptableObject for the same
reason.

Storing the source rather than the conversion is the whole shape of the capture path. Converting at
import turned a 27MB, 1.92M-splat, degree-0 capture into a 432MB project asset, 370MB of which was
zeroes standing in for spherical harmonics the file never contained - and it did nothing at all for
captures fetched from a server at runtime, which is how they usually arrive. Building on load costs
about 16 bytes a splat and serves both.

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public static SplatCaptureAsset FromBytes(byte[] bytes)` | - | Wrap raw `.spz` bytes as a capture asset. Checks the header and returns null with a plain message rather than throwing from inside the decompressor. |

## enum `SplatRigMode`

How a character's animation clips bind to its bones. Chosen per file, on the importer, the same
place a mesh makes the choice. The numbering is load-bearing: `Humanoid` must stay 0, so an asset
carrying no explicit value lands on the package's historical default rather than on no rig at all.

| Constant | Meaning |
| --- | --- |
| `Humanoid = 0` | Bake a Humanoid Avatar and retarget clips through Unity's muscle space. Any Humanoid clip works, at the cost of retargeting approximation. |
| `Generic = 1` | No avatar; clips drive the bone Transforms directly by path. Exact playback, but the clips must be authored on this exact skeleton. |
| `None = 2` | No avatar and no Animator. The character is still skinned, so it renders at rest. |

## static class `SplatCustomPass`

Draw a splat object with your own material, from your own render pass - the splat equivalent of
issuing a draw for a mesh with a material of your choosing. The building block for anything needing
the object's shape on screen: outlines, glows, stencil masks, custom depth, selection highlights.
It is a separate static class rather than members on the component or on `SplatQuality` because it
is a low-level behavioural contract that almost nobody needs - wrong altitude to share a type with
either.

It is public only because everything it touches is not: the per-splat view data, the sorted order
buffer and the projection state all belong to a renderer a host cannot reach. Without it there is
no way to write a custom splat pass at all.

| Member | Purpose |
| --- | --- |
| `public static string VertexTextureFetchKeyword { get; }` | The shader keyword that switches a splat material's vertex stage to read view data from a texture instead of from `StructuredBuffer`s. Declare the matching `#pragma multi_compile`. |
| `public static bool RequiresVertexTextureFetch { get; }` | True on devices whose vertex stage cannot bind those buffers - several mobile GLES drivers report a limit of zero. Probed once from `SystemInfo`; constant for the process. |
| `public static bool Draw(SkinnedSplatRenderer character, CommandBuffer cmd, Camera camera, Material material, MaterialPropertyBlock properties)` | Record a draw of an animated character's splats with your material into the bound target. False when nothing was drawn, i.e. it is not ready. |
| `public static bool Draw(SplatRenderer capture, CommandBuffer cmd, Camera camera, Material material, MaterialPropertyBlock properties)` | The same for a static capture. Both components reach the screen the same way, so a custom pass has no reason to care which it was handed. |

`properties` is required, not optional: the per-splat buffers are bound onto it. Use one material
instance per object - on OpenGL ES, consecutive draws through a shared material alias each
other's vertex-stage buffers. Schedule your pass **after** the splats have rendered; it reuses the
view data the main pass computed this frame, and drawing earlier reads last frame's projection.
Start from the `GaussianSplatOutlineMask` shader in the Effects sample rather than writing the
vertex stage from scratch.

## static class `SplatQuality`

Runtime quality knobs. Plain statics rather than project settings, because these are decisions a
game makes from its own quality menu per platform or device tier - a settings asset could hold only
one value and would have to be read at exactly the right moment to take effect.

| Member | Purpose |
| --- | --- |
| `public static float RenderScale { get; set; }` | Resolution scale of the offscreen target the splats render into, clamped to 0.25-1. Splat rendering is fill-rate bound, so this is the most effective knob on a GPU-bound device. The composite upsamples bilinearly, so the cost is softness, not aliasing. |
| `public static float ScreenSizeCull { get; set; }` | Drop splats whose projected radius falls below this many pixels, clamped at 0 and above. 0 is off. |
| `public static bool DepthOcclusion { get; set; }` | Test each splat against the depth buffer so opaque geometry hides what is behind it. On by default. Turning it off is the single largest mobile saving here, at the cost of splats drawing through walls. |
| `public static void ResetStatics()` | Restore all three to their defaults (1, 0, true). |

## class `SplatSettings` : ScriptableObject

Project-wide defaults, edited under **Edit > Project Settings > Viggle Splat Engine**. No setup is
required: with no settings asset the code defaults apply, and the asset is only created once
something on the page changes. Applied automatically before the first scene loads, and again lazily
before any splat resource is built - the lazy path exists because Unity collects
`[RuntimeInitializeOnLoadMethod]` by scanning assemblies at build time, so an assembly loaded
dynamically after `BeforeSceneLoad` never registers the hook.

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public SplatLogLevel logLevel` | shown on the settings page | How much the package prints. Pushed into `SplatLog.Level` on apply. |
| `public bool renderInEditor` | shown on the settings page | Render splats in the Scene view while editing, so they can be placed without entering Play mode. Applies to characters and captures alike. |
| `public static void ResetStatics()` | - | Clear cached settings state. |

`Instance`, `Apply`, `EnsureApplied`, `Invalidate` and `AppliedBy` are all internal: a host
configures the package through the settings page or through `SplatLog.Level` and `SplatQuality`, not
by driving the settings object.

## static class `SplatLog`

| Member | Purpose |
| --- | --- |
| `public static SplatLogLevel Level` | Severity threshold for everything this package prints. Defaults to `Warning`, so a host that never touches it hears about failures and never sees instrumentation. Settable at any time. |
| `public static void ResetStatics()` | Restore the default level. |

The call-site gates (`Error`, `Warning`, `Info`, `Verbose`) are internal on purpose. Logging is
guarded at the call site rather than inside a helper, because an interpolated string is built before
the call, so a discarding helper would still pay for the formatting on every character, every frame,
in a build with logging off. A host configures `Level` and does not need the gates.

## enum `SplatLogLevel`

| Constant | Meaning |
| --- | --- |
| `Off = 0` | Say nothing at all, including failures. |
| `Error = 1` | Only conditions that stop something working. |
| `Warning = 2` | Errors plus conditions that will probably bite later. The default. |
| `Info = 3` | Adds one-off instrumentation: load timings, buffer audits, chosen paths. |
| `Verbose = 4` | Adds per-frame and per-character chatter. Noisy by design. |

## static class `SplatEngineLifecycle`

| Member | Purpose |
| --- | --- |
| `public static void ReleaseAll()` | Release everything the package owns: stop pipeline callbacks, destroy its live components, free their GPU and native buffers, reset every package static. Safe to call more than once. After it returns, the assembly holding this code can be unloaded. |

Call it whenever the engine's lifetime ends: unloading the scene the characters were in, returning
to a menu, shutting a mini-game down, swapping a hot-updated assembly.

---

# Assembly `GaussianSplatting` (vendored)

Two public types - `SplatAsset`, and `SplatRendererFeature` when URP is installed. Everything else
in this assembly is internal.

## class `SplatAsset` : ScriptableObject - namespace `Viggle.SplatEngine`

A static Gaussian splat capture, packed into the layout the renderer reads. The splat analogue of a
`Mesh`: the data, not the thing that draws it.

**Nothing public hands one out any more.** It used to be what a `SplatRenderer` was pointed at, and
what the capture importer produced; both now go through `SplatCaptureAsset`, and every `SplatAsset`
in existence is built at load time and owned by the component that built it. The type is still
public, and that is now a question rather than an answer - see the surface review.

| Member | Purpose |
| --- | --- |
| `public int splatCount { get; }` | How many splats this capture contains. |
| `public Vector3 boundsMin { get; }` | Minimum corner of the capture's bounds, in the asset's own space. |
| `public Vector3 boundsMax { get; }` | Maximum corner, likewise. |

### Why a public type lives in the vendored assembly

This looks wrong until you try to fix it. Every other public type in the package is in
`Viggle.SplatEngine`, assembly and namespace both. `SplatAsset` is in the `Viggle.SplatEngine`
**namespace** but compiled into the `GaussianSplatting` **assembly**, and it cannot be moved:
`Viggle.SplatEngine.Jobs` packs a decoded capture straight into that layout, and `Viggle.SplatEngine`
references `Jobs` - so moving the type up would make `GaussianSplatting` reference
`Viggle.SplatEngine` transitively, which already references `GaussianSplatting`. Unity rejects the
cycle outright. Namespaces are free to span assemblies; assembly references are not free to loop.

So the type stays where it is compiled and is made public in place, in the namespace a host expects
- nobody should have to type `GaussianSplatting` to name the data they assign to a component. Two
consequences follow:

- `GaussianSplatting.asmdef` must stay `autoReferenced: true`, or user code could not name the type
  at all. Exactly one type in the assembly is public, so the auto-reference exposes nothing else.
- The public surface is three members. Everything else on the type - the packing formats
  (`VectorFormat`, `ColorFormat`, `SHFormat`), the SH table structs, `ChunkInfo`, `CameraInfo`, the
  raw blob accessors - is internal, because it is the GPU layout the renderer reads and changing it
  must not be a breaking API change.

`[MovedFrom(true, "GaussianSplatting.Runtime", null, "GaussianSplatAsset")]` covers assets that
resolve the type by name; assets created before the rename resolve by `.cs.meta` GUID, which is
unchanged.

---

# Assembly `Viggle.SplatEngine.Editor`

Namespace `Viggle.SplatEngine.Editor`. Editor platform only. Two public types, both of them
importers - there is no editor scripting API to call.

## class `SplatCharacterImporter` : ScriptedImporter

Imports `.vsplat` and legacy `.bin`. Emits a **prefab as the main object**, exactly as Unity's model
importer does for an FBX: a GameObject carrying `SkinnedSplatRenderer`, the bone hierarchy, and an
`Animator` already bound to the baked avatar, with the `SplatCharacterAsset` and the `Avatar` as
sub-assets and a rendered thumbnail attached. Dragging a `.vsplat` into a scene therefore gives a
rigged, animatable character.

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public SplatRigMode rigMode` | shown as **Rig Mode** | How this character's clips bind to its bones. The same choice Unity puts on a model importer's Rig tab, in the same place: on the file, not on a scene component. In `Humanoid` mode the importer bakes one Avatar per file, so a crowd of twenty shares one instead of building twenty. |
| `public override void OnImportAsset(AssetImportContext ctx)` | - | The `ScriptedImporter` entry point. Not something to call. |

Two extensions on purpose, and they are not treated the same. A `ScriptedImporter` claims an
extension globally, so registering `bin` means every unrelated `.bin` in the host project is handed
to this importer. A `.bin` whose magic does not match is therefore a warning and still imports as an
inert asset - it is probably somebody else's file. A `.vsplat` with the wrong magic is an error,
because nothing else in the world writes that extension.

The extension decides how loudly to complain about bytes the importer does not recognise, and
nothing else. Which container a file is - `VIGGCHAR` or `VFCHARB1` - comes from its magic, through
the same check the runtime reader uses, so both formats import under both extensions.

The Animator sits on the rig root rather than the character root, which looks like the wrong place
until you know why: the baked Avatar records its `HumanDescription` root as the node's name, and the
rig builder guarantees that name is `SplatRig`. On the character root, a user renaming their
GameObject would silently break avatar binding.

## class `SplatCaptureImporter` : ScriptedImporter

Imports `.spz` and `.ply` static captures. It emits a **prefab as the main object** carrying a
`SplatRenderer`, with the `SplatCaptureAsset` as a sub-asset.

It has **no settings**, and that is the change worth reading twice. It stores the compressed file
and nothing else; the GPU layout is built when the capture loads. Converting here instead is what
produced a 432MB asset from a 27MB file, and it never covered captures fetched at runtime. What the
custom inspector draws now is a description of the file and what it will cost in GPU memory - the
budget that actually binds.

| Member | Inspector | Purpose |
| --- | --- | --- |
| `public override void OnImportAsset(AssetImportContext ctx)` | - | The `ScriptedImporter` entry point. Not something to call. |

**A `.ply` is converted to `.spz` bytes at import**, and those bytes are what the
`SplatCaptureAsset` stores. It cannot be kept verbatim: a `.ply` Gaussian capture is uncompressed
float32 at up to 248 bytes a splat, ten times the size of the same `.spz`, so storing one to decode
on load would be worse in both directions at once. Converting it into the packed **GPU** layout
instead is the import-time pipeline that was deliberately removed, and would mean a second asset
type and a second runtime path. Converting to `.spz` means a `.ply` and an `.spz` of the same
capture produce byte-identical asset contents, and there is exactly one runtime path.

What that costs is **rotation and scale, at 8 bits each**, against float32 in the source. Positions
are effectively unaffected - they become 24-bit fixed point, at a fractional-bit count chosen from
the capture's own extent, which is finer than the Norm11 quantisation the runtime packer applies
afterwards. Colour and opacity were already 8-bit by the time anything looked at them. The SH degree
written is the degree the source carries, so a degree-0 capture produces no SH section at all.

The two extensions are **not** handled the same way, for the reason `SplatCharacterImporter` gives
about `.bin`: a ScriptedImporter claims an extension globally, and most `.ply` files in the world
are somebody's LiDAR scan. So a `.ply` that is not a capture is a **warning** and an inert asset
with no prefab, decided from the header alone - a foreign `.ply` can be hundreds of megabytes and
none of the body is read. `.spz` needs no such hedging: the format exists only to carry Gaussian
splats, so a bad one is an error. Either way no prefab is emitted for a file that did not read.

---

# Assemblies with no public surface

## `Viggle.SplatEngine.Jobs`

Zero public types. `autoReferenced: false`, and internals visible only to `Viggle.SplatEngine`.

The assembly exists for two mechanical reasons: scheduling a job permanently registers its `Execute`
with the engine, which would pin the runtime assembly and block hot assembly swaps, and Burst only
compiles jobs ahead of time in assemblies it can reach statically at build time - a `[BurstCompile]`
job in a dynamically loaded assembly falls back to interpretation. It is a build artefact of those
constraints, not an API.

It holds the skinning jobs and the `.spz` decode-and-pack jobs. `BonePose` in particular is an
80-byte GPU layout contract with `SplatSkinning.compute` - a host that could see it could not
usefully do anything except break it.

## `GaussianSplattingEditor`

Zero public types. `autoReferenced: false`. The vendored editor half: the custom inspectors, the
scene move/rotate/scale tools and the `.ply` export. The capture readers and the asset builder used
to live here too; they are gone. Decoding happens at load time in `Viggle.SplatEngine.Jobs`, the
`.ply` reader that feeds the import-time `.spz` conversion lives in `Viggle.SplatEngine.Editor` with
the importer it serves, and nothing reaches into this assembly from outside it any more.

---

# What is deliberately not public, and why

## Everything on the renderer except the curated surface

The vendored renderer is no longer a separate hidden component. It IS `SplatRenderer`, the public
component a static capture carries and the base class `SkinnedSplatRenderer` derives from - so
"internal" now means member-by-member rather than type-by-type. `SplatRenderer` exposes 14 members
(`splatFile`, `baseColor`, `opacityScale`, `splatScale`, `renderOrder`, `shOrder`, `shOnly`,
`IsLoaded`, `Loaded`, two `LoadAsync` overloads, `TryGetWorldBounds`, `ApplyVisuals`,
`ReleaseResources`); the other ~150 the vendored type had - the GPU buffers, the sorter, the edit /
selection / export API, the LOD hooks, the render-path statics - are `internal`, as are its Unity
message methods (`internal virtual`, so the character can chain them without Unity's
non-virtual-message trap silently dropping the base's).

`GaussianCutout`, `SplatCustomPassHDRP`, `GpuSorting`, `GpuBitonicSort`, `GpuCountingSort`,
`GaussianUtils` remain internal types, along with every type nested inside them (`RenderMode`,
`Props`, `GpuSorting.Args`, `GpuSorting.SupportResources`, `GpuCountingSort.Resources`,
`GaussianCutout.Type`, `GaussianCutout.ShaderData`). A `public` nested type inside an `internal`
type is not part of any surface.

Upstream (`org.nesnausk.gaussian-splatting`) is no longer developed and will never be synced into
here again, so keeping the vendor boundary visible bought nothing except a second family of types a
user had to learn. A host used to see 55 public types across the two assemblies; it now sees 14, and
never has to know which assembly a type came from.

The consequence had to be paid for rather than waved away: a knob that is `internal` is
unreachable, with no workaround available at all. Hence `opacityScale`, `splatScale`, `renderOrder`,
`shOrder`, `shOnly` as fields, and hence `SplatCustomPass` - the outline sample does not merely read
the renderer, it issues a draw through it, so without a public equivalent that sample was
unwritable. The samples are the test: they compile against `Viggle.SplatEngine` alone.

There is no hidden second component any more. A static capture carries one `SplatRenderer`; a
character carries one `SkinnedSplatRenderer`, which IS a `SplatRenderer`. `[DisallowMultipleComponent]`
on the base makes the two mutually exclusive on one GameObject, which is what the old
`HideFlags.HideInInspector` on the forwarded-to component was papering over.

## The skeleton tables and the rig machinery

`SplatSkeleton`, `SplatSkeletonDef`, `SplatSkeletonData`, `SplatSkeletonData441`,
`SplatSkeletonBindPose`, `SplatRigBuilder` (and its public-but-nested `Rig` struct),
`SplatHumanoidAvatar`, `SplatConvention` - internal. Which rig a character uses is decided from its
own bone count; there is nothing to configure, and the bone tables are data the file format implies
rather than something a host chooses. `BoneNames` and `HasBone` exist precisely so the one
string-keyed public API is discoverable without exposing any of this.

## The character file format

`SplatCharacterBin`, `SplatCharacterBinData`, `SplatChunkRange`, `SplatSpzFile`,
`SplatRenderAssetBuilder` - internal. The public route to raw bytes is `SplatCharacterAsset.FromBytes`
/ `SkinnedSplatRenderer.LoadAsync(byte[])` and `SplatCaptureAsset.FromBytes` /
`SplatRenderer.LoadAsync(byte[])`, which validate the header and report plainly instead of failing
deep inside the parser on a background of half-built GPU state.

## The capture packing formats

`SplatAsset.VectorFormat` / `ColorFormat` / `SHFormat`, the SH table structs, the chunk records -
internal. They are the GPU layout, and a host no longer picks between them at all: the load-time
packer chooses one set, sized to what the source file actually contains.

## The debug and diagnostic surface

Deleted rather than made internal or moved to a sample: overdraw heatmaps, bone gizmos, the periodic
perf report, `SplatPerformanceSettings`, `SplatPerfDiagnostics`, and the six debug knobs that used
to sit on the character component. None of it was used by a single line of sample code, which is the
tell - they were tools for developing the package, not for using it. `SplatPerfDiagnostics` in
particular installed a hidden `DontDestroyOnLoad` object on the first character load of every build,
enabled by default; a package should not do that unasked. What survived went where it belonged:
splat quality to `SplatQuality`; the frame-rate cap belonged to the host game, not to a
rendering package, and went nowhere.

---

# Surface review

The surface is in good shape and mostly needs no defending. Eleven types in the runtime assembly,
two of which are components and two of which are enums; two importers in the editor assembly; one
data type and one URP renderer feature in the vendored assembly. Zero `[Obsolete]` members anywhere
a host can see. Both samples compile against `Viggle.SplatEngine` alone. Naming follows Unity's own
(`SplatRenderer` / `SkinnedSplatRenderer`, `updateWhenOffscreen`, Rig Mode on the importer, prefab
as the main object), so nothing here has to be learned twice.

What follows is only what can actually be justified.

## 1. `SplatRendererFeature` is the second public type in the vendored assembly

Under URP the splats never draw until the user adds this renderer feature to the renderer asset the
camera uses. The Add Renderer Feature menu is populated from `TypeCache`, which ignores both
accessibility and assembly references, so the inspector route never needed the type to be public -
but a host that builds or patches its URP renderer asset from a build script does, and it is public
for exactly that. Its own remarks say so.

It is worth knowing that it is there, because it makes the sentence "exactly one type in the
vendored assembly is public" - which appears in that assembly's `AssemblyInfo` and in
`SplatAsset.cs` - out of date. The `autoReferenced: true` argument is unaffected: it is still there
for `SplatAsset`, and it exposes these two types and nothing else.

## 2. Dead code inside the internal renderer

`SplatRenderer.ReleaseForHotReload()` and `ReleaseStaticsForHotReload()` are still there,
still `[Obsolete]`, and their comment points at `Runtime/SplatObsoleteApi.cs`, a file that no longer
exists. They are unreachable from any host now that the type is internal, so they are not a surface
problem - they are simply the last two survivors of a deprecation cleanup and should go the same way
as the nine forwarders that were deleted.

## 3. `SplatAsset` is public and nothing hands one out

It was reachable in two ways, and both went when the capture path stopped converting at import: the
`SplatRenderer.splatAsset` field became `splatFile` (a `SplatCaptureAsset`), and the importer stopped
producing one. Every `SplatAsset` is now built at load and owned by the component that built it, so a
host can name the type but can never obtain an instance of it - its three members describe data
nothing will hand over.

The options are to make it internal, which would take the vendored assembly to zero public types and
let `GaussianSplatting.asmdef` drop `autoReferenced`, or to expose the built asset from
`SplatRenderer`. Internalizing is the honest answer to "what can a host do with this"; exposing it is
the honest answer to "how do I ask how many splats are on screen". It is left public and flagged
here rather than decided in passing, because it is a breaking change either way.

## 4. Cosmetic: three vestigial `partial` modifiers

`SplatLog`, `SplatQuality` and `SplatSettings` are declared `partial` with exactly one part each,
left over from files that were merged or deleted. Harmless, but it implies a second half a reader
will go looking for.

---

# Totals

| Assembly | Public types | Public members | Enum constants | `[Obsolete]` |
| --- | --- | --- | --- | --- |
| `Viggle.SplatEngine` | 11 | 51 | 8 | 0 |
| `Viggle.SplatEngine.Editor` | 2 | 3 | 0 | 0 |
| `GaussianSplatting` (vendored) | 2 | 8 | 0 | 0 |
| `GaussianSplattingEditor` (vendored) | 0 | 0 | 0 | 0 |
| `Viggle.SplatEngine.Jobs` | 0 | 0 | 0 | 0 |
| **Total** | **15** | **62** | **8** | **0** |

Members counts fields, properties, events and methods, including the two `OnImportAsset` overrides
and all four `LoadAsync` overloads; enum constants are counted separately. The total held at
fifteen types across the capture-path rewrite by accident rather than by design: `SplatCaptureAsset`
arrived and `SplatImportQuality` left. `SplatRendererFeature`'s five public overrides are counted
here for the first time; earlier revisions of this file described it as internal, which it is not.
