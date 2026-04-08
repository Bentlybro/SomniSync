/// SQLite-backed player persistence store.
///
/// Schema: player_data(player_id, namespace, key, value)
/// Primary key: (player_id, namespace, key)
///
/// All values are stored as UTF-8 strings — the client encodes/decodes
/// typed values (float, int, bool) using string representation.

use log::error;
use rusqlite::{params, Connection, OptionalExtension, Result};
use std::sync::{Arc, Mutex};

pub type PersistenceDb = Arc<Db>;

pub struct Db {
    conn: Mutex<Connection>,
}

impl Db {
    /// Open (or create) the database at `path` and ensure the schema exists.
    pub fn open(path: &str) -> Result<PersistenceDb> {
        let conn = Connection::open(path)?;
        // WAL mode allows concurrent reads while a write is in progress,
        // significantly improving throughput under load.
        conn.execute_batch("PRAGMA journal_mode = WAL;")?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS player_data (
                player_id  TEXT NOT NULL,
                namespace  TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      TEXT NOT NULL,
                updated_at INTEGER NOT NULL DEFAULT (unixepoch()),
                PRIMARY KEY (player_id, namespace, key)
            );",
        )?;
        Ok(Arc::new(Db {
            conn: Mutex::new(conn),
        }))
    }

    /// Write (insert or replace) a value for a player.
    pub fn set(&self, player_id: &str, namespace: &str, key: &str, value: &str) {
        let conn = match self.conn.lock() {
            Ok(guard) => guard,
            Err(poisoned) => {
                error!("[DB] Mutex was poisoned — recovering lock for set");
                poisoned.into_inner()
            }
        };
        if let Err(e) = conn.execute(
            "INSERT OR REPLACE INTO player_data
                (player_id, namespace, key, value, updated_at)
             VALUES (?1, ?2, ?3, ?4, unixepoch())",
            params![player_id, namespace, key, value],
        ) {
            error!("[DB] Set failed for player={} ns={} key={}: {}", player_id, namespace, key, e);
        }
    }

    /// Read a value for a player. Returns None if not found.
    pub fn get(&self, player_id: &str, namespace: &str, key: &str) -> Option<String> {
        let conn = match self.conn.lock() {
            Ok(guard) => guard,
            Err(poisoned) => {
                error!("[DB] Mutex was poisoned — recovering lock for get");
                poisoned.into_inner()
            }
        };
        conn.query_row(
            "SELECT value FROM player_data
             WHERE player_id = ?1 AND namespace = ?2 AND key = ?3",
            params![player_id, namespace, key],
            |row| row.get(0),
        )
        .optional()
        .unwrap_or_else(|e| {
            error!("[DB] Get failed for player={} ns={} key={}: {}", player_id, namespace, key, e);
            None
        })
    }
}
