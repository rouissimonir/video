// SPDX-License-Identifier: MIT
//
// This assembly exists for one mechanical reason (see SplatSkinningJobs): scheduling a job
// permanently registers its Execute with the engine, which would pin the runtime assembly and
// block hot assembly swaps. It is a build artefact of that constraint, not an API.
//
// THE RULE THAT FOLLOWS FROM THAT: IT REFERENCES NOTHING OF OURS
//
// Because it cannot be swapped, a host that hot-updates the package compiles this assembly into
// the player and reloads the rest. That makes the dependency edge one-way by force: an assembly
// in the build cannot reference one that only exists at runtime, so this asmdef lists Unity
// packages and nothing else - Burst, Collections, Mathematics, and the engine. It is the reason
// SplatChunkInfo and GaussianUtils are declared down here rather than beside the asset format
// they belong to: the jobs need them, so they have to be on this side of the line.
//
// Everything above the line reaches down instead, which is why the list below is four names long
// rather than one. Nothing in the assembly is public: BonePose and SplatChunkInfo are GPU layout
// contracts with SplatSkinning.compute and GaussianSplatting.hlsl, and a host that could see them
// could not usefully do anything except break them.

using System.Runtime.CompilerServices;

// The package's public API. It builds capture assets through SplatCaptureJobs and drives skinning
// through SplatSkinningJobs.
[assembly: InternalsVisibleTo("Viggle.SplatEngine")]

// The vendored leaf assembly. SplatAsset sizes its chunk blob by SplatChunkInfo, the layout the
// jobs here write - see the header of SplatChunkInfo.cs for why the declaration is on this side.
[assembly: InternalsVisibleTo("GaussianSplatting")]

// The vendored editor half: its asset inspector reports the chunk record's size.
[assembly: InternalsVisibleTo("GaussianSplattingEditor")]

// The package's own editor assembly. Its .spz encoder is the inverse of the decoder here and has
// to pack opacity and rotation through the same GaussianUtils helpers, or the round trip drifts.
[assembly: InternalsVisibleTo("Viggle.SplatEngine.Editor")]
