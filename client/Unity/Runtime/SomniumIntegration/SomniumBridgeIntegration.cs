#if SOMNIUM_SDK
using System;
using System.Reflection;
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
    ///   2. NetworkManager's "Local Player" transform is discovered from
    ///      <c>SomniumBridge.PlayersContainer.LocalPlayer</c> so server-side
    ///      AOI actually tracks where you are in the world.
    ///
    /// Requires: Somnium Space SDK (SomniumBridge namespace).
    /// This script will NOT compile outside a Somnium Space Unity project.
    /// </summary>
    public class SomniumBridgeIntegration : MonoBehaviour
    {
        // ISomniumPlayer is an interface — Root/Transform/GameObject properties
        // live on the concrete implementation and their exact names have shifted
        // across SDK versions. Reflection keeps us forward-compatible.
        private static readonly string[] RootPropertyCandidates =
        {
            "Root", "RootTransform", "Transform", "Avatar",
            "Body", "GameObject", "PlayerObject"
        };

        private bool _playerRegistered;

        protected virtual void Start()
        {
            NetworkPersistence.PlayerIdProvider = GetPlayerId;
        }

        protected virtual void Update()
        {
            if (_playerRegistered) return;
            if (NetworkManager.Instance == null) return;

            ISomniumPlayer lp;
            try
            {
                lp = SomniumBridge.PlayersContainer?.LocalPlayer;
            }
            catch
            {
                return; // SDK not initialised yet, retry next frame
            }
            if (lp == null) return;

            Transform root = FindPlayerTransform(lp);
            if (root == null) return;

            // Some rigs put the movable transform on a CharacterController under
            // the root — prefer that when it exists.
            var cc = root.GetComponentInChildren<CharacterController>(true);
            Transform target = cc != null ? cc.transform : root;

            NetworkManager.Instance.SetLocalPlayer(target);
            _playerRegistered = true;
            Debug.Log($"[SomniumBridge] Local player registered for AOI: {target.name}");
        }

        private static Transform FindPlayerTransform(ISomniumPlayer lp)
        {
            Type t = lp.GetType();
            foreach (string name in RootPropertyCandidates)
            {
                PropertyInfo prop = t.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null) continue;

                object val;
                try   { val = prop.GetValue(lp); }
                catch { continue; }
                if (val == null) continue;

                switch (val)
                {
                    case Transform tr:  return tr;
                    case GameObject go: return go.transform;
                    case Component c:   return c.transform;
                }
            }
            return null;
        }

        private string GetPlayerId()
        {
            return SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id;
        }
    }
}
#endif
