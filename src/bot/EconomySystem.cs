// =============================================================================
// ADVANCED PURE ECONOMY SYSTEM — Streamer.bot v1.0.4
// =============================================================================
// HOW TO USE:
//   Each "ACTION BLOCK" below is a SEPARATE Streamer.bot Action.
//   Copy only the CPHInline class (and the shared helpers above it) into each
//   action's "Execute C# Code" editor.
//   The SHARED HELPERS section must be pasted at the TOP of EVERY action.
// =============================================================================

// ─────────────────────────────────────────────────────────────────────────────
//  SHARED HELPERS  ◄── paste this block at the top of EVERY action
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Data;
using System.Data.SQLite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ── Configuration ─────────────────────────────────────────────────────────────
public static class EconomyConfig
{
    // ★ Adjust paths here if you move the database or log folder
    public const string DB_PATH  = @"C:\StreamerBot\Data\economy.db";
    public const string LOG_DIR  = @"C:\StreamerBot\Data\Logs";
    public const string ICON_DIR = @"C:\StreamerBot\Data\icons";   // local overlay path

    // Connection string — pooling keeps connections warm without leaking handles
    public static string ConnStr =>
        $"Data Source={DB_PATH};Version=3;Pooling=True;Max Pool Size=5;" +
        "Journal Mode=WAL;Synchronous=Normal;";

    // ★ Economy constants — adjust here centrally
    public const int WATCH_TIME_POINTS = 1;        // pts per minute
    public const int ANNUNCIO_COST     = 150;      // pts deducted by !annuncio
    public const int BET_PAYOUT_MULT   = 2;        // winner multiplier (2.0x)
    public const int ETERNAL_MIN_PTS   = 35000;    // floor to qualify for Eternal
    public const int ETERNAL_TOP_N     = 3;        // top N positions get Eternal

    // ★ Rank thresholds — index 0=Wood … 8=Grandmaster (9=Eternal is dynamic)
    public static readonly int[] RankThresholds = { 0, 500, 1500, 3500, 7000, 12000, 20000, 35000, 60000 };
    public static readonly string[] RankNames    = { "Wood","Bronze","Silver","Gold","Platinum","Emerald","Diamond","Master","Grandmaster","Eternal" };

    // ★ Max bet per rank (index-aligned with RankNames)
    public static readonly int[] MaxBets = { 25, 50, 100, 200, 400, 800, 1500, 3000, 5000, 10000 };

    // ★ Rank colours for overlay Legacy Aura
    public static readonly string[] RankColors = {
        "#8B6914", // Wood
        "#CD7F32", // Bronze
        "#C0C0C0", // Silver
        "#FFD700", // Gold
        "#00E5FF", // Platinum
        "#00E676", // Emerald
        "#40C4FF", // Diamond
        "#CE93D8", // Master
        "#FF6D00", // Grandmaster
        "#FFFFFF"  // Eternal
    };

    // WebSocket server address (Streamer.bot built-in WS server)
    public const string WS_BROADCAST = "ws://127.0.0.1:8080/";
}

// ── Logger ────────────────────────────────────────────────────────────────────
public static class EconomyLogger
{
    private static readonly object _fileLock = new object();
    private static string _file;
    public  static string InitError { get; private set; }

    static EconomyLogger()
    {
        try
        {
            if (!Directory.Exists(EconomyConfig.LOG_DIR))
                Directory.CreateDirectory(EconomyConfig.LOG_DIR);

            _file = Path.Combine(
                EconomyConfig.LOG_DIR,
                $"economy_{DateTime.Now:yyyyMMdd}.log");

            // Append startup line; creates file if not exists
            Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] [----] Economy logger initialised. DB={EconomyConfig.DB_PATH}");
        }
        catch (Exception ex) { InitError = ex.Message; }
    }

    public static void Info (string comp, string msg, string tid) => Append(Format("INFO ", comp, msg, tid));
    public static void Debug(string comp, string msg, string tid) => Append(Format("DEBUG", comp, msg, tid));
    public static void Warn (string comp, string msg, string tid) => Append(Format("WARN ", comp, msg, tid));
    public static void Error(string comp, string msg, Exception ex, string tid)
        => Append(Format("ERROR", comp, $"{msg} | {ex?.GetType().Name}: {ex?.Message}\n  {ex?.StackTrace}", tid));
    public static void Fatal(string comp, string msg, Exception ex, string tid)
        => Append(Format("FATAL", comp, $"{msg} | {ex?.GetType().Name}: {ex?.Message}\n  {ex?.StackTrace}", tid));

    private static string Format(string lvl, string comp, string msg, string tid)
        => $"[{DateTime.Now:HH:mm:ss.fff}] [{lvl}] [{comp,-14}] [{tid}] {msg}";

    private static void Append(string line)
    {
        if (_file == null) return;
        lock (_fileLock)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine); }
            catch { /* cannot log the log failure */ }
        }
    }
}

// ── Database helpers ──────────────────────────────────────────────────────────
public static class EconomyDb
{
    // Open a connection with WAL already set via connection string
    public static SQLiteConnection Open()
    {
        var c = new SQLiteConnection(EconomyConfig.ConnStr);
        c.Open();
        using (var cmd = new SQLiteCommand("PRAGMA foreign_keys=ON;", c))
            cmd.ExecuteNonQuery();
        return c;
    }

    // Ensure the user row + current-season row exist (INSERT OR IGNORE)
    public static void EnsureUser(SQLiteConnection c, string userId, string username, int seasonId, string tid)
    {
        using (var cmd = new SQLiteCommand(c))
        {
            cmd.CommandText = @"
                INSERT OR IGNORE INTO users (user_id, username) VALUES (@uid, @uname);
                UPDATE users SET username=@uname, updated_at=datetime('now') WHERE user_id=@uid;
                INSERT OR IGNORE INTO seasonal_stats (user_id, season_id) VALUES (@uid, @sid);";
            cmd.Parameters.AddWithValue("@uid",   userId);
            cmd.Parameters.AddWithValue("@uname", username);
            cmd.Parameters.AddWithValue("@sid",   seasonId);
            cmd.ExecuteNonQuery();
        }
        EconomyLogger.Debug("EnsureUser", $"uid={userId} uname={username} season={seasonId}", tid);
    }

    // Return current active season id
    public static int GetCurrentSeasonId(SQLiteConnection c)
    {
        using (var cmd = new SQLiteCommand(
            "SELECT id FROM seasons ORDER BY id DESC LIMIT 1;", c))
        {
            var r = cmd.ExecuteScalar();
            return r == null ? 1 : Convert.ToInt32(r);
        }
    }

    // Read seasonal_points for one user
    public static int GetSeasonalPoints(SQLiteConnection c, string userId, int seasonId)
    {
        using (var cmd = new SQLiteCommand(
            "SELECT seasonal_points FROM seasonal_stats WHERE user_id=@uid AND season_id=@sid;", c))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@sid", seasonId);
            var r = cmd.ExecuteScalar();
            return r == null ? 0 : Convert.ToInt32(r);
        }
    }

    // Read lifetime_points for one user
    public static int GetLifetimePoints(SQLiteConnection c, string userId)
    {
        using (var cmd = new SQLiteCommand(
            "SELECT lifetime_points FROM users WHERE user_id=@uid;", c))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            var r = cmd.ExecuteScalar();
            return r == null ? 0 : Convert.ToInt32(r);
        }
    }

    // Compute rank id from seasonal points (0-8; Eternal handled separately)
    public static int ComputeRankId(int points)
    {
        int rankId = 0;
        for (int i = EconomyConfig.RankThresholds.Length - 1; i >= 0; i--)
        {
            if (points >= EconomyConfig.RankThresholds[i]) { rankId = i; break; }
        }
        return rankId;
    }

    // Recalculate rank for a single user and write delta columns
    // Returns (newRankId, oldRankId)
    public static (int newRank, int oldRank) RecalcRank(
        SQLiteConnection c, string userId, int seasonId, string tid)
    {
        int pts    = GetSeasonalPoints(c, userId, seasonId);
        int newRid = ComputeRankId(pts);

        int oldRid;
        using (var cmd = new SQLiteCommand(
            "SELECT rank_id FROM seasonal_stats WHERE user_id=@uid AND season_id=@sid;", c))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@sid", seasonId);
            var r = cmd.ExecuteScalar();
            oldRid = r == null ? 0 : Convert.ToInt32(r);
        }

        int delta = newRid - oldRid;

        using (var cmd = new SQLiteCommand(c))
        {
            cmd.CommandText = @"
                UPDATE seasonal_stats
                   SET rank_id=@rid, rank_change=@delta
                 WHERE user_id=@uid AND season_id=@sid;
                UPDATE users
                   SET rank_change=@delta,
                       lifetime_peak_rank_id = MAX(lifetime_peak_rank_id, @rid),
                       updated_at=datetime('now')
                 WHERE user_id=@uid;";
            cmd.Parameters.AddWithValue("@rid",   newRid);
            cmd.Parameters.AddWithValue("@delta", delta);
            cmd.Parameters.AddWithValue("@uid",   userId);
            cmd.Parameters.AddWithValue("@sid",   seasonId);
            cmd.ExecuteNonQuery();
        }

        EconomyLogger.Debug("RecalcRank",
            $"uid={userId} pts={pts} old={oldRid}({EconomyConfig.RankNames[oldRid]}) " +
            $"new={newRid}({EconomyConfig.RankNames[newRid]}) delta={delta}", tid);

        return (newRid, oldRid);
    }

    // Adjust seasonal AND lifetime points atomically
    public static bool AdjustPoints(
        SQLiteConnection c, string userId, int seasonId, int delta, string tid)
    {
        try
        {
            using (var cmd = new SQLiteCommand(c))
            {
                cmd.CommandText = @"
                    UPDATE seasonal_stats
                       SET seasonal_points = MAX(0, seasonal_points + @d),
                           updated_at      = datetime('now')
                     WHERE user_id=@uid AND season_id=@sid;
                    UPDATE users
                       SET lifetime_points = MAX(0, lifetime_points + @d),
                           updated_at      = datetime('now')
                     WHERE user_id=@uid;";
                cmd.Parameters.AddWithValue("@d",   delta);
                cmd.Parameters.AddWithValue("@uid", userId);
                cmd.Parameters.AddWithValue("@sid", seasonId);
                cmd.ExecuteNonQuery();
            }
            EconomyLogger.Debug("AdjustPts", $"uid={userId} delta={delta:+#;-#;0}", tid);
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Error("AdjustPts", "Failed", ex, tid);
            return false;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 1 — WATCH TIME ACCUMULATION
//  Trigger: Streamer.bot Timer (every 1 minute) → "Present Viewers" list
//  The timer action provides a list of present viewers via the args dictionary.
// ─────────────────────────────────────────────────────────────────────────────
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("WatchTime", "ACTION START", tid);

        try
        {
            step = 1;
            // Streamer.bot v1.0.4 exposes present viewers as args["users"]
            // Each item has .UserId / .UserName / .UserLogin properties
            if (!CPH.TryGetArg("users", out List<object> rawUsers) || rawUsers == null)
            {
                EconomyLogger.Warn("WatchTime", "No 'users' arg found — nothing to process.", tid);
                return true; // Not an error; stream may have no viewers
            }

            step = 2;
            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);
                EconomyLogger.Info("WatchTime", $"Season={seasonId} Users={rawUsers.Count}", tid);

                using (var tx = conn.BeginTransaction())
                {
                    step = 3;
                    int processed = 0;
                    foreach (var raw in rawUsers)
                    {
                        if (raw == null) continue;
                        try
                        {
                            // Streamer.bot viewer objects: use dynamic to access properties
                            dynamic viewer  = raw;
                            string  userId  = viewer.UserId?.ToString()  ?? "";
                            string  uname   = viewer.UserName?.ToString() ?? viewer.UserLogin?.ToString() ?? "";

                            if (string.IsNullOrEmpty(userId)) continue;

                            EconomyDb.EnsureUser(conn, userId, uname, seasonId, tid);
                            EconomyDb.AdjustPoints(conn, userId, seasonId, EconomyConfig.WATCH_TIME_POINTS, tid);
                            processed++;
                        }
                        catch (Exception exInner)
                        {
                            EconomyLogger.Error("WatchTime", "Inner loop error on viewer", exInner, tid);
                        }
                    }

                    step = 4;
                    tx.Commit();
                    EconomyLogger.Info("WatchTime", $"Awarded {EconomyConfig.WATCH_TIME_POINTS}pt to {processed} viewers.", tid);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("WatchTime", $"Crash at step {step}", ex, tid);
            return false;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 2 — OPEN BET
//  Trigger: Broadcaster command  !openbet <title> | <outcomeA> | <outcomeB>
//  Example: !openbet Will we win? | Yes | No
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("OpenBet", "ACTION START", tid);

        try
        {
            step = 1;
            CPH.TryGetArg("rawInput", out string rawInput);
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                CPH.SendMessage("Usage: !openbet <title> | <outcomeA> | <outcomeB>");
                return false;
            }

            step = 2;
            string[] parts = rawInput.Split('|');
            if (parts.Length < 3)
            {
                CPH.SendMessage("Usage: !openbet <title> | <outcomeA> | <outcomeB>");
                return false;
            }
            string title = parts[0].Trim();
            string outA  = parts[1].Trim();
            string outB  = parts[2].Trim();

            step = 3;
            using (var conn = EconomyDb.Open())
            {
                // Check no open bet already exists
                using (var chk = new SQLiteCommand(
                    "SELECT COUNT(*) FROM active_bets WHERE status IN ('open','locked');", conn))
                {
                    int existing = Convert.ToInt32(chk.ExecuteScalar());
                    if (existing > 0)
                    {
                        CPH.SendMessage("❌ A bet is already open or locked. Resolve it first.");
                        EconomyLogger.Warn("OpenBet", "Attempted to open bet while one active.", tid);
                        return false;
                    }
                }

                step = 4;
                using (var cmd = new SQLiteCommand(conn))
                {
                    cmd.CommandText = @"
                        INSERT INTO active_bets (title, outcome_a, outcome_b, status)
                        VALUES (@title, @a, @b, 'open');";
                    cmd.Parameters.AddWithValue("@title", title);
                    cmd.Parameters.AddWithValue("@a",     outA);
                    cmd.Parameters.AddWithValue("@b",     outB);
                    cmd.ExecuteNonQuery();
                }

                long betId;
                using (var cmd = new SQLiteCommand("SELECT last_insert_rowid();", conn))
                    betId = (long)cmd.ExecuteScalar();

                EconomyLogger.Info("OpenBet", $"Bet #{betId} opened: '{title}' ({outA} vs {outB})", tid);
                CPH.SendMessage($"🎲 Bet OPEN! #{betId}: \"{title}\" — [A] {outA}  |  [B] {outB}  — !bet a/b <amount>");
            }

            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("OpenBet", $"Crash at step {step}", ex, tid);
            CPH.SendMessage("❌ Internal error opening bet. Check logs.");
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 3 — PLACE BET
//  Trigger: Chat command  !bet <a|b> <amount>
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("PlaceBet", "ACTION START", tid);

        try
        {
            step = 1;
            CPH.TryGetArg("userId",   out string userId);
            CPH.TryGetArg("userName", out string username);
            CPH.TryGetArg("input0",   out string outcomeRaw);   // "a" or "b"
            CPH.TryGetArg("input1",   out string amountRaw);

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(outcomeRaw) || string.IsNullOrEmpty(amountRaw))
            {
                CPH.SendMessage($"@{username} Usage: !bet <a|b> <amount>");
                return false;
            }

            step = 2;
            string outcome = outcomeRaw.Trim().ToLower();
            if (outcome != "a" && outcome != "b")
            {
                CPH.SendMessage($"@{username} Choose [a] or [b].");
                return false;
            }

            if (!int.TryParse(amountRaw.Trim(), out int amount) || amount <= 0)
            {
                CPH.SendMessage($"@{username} Amount must be a positive number.");
                return false;
            }

            step = 3;
            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);
                EconomyDb.EnsureUser(conn, userId, username, seasonId, tid);

                // Fetch open bet
                int  betId   = 0;
                string outA  = "", outB = "";
                using (var cmd = new SQLiteCommand(
                    "SELECT bet_id, outcome_a, outcome_b FROM active_bets WHERE status='open' LIMIT 1;", conn))
                using (var rdr = cmd.ExecuteReader())
                {
                    if (!rdr.Read())
                    {
                        CPH.SendMessage($"@{username} No bet is currently open.");
                        return false;
                    }
                    betId = rdr.GetInt32(0);
                    outA  = rdr.GetString(1);
                    outB  = rdr.GetString(2);
                }

                step = 4;
                // Enforce max bet by rank
                int pts    = EconomyDb.GetSeasonalPoints(conn, userId, seasonId);
                int rankId = EconomyDb.ComputeRankId(pts);
                int maxBet = EconomyConfig.MaxBets[rankId];

                if (amount > maxBet)
                {
                    CPH.SendMessage($"@{username} Max bet for {EconomyConfig.RankNames[rankId]} rank is {maxBet} pts.");
                    return false;
                }
                if (amount > pts)
                {
                    CPH.SendMessage($"@{username} Not enough points (you have {pts}).");
                    return false;
                }

                step = 5;
                using (var tx = conn.BeginTransaction())
                {
                    // Deduct points immediately
                    EconomyDb.AdjustPoints(conn, userId, seasonId, -amount, tid);

                    // Record entry (UNIQUE constraint prevents double-bet)
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            INSERT INTO bet_entries (bet_id, user_id, username, amount, outcome_chosen)
                            VALUES (@bid, @uid, @uname, @amt, @out);";
                        cmd.Parameters.AddWithValue("@bid",   betId);
                        cmd.Parameters.AddWithValue("@uid",   userId);
                        cmd.Parameters.AddWithValue("@uname", username);
                        cmd.Parameters.AddWithValue("@amt",   amount);
                        cmd.Parameters.AddWithValue("@out",   outcome);
                        cmd.ExecuteNonQuery();
                    }

                    // Update last_bet_at
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            UPDATE seasonal_stats
                               SET last_bet_at = datetime('now')
                             WHERE user_id=@uid AND season_id=@sid;";
                        cmd.Parameters.AddWithValue("@uid", userId);
                        cmd.Parameters.AddWithValue("@sid", seasonId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }

                string chosenLabel = outcome == "a" ? outA : outB;
                int    remaining   = EconomyDb.GetSeasonalPoints(conn, userId, seasonId);
                EconomyLogger.Info("PlaceBet",
                    $"uid={userId} bet {amount} on [{outcome}]{chosenLabel} in bet#{betId}. Remaining={remaining}", tid);
                CPH.SendMessage(
                    $"@{username} ✅ Bet placed! {amount} pts on [{chosenLabel}]. Balance: {remaining} pts.");
            }

            return true;
        }
        catch (SQLiteException sqlex) when (sqlex.Message.Contains("UNIQUE"))
        {
            EconomyLogger.Warn("PlaceBet", $"uid={0} tried to double-bet.", tid);
            CPH.TryGetArg("userName", out string un);
            CPH.SendMessage($"@{un} You already have a bet on this round.");
            return false;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("PlaceBet", $"Crash at step {step}", ex, tid);
            CPH.SendMessage("❌ Internal error. Check logs.");
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 4 — LOCK BET
//  Trigger: Broadcaster command  !lockbet
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid = Guid.NewGuid().ToString("N").Substring(0, 8);
        EconomyLogger.Info("LockBet", "ACTION START", tid);
        try
        {
            using (var conn = EconomyDb.Open())
            {
                using (var cmd = new SQLiteCommand(conn))
                {
                    cmd.CommandText = @"
                        UPDATE active_bets
                           SET status='locked', locked_at=datetime('now')
                         WHERE status='open';";
                    int rows = cmd.ExecuteNonQuery();
                    if (rows == 0)
                    {
                        CPH.SendMessage("❌ No open bet to lock.");
                        return false;
                    }
                }
                EconomyLogger.Info("LockBet", "Bet locked.", tid);
                CPH.SendMessage("🔒 Bet is now LOCKED — no more entries!");
            }
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("LockBet", "Crash", ex, tid);
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 5 — RESOLVE BET  (Snapshot → Payout → Recalc → Delta)
//  Trigger: Broadcaster command  !resolvebet <a|b>
//  This is the ONLY place rank recalculation happens.
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("ResolveBet", "ACTION START", tid);

        try
        {
            step = 1;
            CPH.TryGetArg("input0",   out string winningOutcome);  // "a" or "b"
            winningOutcome = (winningOutcome ?? "").Trim().ToLower();
            if (winningOutcome != "a" && winningOutcome != "b")
            {
                CPH.SendMessage("Usage: !resolvebet <a|b>");
                return false;
            }

            step = 2;
            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);

                // Fetch the bet
                int    betId = 0;
                string outA = "", outB = "";
                using (var cmd = new SQLiteCommand(
                    "SELECT bet_id, outcome_a, outcome_b FROM active_bets WHERE status IN ('open','locked') LIMIT 1;", conn))
                using (var rdr = cmd.ExecuteReader())
                {
                    if (!rdr.Read())
                    {
                        CPH.SendMessage("❌ No active bet to resolve.");
                        return false;
                    }
                    betId = rdr.GetInt32(0);
                    outA  = rdr.GetString(1);
                    outB  = rdr.GetString(2);
                }
                string winLabel = winningOutcome == "a" ? outA : outB;
                EconomyLogger.Info("ResolveBet", $"Resolving bet#{betId} winner=[{winningOutcome}]{winLabel}", tid);

                step = 3;
                // Collect all entries for this bet
                var winners = new List<(string uid, string uname, int amount)>();
                var losers  = new List<(string uid, string uname, int amount)>();

                using (var cmd = new SQLiteCommand(
                    "SELECT user_id, username, amount, outcome_chosen FROM bet_entries WHERE bet_id=@bid;", conn))
                {
                    cmd.Parameters.AddWithValue("@bid", betId);
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            string uid   = rdr.GetString(0);
                            string uname = rdr.GetString(1);
                            int    amt   = rdr.GetInt32(2);
                            string oc    = rdr.GetString(3);
                            if (oc == winningOutcome)
                                winners.Add((uid, uname, amt));
                            else
                                losers.Add((uid, uname, amt));
                        }
                    }
                }

                step = 4;
                // SNAPSHOT ranks BEFORE payout
                var preRanks = new Dictionary<string, int>();
                foreach (var (uid, _, _) in winners)
                {
                    int r = EconomyDb.ComputeRankId(EconomyDb.GetSeasonalPoints(conn, uid, seasonId));
                    preRanks[uid] = r;
                }
                foreach (var (uid, _, _) in losers)
                {
                    if (!preRanks.ContainsKey(uid))
                    {
                        int r = EconomyDb.ComputeRankId(EconomyDb.GetSeasonalPoints(conn, uid, seasonId));
                        preRanks[uid] = r;
                    }
                }
                EconomyLogger.Info("ResolveBet", $"Pre-payout snapshot: {preRanks.Count} users recorded.", tid);

                step = 5;
                // Payout
                var rankChanges = new List<object>();
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (uid, uname, amt) in winners)
                    {
                        int payout = amt * EconomyConfig.BET_PAYOUT_MULT;
                        EconomyDb.AdjustPoints(conn, uid, seasonId, payout, tid);

                        // Update lifetime win stats
                        using (var cmd = new SQLiteCommand(conn))
                        {
                            cmd.CommandText = @"
                                UPDATE users
                                   SET lifetime_wins         = lifetime_wins + 1,
                                       lifetime_total_bets   = lifetime_total_bets + 1,
                                       lifetime_points_wagered = lifetime_points_wagered + @amt,
                                       updated_at            = datetime('now')
                                 WHERE user_id=@uid;";
                            cmd.Parameters.AddWithValue("@uid", uid);
                            cmd.Parameters.AddWithValue("@amt", amt);
                            cmd.ExecuteNonQuery();
                        }
                        EconomyLogger.Info("ResolveBet", $"Winner uid={uid} uname={uname} payout={payout}", tid);
                    }

                    foreach (var (uid, uname, amt) in losers)
                    {
                        // Points already deducted on entry; update stats only
                        using (var cmd = new SQLiteCommand(conn))
                        {
                            cmd.CommandText = @"
                                UPDATE users
                                   SET lifetime_losses       = lifetime_losses + 1,
                                       lifetime_total_bets   = lifetime_total_bets + 1,
                                       lifetime_points_wagered = lifetime_points_wagered + @amt,
                                       updated_at            = datetime('now')
                                 WHERE user_id=@uid;";
                            cmd.Parameters.AddWithValue("@uid", uid);
                            cmd.Parameters.AddWithValue("@amt", amt);
                            cmd.ExecuteNonQuery();
                        }
                        EconomyLogger.Info("ResolveBet", $"Loser uid={uid} uname={uname} lost={amt}", tid);
                    }

                    tx.Commit();
                }

                step = 6;
                // RECALCULATE RANKS + compute deltas
                var allUids = new HashSet<string>();
                foreach (var (uid, _, _) in winners) allUids.Add(uid);
                foreach (var (uid, _, _) in losers)  allUids.Add(uid);

                foreach (string uid in allUids)
                {
                    var (newRank, oldRank) = EconomyDb.RecalcRank(conn, uid, seasonId, tid);
                    bool promoted = newRank > oldRank;
                    bool demoted  = newRank < oldRank;

                    // Find uname for overlay payload
                    string uname = "";
                    foreach (var (wu, wn, _) in winners) if (wu == uid) { uname = wn; break; }
                    if (uname == "") foreach (var (lu, ln, _) in losers) if (lu == uid) { uname = ln; break; }

                    rankChanges.Add(new {
                        userId    = uid,
                        username  = uname,
                        oldRank   = EconomyConfig.RankNames[oldRank],
                        newRank   = EconomyConfig.RankNames[newRank],
                        newRankId = newRank,
                        delta     = newRank - oldRank,
                        promoted  = promoted,
                        demoted   = demoted,
                        newRankName = EconomyConfig.RankNames[newRank]
                    });
                }

                step = 7;
                // Mark bet resolved
                using (var cmd = new SQLiteCommand(conn))
                {
                    cmd.CommandText = @"
                        UPDATE active_bets
                           SET status='resolved', winning_outcome=@wo, resolved_at=datetime('now')
                         WHERE bet_id=@bid;";
                    cmd.Parameters.AddWithValue("@wo",  winningOutcome);
                    cmd.Parameters.AddWithValue("@bid", betId);
                    cmd.ExecuteNonQuery();
                }

                step = 8;
                // Broadcast overlay event
                var payload = new {
                    @event      = "bet_resolved",
                    betId       = betId,
                    winOutcome  = winningOutcome,
                    winLabel    = winLabel,
                    winnersCount= winners.Count,
                    losersCount = losers.Count,
                    rankChanges = rankChanges
                };
                CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(payload));

                EconomyLogger.Info("ResolveBet",
                    $"Bet#{betId} resolved. Winners={winners.Count} Losers={losers.Count} RankChanges={rankChanges.Count}", tid);
                CPH.SendMessage($"✅ Bet resolved! [{winLabel}] wins! {winners.Count} winners paid out at 2x. GG!");
            }

            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("ResolveBet", $"Crash at step {step}", ex, tid);
            CPH.SendMessage("❌ Internal error resolving bet. Check logs.");
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 6 — ANNUNCIO COMMAND
//  Trigger: Chat command  !annuncio <message>
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("Annuncio", "ACTION START", tid);

        try
        {
            step = 1;
            CPH.TryGetArg("userId",   out string userId);
            CPH.TryGetArg("userName", out string username);
            CPH.TryGetArg("rawInput", out string rawMsg);

            if (string.IsNullOrWhiteSpace(rawMsg))
            {
                CPH.SendMessage($"@{username} Usage: !annuncio <your message>");
                return false;
            }

            step = 2;
            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);
                EconomyDb.EnsureUser(conn, userId, username, seasonId, tid);

                int pts = EconomyDb.GetSeasonalPoints(conn, userId, seasonId);
                if (pts < EconomyConfig.ANNUNCIO_COST)
                {
                    CPH.SendMessage($"@{username} You need {EconomyConfig.ANNUNCIO_COST} pts for !annuncio (you have {pts}).");
                    return false;
                }

                step = 3;
                EconomyDb.AdjustPoints(conn, userId, seasonId, -EconomyConfig.ANNUNCIO_COST, tid);
                int newPts = EconomyDb.GetSeasonalPoints(conn, userId, seasonId);

                step = 4;
                var payload = new {
                    @event   = "annuncio",
                    username = username,
                    message  = System.Net.WebUtility.HtmlEncode(rawMsg),
                    cost     = EconomyConfig.ANNUNCIO_COST,
                    newBalance = newPts
                };
                CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(payload));

                EconomyLogger.Info("Annuncio",
                    $"uid={userId} msg='{rawMsg}' cost={EconomyConfig.ANNUNCIO_COST} remaining={newPts}", tid);
                CPH.SendMessage($"@{username} 📢 Annuncio sent! (-{EconomyConfig.ANNUNCIO_COST} pts, balance: {newPts})");
            }

            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("Annuncio", $"Crash at step {step}", ex, tid);
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 7 — CHAT MESSAGE WEBSOCKET PAYLOAD
//  Trigger: Streamer.bot Event → "Message" (chat message)
//  Pushes the full payload (rank icon, legacy aura color, points) to the OBS overlay.
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Debug("ChatPayload", "ACTION START", tid);

        try
        {
            step = 1;
            CPH.TryGetArg("userId",   out string userId);
            CPH.TryGetArg("userName", out string username);
            CPH.TryGetArg("rawInput", out string message);

            if (string.IsNullOrEmpty(userId)) { return true; } // system msg, skip

            step = 2;
            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);
                EconomyDb.EnsureUser(conn, userId, username, seasonId, tid);

                step = 3;
                int pts      = EconomyDb.GetSeasonalPoints(conn, userId, seasonId);
                int rankId   = EconomyDb.ComputeRankId(pts);

                int peakRankId;
                using (var cmd = new SQLiteCommand(
                    "SELECT lifetime_peak_rank_id FROM users WHERE user_id=@uid;", conn))
                {
                    cmd.Parameters.AddWithValue("@uid", userId);
                    var r = cmd.ExecuteScalar();
                    peakRankId = r == null ? 0 : Convert.ToInt32(r);
                }

                // If current rank exceeds stored peak, update it on the fly
                if (rankId > peakRankId)
                {
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            UPDATE users SET lifetime_peak_rank_id=@rid, updated_at=datetime('now')
                            WHERE user_id=@uid;";
                        cmd.Parameters.AddWithValue("@rid", rankId);
                        cmd.Parameters.AddWithValue("@uid", userId);
                        cmd.ExecuteNonQuery();
                    }
                    peakRankId = rankId;
                }

                step = 4;
                string rankName      = EconomyConfig.RankNames[rankId];
                string rankColor     = EconomyConfig.RankColors[rankId];
                string peakColor     = EconomyConfig.RankColors[peakRankId];
                string rankIcon      = $"/icons/{rankName.ToLower()}.svg";

                var payload = new {
                    @event              = "chat_message",
                    userId              = userId,
                    username            = username,
                    message             = System.Net.WebUtility.HtmlEncode(message ?? ""),
                    points              = pts,
                    rankId              = rankId,
                    rankName            = rankName,
                    rankColor           = rankColor,
                    rankIcon            = rankIcon,
                    lifetimePeakRankId  = peakRankId,
                    lifetimePeakRankName= EconomyConfig.RankNames[peakRankId],
                    lifetimePeakRankColor = peakColor
                };

                CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(payload));
                EconomyLogger.Debug("ChatPayload",
                    $"uid={userId} rank={rankName} peakRank={EconomyConfig.RankNames[peakRankId]} pts={pts}", tid);
            }

            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("ChatPayload", $"Crash at step {step}", ex, tid);
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 8 — ETERNAL RANK CHECK  (run every 15 minutes via timer)
//  Reads v_eternal_candidates view, sets rank_id=9 for qualifiers,
//  reverts non-qualifiers to their computed rank.
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("EternalCheck", "ACTION START", tid);

        try
        {
            step = 1;
            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);

                // Collect current Eternal candidates
                var eternalUids = new HashSet<string>();
                using (var cmd = new SQLiteCommand(
                    "SELECT user_id FROM v_eternal_candidates;", conn))
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                        eternalUids.Add(rdr.GetString(0));
                }

                EconomyLogger.Info("EternalCheck", $"Eternal candidates: {eternalUids.Count}", tid);

                step = 2;
                // Fetch all users who currently hold rank_id=9 in seasonal_stats
                var currentEternals = new HashSet<string>();
                using (var cmd = new SQLiteCommand(
                    "SELECT user_id FROM seasonal_stats WHERE season_id=@sid AND rank_id=9;", conn))
                {
                    cmd.Parameters.AddWithValue("@sid", seasonId);
                    using (var rdr = cmd.ExecuteReader())
                        while (rdr.Read())
                            currentEternals.Add(rdr.GetString(0));
                }

                step = 3;
                using (var tx = conn.BeginTransaction())
                {
                    // Grant Eternal to new qualifiers
                    foreach (string uid in eternalUids)
                    {
                        if (!currentEternals.Contains(uid))
                        {
                            using (var cmd = new SQLiteCommand(conn))
                            {
                                cmd.CommandText = @"
                                    UPDATE seasonal_stats
                                       SET rank_id=9, rank_change=1
                                     WHERE user_id=@uid AND season_id=@sid;
                                    UPDATE users
                                       SET lifetime_peak_rank_id=MAX(lifetime_peak_rank_id,9),
                                           updated_at=datetime('now')
                                     WHERE user_id=@uid;";
                                cmd.Parameters.AddWithValue("@uid", uid);
                                cmd.Parameters.AddWithValue("@sid", seasonId);
                                cmd.ExecuteNonQuery();
                            }
                            EconomyLogger.Info("EternalCheck", $"GRANTED Eternal to uid={uid}", tid);
                        }
                    }

                    // Revoke Eternal from users no longer qualifying
                    foreach (string uid in currentEternals)
                    {
                        if (!eternalUids.Contains(uid))
                        {
                            int pts   = EconomyDb.GetSeasonalPoints(conn, uid, seasonId);
                            int newRid = EconomyDb.ComputeRankId(pts);
                            using (var cmd = new SQLiteCommand(conn))
                            {
                                cmd.CommandText = @"
                                    UPDATE seasonal_stats
                                       SET rank_id=@rid, rank_change=-1
                                     WHERE user_id=@uid AND season_id=@sid;";
                                cmd.Parameters.AddWithValue("@rid", newRid);
                                cmd.Parameters.AddWithValue("@uid", uid);
                                cmd.Parameters.AddWithValue("@sid", seasonId);
                                cmd.ExecuteNonQuery();
                            }
                            EconomyLogger.Info("EternalCheck",
                                $"REVOKED Eternal from uid={uid} new rank={EconomyConfig.RankNames[newRid]}", tid);
                        }
                    }

                    tx.Commit();
                }
            }

            EconomyLogger.Info("EternalCheck", "ACTION COMPLETE", tid);
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("EternalCheck", $"Crash at step {step}", ex, tid);
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 9 — SEASONAL RESET
//  Trigger: Broadcaster command  !seasonreset  (or a monthly scheduler)
//  Zeroes seasonal points & rank for everyone; creates a new season row.
//  NEVER touches lifetime_points or lifetime stats.
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid  = Guid.NewGuid().ToString("N").Substring(0, 8);
        int    step = 0;
        EconomyLogger.Info("SeasonReset", "ACTION START", tid);

        try
        {
            step = 1;
            using (var conn = EconomyDb.Open())
            {
                int oldSeasonId = EconomyDb.GetCurrentSeasonId(conn);

                step = 2;
                using (var tx = conn.BeginTransaction())
                {
                    // Close the old season
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            UPDATE seasons SET end_date=date('now') WHERE id=@id;";
                        cmd.Parameters.AddWithValue("@id", oldSeasonId);
                        cmd.ExecuteNonQuery();
                    }

                    // Create new season
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            INSERT INTO seasons (name, start_date)
                            VALUES ('Season ' || (SELECT COUNT(*)+1 FROM seasons), date('now'));";
                        cmd.ExecuteNonQuery();
                    }

                    long newSeasonId;
                    using (var cmd = new SQLiteCommand("SELECT last_insert_rowid();", conn))
                        newSeasonId = (long)cmd.ExecuteScalar();

                    // Insert blank seasonal rows for all existing users in new season
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            INSERT OR IGNORE INTO seasonal_stats (user_id, season_id, seasonal_points, rank_id, rank_change)
                            SELECT user_id, @sid, 0, 0, 0 FROM users;";
                        cmd.Parameters.AddWithValue("@sid", newSeasonId);
                        int rows = cmd.ExecuteNonQuery();
                        EconomyLogger.Info("SeasonReset", $"Created {rows} blank seasonal rows for season {newSeasonId}.", tid);
                    }

                    tx.Commit();
                    EconomyLogger.Info("SeasonReset",
                        $"Old season {oldSeasonId} closed. New season {newSeasonId} started.", tid);

                    // Broadcast overlay event
                    var payload = new {
                        @event     = "season_reset",
                        oldSeason  = oldSeasonId,
                        newSeason  = $"Season {newSeasonId}"
                    };
                    CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(payload));

                    CPH.SendMessage($"🔄 SEASON RESET complete! Season {newSeasonId} has begun. Lifetime stats preserved. GL HF! 🎮");
                }
            }

            EconomyLogger.Info("SeasonReset", "ACTION COMPLETE", tid);
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("SeasonReset", $"Crash at step {step}", ex, tid);
            CPH.SendMessage("❌ Season reset failed. Check logs immediately.");
            return false;
        }
    }
}
*/

// ─────────────────────────────────────────────────────────────────────────────
//  ACTION 10 — BALANCE COMMAND
//  Trigger: Chat command  !balance  or  !points
// ─────────────────────────────────────────────────────────────────────────────
/*
public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string tid = Guid.NewGuid().ToString("N").Substring(0, 8);
        EconomyLogger.Info("Balance", "ACTION START", tid);

        try
        {
            CPH.TryGetArg("userId",   out string userId);
            CPH.TryGetArg("userName", out string username);

            using (var conn = EconomyDb.Open())
            {
                int seasonId = EconomyDb.GetCurrentSeasonId(conn);
                EconomyDb.EnsureUser(conn, userId, username, seasonId, tid);

                int pts    = EconomyDb.GetSeasonalPoints(conn, userId, seasonId);
                int rankId = EconomyDb.ComputeRankId(pts);
                int maxBet = EconomyConfig.MaxBets[rankId];

                CPH.SendMessage(
                    $"@{username} 💰 {pts} pts | Rank: {EconomyConfig.RankNames[rankId]} | Max Bet: {maxBet} pts");
                EconomyLogger.Info("Balance", $"uid={userId} pts={pts} rank={EconomyConfig.RankNames[rankId]}", tid);
            }

            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("Balance", "Crash", ex, tid);
            return false;
        }
    }
}
*/