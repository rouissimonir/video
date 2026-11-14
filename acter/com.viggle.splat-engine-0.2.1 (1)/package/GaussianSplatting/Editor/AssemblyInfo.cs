// SPDX-License-Identifier: MIT
//
// Same rule as the runtime half: this vendored editor assembly is an implementation detail, and
// every type in it is internal.
//
// Nothing is let in here any more. The one thing that needed it was the capture importer, which
// used to drive the conversion code that lived in this assembly; captures are converted at load
// time now and that code is gone, so the exception went with it.
//
// Deliberately NOT extended to any Samples assembly. Sample code is user-facing example code:
// anything a sample needs, a user needs, and that belongs in the public API.

using System.Runtime.CompilerServices;

// The package's own editor assembly still references this one - it has to, because the scene tools
// and inspectors in here have to compile alongside it - but it no longer reaches into it.
[assembly: InternalsVisibleTo("Viggle.SplatEngine.Editor")]
