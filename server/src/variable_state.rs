//! Bounded, per-room replay of the latest persistent NetworkVariables values.
//! Live updates still use 0x04; snapshot chunks use 0x12 only on join.

use std::collections::{HashMap, HashSet};

use crate::protocol::{read_u16, MSG_VARIABLE_SNAPSHOT, MSG_VARIABLE_UPDATE};

const MAX_NAME_BYTES: usize = 64;
const MAX_VALUE_BYTES: usize = 4096;
const MAX_CACHED_BYTES: usize = 2 * 1024 * 1024;
const MAX_CACHED_VALUES: usize = 16_384;
const SNAPSHOT_FRAME_BYTES: usize = 64 * 1024;

type Key = (u16, Vec<u8>);

#[derive(Default)]
pub struct VariableState {
    values: HashMap<Key, Vec<u8>>,
    cached_bytes: usize,
}

#[derive(Debug, PartialEq, Eq)]
pub enum UpdateError {
    Malformed,
    CacheFull,
}

struct Record<'a> {
    name: &'a [u8],
    wire: &'a [u8],
    persist: bool,
}

impl VariableState {
    /// Validate the entire update before changing state or relaying it.
    pub fn apply(&mut self, data: &[u8]) -> Result<(), UpdateError> {
        if data.len() < 4 || data[0] != MSG_VARIABLE_UPDATE || data[3] == 0 {
            return Err(UpdateError::Malformed);
        }
        let object_id = read_u16(data, 1);
        let mut offset = 4;
        let mut parsed = Vec::with_capacity(data[3] as usize);
        let mut seen = HashSet::new();
        for _ in 0..data[3] {
            let start = offset;
            let name_len = *data.get(offset).ok_or(UpdateError::Malformed)? as usize;
            offset += 1;
            if name_len == 0 || name_len > MAX_NAME_BYTES || offset + name_len + 3 > data.len() {
                return Err(UpdateError::Malformed);
            }
            let name = &data[offset..offset + name_len];
            if std::str::from_utf8(name).is_err() || !seen.insert(name) {
                return Err(UpdateError::Malformed);
            }
            offset += name_len;
            let flags = data[offset];
            offset += 1;
            if flags & 0x7c != 0 {
                return Err(UpdateError::Malformed);
            }
            let var_type = flags & 0x03;
            let value_len = read_u16(data, offset) as usize;
            offset += 2;
            if value_len > MAX_VALUE_BYTES || offset + value_len > data.len() {
                return Err(UpdateError::Malformed);
            }
            if (var_type <= 1 && value_len != 4)
                || (var_type == 2 && value_len != 1)
                || (var_type == 3
                    && std::str::from_utf8(&data[offset..offset + value_len]).is_err())
            {
                return Err(UpdateError::Malformed);
            }
            if var_type == 1
                && !f32::from_le_bytes(data[offset..offset + 4].try_into().unwrap()).is_finite()
            {
                return Err(UpdateError::Malformed);
            }
            offset += value_len;
            parsed.push(Record {
                name,
                wire: &data[start..offset],
                persist: flags & 0x80 != 0,
            });
        }
        if offset != data.len() {
            return Err(UpdateError::Malformed);
        }

        let mut new_bytes = self.cached_bytes as i64;
        let mut new_count = self.values.len() as i64;
        for record in &parsed {
            let key = (object_id, record.name.to_vec());
            let old_size = self.values.get(&key).map_or(0, |v| 2 + v.len());
            let size = if record.persist {
                2 + record.wire.len()
            } else {
                0
            };
            new_bytes += size as i64 - old_size as i64;
            if old_size == 0 && size != 0 {
                new_count += 1;
            } else if old_size != 0 && size == 0 {
                new_count -= 1;
            }
        }
        if new_bytes as usize > MAX_CACHED_BYTES || new_count as usize > MAX_CACHED_VALUES {
            return Err(UpdateError::CacheFull);
        }

        for record in parsed {
            let key = (object_id, record.name.to_vec());
            if record.persist {
                self.values.insert(key, record.wire.to_vec());
            } else {
                self.values.remove(&key);
            }
        }
        self.cached_bytes = new_bytes as usize;
        Ok(())
    }

    pub fn remove_object(&mut self, object_id: u16) {
        self.values.retain(|(id, _), value| {
            if *id == object_id {
                self.cached_bytes -= 2 + value.len();
                false
            } else {
                true
            }
        });
    }

    /// Chunk the snapshot so thousands of objects still take only a handful of
    /// reliable WebSocket messages instead of overflowing a client's queue.
    pub fn snapshot_frames(&self) -> Vec<Vec<u8>> {
        let mut ordered: Vec<_> = self.values.iter().collect();
        ordered.sort_unstable_by(|a, b| a.0.cmp(b.0));
        let mut frames = Vec::new();
        let mut frame = vec![MSG_VARIABLE_SNAPSHOT, 0, 0];
        let mut count: u16 = 0;
        for ((object_id, _), record) in ordered {
            if frame.len() + 2 + record.len() > SNAPSHOT_FRAME_BYTES && count != 0 {
                frame[1..3].copy_from_slice(&count.to_le_bytes());
                frames.push(frame);
                frame = vec![MSG_VARIABLE_SNAPSHOT, 0, 0];
                count = 0;
            }
            frame.extend_from_slice(&object_id.to_le_bytes());
            frame.extend_from_slice(record);
            count += 1;
        }
        if count != 0 {
            frame[1..3].copy_from_slice(&count.to_le_bytes());
            frames.push(frame);
        }
        frames
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn update(object: u16, name: &[u8], flags: u8, value: &[u8]) -> Vec<u8> {
        let mut msg = vec![MSG_VARIABLE_UPDATE];
        msg.extend_from_slice(&object.to_le_bytes());
        msg.extend_from_slice(&[1, name.len() as u8]);
        msg.extend_from_slice(name);
        msg.push(flags);
        msg.extend_from_slice(&(value.len() as u16).to_le_bytes());
        msg.extend_from_slice(value);
        msg
    }

    #[test]
    fn persistent_values_replay_latest_and_transient_values_do_not() {
        let mut state = VariableState::default();
        state
            .apply(&update(7, b"hp", 0x80, &5i32.to_le_bytes()))
            .unwrap();
        state
            .apply(&update(7, b"hp", 0x80, &4i32.to_le_bytes()))
            .unwrap();
        state.apply(&update(7, b"flash", 0x82, &[1])).unwrap();
        state.apply(&update(7, b"flash", 0x02, &[0])).unwrap();
        let frames = state.snapshot_frames();
        assert_eq!(frames.len(), 1);
        assert_eq!(read_u16(&frames[0], 1), 1);
        assert!(frames[0].windows(4).any(|x| x == 4i32.to_le_bytes()));
        state.remove_object(7);
        assert!(state.snapshot_frames().is_empty());
    }

    #[test]
    fn malformed_updates_cannot_change_cache() {
        let mut state = VariableState::default();
        let good = update(1, b"hp", 0x80, &5i32.to_le_bytes());
        state.apply(&good).unwrap();
        let mut bad = update(1, b"hp", 0x80, &4i32.to_le_bytes());
        bad.push(99);
        assert_eq!(state.apply(&bad), Err(UpdateError::Malformed));
        assert_eq!(state.snapshot_frames()[0].last(), good.last());
    }
}
