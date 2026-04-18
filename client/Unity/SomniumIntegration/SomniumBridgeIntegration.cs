using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Drop this on any GameObject in your Somnium Space scene to wire
    /// SomniSync into the SDK properly:
    ///
    ///   1. NetworkPersistence uses the player's Somnium account UUID as the
    ///      persistence key — saved data follows the account, not the device.
    ///   2. NetworkManager's "Local Player" transform is set from
    ///      <c>SomniumBridge.PlayersContainer.LocalPlayer.Root</c> so server-
    ///      side AOI actually tracks where you are in the world.
    ///
    /// Requires: Somnium Space SDK (SomniumBridge namespace).
    /// This script will NOT compile outside a Somnium Space Unity project.
    /// </summary>
    public class SomniumBridgeIntegration : MonoBehaviour
    {
        private bool _playerRegistered;

        private void Start()
        {
            NetworkPersistence.PlayerIdProvider = GetPlayerId;
        }

        private void Update()
        {
            if (_playerRegistered) return;
            if (NetworkManager.Instance == null) return;

            Transform root;
            try
            {
                root = SomniumBridge.PlayersContainer?.LocalPlayer?.Root;
            }
            catch
            {
                // SDK access can throw early in load (XR types not yet resolved).
                // Silently retry next frame.
                return;
            }
            if (root == null) return;

            // LocalPlayer.Root is the rig root. On some rigs that stays at world
            // origin while a child CharacterController carries the actual world
            // position. Prefer the CC when present — that's the transform server-
            // side AOI needs to follow you around.
            var cc = root.GetComponentInChildren<CharacterController>(true);
            Transform target = cc != null ? cc.transform : root;

            NetworkManager.Instance.SetLocalPlayer(target);
            _playerRegistered = true;
            Debug.Log($"[SomniumBridge] Local player registered for AOI: {target.name}");
        }

        private string GetPlayerId()
        {
            return SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id;
        }
    }
}
