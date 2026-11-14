# Third Party Notices

## Attribution

This package is MIT throughout - see `LICENSE.md`. There is no licensing boundary inside it.
What this file records is **provenance**: which files were derived from someone else's work,
and what was changed. MIT requires the upstream copyright notice and permission notice to be
preserved in all copies, and a shipped package is a copy, so `GaussianSplatting/LICENSE.md`
travels with this one and must stay in any copy you distribute.

Where the code came from:

| path | origin |
|------|--------|
| `GaussianSplatting/` | Gaussian Splatting package, Aras Pranckevičius - notice in `GaussianSplatting/LICENSE.md` |
| `Runtime/SplatRenderer.cs`, `Runtime/SplatRendererFeature.cs`, `Runtime/SplatCustomPassHDRP.cs`, `Runtime/GaussianCutout.cs`, `Runtime/Jobs/GaussianUtils.cs`, `Runtime/Jobs/SplatChunkInfo.cs` | derived from the same source; they moved out of `GaussianSplatting/` and are covered by the same notice |
| everything else | written for this package by Viggle / WarpEngine |

This table is the authoritative list of what is derived. Scoping by directory alone stopped
being accurate when those files moved out of `GaussianSplatting/`.

## Upstream

This package vendors, and (for the animation feature) makes small additive
modifications to, the **Gaussian Splatting** package:

- **Package:** `org.nesnausk.gaussian-splatting`
- **Author:** Aras Pranckevičius
- **License:** MIT
- **Source:** https://github.com/aras-p/UnityGaussianSplatting

Modifications made to that package in this repository:

- Removed three editor-only debug entry points that registered items under
  `Tools/Gaussian Splats/Debug/`: `GaussianSplatValidator` (render comparison against
  SBIR/D3D12 reference images), `CaptureScreenshot` (which also bound a global Cmd+G /
  Ctrl+G hotkey, so it could collide with the host project's own shortcuts), and
  `ForceGLESPathToggle` (added in this repository, not upstream, to reproduce mobile GLES
  behaviour on a desktop GPU). None are referenced by anything else, and none belong in a
  package shipped to users.

- Deleted upstream's editor-time capture conversion outright: the
  `Tools/Gaussian Splats/Create GaussianSplatAsset` window, the packing jobs and encoders
  behind it, the k-means spherical-harmonic clustering, the BC7 colour path, and the
  `.ply` and `.spz` file readers. Captures are decoded and packed at LOAD time in this
  package's own assemblies instead. The conversion produced an asset far larger than the
  source (a 27MB `.spz` came out as 432MB) and did nothing for captures fetched at
  runtime, which is how they arrive here.

  Upstream's `PLYFileReader` and `GaussianFileReader` are removed and have not come back.
  `.ply` import is served instead by `SplatPlyFile` and `SplatSpzEncoder` in this package's
  own editor assembly, which are new code: they stream the file rather than materialising
  it, and they convert to `.spz` bytes rather than to the packed GPU layout. What they take
  from upstream is knowledge rather than code - the property names a Gaussian `.ply`
  carries, that `f_rest_*` is stored channel-major, and that `rot_0` is the quaternion's
  `w`. The `.ply` EXPORT in `SplatRendererEditor` is unchanged and still works, and now
  declares the splat struct it writes next to itself.

- Renamed `GaussianSplatAsset` to `SplatAsset` and moved it into the `Viggle.SplatEngine`
  namespace, where it is the one public type in this vendored assembly. It could not move
  into the `Viggle.SplatEngine` assembly: the vendored renderer names it, and that assembly
  already references this one.

- Moved `GaussianUtils` and the 64-byte chunk record out of the vendored assembly and into
  `Runtime/Jobs/`, where the latter is `SplatChunkInfo` rather than a type nested in the
  asset. Both are unchanged apart from their namespace. The jobs assembly is the one a host
  compiles into the player when it hot-updates the rest of the package, so it may not
  reference any assembly that gets swapped - which means the maths and the layout its jobs
  write have to be declared on its side. `Viggle.SplatEngine.Jobs` now references Unity
  packages only, and the vendored assembly references it.

- `Shaders/GaussianComposite.shader` - guard the un-premultiply divide so empty
  pixels (alpha 0) compose as transparent instead of NaN→black, letting the
  scene background / skybox show through. (General bugfix.)
- `Shaders/GaussianSplatting.hlsl` and `Runtime/GaussianSplatRenderer.cs` -
  additive, default-off hooks for an external per-splat position/rotation
  override buffer, used by this package's GPU skinning. Inert when skinning is
  disabled.

The MIT license of the Gaussian Splatting package, with its copyright notice, is
reproduced verbatim in that package's own `LICENSE.md` at
`GaussianSplatting/LICENSE.md`.
