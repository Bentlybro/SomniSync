using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Base class for components that sync state through a sibling
    /// <see cref="NetworkObject"/>.
    ///
    /// Handles the boilerplate every networked component was repeating:
    ///   - Find the sibling NetworkObject at Awake
    ///   - Log + disable the component if it's missing
    ///   - Expose a one-line <see cref="HasAuthority"/> helper
    ///
    /// Subclasses that override <c>Awake</c> MUST call <c>base.Awake()</c> first.
    /// When the base class disables the component (missing NetworkObject),
    /// <c>enabled</c> is already false by the time control returns, so subclass
    /// Awake / Start / Update bodies don't need to repeat the null check.
    /// </summary>
    public abstract class NetworkComponent : MonoBehaviour
    {
        /// <summary>
        /// The sibling NetworkObject. Never null inside Start / Update — the
        /// base Awake disables the component if it's missing, so those
        /// lifecycle hooks never fire.
        /// </summary>
        protected NetworkObject NetObj { get; private set; }

        protected virtual void Awake()
        {
            NetObj = GetComponent<NetworkObject>();
            if (NetObj == null)
            {
                Debug.LogError($"[Net] {GetType().Name} on '{name}' requires a NetworkObject on the same GameObject — disabling.");
                enabled = false;
            }
        }

        /// <summary>
        /// True when this client is allowed to write sibling state
        /// (NetworkVariables, NetworkAnimator, NetworkTimer, NetworkEvents).
        /// Thin wrapper around <see cref="NetworkObject.HasAuthority"/>.
        /// </summary>
        protected bool HasAuthority() => NetObj != null && NetObj.HasAuthority();
    }
}
