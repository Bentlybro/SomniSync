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
                obj.SetStateAuthority(ClientId);
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
