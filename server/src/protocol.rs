/// Protocol constants and message type definitions.
///
/// Architecture: server-authoritative. The server runs Rapier3D physics for
/// the world; clients only send inputs (claims, held-object positions, events,
/// variables). Clients never simulate physics on behalf of other clients.
///
/// Entity format (compact): 14 bytes per entity
///   [u16 local_id][half x][half y][half z][u16 rx][u16 ry][u16 rz]
///
/// Velocity entity: 20 bytes
///   compact + [half vx][half vy][half vz]
///
/// MSG_POSITION_VELOCITY (0x0B):
///   [u8 type][u16 sender_client_id][u16 sequence][u16 count][N × 20B entity]
///   Server → clients only. sender_client_id = 0 means "server".
///
/// Persistence protocol (all inside 0xFF control messages):
///   CTRL_PLAYER_IDENT (0x20):  [0xFF][0x20][u8 len][utf8 player_uuid...]
///   CTRL_PERSIST_SET  (0x21):  [0xFF][0x21][u8 ns_len][ns][u8 key_len][key][u16 val_len][value]
///   CTRL_PERSIST_GET  (0x22):  [0xFF][0x22][u16 req_id][u8 ns_len][ns][u8 key_len][key]
///   CTRL_PERSIST_DATA (0x23):  [0xFF][0x23][u16 req_id][u8 found][u16 val_len][value]

// --- Message types ---
pub const MSG_INTERACTION_UPDATE: u8     = 0x03; // client → server: claimed-object position
pub const MSG_VARIABLE_UPDATE: u8        = 0x04; // NetworkVariables (any → others)
pub const MSG_EVENT: u8                  = 0x06; // fire-and-forget RPCs (any → others)
pub const MSG_ANIMATOR: u8               = 0x07; // animator state (any → others)
pub const MSG_SCALED_INTERACTION: u8     = 0x09; // claimed scaled-object position
pub const MSG_POSITION_VELOCITY: u8      = 0x0B; // server → clients: authoritative position + velocity
pub const MSG_PLAYER_POS: u8             = 0x0C; // client → server: local player position (for AOI)
pub const MSG_PUSH_IMPULSE: u8           = 0x0D; // client → server: apply impulse to an object (no claim)
pub const MSG_TIME_PING: u8              = 0x0E; // client → server: [u64 client_local_ms] — time sync ping
pub const MSG_SCENE_INIT: u8             = 0x10; // client → server: physics scene description

// --- Control subtypes (inside 0xFF messages) ---
pub const CTRL_WELCOME: u8           = 0x01; // server → client: [u16 client_id][u8 needs_scene_init][u64 unix_ms][u64 scene_ready_ms]
pub const CTRL_CLAIM: u8             = 0x05; // any → server → others: grab an object
pub const CTRL_RELEASE: u8           = 0x06; // any → server → others: release a grab
pub const CTRL_CLAIM_REJECTED: u8    = 0x07; // server → requester: your claim was refused, [u16 obj_id]
pub const CTRL_PHYSICS_READY: u8     = 0x08; // server → all: physics sim is live, [u64 scene_ready_ms]
pub const CTRL_SPAWN_REQUEST: u8     = 0x0B; // client → server: please spawn [u8 prefab_idx][60B scene_object]
pub const CTRL_SPAWN: u8             = 0x0C; // server → all: spawned [u16 id][u8 prefab_idx][60B scene_object][u64 spawn_server_ms]
pub const CTRL_DESPAWN: u8           = 0x0D; // either direction: destroy [u16 id]
pub const CTRL_TIME_PONG: u8         = 0x0E; // server → client: [u64 client_local_ms][u64 server_ms] — time sync reply

// --- Persistence control subtypes ---
pub const CTRL_PLAYER_IDENT: u8  = 0x20;
pub const CTRL_PERSIST_SET: u8   = 0x21;
pub const CTRL_PERSIST_GET: u8   = 0x22;
pub const CTRL_PERSIST_DATA: u8  = 0x23;

// --- Server error notification ---
pub const CTRL_ERROR: u8 = 0xFE;

/// Build a control message: [0xFF][ctrl_type][payload...]
pub fn ctrl_msg(ctrl_type: u8, payload: &[u8]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(2 + payload.len());
    buf.push(0xFF);
    buf.push(ctrl_type);
    buf.extend_from_slice(payload);
    buf
}
