// SPDX-License-Identifier: MIT

namespace Viggle.SplatEngine
{
    /// <summary>
    /// How a splat character's animation clips bind to its bones. Chosen per character file, on
    /// the .vsplat importer's Rig Mode - the same place a mesh makes the choice, on the model
    /// importer's Rig tab.
    /// </summary>
    /// <remarks>
    /// The numbering is load-bearing. <see cref="Humanoid"/> must stay 0, so a
    /// <see cref="SplatCharacterAsset"/> carrying no explicit value - one created at runtime from raw
    /// bytes, or written by an older version of the package - lands on the behaviour the package
    /// has always had by default rather than on no rig at all.
    /// </remarks>
    public enum SplatRigMode
    {
        /// <summary>
        /// Build a Humanoid Avatar and retarget clips through Unity's muscle space. Works with any
        /// Humanoid clip, at the cost of retargeting approximation.
        /// </summary>
        Humanoid = 0,

        /// <summary>
        /// No avatar: clips drive the bone Transforms directly by path. Exact playback, but the
        /// clips must be authored on this same skeleton with matching bone names.
        /// </summary>
        Generic = 1,

        /// <summary>
        /// No avatar and no Animator. The bones are still built and still skin the splats, so the
        /// character deforms whenever something moves them - script, physics, IK, or dragging a
        /// bone in the Scene view.
        /// </summary>
        /// <remarks>
        /// This is NOT Unity's Rig-tab None, which means no rig at all. A splat character's data is
        /// bone-weighted, so it always keeps its skeleton; what this drops is the Animator. Picking
        /// it expecting a bare object gets the full bone hierarchy.
        /// </remarks>
        None = 2,
    }
}
