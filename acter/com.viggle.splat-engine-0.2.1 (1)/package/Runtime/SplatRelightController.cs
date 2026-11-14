// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Viggle.SplatEngine
{
    /// <summary>
    /// DEPRECATED - merged into <see cref="SplatRelight"/>, which also generates the proxy and
    /// hosts the debug switch. This stub keeps old scenes loading (same script GUID) and removes
    /// itself at play time; delete the component whenever you see it in the inspector.
    /// </summary>
    [AddComponentMenu("")] // hidden from the Add Component menu
    public class SplatRelightController : MonoBehaviour
    {
        void Awake()
        {
            Debug.LogWarning("SplatRelightController is deprecated - its job moved to SplatRelight. " +
                             "Removing this component; delete it from the scene to silence this.", this);
            Destroy(this);
        }
    }
}
