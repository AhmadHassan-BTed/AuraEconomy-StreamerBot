# Advanced Pure Economy System — Implementation Guide

**Streamer.bot v1.0.4 · SQLite WAL · OBS Browser Source**

---

## 0. Prerequisites

| Requirement           | Notes                                          |
| --------------------- | ---------------------------------------------- |
| Streamer.bot v1.0.4   | Download from streamer.bot                     |
| SQLite for .NET       | `System.Data.SQLite` — already bundled with SB |
| Newtonsoft.Json       | Already bundled with SB                        |
| OBS Studio            | Browser Source support                         |
| DB Browser for SQLite | (optional) for manual inspection               |

---

## 1. Folder Structure — Create These First

```
C:\StreamerBot\
│
├── Data\
│   ├── economy.db          ← created automatically by schema script
│   ├── Logs\               ← log files written here daily
│   └── icons\              ← your SVG rank icons (see step 3)
│
└── Overlay\
    └── Overlay.html        ← copy from deliverable
```

Create the folders manually or run in PowerShell:

```powershell
New-Item -ItemType Directory -Force "C:\StreamerBot\Data\Logs"
New-Item -ItemType Directory -Force "C:\StreamerBot\Data\icons"
New-Item -ItemType Directory -Force "C:\StreamerBot\Overlay"
```

---

## 2. Initialise the Database

1. Download **DB Browser for SQLite** (free): https://sqlitebrowser.org/
2. Open it → **New Database** → save as `C:\StreamerBot\Data\economy.db`
3. Click **Execute SQL** tab
4. Paste the entire contents of `schema.sql`
5. Click ▶ **Run**
6. You should see: `Query executed successfully` with no errors
7. **File → Write Changes** then close DB Browser

> The `PRAGMA journal_mode=WAL;` line is the first thing executed — this is what enables the Minecraft plugin to read simultaneously without locking.

---

## 3. Place Rank Icons

Copy your SVG files into `C:\StreamerBot\Data\icons\` with **exactly** these filenames (all lowercase):

```
wood.svg
bronze.svg
silver.svg
gold.svg
platinum.svg
emerald.svg
diamond.svg
master.svg
grandmaster.svg
eternal.svg
```

The overlay loads them via the `rankIcon` field in the WebSocket payload (e.g. `/icons/wood.svg`). Since OBS Browser Source runs locally, you must configure the **Local File** base path in OBS (see step 7).

---

## 4. Enable the WebSocket Server in Streamer.bot

1. Open Streamer.bot
2. Go to **Servers/Clients → WebSocket Server**
3. Check **"Enabled"**
4. Set **Port** to `8080` (or match what's in `Overlay.html` → `WS_URL`)
5. Leave **Endpoint** as `/`
6. Click **Save**

---

## 5. Create Streamer.bot Actions

For each action below, go to **Actions** tab → **+** (Add Action) → name it → add a **Sub-Action** of type **"Execute C# Code"**.

Inside the code editor, paste:

1. The entire **Shared Helpers** block (everything above `ACTION 1`)
2. Then **only** the `CPHInline` class for that specific action (uncomment the block you need)

### Action List

| #   | Action Name               | Trigger                                   | Which CPHInline block    |
| --- | ------------------------- | ----------------------------------------- | ------------------------ |
| 1   | `Economy - Watch Time`    | Timer (60s)                               | ACTION 1 — Watch Time    |
| 2   | `Economy - Open Bet`      | Command `!openbet` (Broadcaster only)     | ACTION 2 — Open Bet      |
| 3   | `Economy - Place Bet`     | Command `!bet` (Everyone)                 | ACTION 3 — Place Bet     |
| 4   | `Economy - Lock Bet`      | Command `!lockbet` (Broadcaster only)     | ACTION 4 — Lock Bet      |
| 5   | `Economy - Resolve Bet`   | Command `!resolvebet` (Broadcaster only)  | ACTION 5 — Resolve Bet   |
| 6   | `Economy - Annuncio`      | Command `!annuncio` (Everyone)            | ACTION 6 — Annuncio      |
| 7   | `Economy - Chat Payload`  | Event: Chat Message                       | ACTION 7 — Chat Payload  |
| 8   | `Economy - Eternal Check` | Timer (15 min)                            | ACTION 8 — Eternal Check |
| 9   | `Economy - Season Reset`  | Command `!seasonreset` (Broadcaster only) | ACTION 9 — Season Reset  |
| 10  | `Economy - Balance`       | Command `!balance` / `!points` (Everyone) | ACTION 10 — Balance      |

### How to create a 60-second timer (Action 1)

1. Go to **Timers** tab → **+**
2. Name it `Watch Time Timer`
3. Set interval to **60 seconds**
4. Assign action: `Economy - Watch Time`
5. Enable **"Fire on Start"**: No (to avoid firing while stream is offline)

### How to create a 15-minute timer (Action 8)

Same as above but interval = **900 seconds**, action = `Economy - Eternal Check`.

### How to set up the Chat Payload trigger (Action 7)

1. Open action `Economy - Chat Payload`
2. In the **Triggers** panel → **+** → **Twitch** → **Chat Message**
3. Leave it as **Any message** (no filter)
4. This fires for every single chat message and pushes the overlay payload

### How to set up commands

For each command action:

1. Go to **Commands** tab → **+**
2. Set the **Command** field to e.g. `!bet`
3. Set **Action** to the corresponding economy action
4. For broadcaster-only commands: set **Permission** → **Broadcaster**
5. For everyone: set **Permission** → **Everyone**

#### Command argument mapping

| Command                          | `input0`   | `input1` | `rawInput`              |
| -------------------------------- | ---------- | -------- | ----------------------- |
| `!openbet <title> \| <a> \| <b>` | —          | —        | full text after command |
| `!bet <a\|b> <amount>`           | `a` or `b` | amount   | —                       |
| `!resolvebet <a\|b>`             | `a` or `b` | —        | —                       |
| `!annuncio <message>`            | —          | —        | full text after command |

In Streamer.bot v1.0.4, `%rawInput%` is the full message after the command trigger. `%input0%` and `%input1%` are space-split tokens. These are automatically available in `args` via `CPH.TryGetArg()`.

---

## 6. Compile / Test Each Action

After pasting code into the editor:

1. Click **"Compile"** — it must say `Compiled Successfully` with **zero errors**
2. Click **"Execute"** once to test (or trigger the chat command in your test channel)
3. Check `C:\StreamerBot\Data\Logs\economy_YYYYMMDD.log` for output

---

## 7. Set Up the OBS Browser Source

1. Copy `Overlay.html` to `C:\StreamerBot\Overlay\Overlay.html`
2. In OBS: **Sources** → **+** → **Browser**
3. Check **"Local File"**
4. Browse to `C:\StreamerBot\Overlay\Overlay.html`
5. Set **Width**: 1920, **Height**: 1080
6. Check **"Refresh browser when scene becomes active"**
7. Under **Custom CSS**: leave blank (styles are already inline)

> **Important:** Because OBS Browser Source uses a local file path, the `/icons/wood.svg` URL in the payload won't resolve automatically. Two options:
>
> - **Option A (recommended):** Place `Overlay.html` directly inside `C:\StreamerBot\Data\` and set `icons\` as a sibling folder. Use a relative path in the JS: change `rankIcon` construction in `EconomyConfig` from `/icons/` to `icons/`.
> - **Option B:** Use `http://localhost/` with a tiny local HTTP server (Python: `python -m http.server 8081 --directory C:\StreamerBot\Data`), and set icon paths as `http://localhost:8081/icons/wood.svg`.

---

## 8. Verify the Full Loop

1. Start Streamer.bot + connect to Twitch
2. Open OBS with the Browser Source visible
3. Type any message in your Twitch chat
4. The overlay should show the message with rank icon and glow
5. Check the log file — you should see `[INFO] [ChatPayload]` entries
6. Run `!balance` in chat — bot should reply with your points

---

## 9. Adjusting Thresholds & Constants

All tunable values are in `EconomyConfig` (at the top of `EconomySystem.cs`):

| Constant            | What it controls                                                 |
| ------------------- | ---------------------------------------------------------------- |
| `WATCH_TIME_POINTS` | Points per minute of watch time (default: 1)                     |
| `ANNUNCIO_COST`     | Points cost of `!annuncio` (default: 150)                        |
| `BET_PAYOUT_MULT`   | Winner payout multiplier (default: 2 = 2×)                       |
| `ETERNAL_MIN_PTS`   | Minimum seasonal points to qualify for Eternal (default: 35,000) |
| `ETERNAL_TOP_N`     | How many top positions get Eternal (default: 3)                  |
| `RankThresholds[]`  | Array of point thresholds per rank (index 0–8)                   |
| `MaxBets[]`         | Max bet amount per rank (index-aligned with ranks)               |
| `RankColors[]`      | Hex colour used for glow and icons                               |
| `DB_PATH`           | Change if you move the database file                             |
| `LOG_DIR`           | Change if you want logs elsewhere                                |

After editing `EconomyConfig`, re-paste the updated Shared Helpers block into **every** action and recompile each one.

---

## 10. Seasonal Reset Procedure

Run `!seasonreset` in chat while the broadcaster account is live. This:

1. Closes the current season (sets `end_date`)
2. Creates a new season row
3. Inserts blank `seasonal_stats` rows for all users in the new season
4. Broadcasts a `season_reset` event to the overlay
5. Posts confirmation in chat

**What is preserved:** `lifetime_points`, `lifetime_wins`, `lifetime_losses`, `lifetime_total_bets`, `lifetime_points_wagered`, `lifetime_peak_rank_id`

**What resets:** `seasonal_points`, `rank_id`, `rank_change` (in `seasonal_stats` for the new season)

---

## 11. Troubleshooting

| Symptom                                         | Fix                                                                                                                            |
| ----------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| `Compiled Successfully` but action does nothing | Check that `CPH.TryGetArg` key names match SB's actual arg keys. Print all args with a test action.                            |
| Log file not created                            | Verify `C:\StreamerBot\Data\Logs\` exists and the SB process has write permission                                              |
| WebSocket not connecting                        | Ensure WS server is enabled in SB (Step 4) and port 8080 is not blocked by firewall                                            |
| Icons not showing                               | Verify filenames are lowercase `.svg` and the path in OBS resolves (see Step 7 options)                                        |
| Database locked error                           | Ensure `journal_mode=WAL` was set when schema was created (check with `PRAGMA journal_mode;` in DB Browser)                    |
| `No 'users' arg found` in Watch Time log        | The timer trigger in SB v1.0.4 must have "Present Viewers" action configured — check the timer sub-action passes the user list |
| Eternal rank not updating                       | Check the 15-min timer is enabled and the `v_eternal_candidates` view exists in the DB                                         |

---

## 12. Minecraft Plugin Integration

The plugin simply opens `economy.db` in **read-only WAL mode**:

```java
// Java example
String url = "jdbc:sqlite:C:/StreamerBot/Data/economy.db";
Properties props = new Properties();
props.setProperty("open_mode", "1"); // SQLITE_OPEN_READONLY
Connection conn = DriverManager.getConnection(url, props);
```

It can safely `SELECT` from any view (`v_season_leaderboard`, `v_win_rate_leaderboard`, `v_eternal_candidates`) or table without blocking Streamer.bot writes, because WAL mode allows unlimited concurrent readers.
