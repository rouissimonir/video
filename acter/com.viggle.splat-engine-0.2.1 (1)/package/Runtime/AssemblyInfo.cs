// SPDX-License-Identifier: MIT
//
// The package's editor tooling is part of the package, not a consumer of it. It needs the
// parser, the skeleton tables and the runtime asset builder, and none of those are things a
// host should be calling - so they are `internal` and the editor assembly is let in here,
// rather than being made `public` for the editor's benefit.
//
// Deliberately NOT extended to the Samples assemblies. Sample code is user-facing example
// code: anything a sample needs, a user needs, and that belongs in the public API.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Viggle.SplatEngine.Editor")]

// The vendored editor half: the splat-editing inspector and the scene tools. The renderer they
// edit lives here now rather than in the vendored runtime assembly - it has to, because loading a
// capture needs the .spz reader, the asset builder and the settings, all of which are here and all
// of which already depend on the vendored assembly. So the vendored editor reaches up into this
// one instead of sideways into its own runtime half.
[assembly: InternalsVisibleTo("GaussianSplattingEditor")]
