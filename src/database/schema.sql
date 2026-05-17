-- =============================================================================
-- ADVANCED PURE ECONOMY SYSTEM — SQLite Schema v1.0
-- Streamer.bot v1.0.4 + Minecraft Plugin concurrent-read safe (WAL mode)
-- Run this script ONCE to initialise the database before starting the bot.
-- =============================================================================

PRAGMA journal_mode=WAL;          -- concurrent reads (Minecraft) + SB writes
PRAGMA synchronous=NORMAL;        -- safe with WAL; faster than FULL
PRAGMA foreign_keys=ON;
PRAGMA cache_size=-8000;          -- 8 MB page cache

-- =============================================================================
-- SEASONS — tracks monthly resets; never deleted
-- =============================================================================
CREATE TABLE IF NOT EXISTS seasons (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL,
    start_date  TEXT    NOT NULL DEFAULT (date('now')),
    end_date    TEXT
);

-- Seed the very first season so FKs always have a valid reference
INSERT OR IGNORE INTO seasons (id, name, start_date)
    VALUES (1, 'Season 1', date('now'));

-- =============================================================================
-- USERS — lifetime / historical data — NEVER wiped on seasonal reset
--   rank_id encoding:  0=Wood 1=Bronze 2=Silver 3=Gold 4=Platinum
--                      5=Emerald 6=Diamond 7=Master 8=Grandmaster 9=Eternal
-- =============================================================================
CREATE TABLE IF NOT EXISTS users (
    user_id                  TEXT    PRIMARY KEY,
    username                 TEXT    NOT NULL,
    lifetime_points          INTEGER NOT NULL DEFAULT 0,
    lifetime_peak_rank_id    INTEGER NOT NULL DEFAULT 0,
    lifetime_wins            INTEGER NOT NULL DEFAULT 0,
    lifetime_losses          INTEGER NOT NULL DEFAULT 0,
    lifetime_total_bets      INTEGER NOT NULL DEFAULT 0,
    lifetime_points_wagered  INTEGER NOT NULL DEFAULT 0,
    -- delta from last bet resolution (positive = promoted, negative = demoted)
    rank_change              INTEGER NOT NULL DEFAULT 0,
    created_at               TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at               TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- =============================================================================
-- SEASONAL_STATS — per-season data; points/rank zeroed on monthly reset
-- =============================================================================
CREATE TABLE IF NOT EXISTS seasonal_stats (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id          TEXT    NOT NULL,
    season_id        INTEGER NOT NULL,
    seasonal_points  INTEGER NOT NULL DEFAULT 0,
    rank_id          INTEGER NOT NULL DEFAULT 0,
    -- delta from last bet resolution (positive = promoted, negative = demoted)
    rank_change      INTEGER NOT NULL DEFAULT 0,
    last_bet_at      TEXT,           -- ISO-8601 UTC; used for 5-day activity filter
    FOREIGN KEY (user_id)   REFERENCES users(user_id)  ON DELETE CASCADE,
    FOREIGN KEY (season_id) REFERENCES seasons(id)     ON DELETE CASCADE,
    UNIQUE (user_id, season_id)
);

-- =============================================================================
-- ACTIVE_BETS — one open bet at a time; status lifecycle: open→locked→resolved
-- =============================================================================
CREATE TABLE IF NOT EXISTS active_bets (
    bet_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title           TEXT    NOT NULL,
    outcome_a       TEXT    NOT NULL,   -- e.g. "Win"
    outcome_b       TEXT    NOT NULL,   -- e.g. "Loss"
    status          TEXT    NOT NULL DEFAULT 'open'
                            CHECK (status IN ('open','locked','resolved')),
    winning_outcome TEXT    CHECK (winning_outcome IN ('a','b') OR winning_outcome IS NULL),
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    locked_at       TEXT,
    resolved_at     TEXT
);

-- =============================================================================
-- BET_ENTRIES — individual user wagers; points deducted immediately on entry
-- =============================================================================
CREATE TABLE IF NOT EXISTS bet_entries (
    entry_id        INTEGER PRIMARY KEY AUTOINCREMENT,
    bet_id          INTEGER NOT NULL,
    user_id         TEXT    NOT NULL,
    username        TEXT    NOT NULL,
    amount          INTEGER NOT NULL CHECK (amount > 0),
    outcome_chosen  TEXT    NOT NULL CHECK (outcome_chosen IN ('a','b')),
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (bet_id)  REFERENCES active_bets(bet_id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(user_id)      ON DELETE CASCADE,
    UNIQUE (bet_id, user_id)   -- one entry per user per bet
);

-- =============================================================================
-- INDEXES
-- =============================================================================
CREATE INDEX IF NOT EXISTS idx_seasonal_user_season
    ON seasonal_stats (user_id, season_id);

CREATE INDEX IF NOT EXISTS idx_seasonal_season_points
    ON seasonal_stats (season_id, seasonal_points DESC);

CREATE INDEX IF NOT EXISTS idx_seasonal_last_bet
    ON seasonal_stats (season_id, last_bet_at);

CREATE INDEX IF NOT EXISTS idx_bet_entries_bet
    ON bet_entries (bet_id, outcome_chosen);

CREATE INDEX IF NOT EXISTS idx_bet_entries_user
    ON bet_entries (user_id);

CREATE INDEX IF NOT EXISTS idx_users_lifetime_points
    ON users (lifetime_points DESC);

CREATE INDEX IF NOT EXISTS idx_users_lifetime_peak
    ON users (lifetime_peak_rank_id DESC);

-- =============================================================================
-- VIEWS — convenience; Minecraft plugin can read these directly
-- =============================================================================

-- Full current-season leaderboard
CREATE VIEW IF NOT EXISTS v_season_leaderboard AS
SELECT
    u.user_id,
    u.username,
    ss.seasonal_points,
    ss.rank_id,
    ss.rank_change              AS seasonal_rank_change,
    u.lifetime_peak_rank_id,
    u.rank_change               AS lifetime_rank_change,
    u.lifetime_points,
    u.lifetime_wins,
    u.lifetime_total_bets,
    u.lifetime_points_wagered,
    ss.last_bet_at
FROM users u
JOIN seasonal_stats ss
    ON  u.user_id   = ss.user_id
    AND ss.season_id = (SELECT id FROM seasons ORDER BY id DESC LIMIT 1)
ORDER BY ss.seasonal_points DESC;

-- Eternal candidates: RANK() so ties share the same slot
-- All users at seasonal_points >= 35000 whose RANK() position <= 3
CREATE VIEW IF NOT EXISTS v_eternal_candidates AS
WITH ranked AS (
    SELECT
        user_id,
        username,
        seasonal_points,
        RANK() OVER (ORDER BY seasonal_points DESC) AS pts_rank
    FROM v_season_leaderboard
    WHERE seasonal_points >= 35000
)
SELECT user_id, username, seasonal_points, pts_rank
FROM ranked
WHERE pts_rank <= 3;

-- =============================================================================
-- WIN RATE LEADERBOARD QUERY (also exposed as a view for Minecraft plugin)
-- Rules:  >= 10 total bets  AND  >= 400 points wagered
--         AND last_bet_at within 5 days
-- =============================================================================
CREATE VIEW IF NOT EXISTS v_win_rate_leaderboard AS
SELECT
    u.user_id,
    u.username,
    u.lifetime_wins,
    u.lifetime_losses,
    u.lifetime_total_bets,
    u.lifetime_points_wagered,
    ROUND(
        CAST(u.lifetime_wins AS REAL)
        / NULLIF(u.lifetime_total_bets, 0)
        * 100.0,
        2
    ) AS win_rate_pct,
    ss.rank_id,
    ss.seasonal_points,
    ss.last_bet_at
FROM users u
JOIN seasonal_stats ss
    ON  u.user_id   = ss.user_id
    AND ss.season_id = (SELECT id FROM seasons ORDER BY id DESC LIMIT 1)
WHERE
    u.lifetime_total_bets       >= 10
    AND u.lifetime_points_wagered >= 400
    AND ss.last_bet_at            >= datetime('now', '-5 days')
ORDER BY win_rate_pct DESC
LIMIT 10;