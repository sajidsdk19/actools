-- ============================================================
--  AC Remote Manager — PostgreSQL Schema
--  Run: psql -U <user> -d ac_manager -f schema.sql
-- ============================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ── Users (dashboard / mobile app logins) ────────────────────
CREATE TABLE IF NOT EXISTS users (
    id           UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    email        TEXT        UNIQUE NOT NULL,
    password_hash TEXT       NOT NULL,
    role         TEXT        NOT NULL DEFAULT 'operator',  -- admin | operator
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Devices (registered gaming PCs) ──────────────────────────
CREATE TABLE IF NOT EXISTS devices (
    id           UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    machine_name TEXT        UNIQUE NOT NULL,   -- from Environment.MachineName / os.hostname()
    display_name TEXT,
    token        TEXT        UNIQUE NOT NULL,   -- shared secret for WebSocket auth
    ac_root      TEXT,                          -- path to AC install on that machine
    status       TEXT        NOT NULL DEFAULT 'offline',  -- online | offline | in_session
    last_seen    TIMESTAMPTZ,
    registered_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Sessions ─────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sessions (
    id                    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id             UUID        NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    car_id                TEXT        NOT NULL,
    track_id              TEXT        NOT NULL,
    track_layout          TEXT,
    mode                  TEXT        NOT NULL DEFAULT 'Practice',
    easy_assists          BOOLEAN     NOT NULL DEFAULT FALSE,
    configured_duration_min INT       NOT NULL,
    start_time            TIMESTAMPTZ,
    end_time              TIMESTAMPTZ,
    actual_duration_min   NUMERIC(8,2),
    timer_ended           BOOLEAN     NOT NULL DEFAULT FALSE,
    player_exited_early   BOOLEAN     NOT NULL DEFAULT FALSE,
    status                TEXT        NOT NULL DEFAULT 'pending',  -- pending | running | completed | error
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sessions_device   ON sessions(device_id);
CREATE INDEX IF NOT EXISTS idx_sessions_status   ON sessions(status);
CREATE INDEX IF NOT EXISTS idx_sessions_start    ON sessions(start_time);

-- ── Daily Reports ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS daily_reports (
    id           UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    report_date  DATE        UNIQUE NOT NULL,
    total_sessions INT       NOT NULL DEFAULT 0,
    total_minutes NUMERIC(10,2) NOT NULL DEFAULT 0,
    per_device   JSONB,      -- { "device_id": { sessions, minutes } }
    generated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
