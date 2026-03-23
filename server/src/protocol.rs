/// Protocol constants and message type definitions.
///
/// Entity format (compact): 14 bytes per entity
///   [u16 local_id][half x][half y][half z][u16 rx][u16 ry][u16 rz]
///
/// Position messages (0x01, 0x02, 0x03):
///   [u8 type][u16 count][N × 14B entity]

// --- Message types ---
pub const MSG_POSITION_UPDATE: u8 = 0x01;
pub const MSG_SNAPSHOT: u8 = 0x02;
pub const MSG_INTERACTION_UPDATE: u8 = 0x03;
pub const MSG_VARIABLE_UPDATE: u8 = 0x04;
pub const MSG_EVENT: u8 = 0x06;
pub const MSG_ANIMATOR: u8 = 0x07;
pub const MSG_RIGIDBODY: u8 = 0x08;
pub const MSG_POSITION_UPDATE_SCALED: u8 = 0x05;
pub const MSG_SCALED_INTERACTION: u8 = 0x09;

// --- Control subtypes (inside 0xFF messages) ---
pub const CTRL_YOU_ARE_MASTER: u8 = 0x01;
pub const CTRL_YOU_ARE_SPECTATOR: u8 = 0x02;
pub const CTRL_SEND_SNAPSHOT: u8 = 0x03;
pub const CTRL_MASTER_CHANGED: u8 = 0x04;
pub const CTRL_CLAIM: u8 = 0x05;
pub const CTRL_RELEASE: u8 = 0x06;
pub const CTRL_PLAYER_POSITION: u8 = 0x07;

/// Build a control message: [0xFF][ctrl_type][payload...]
pub fn ctrl_msg(ctrl_type: u8, payload: &[u8]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(2 + payload.len());
    buf.push(0xFF);
    buf.push(ctrl_type);
    buf.extend_from_slice(payload);
    buf
}

/// Build a control message with a u16 value
pub fn ctrl_msg_u16(ctrl_type: u8, value: u16) -> Vec<u8> {
    ctrl_msg(ctrl_type, &value.to_le_bytes())
}
