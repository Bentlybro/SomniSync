using System.Collections.Generic;
using System.Buffers;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Area of Interest (AOI) — freezes objects far from all players to save CPU and bandwidth.
    /// Master runs UpdateAOI(); spectators send their position so the master can include them.
    /// </summary>
    public partial class NetworkManager
    {
        // Reuse this list every AOI tick — avoids per-tick allocation for farewell batching.
        private readonly List<NetworkObject> _farewellScratch = new List<NetworkObject>(32);

        // =====================================================================
        // AOI calculation (master only)
        // =====================================================================

        private void UpdateAOI()
        {
            // Remove stale player records (disconnected without notice)
            List<ushort> stale = null;
            foreach (var kvp in _playerLastSeen)
            {
                if (Time.time - kvp.Value > PLAYER_TIMEOUT)
                {
                    if (stale == null) stale = new List<ushort>();
                    stale.Add(kvp.Key);
                }
            }
            if (stale != null)
            {
                foreach (var id in stale)
                {
                    _playerPositions.Remove(id);
                    _playerLastSeen.Remove(id);
                }
            }

            // Collect all player positions into reused scratch list (no allocation)
            _aoiPlayerPosScratch.Clear();

            if (_localPlayerTransform == null)
                _localPlayerTransform = FindLocalPlayerTransform();
            if (_localPlayerTransform != null)
                _aoiPlayerPosScratch.Add(_localPlayerTransform.position);

            foreach (var kvp in _playerPositions)
                _aoiPlayerPosScratch.Add(kvp.Value);

            if (_aoiPlayerPosScratch.Count == 0)
                return;

            float activateRadiusSq   = aoiRadius * aoiRadius;
            float deactivateRadiusSq = (aoiRadius + aoiHysteresis) * (aoiRadius + aoiHysteresis);
            int activeCount = 0;

            _farewellScratch.Clear();

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];

                // Never deactivate remotely claimed objects
                if (obj.IsClaimedByRemote)
                {
                    obj.AOIActivate();
                    activeCount++;
                    continue;
                }

                Vector3 objPos = obj.transform.position;
                float closestDistSq = float.MaxValue;

                for (int p = 0; p < _aoiPlayerPosScratch.Count; p++)
                {
                    float distSq = (objPos - _aoiPlayerPosScratch[p]).sqrMagnitude;
                    if (distSq < closestDistSq)
                        closestDistSq = distSq;
                }

                if (obj.IsAOIActive)
                {
                    // Active — use wider deactivate radius (hysteresis prevents flicker)
                    if (closestDistSq > deactivateRadiusSq)
                    {
                        obj.AOIDeactivate();
                        // Collect for batched farewell — one message for all deactivations
                        // this tick, instead of N separate messages (which could flood the
                        // send queue when many objects leave the zone simultaneously).
                        if (!obj.IsAOIActive)
                            _farewellScratch.Add(obj);
                    }
                    else
                        activeCount++;
                }
                else
                {
                    // Inactive — use tighter activate radius
                    if (closestDistSq <= activateRadiusSq)
                    {
                        obj.AOIActivate();
                        activeCount++;
                    }
                }
            }

            // Send all farewell updates in one batched message
            if (_farewellScratch.Count > 0)
                SendAOIFarewellBatch(_farewellScratch);

            ActiveAOIObjects = activeCount;
            TotalObjects = _objects.Count;
        }

        // =====================================================================
        // Player position broadcast (spectators only)
        // =====================================================================

        private void SendPlayerPosition()
        {
            if (_localPlayerTransform == null)
                _localPlayerTransform = FindLocalPlayerTransform();
            if (_localPlayerTransform == null) return;

            Vector3 pos = _localPlayerTransform.position;
            // [0xFF][0x07][u16 client_id][half x][half y][half z]
            byte[] buf = new byte[10];
            buf[0] = 0xFF;
            buf[1] = Protocol.CTRL_PLAYER_POSITION;
            Protocol.WriteU16(buf, 2, ClientId);
            Protocol.WriteHalf(buf, 4, pos.x);
            Protocol.WriteHalf(buf, 6, pos.y);
            Protocol.WriteHalf(buf, 8, pos.z);
            SendBinary(buf);
        }

        // =====================================================================
        // Local player transform resolution
        // =====================================================================

        private Transform FindLocalPlayerTransform()
        {
            // 1. Inspector override — highest priority
            if (localPlayerTransformOverride != null)
                return localPlayerTransformOverride;

            // 2. Static provider hook (set by SomniumBridgeIntegration or world maker)
            if (LocalPlayerTransformProvider != null)
            {
                var t = LocalPlayerTransformProvider();
                if (t != null) return t;
            }

            // 3. Somnium Space specific: find local player body
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                string name = cc.gameObject.name;
                if (name != "NonXR.Body" && name != "XR.Body") continue;

                Transform root = cc.transform;
                while (root.parent != null) root = root.parent;
                if (!root.name.StartsWith("Original Player:")) continue;

                return cc.transform;
            }

            // 3. Fall back to main camera (tagged MainCamera)
            if (Camera.main != null)
                return Camera.main.transform;

            // 4. Fall back to any active camera in the scene
            var anyCam = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            if (anyCam.Length > 0)
                return anyCam[0].transform;

            if (!_aoiTransformWarningLogged)
            {
                _aoiTransformWarningLogged = true;
                Debug.LogWarning("[Net] AOI: could not find local player transform. Assign 'Local Player Transform Override' on NetworkManager, or tag your camera as MainCamera.");
            }
            return null;
        }
    }
}
