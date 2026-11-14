// SPDX-License-Identifier: MIT
//
// This assembly is vendored, not consumed. Upstream (org.nesnausk.gaussian-splatting) is no
// longer developed and will never be synced into here again, so its public shape buys nothing
// and every type in it is `internal`: a host sees only the Viggle.SplatEngine surface, and never
// has to know which of the two assemblies a type came from.
//
// What is left here is the leaf: the packed asset format (SplatAsset) and the three GPU sorters.
// The renderer itself, the render system, the URP feature and the HDRP pass all moved UP into
// Viggle.SplatEngine when SplatRenderer became a public component. They had to: loading a capture
// needs the .spz reader, the runtime asset builder and the project settings, all of which live in
// Viggle.SplatEngine, and that assembly already references this one - so the renderer could not
// stay down here and also load a file. Moving it up also means a host assembly that names
// SplatRenderer needs a reference to Viggle.SplatEngine and nothing else.
//
// ONE EXCEPTION to the internal rule. SplatAsset is public, and is in the Viggle.SplatEngine
// namespace, because it is the packed data every splat component ends up driving the renderer
// with. See the header of SplatAsset.cs. That type is why this asmdef stays auto-referenced:
// without it, user code could not name it.
//
// MIT permits this; the attribution duty survives it. The copyright notice in LICENSE.md stays.
// The four files that moved up into Runtime/ keep their MIT SPDX headers and are listed by path in
// Third Party Notices.md, so the boundary is still stated even though it is no longer one
// directory.
//
// Deliberately NOT extended to any Samples assembly. Sample code is user-facing example code:
// anything a sample needs, a user needs, and that belongs in the public Viggle.SplatEngine API.

using System.Runtime.CompilerServices;

// The package's public API. It owns SplatRenderer and SkinnedSplatRenderer, which drive the
// sorters declared here. Without this the internals are unreachable and the package cannot render
// anything.
[assembly: InternalsVisibleTo("Viggle.SplatEngine")]

// The vendored editor half: the splat-editing inspector and the scene tools. They still name the
// asset formats declared here. Separate assembly, so the `internal` above does not reach it on its
// own.
[assembly: InternalsVisibleTo("GaussianSplattingEditor")]

// The package's own editor assembly. It owns the .spz importer, which reports what a capture will
// cost on the GPU - and the formats and size helpers that answer that are internal here by design.
[assembly: InternalsVisibleTo("Viggle.SplatEngine.Editor")]

// Viggle.SplatEngine.Jobs is deliberately NOT on this list, and must not be added to it. That
// assembly is compiled into the player while this one can be hot-swapped, so the reference runs
// the other way: this asmdef references Jobs, for the chunk layout the jobs there write. Anything
// the jobs need has to be declared on their side of that line - see Runtime/Jobs/AssemblyInfo.cs.

