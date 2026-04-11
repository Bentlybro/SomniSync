/// Protocol constants and message type definitions.
///
/// Entity format (compact): 14 bytes per entity
///   [u16 local_id][half x][half y][half z][u16 rx][u16 ry][u16 rz]
///
/// Velocity entity: 20 bytes
///   compact + [half vx][half vy][half vz]
///
/// MSG_POSITION_VELOCITY (0x0B):
///   [u8 type][u16 sender_client_id][u16 sequence][u16 count][N × 20B entity]
///
/// Persistence protocol (all inside 0xFF control messages):
///   CTRL_PLAYER_IDENT (0x20):  [0xFF][0x20][u8 len][utf8 player_uuid...]
///   CTRL_PERSIST_SET  (0x21):  [0xFF][0x21][u8 ns_len][ns][u8 key_len][key][u16 val_len][value]
///   CTRL_PERSIST_GET  (0x22):  [0xFF][0x22][u16 req_id][u8 ns_len][ns][u8 key_len][key]
///   CTRL_PERSIST_DATA (0x23):  [0xFF][0x23][u16 req_id][u8 found][u16 val_len][value]

// --- Message types ---
pub const MSG_POSITION_UPDATE: u8        = 0x01; // legacy master-only batch
pub const MSG_SNAPSHOT: u8               = 0x02; // full world snapshot
pub const MSG_INTERACTION_UPDATE: u8     = 0x03; // claimed-object updates
pub const MSG_VARIABLE_UPDATE: u8        = 0x04; // NetworkVariables
pub const MSG_POSITION_UPDATE_SCALED: u8 = 0x05; // scaled objects
pub const MSG_EVENT: u8                  = 0x06; // fire-and-forget RPCs
pub const MSG_ANIMATOR: u8               = 0x07; // animator state
pub const MSG_RIGIDBODY: u8              = 0x08; // velocity sync
pub const MSG_SCALED_INTERACTION: u8     = 0x09; // claimed scaled objects
pub const MSG_SNAPSHOT_SCALED: u8        = 0x0A; // full scaled snapshot
pub const MSG_POSITION_VELOCITY: u8      = 0x0B; // authority position + velocity (dead reckoning)
pub const MSG_SCENE_INIT: u8             = 0x10; // client→server: physics scene description

// --- Control subtypes (inside 0xFF messages) ---
pub const CTRL_YOU_ARE_MASTER: u8    = 0x01;
pub const CTRL_YOU_ARE_SPECTATOR: u8 = 0x02;
pub const CTRL_SEND_SNAPSHOT: u8     = 0x03;
pub const CTRL_MASTER_CHANGED: u8    = 0x04;
pub const CTRL_CLAIM: u8             = 0x05;
pub const CTRL_RELEASE: u8           = 0x06;
pub const CTRL_PLAYER_POSITION: u8   = 0x07;
pub const CTRL_PHYSICS_READY: u8     = 0x08; // server→all: physics mode active
pub const CTRL_AUTHORITY_ASSIGN: u8  = 0x0A; // master → all: [u16 clientId][u16 count][u16... objectIds]

// --- Persistence control subtypes ---
pub const CTRL_PLAYER_IDENT: u8  = 0x20; // client → server: stable player UUID
pub const CTRL_PERSIST_SET: u8   = 0x21; // client → server: write key/value
pub const CTRL_PERSIST_GET: u8   = 0x22; // client → server: read request
pub const CTRL_PERSIST_DATA: u8  = 0x23; // server → client: read response

// --- Server error notification ---
pub const CTRL_ERROR: u8 = 0xFE; // server → client: operation failed (e.g. snapshot too large)

/// Build a control message: [0xFF][ctrl_type][payload...]
pub fn ctrl_msg(ctrl_type: u8, payload: &[u8]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(2 + payload.len());
    buf.push(0xFF);
    buf.push(ctrl_type);
    buf.extend_from_slice(payload);
    buf
}

/// Build a control message with a single u16 value
pub fn ctrl_msg_u16(ctrl_type: u8, value: u16) -> Vec<u8> {
    ctrl_msg(ctrl_type, &value.to_le_bytes())
}
