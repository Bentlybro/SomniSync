using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Attached at runtime to the local player's body (NonXR.Body / XR.Body).
    /// Receives OnControllerColliderHit from the CharacterController and
    /// notifies any NetworkObject that gets pushed.
    /// 
    /// Zero per-frame cost — only fires when the CharacterController actually
    /// collides with something.
    /// </summary>
    public class PlayerPushDetector : MonoBehaviour
    {
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            // Fast path: check for NetworkObject directly on the hit object
            var netObj = hit.gameObject.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.OnPlayerPush();
            }
        }
    }
}
