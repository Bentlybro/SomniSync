using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Manages per-object StateAuthority assignment and zone-based redistribution.
    /// The master runs RecalculateAuthority() periodically to spread simulation load
    /// across all connected clients based on proximity.
    /// </summary>
    public partial class NetworkManager
    {
        /// <summary>
        /// Assign StateAuthority for all objects to this client (called on becoming master).
        /// Enables physics on all objects and marks this client as authority for all updates.
        /// </summary>
        private void AssignAllAuthorityToSelf()
        {
            foreach (var obj in _objects)
            {
                obj.IsAOIActive = true;
                obj.SetStateAuthority(ClientId);
            }
        }

        // =====================================================================
        // Zone-based authority redistribution (master only, runs at AOI rate)
        // =====================================================================

        private void RecalculateAuthority()
        {
            if (!IsMaster) return;

            // Reuse scratch collections — clear instead of allocate
            _authorityPlayersScratch.Clear();
            if (_localPlayerTransform != null)
                _authorityPlayersScratch.Add((ClientId, _localPlayerTransform.position));
            foreach (var kvp in _playerPositions)
                _authorityPlayersScratch.Add((kvp.Key, kvp.Value));

            // Nothing to redistribute with only one player
            if (_authorityPlayersScratch.Count <= 1) return;

            // For each active object, find nearest player; collect assignments that changed
            // Reuse the dictionary — clear values but keep List<ushort> instances to avoid allocs
            foreach (var list in _newAssignmentsScratch.Values) list.Clear();
            _newAssignmentsScratch.Clear();

            foreach (var obj in _objects)
            {
                if (obj.IsClaimedByRemote) continue; // never reassign a grabbed object
                if (!obj.IsAOIActive) continue;       // frozen objects keep current authority

                ushort nearestClient = ClientId;
                float  nearestDistSq = float.MaxValue;

                foreach (var (cid, pos) in _authorityPlayersScratch)
                {
                    float distSq = (obj.transform.position - pos).sqrMagnitude;
                    if (distSq < nearestDistSq) { nearestDistSq = distSq; nearestClient = cid; }
                }

                if (obj.StateAuthorityId != nearestClient)
                {
                    if (!_newAssignmentsScratch.ContainsKey(nearestClient))
                        _newAssignmentsScratch[nearestClient] = new List<ushort>();
                    _newAssignmentsScratch[nearestClient].Add(obj.LocalId);
                }
            }

            foreach (var kvp in _newAssignmentsScratch)
                SendAuthorityAssignment(kvp.Key, kvp.Value);
        }

        private void SendAuthorityAssignment(ushort clientId, List<ushort> objectIds)
        {
            // [0xFF][0x0A][u16 clientId][u16 count][u16... objectIds]
            int size = 6 + objectIds.Count * 2;
            byte[] buf = new byte[size];
            buf[0] = 0xFF;
            buf[1] = Protocol.CTRL_AUTHORITY_ASSIGN;
            Protocol.WriteU16(buf, 2, clientId);
            Protocol.WriteU16(buf, 4, (ushort)objectIds.Count);
            int offset = 6;
            foreach (var objId in objectIds) { Protocol.WriteU16(buf, offset, objId); offset += 2; }
            SendBinary(buf);
        }
    }
}
