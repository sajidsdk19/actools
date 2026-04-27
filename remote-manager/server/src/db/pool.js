/**
 * SQLite adapter using Node.js built-in `node:sqlite` (Node 22.5+).
 * No native compilation required.
 *
 * Exposes the same `pool.query(sql, params)` interface used throughout the server.
 * DB file: server/data/ac_manager.db
 */
const { DatabaseSync } = require('node:sqlite');
const path = require('path');
const fs   = require('fs');
const logger = require('../utils/logger');

const DATA_DIR = path.join(__dirname, '..', '..', 'data');
const DB_FILE  = path.join(DATA_DIR, 'ac_manager.db');

let db = null;

function getDb() {
  if (!db) throw new Error('Database not initialised — call connectDb() first');
  return db;
}

/** Translate $1, $2 … → ? for SQLite */
function translate(sql) {
  return sql.replace(/\$\d+/g, '?');
}

/** Naive table name extractor for RETURNING emulation */
function extractTable(sql) {
  const m = sql.match(/(?:INSERT\s+INTO|UPDATE)\s+"?(\w+)"?/i);
  return m ? m[1] : null;
}

/**
 * pool.query(sql, params) → Promise<{ rows: [] }>
 */
async function query(sql, params = []) {
  const d   = getDb();
  const tsql = translate(sql);
  const upper = sql.trim().toUpperCase();

  try {
    if (upper.startsWith('SELECT') || upper.startsWith('WITH')) {
      const stmt = d.prepare(tsql);
      const rows = stmt.all(...params);
      return { rows };
    }

    if (upper.includes('RETURNING')) {
      // Strip RETURNING, run the write, fetch back by last insert rowid
      const withoutReturning = tsql.replace(/RETURNING[\s\S]+$/i, '').trim();
      const stmt = d.prepare(withoutReturning);
      const info = stmt.run(...params);

      const table = extractTable(sql);
      if (table && info.lastInsertRowid) {
        const row = d.prepare(`SELECT * FROM "${table}" WHERE rowid = ?`).get(info.lastInsertRowid);
        return { rows: row ? [row] : [] };
      }
      return { rows: [] };
    }

    // Plain INSERT / UPDATE / DELETE
    d.prepare(tsql).run(...params);
    return { rows: [] };

  } catch (e) {
    logger.error('[DB] Query error: ' + e.message + '\nSQL: ' + sql);
    throw e;
  }
}

function migrate() {
  db.exec(`
    CREATE TABLE IF NOT EXISTS users (
      id            TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
      email         TEXT UNIQUE NOT NULL,
      password_hash TEXT NOT NULL,
      role          TEXT NOT NULL DEFAULT 'operator',
      created_at    TEXT NOT NULL DEFAULT (datetime('now'))
    );

    CREATE TABLE IF NOT EXISTS devices (
      id            TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
      machine_name  TEXT UNIQUE NOT NULL,
      display_name  TEXT,
      token         TEXT UNIQUE NOT NULL,
      ac_root       TEXT,
      status        TEXT NOT NULL DEFAULT 'offline',
      last_seen     TEXT,
      registered_at TEXT NOT NULL DEFAULT (datetime('now'))
    );

    CREATE TABLE IF NOT EXISTS sessions (
      id                      TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
      device_id               TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      car_id                  TEXT NOT NULL,
      track_id                TEXT NOT NULL,
      track_layout            TEXT,
      mode                    TEXT NOT NULL DEFAULT 'Practice',
      easy_assists            INTEGER NOT NULL DEFAULT 0,
      configured_duration_min INTEGER NOT NULL,
      start_time              TEXT,
      end_time                TEXT,
      actual_duration_min     REAL,
      timer_ended             INTEGER NOT NULL DEFAULT 0,
      player_exited_early     INTEGER NOT NULL DEFAULT 0,
      status                  TEXT NOT NULL DEFAULT 'pending',
      created_at              TEXT NOT NULL DEFAULT (datetime('now'))
    );

    CREATE INDEX IF NOT EXISTS idx_sessions_device ON sessions(device_id);
    CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions(status);

    CREATE TABLE IF NOT EXISTS daily_reports (
      id             TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
      report_date    TEXT UNIQUE NOT NULL,
      total_sessions INTEGER NOT NULL DEFAULT 0,
      total_minutes  REAL    NOT NULL DEFAULT 0,
      per_device     TEXT,
      generated_at   TEXT NOT NULL DEFAULT (datetime('now'))
    );
  `);
  logger.info('[DB] SQLite migrations complete');
}

async function connectDb() {
  fs.mkdirSync(DATA_DIR, { recursive: true });
  db = new DatabaseSync(DB_FILE);
  logger.info('[DB] SQLite connected: ' + DB_FILE);
  migrate();
}

const pool = { query };
module.exports = { pool, connectDb };
