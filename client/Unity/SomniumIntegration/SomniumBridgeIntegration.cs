using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Drop this onto any GameObject in your Somnium Space scene to connect
    /// SomniSync with SomniumBridge — gives accurate player identity and head
    /// transform without any additional setup.
    ///
    /// What it does:
    ///   - NetworkPersistence.PlayerIdProvider → player's Somnium account UUID
    ///     (used as the persistence key so data follows the player's account)
    ///
    /// Requires: Somnium Space SDK (SomniumBridge namespace).
    /// This script will NOT compile outside a Somnium Space Unity project.
    /// </summary>
    public class SomniumBridgeIntegration : MonoBehaviour
    {
        private void Start()
        {
            NetworkPersistence.PlayerIdProvider = GetPlayerId;
            Debug.Log("[SomniumBridge] Integration active — using Somnium player identity.");
        }

        private string GetPlayerId()
        {
            return SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id;
        }
    }
}
