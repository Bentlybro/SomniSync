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
    ///   - NetworkManager.LocalPlayerTransformProvider → player's Head transform
    ///     (used for AOI distance checks and player position sync)
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
            NetworkManager.LocalPlayerTransformProvider = GetLocalPlayerTransform;
            NetworkPersistence.PlayerIdProvider = GetPlayerId;
            Debug.Log("[SomniumBridge] Integration active — using Somnium player identity and head transform.");
        }

        private Transform GetLocalPlayerTransform()
        {
            return SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Head;
        }

        private string GetPlayerId()
        {
            return SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id;
        }
    }
}
