// =============================================================================
// ADVANCED PURE ECONOMY SYSTEM — Streamer.bot C# Implementation
// =============================================================================
//
// SETUP REQUIREMENTS
// ------------------
//  1. Download System.Data.SQLite (x64) from https://system.data.sqlite.org
//     and place System.Data.SQLite.dll in your Streamer.bot root folder.
//  2. In each action's C# editor, open Settings → References and add that DLL.
//  3. Also add Newtonsoft.Json.dll (already shipped with Streamer.bot).
//  4. Run schema.sql once to initialise the database file.
//  5. Update EconomyConfig.DB_PATH to match your actual file path.
//
// STRUCTURE
// ---------
//  Each #region is one Streamer.bot "Execute C# Code" sub-action.
//  Copy the SHARED HELPER region + the specific action region into the editor.
//  The CPHInline class that Streamer.bot expects is inside each action region.
//
//  Recommended Streamer.bot action layout:
//   ┌─ WATCH TIME          [Trigger: Twitch -> General -> Present Viewers]
//   ├─ CHAT PAYLOAD        [Trigger: Chat Message]
//   ├─ !annuncio           [Trigger: Command !annuncio, cooldown 30s]
//   ├─ !startbet           [Trigger: Command !startbet, Broadcaster/Mod only]
//   ├─ !bet                [Trigger: Command !bet]
//   ├─ !lockbet            [Trigger: Command !lockbet, Broadcaster/Mod only]
//   ├─ !resolvebet         [Trigger: Command !resolvebet, Broadcaster/Mod only]
//   ├─ ETERNAL RANK CHECK  [Trigger: Timer every 15 min]
//   └─ SEASONAL RESET      [Trigger: Manual / Scheduled monthly]
// =============================================================================


// =============================================================================
// ██████  SHARED HELPER — paste above CPHInline in EVERY action block
// =============================================================================
#region SHARED HELPER

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using Newtonsoft.Json;
using System.IO;

/// <summary>Central configuration. Change values here; they propagate everywhere.</summary>
public static class EconomyConfig
{
    // ── Database ──────────────────────────────────────────────────────────────
    public const string LOG_DIR  = @"C:\StreamerBot\Data\Logs";
    public const string DB_PATH  = @"C:\StreamerBot\Data\economy.db";
    public static string ConnStr => $"Data Source={DB_PATH};Version=3;Pooling=True;Max Pool Size=5;";

    // ── Economy rules ─────────────────────────────────────────────────────────
    public const int    ANNUNCIO_COST       = 150;
    public const int    WATCH_TIME_POINTS   = 1;        // per minute
    public const double BET_WIN_MULTIPLIER  = 2.0;
    public const int    ETERNAL_MIN_POINTS  = 35_000;
    public const int    ETERNAL_TOP_SLOTS   = 3;        // top-3 positions (ties share)
    public const int    WIN_RATE_MIN_BETS   = 10;
    public const int    WIN_RATE_MIN_WAGER  = 400;
    public const int    WIN_RATE_DAYS       = 5;

    // ── Rank table: (id, name, minSeasonalPts, betCap, hexColor) ─────────────
    // Note: Eternal (id=9) is assigned by the scheduler, NOT by threshold here.
    public static readonly RankDef[] RANKS = new[]
    {
        new RankDef(0, "Wood",        0,      25,    "#8B6914"),
        new RankDef(1, "Bronze",      500,    50,    "#CD7F32"),
        new RankDef(2, "Silver",      1_500,  100,   "#A8A9AD"),
        new RankDef(3, "Gold",        3_500,  200,   "#FFD700"),
        new RankDef(4, "Platinum",    7_000,  400,   "#E8E8FF"),
        new RankDef(5, "Emerald",     12_000, 800,   "#00D26A"),
        new RankDef(6, "Diamond",     20_000, 1_500, "#A0EEFF"),
        new RankDef(7, "Master",      35_000, 3_000, "#AA44FF"),
        new RankDef(8, "Grandmaster", 60_000, 5_000, "#FF4444"),
        new RankDef(9, "Eternal",     35_000, 10_000,"#FFE566"),
    };

    public static int    CalcRankId(int seasonalPoints)
    {
        // Returns highest non-Eternal rank the user qualifies for.
        int id = 0;
        for (int i = 0; i <= 8; i++)          // 0..8, Eternal excluded
            if (seasonalPoints >= RANKS[i].MinPoints) id = i;
        return id;
    }
    public static RankDef GetRank(int rankId) => RANKS[Math.Max(0, Math.Min(9, rankId))];
}


public static class EconomyLogger
{
    private static readonly object _lock = new object();
    private static string _sessionFile;
    private static string _sessionId;

    public enum LogLevel { TRACE, DEBUG, INFO, WARN, ERROR, FATAL }
    public static string InitError { get; private set; }

    static EconomyLogger()
    {
        try
        {
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            
            // Try to create the directory
            try {
                if (!Directory.Exists(EconomyConfig.LOG_DIR))
                    Directory.CreateDirectory(EconomyConfig.LOG_DIR);
            } catch (Exception ex) {
                InitError = $"Failed to create/access LOG_DIR ({EconomyConfig.LOG_DIR}): {ex.Message}";
                return;
            }
            
            _sessionFile = Path.Combine(EconomyConfig.LOG_DIR, $"economy_session_{DateTime.Now:yyyyMMdd_HHmmss}_{_sessionId}.log");

            // Test write
            File.WriteAllText(_sessionFile, $"SYSTEM: EconomyLogger started. Session: {_sessionId}\r\n");
        }
        catch (Exception ex) 
        { 
            InitError = $"General initialization failure: {ex.Message}";
        }
    }

    public static void Log(LogLevel level, string component, string message, string stateSnapshot = null, Exception ex = null, string traceId = null)
    {
        if (_sessionFile == null) return;
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string tid = string.IsNullOrEmpty(traceId) ? "" : $"[Trace:{traceId}] ";
            string state = string.IsNullOrEmpty(stateSnapshot) ? "" : $"\n    -> State: {stateSnapshot}";
            string exception = ex != null ? $"\n    -> Exception: {ex.Message}\n    -> StackTrace: {ex.StackTrace}" : "";
            
            string logLine = $"[{timestamp}] [{level}] [{component}] {tid}{message}{state}{exception}";
            
            lock (_lock)
            {
                File.AppendAllText(_sessionFile, logLine + Environment.NewLine);
            }
        }
        catch { /* Fallback to nothing */ }
    }

    public static void CheckInit(dynamic cph)
    {
        if (!string.IsNullOrEmpty(InitError))
        {
            cph.LogWarn($"[AuraEconomy] Logger failed: {InitError}");
            // We don't null it out here because other actions might need to see it too, 
            // but we could use a static flag to avoid spamming the console.
        }
    }

    public static void Trace(string component, string msg, string traceId=null, string state=null) => Log(LogLevel.TRACE, component, msg, state, null, traceId);
    public static void Debug(string component, string msg, string traceId=null, string state=null) => Log(LogLevel.DEBUG, component, msg, state, null, traceId);
    public static void Info(string component, string msg, string traceId=null, string state=null)  => Log(LogLevel.INFO, component, msg, state, null, traceId);
    public static void Warn(string component, string msg, string traceId=null, string state=null)  => Log(LogLevel.WARN, component, msg, state, null, traceId);
    public static void Error(string component, string msg, Exception ex=null, string traceId=null, string state=null) => Log(LogLevel.ERROR, component, msg, state, ex, traceId);
    public static void Fatal(string component, string msg, Exception ex=null, string traceId=null, string state=null) => Log(LogLevel.FATAL, component, msg, state, ex, traceId);
}

public struct RankDef
{
    public int    Id, BetCap, MinPoints;
    public string Name, Color;
    public RankDef(int id, string name, int min, int cap, string color)
    { Id=id; Name=name; MinPoints=min; BetCap=cap; Color=color; }
}

/// <summary>All database operations. Every method opens/closes its own connection (WAL safe).</summary>
public static class EconomyDb
{
    private static SQLiteConnection Open(string traceId = null)
    {
        EconomyLogger.Trace("EconomyDb.Open", "Opening SQLite connection", traceId);
        try
        {
            var conn = new SQLiteConnection(EconomyConfig.ConnStr);
            conn.Open();
            using (var p = new SQLiteCommand("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;", conn))
                p.ExecuteNonQuery();
            return conn;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("EconomyDb.Open", "Failed to open SQLite connection", ex, traceId);
            throw;
        }
    }

    // ── Season helpers ────────────────────────────────────────────────────────
    public static int CurrentSeasonId()
    {
        using (var c = Open())
        using (var q = new SQLiteCommand("SELECT id FROM seasons ORDER BY id DESC LIMIT 1;", c))
        {
            var r = q.ExecuteScalar();
            return r != null ? Convert.ToInt32(r) : 1;
        }
    }

    // ── User bootstrap ────────────────────────────────────────────────────────
    public static void EnsureUser(string userId, string username, string traceId = null)
    {
        EconomyLogger.Debug("EconomyDb", $"EnsureUser called", traceId, $"uid={userId}, uname={username}");
        try
        {
            int sid = CurrentSeasonId();
            using (var c = Open(traceId))
            using (var tx = c.BeginTransaction())
            {
                using (var q = new SQLiteCommand(@"
                    INSERT INTO users(user_id, username, created_at, updated_at)
                    VALUES(@uid,@name,datetime('now'),datetime('now'))
                    ON CONFLICT(user_id) DO UPDATE SET username=excluded.username, updated_at=datetime('now');", c, tx))
                {
                    q.Parameters.AddWithValue("@uid",  userId);
                    q.Parameters.AddWithValue("@name", username);
                    q.ExecuteNonQuery();
                }
                using (var q = new SQLiteCommand(@"
                    INSERT INTO seasonal_stats(user_id, season_id, seasonal_points, rank_id, rank_change)
                    VALUES(@uid,@sid,0,0,0)
                    ON CONFLICT(user_id, season_id) DO NOTHING;", c, tx))
                {
                    q.Parameters.AddWithValue("@uid", userId);
                    q.Parameters.AddWithValue("@sid", sid);
                    q.ExecuteNonQuery();
                }
                tx.Commit();
                EconomyLogger.Trace("EconomyDb", $"EnsureUser completed for {userId}", traceId);
            }
        }
        catch (Exception ex)
        {
            EconomyLogger.Error("EconomyDb", $"Failed to EnsureUser for {userId}", ex, traceId);
            throw;
        }
    }

    // ── Read user stats ───────────────────────────────────────────────────────
    public struct UserStats
    {
        public int SeasonalPoints, RankId, LifetimePoints, LifetimePeakRankId;
    }

    public static UserStats GetStats(string userId)
    {
        int sid = CurrentSeasonId();
        using (var c = Open())
        using (var q = new SQLiteCommand(@"
            SELECT ss.seasonal_points, ss.rank_id, u.lifetime_points, u.lifetime_peak_rank_id
            FROM users u
            JOIN seasonal_stats ss ON u.user_id=ss.user_id AND ss.season_id=@sid
            WHERE u.user_id=@uid;", c))
        {
            q.Parameters.AddWithValue("@uid", userId);
            q.Parameters.AddWithValue("@sid", sid);
            using (var r = q.ExecuteReader())
            {
                if (r.Read())
                    return new UserStats { SeasonalPoints=r.GetInt32(0), RankId=r.GetInt32(1),
                                          LifetimePoints=r.GetInt32(2), LifetimePeakRankId=r.GetInt32(3) };
            }
        }
        return new UserStats();
    }

    // ── Award/deduct points (pass negative to deduct) ─────────────────────────
    public static bool AdjustPoints(string userId, int delta, string traceId = null)
    {
        EconomyLogger.Debug("EconomyDb", $"AdjustPoints called", traceId, $"uid={userId}, delta={delta}");
        try
        {
            int sid = CurrentSeasonId();
            using (var c = Open(traceId))
            using (var tx = c.BeginTransaction())
            {
                if (delta < 0)
                {
                    using (var chk = new SQLiteCommand("SELECT seasonal_points FROM seasonal_stats WHERE user_id=@uid AND season_id=@sid;", c, tx))
                    {
                        chk.Parameters.AddWithValue("@uid", userId);
                        chk.Parameters.AddWithValue("@sid", sid);
                        var cur = chk.ExecuteScalar();
                        if (cur == null || Convert.ToInt32(cur) + delta < 0) 
                        { 
                            tx.Rollback(); 
                            EconomyLogger.Warn("EconomyDb", $"AdjustPoints failed: insufficient points.", traceId, $"uid={userId}, current={cur}, delta={delta}");
                            return false; 
                        }
                    }
                }
                using (var q = new SQLiteCommand(@"
                    UPDATE seasonal_stats SET seasonal_points = MAX(0, seasonal_points + @d)
                    WHERE user_id=@uid AND season_id=@sid;", c, tx))
                {
                    q.Parameters.AddWithValue("@d",   delta);
                    q.Parameters.AddWithValue("@uid", userId);
                    q.Parameters.AddWithValue("@sid", sid);
                    q.ExecuteNonQuery();
                }
                using (var q = new SQLiteCommand(@"
                    UPDATE users SET lifetime_points=MAX(0, lifetime_points+@d), updated_at=datetime('now')
                    WHERE user_id=@uid;", c, tx))
                {
                    q.Parameters.AddWithValue("@d",   delta);
                    q.Parameters.AddWithValue("@uid", userId);
                    q.ExecuteNonQuery();
                }
                tx.Commit();
                EconomyLogger.Info("EconomyDb", $"Adjusted points successfully.", traceId, $"uid={userId}, delta={delta}");
            }
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Error("EconomyDb", $"Failed to AdjustPoints for {userId}", ex, traceId);
            return false;
        }
    }

    // ── Rank recalculation (call after point changes) ─────────────────────────
    public struct RankDelta { public int OldRankId, NewRankId, Delta; }

    public static RankDelta RecalcRank(string userId, string traceId = null)
    {
        EconomyLogger.Trace("EconomyDb", $"RecalcRank called", traceId, $"uid={userId}");
        try
        {
            int sid = CurrentSeasonId();
            using (var c = Open(traceId))
            {
                int pts = 0, oldRank = 0;
                using (var q = new SQLiteCommand("SELECT seasonal_points, rank_id FROM seasonal_stats WHERE user_id=@uid AND season_id=@sid;", c))
                {
                    q.Parameters.AddWithValue("@uid", userId);
                    q.Parameters.AddWithValue("@sid", sid);
                    using (var r = q.ExecuteReader())
                        if (r.Read()) { pts = r.GetInt32(0); oldRank = r.GetInt32(1); }
                }

                int newRank = (oldRank == 9) ? 9 : EconomyConfig.CalcRankId(pts);
                int delta   = newRank - oldRank;

                if (delta != 0) {
                    EconomyLogger.Info("EconomyDb", $"Rank changed for {userId}", traceId, $"oldRank={oldRank}, newRank={newRank}, pts={pts}");
                }

                using (var q = new SQLiteCommand(@"
                    UPDATE seasonal_stats SET rank_id=@nr, rank_change=@d
                    WHERE user_id=@uid AND season_id=@sid;", c))
                {
                    q.Parameters.AddWithValue("@nr",  newRank);
                    q.Parameters.AddWithValue("@d",   delta);
                    q.Parameters.AddWithValue("@uid", userId);
                    q.Parameters.AddWithValue("@sid", sid);
                    q.ExecuteNonQuery();
                }
                using (var q = new SQLiteCommand(@"
                    UPDATE users SET lifetime_peak_rank_id=MAX(lifetime_peak_rank_id,@nr), rank_change=@d, updated_at=datetime('now')
                    WHERE user_id=@uid;", c))
                {
                    q.Parameters.AddWithValue("@nr",  newRank);
                    q.Parameters.AddWithValue("@d",   delta);
                    q.Parameters.AddWithValue("@uid", userId);
                    q.ExecuteNonQuery();
                }

                return new RankDelta { OldRankId=oldRank, NewRankId=newRank, Delta=delta };
            }
        }
        catch (Exception ex)
        {
            EconomyLogger.Error("EconomyDb", $"Failed to RecalcRank for {userId}", ex, traceId);
            return new RankDelta { OldRankId=0, NewRankId=0, Delta=0 };
        }
    }

    // ── Bet helpers ───────────────────────────────────────────────────────────
    public static int GetOpenBetId()
    {
        using (var c = Open())
        using (var q = new SQLiteCommand("SELECT bet_id FROM active_bets WHERE status IN ('open','locked') ORDER BY bet_id DESC LIMIT 1;", c))
        {
            var r = q.ExecuteScalar();
            return r != null ? Convert.ToInt32(r) : -1;
        }
    }
}
#endregion SHARED HELPER


// =============================================================================
// ██  ACTION 1 — WATCH TIME ACCUMULATION
// =============================================================================
// Trigger: Twitch -> General -> Present Viewers (recommended)
//          OR Twitch -> General -> Present (single user)
// =============================================================================
#region ACTION: Watch Time

// --- PASTE SHARED HELPER ABOVE THIS LINE IN STREAMER.BOT ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        EconomyLogger.CheckInit(CPH);
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("WatchTime", ">>> WATCH TIME ACTION TRIGGERED <<<", traceId);
        try
        {
            if (args.ContainsKey("users") && args["users"] != null)
            {
                var enumerable = args["users"] as System.Collections.IEnumerable;
                EconomyLogger.Debug("WatchTime", $"'users' array detected.", traceId, $"Enumerable type: {enumerable?.GetType()}");
                
                if (enumerable != null)
                {
                    int processedCount = 0;
                    foreach (var item in enumerable)
                    {
                        try 
                        {
                            var userDict = item as Dictionary<string, object>;
                            if (userDict == null) { 
                                EconomyLogger.Warn("WatchTime", "Item in 'users' is not a dictionary.", traceId, $"Item type: {item?.GetType()}"); 
                                continue; 
                            }
                            
                            string uId   = userDict.ContainsKey("id") ? userDict["id"].ToString() : (userDict.ContainsKey("userId") ? userDict["userId"].ToString() : "");
                            string uName = userDict.ContainsKey("display") ? userDict["display"].ToString() : (userDict.ContainsKey("userName") ? userDict["userName"].ToString() : (userDict.ContainsKey("name") ? userDict["name"].ToString() : ""));
                            
                            if (!string.IsNullOrEmpty(uId))
                            {
                                EconomyLogger.Trace("WatchTime", $"Processing viewer: {uName}", traceId, $"uid={uId}");
                                EconomyDb.EnsureUser(uId, uName, traceId);
                                bool adjustSuccess = EconomyDb.AdjustPoints(uId, EconomyConfig.WATCH_TIME_POINTS, traceId);
                                if (adjustSuccess) {
                                    EconomyDb.RecalcRank(uId, traceId);
                                    processedCount++;
                                } else {
                                    EconomyLogger.Warn("WatchTime", $"Failed to adjust points for {uId}", traceId);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            EconomyLogger.Error("WatchTime", "Exception processing individual viewer.", ex, traceId);
                        }
                    }
                    EconomyLogger.Info("WatchTime", $"Successfully processed {processedCount} viewers in array.", traceId);
                    return true;
                }
            }

            EconomyLogger.Debug("WatchTime", "'users' array not found, checking single user.", traceId);
            string userId   = args.ContainsKey("userId") ? args["userId"].ToString() : "";
            string username = args.ContainsKey("user")   ? args["user"].ToString()   : (args.ContainsKey("userName") ? args["userName"].ToString() : "");

            if (!string.IsNullOrEmpty(userId))
            {
                EconomyLogger.Info("WatchTime", $"Processing single viewer: {username}", traceId, $"uid={userId}");
                EconomyDb.EnsureUser(userId, username, traceId);
                bool adjustSuccess = EconomyDb.AdjustPoints(userId, EconomyConfig.WATCH_TIME_POINTS, traceId);
                if (adjustSuccess) {
                    EconomyDb.RecalcRank(userId, traceId);
                    EconomyLogger.Info("WatchTime", $"Successfully awarded points to {username}.", traceId);
                }
                return true;
            }

            EconomyLogger.Warn("WatchTime", "Action ran but no user data found. Args: " + string.Join(", ", args.Keys), traceId);
            CPH.LogWarn("[AuraEconomy] Watch Time action ran, but no user data found! Please ensure this is triggered by the 'Twitch -> General -> Present Viewers' trigger (not a Timer).");
            return false;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("WatchTime", "Fatal error executing Watch Time action.", ex, traceId);
            return false;
        }
    }

    private Dictionary<string, object> args => CPH.GetArgs();
}
#endregion ACTION: Watch Time


// =============================================================================
// ██  ACTION 2 — !annuncio [message]
// =============================================================================
// Trigger: Command "!annuncio" (all users, per-user cooldown 30s recommended)
// =============================================================================
#region ACTION: !annuncio

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        EconomyLogger.CheckInit(CPH);
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("!annuncio [message]", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            var    args     = CPH.GetArgs();
            string userId   = args["userId"].ToString();
            string username = args["user"].ToString();
            string input    = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : "";
    
            if (string.IsNullOrWhiteSpace(input))
            {
                CPH.SendMessage($"@{username} Usage: !annuncio [your message]");
                return false;
            }
    
            EconomyDb.EnsureUser(userId, username);
            var stats = EconomyDb.GetStats(userId);
    
            if (stats.SeasonalPoints < EconomyConfig.ANNUNCIO_COST)
            {
                CPH.SendMessage($"@{username} You need {EconomyConfig.ANNUNCIO_COST} points but only have {stats.SeasonalPoints}.");
                return false;
            }
    
            EconomyDb.AdjustPoints(userId, -EconomyConfig.ANNUNCIO_COST);
            EconomyDb.RecalcRank(userId);
    
            var payload = JsonConvert.SerializeObject(new
            {
                @event   = "annuncio",
                username,
                message  = input,
                rankId   = stats.RankId,
                rankName = EconomyConfig.GetRank(stats.RankId).Name,
                rankColor= EconomyConfig.GetRank(stats.RankId).Color,
            });
    
            CPH.WebsocketBroadcastString(payload);
            CPH.SendMessage($"📢 {username} sent an announcement! (-{EconomyConfig.ANNUNCIO_COST} pts)");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("!annuncio [message]", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("!annuncio [message]", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: !annuncio


// =============================================================================
// ██  ACTION 3 — !startbet [title] | [outcomeA] | [outcomeB]
// =============================================================================
// Trigger: Command "!startbet", Broadcaster/Moderator permission
// Example: !startbet Will we win? | Win | Loss
// =============================================================================
#region ACTION: !startbet

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("!startbet [title] | [outcomeA] | [outcomeB]", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            var    args    = CPH.GetArgs();
            string input   = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : "";
            string[] parts = input.Split('|');
    
            if (parts.Length < 3)
            {
                CPH.SendMessage("Usage: !startbet [title] | [outcomeA] | [outcomeB]");
                return false;
            }
    
            string title = parts[0].Trim();
            string outA  = parts[1].Trim();
            string outB  = parts[2].Trim();
    
            // Ensure no bet is already open
            if (EconomyDb.GetOpenBetId() != -1)
            {
                CPH.SendMessage("⚠️ A bet is already open! Use !resolvebet or !lockbet first.");
                return false;
            }
    
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand(@"
                    INSERT INTO active_bets (title, outcome_a, outcome_b, status, created_at)
                    VALUES (@t, @a, @b, 'open', datetime('now'));", conn))
                {
                    q.Parameters.AddWithValue("@t", title);
                    q.Parameters.AddWithValue("@a", outA);
                    q.Parameters.AddWithValue("@b", outB);
                    q.ExecuteNonQuery();
                }
            }
    
            var payload = JsonConvert.SerializeObject(new
            {
                @event = "bet_open",
                title, outcomeA = outA, outcomeB = outB
            });
            CPH.WebsocketBroadcastString(payload);
            CPH.SendMessage($"🎲 Bet OPEN: \"{title}\" — (A) {outA}  vs  (B) {outB} | Use !bet a [amount] or !bet b [amount]");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("!startbet [title] | [outcomeA] | [outcomeB]", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("!startbet [title] | [outcomeA] | [outcomeB]", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: !startbet


// =============================================================================
// ██  ACTION 4 — !bet [a|b] [amount]
// =============================================================================
// Trigger: Command "!bet"
// Points are deducted immediately (escrow model).
// =============================================================================
#region ACTION: !bet

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("!bet [a|b] [amount]", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            var    args     = CPH.GetArgs();
            string userId   = args["userId"].ToString();
            string username = args["user"].ToString();
            string input    = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : "";
    
            var split = input.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length < 2 || !int.TryParse(split[1], out int amount) || amount <= 0)
            {
                CPH.SendMessage($"@{username} Usage: !bet [a|b] [amount]");
                return false;
            }
    
            string outcome = split[0].ToLower();
            if (outcome != "a" && outcome != "b")
            {
                CPH.SendMessage($"@{username} Choose 'a' or 'b'.");
                return false;
            }
    
            int betId = EconomyDb.GetOpenBetId();
            if (betId == -1)
            {
                CPH.SendMessage($"@{username} No bet is currently open.");
                return false;
            }
    
            // Check bet is still 'open' (not locked)
            string betStatus = "";
            string outALabel = "", outBLabel = "";
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand("SELECT status, outcome_a, outcome_b FROM active_bets WHERE bet_id=@bid;", conn))
                {
                    q.Parameters.AddWithValue("@bid", betId);
                    using (var r = q.ExecuteReader())
                        if (r.Read()) { betStatus = r.GetString(0); outALabel = r.GetString(1); outBLabel = r.GetString(2); }
                }
            }
    
            if (betStatus != "open")
            {
                CPH.SendMessage($"@{username} Bet is locked. No more entries!");
                return false;
            }
    
            EconomyDb.EnsureUser(userId, username);
            var stats = EconomyDb.GetStats(userId);
            int cap   = EconomyConfig.GetRank(stats.RankId).BetCap;
    
            if (amount > cap)
            {
                CPH.SendMessage($"@{username} Your {EconomyConfig.GetRank(stats.RankId).Name} rank caps bets at {cap} pts. Use !bet {outcome} {cap}.");
                return false;
            }
    
            if (stats.SeasonalPoints < amount)
            {
                CPH.SendMessage($"@{username} Insufficient points. You have {stats.SeasonalPoints}.");
                return false;
            }
    
            // Check for duplicate entry
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand("SELECT COUNT(*) FROM bet_entries WHERE bet_id=@bid AND user_id=@uid;", conn))
                {
                    q.Parameters.AddWithValue("@bid", betId);
                    q.Parameters.AddWithValue("@uid", userId);
                    long count = (long)q.ExecuteScalar();
                    if (count > 0) { CPH.SendMessage($"@{username} You already placed a bet!"); return false; }
                }
    
                // Deduct points and record entry
                if (!EconomyDb.AdjustPoints(userId, -amount)) { CPH.SendMessage($"@{username} Error deducting points."); return false; }
                EconomyDb.RecalcRank(userId);
    
                using (var q = new SQLiteCommand(@"
                    INSERT INTO bet_entries (bet_id, user_id, username, amount, outcome_chosen, created_at)
                    VALUES (@bid,@uid,@uname,@amt,@out,datetime('now'));", conn))
                {
                    q.Parameters.AddWithValue("@bid",   betId);
                    q.Parameters.AddWithValue("@uid",   userId);
                    q.Parameters.AddWithValue("@uname", username);
                    q.Parameters.AddWithValue("@amt",   amount);
                    q.Parameters.AddWithValue("@out",   outcome);
                    q.ExecuteNonQuery();
                }
    
                // Update lifetime wagered and bet count
                using (var q = new SQLiteCommand(@"
                    UPDATE users SET lifetime_total_bets=lifetime_total_bets+1,
                                     lifetime_points_wagered=lifetime_points_wagered+@amt,
                                     updated_at=datetime('now')
                    WHERE user_id=@uid;", conn))
                {
                    q.Parameters.AddWithValue("@amt", amount);
                    q.Parameters.AddWithValue("@uid", userId);
                    q.ExecuteNonQuery();
                }
    
                // Update last_bet_at in seasonal_stats
                int sid = EconomyDb.CurrentSeasonId();
                using (var q = new SQLiteCommand("UPDATE seasonal_stats SET last_bet_at=datetime('now') WHERE user_id=@uid AND season_id=@sid;", conn))
                {
                    q.Parameters.AddWithValue("@uid", userId);
                    q.Parameters.AddWithValue("@sid", sid);
                    q.ExecuteNonQuery();
                }
            }
    
            string outLabel = (outcome == "a") ? outALabel : outBLabel;
            CPH.SendMessage($"@{username} ✅ Bet {amount} pts on [{outLabel}]! Good luck!");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("!bet [a|b] [amount]", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("!bet [a|b] [amount]", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: !bet


// =============================================================================
// ██  ACTION 5 — !lockbet
// =============================================================================
// Trigger: Command "!lockbet", Broadcaster/Moderator only
// =============================================================================
#region ACTION: !lockbet

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("!lockbet", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            int betId = EconomyDb.GetOpenBetId();
            if (betId == -1) { CPH.SendMessage("No open bet to lock."); return false; }
    
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand("UPDATE active_bets SET status='locked', locked_at=datetime('now') WHERE bet_id=@bid;", conn))
                {
                    q.Parameters.AddWithValue("@bid", betId);
                    q.ExecuteNonQuery();
                }
            }
    
            CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(new { @event = "bet_locked" }));
            CPH.SendMessage("🔒 Bet is now LOCKED. No more entries!");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("!lockbet", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("!lockbet", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: !lockbet


// =============================================================================
// ██  ACTION 6 — !resolvebet [a|b]
// =============================================================================
// Trigger: Command "!resolvebet", Broadcaster only
//
// STRICT WORKFLOW:
//  1. Pre-payout rank snapshot for all participants.
//  2. Award winners (2x) — losers already had points deducted at bet time.
//  3. Recalculate rank for every participant.
//  4. Compute and persist rank_change delta.
// =============================================================================
#region ACTION: !resolvebet

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("!resolvebet [a|b]", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            var    args    = CPH.GetArgs();
            string input   = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim().ToLower() : "";
    
            if (input != "a" && input != "b")
            {
                CPH.SendMessage("Usage: !resolvebet [a|b]");
                return false;
            }
    
            int betId = EconomyDb.GetOpenBetId();
            if (betId == -1) { CPH.SendMessage("No active bet to resolve."); return false; }
    
            string betTitle = "", outA = "", outB = "";
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand("SELECT title, outcome_a, outcome_b FROM active_bets WHERE bet_id=@bid;", conn))
                {
                    q.Parameters.AddWithValue("@bid", betId);
                    using (var r = q.ExecuteReader())
                        if (r.Read()) { betTitle = r.GetString(0); outA = r.GetString(1); outB = r.GetString(2); }
                }
            }
    
            // ── STEP 1: Pre-payout rank snapshot ─────────────────────────────────
            var participants = new List<(string uid, string uname, int amount, string chosen)>();
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand("SELECT user_id, username, amount, outcome_chosen FROM bet_entries WHERE bet_id=@bid;", conn))
                {
                    q.Parameters.AddWithValue("@bid", betId);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                            participants.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
                }
            }
    
            // Snapshot pre-payout ranks
            var preRanks = new Dictionary<string, int>();
            foreach (var p in participants)
            {
                var s = EconomyDb.GetStats(p.uid);
                preRanks[p.uid] = s.RankId;
            }
    
            // ── STEP 2: Distribute payouts ────────────────────────────────────────
            int winners = 0, losers = 0;
            foreach (var p in participants)
            {
                bool won = (p.chosen == input);
                if (won)
                {
                    // Return original bet + winnings (total = 2x)
                    EconomyDb.AdjustPoints(p.uid, (int)(p.amount * EconomyConfig.BET_WIN_MULTIPLIER));
                    using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
                    {
                        conn.Open();
                        using (var q = new SQLiteCommand("UPDATE users SET lifetime_wins=lifetime_wins+1, updated_at=datetime('now') WHERE user_id=@uid;", conn))
                        {
                            q.Parameters.AddWithValue("@uid", p.uid);
                            q.ExecuteNonQuery();
                        }
                    }
                    winners++;
                }
                else
                {
                    // Losses: points already deducted; just tally
                    using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
                    {
                        conn.Open();
                        using (var q = new SQLiteCommand("UPDATE users SET lifetime_losses=lifetime_losses+1, updated_at=datetime('now') WHERE user_id=@uid;", conn))
                        {
                            q.Parameters.AddWithValue("@uid", p.uid);
                            q.ExecuteNonQuery();
                        }
                    }
                    losers++;
                }
            }
    
            // ── STEP 3 & 4: Recalculate ranks and persist deltas ──────────────────
            var rankChanges = new List<object>();
            foreach (var p in participants)
            {
                var delta = EconomyDb.RecalcRank(p.uid);
                if (delta.Delta != 0)
                {
                    rankChanges.Add(new
                    {
                        username    = p.uname,
                        oldRankName = EconomyConfig.GetRank(delta.OldRankId).Name,
                        newRankName = EconomyConfig.GetRank(delta.NewRankId).Name,
                        delta       = delta.Delta,
                        promoted    = delta.Delta > 0
                    });
                }
            }
    
            // Mark bet resolved
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand(@"
                    UPDATE active_bets SET status='resolved', winning_outcome=@wo, resolved_at=datetime('now')
                    WHERE bet_id=@bid;", conn))
                {
                    q.Parameters.AddWithValue("@wo",  input);
                    q.Parameters.AddWithValue("@bid", betId);
                    q.ExecuteNonQuery();
                }
            }
    
            string winLabel = (input == "a") ? outA : outB;
            CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(new
            {
                @event      = "bet_resolved",
                betTitle,
                winLabel,
                winners, losers,
                rankChanges
            }));
    
            CPH.SendMessage($"✅ Bet resolved! [{winLabel}] wins! 🏆 {winners} winners, {losers} losers. Rank-ups: {rankChanges.Count}");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("!resolvebet [a|b]", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("!resolvebet [a|b]", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: !resolvebet


// =============================================================================
// ██  ACTION 7 — ETERNAL RANK SCHEDULER (every 15 minutes)
// =============================================================================
// Trigger: Streamer.bot Timer, interval 15 minutes
//
// Logic:
//  • Find top-N seasonal positions among users with >= 35,000 pts (RANK window).
//  • All users sharing a qualifying position receive Eternal (id=9).
//  • Users previously Eternal who no longer qualify are demoted to their
//    threshold rank (Grandmaster if still >= 60k, Master if >= 35k, etc.).
// =============================================================================
#region ACTION: Eternal Rank Scheduler

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("ETERNAL RANK SCHEDULER (every 15 minutes)", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            int sid = EconomyDb.CurrentSeasonId();
    
            // Fetch all users >= ETERNAL_MIN_POINTS, ordered by seasonal_points DESC
            var candidates = new List<(string uid, string uname, int pts)>();
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var q = new SQLiteCommand(@"
                    SELECT user_id, username, seasonal_points
                    FROM v_season_leaderboard
                    WHERE seasonal_points >= @min
                    ORDER BY seasonal_points DESC;", conn))
                {
                    q.Parameters.AddWithValue("@min", EconomyConfig.ETERNAL_MIN_POINTS);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                            candidates.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
                }
            }
    
            // Determine which positions count as "Top 3" with tie-breaking
            // RANK() semantics: ties share a position; position 3 may have more than 3 users.
            var eternalIds = new HashSet<string>();
            if (candidates.Count > 0)
            {
                int position = 1;
                int prevPts  = candidates[0].pts;
                int sameCount = 0;
    
                for (int i = 0; i < candidates.Count; i++)
                {
                    int pts = candidates[i].pts;
                    if (pts < prevPts)
                    {
                        position += sameCount;
                        sameCount  = 0;
                        prevPts    = pts;
                    }
                    sameCount++;
    
                    if (position <= EconomyConfig.ETERNAL_TOP_SLOTS)
                        eternalIds.Add(candidates[i].uid);
                    else if (position > EconomyConfig.ETERNAL_TOP_SLOTS)
                        break; // no further ties can qualify
                }
            }
    
            // Apply / revoke Eternal
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                // Get all users currently in this season
                var allUsers = new List<(string uid, int pts, int rankId)>();
                using (var q = new SQLiteCommand("SELECT user_id, seasonal_points, rank_id FROM seasonal_stats WHERE season_id=@sid;", conn))
                {
                    q.Parameters.AddWithValue("@sid", sid);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                            allUsers.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
                }
    
                foreach (var (uid, pts, rankId) in allUsers)
                {
                    bool shouldBeEternal = eternalIds.Contains(uid);
                    bool isEternal       = (rankId == 9);
    
                    if (shouldBeEternal && !isEternal)
                    {
                        // Promote to Eternal
                        using (var q = new SQLiteCommand("UPDATE seasonal_stats SET rank_id=9, rank_change=1 WHERE user_id=@uid AND season_id=@sid;", conn))
                        {
                            q.Parameters.AddWithValue("@uid", uid);
                            q.Parameters.AddWithValue("@sid", sid);
                            q.ExecuteNonQuery();
                        }
                        using (var q = new SQLiteCommand("UPDATE users SET lifetime_peak_rank_id=MAX(lifetime_peak_rank_id,9), updated_at=datetime('now') WHERE user_id=@uid;", conn))
                        {
                            q.Parameters.AddWithValue("@uid", uid);
                            q.ExecuteNonQuery();
                        }
                        CPH.LogInfo($"[Economy] {uid} promoted to Eternal!");
                    }
                    else if (!shouldBeEternal && isEternal)
                    {
                        // Demote to appropriate threshold rank
                        int correctRank = EconomyConfig.CalcRankId(pts);
                        using (var q = new SQLiteCommand("UPDATE seasonal_stats SET rank_id=@nr, rank_change=-1 WHERE user_id=@uid AND season_id=@sid;", conn))
                        {
                            q.Parameters.AddWithValue("@nr",  correctRank);
                            q.Parameters.AddWithValue("@uid", uid);
                            q.Parameters.AddWithValue("@sid", sid);
                            q.ExecuteNonQuery();
                        }
                        CPH.LogInfo($"[Economy] {uid} demoted from Eternal to rank {correctRank}.");
                    }
                }
            }
    
            CPH.LogInfo($"[Economy] Eternal rank check complete. {eternalIds.Count} Eternal user(s).");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("ETERNAL RANK SCHEDULER (every 15 minutes)", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("ETERNAL RANK SCHEDULER (every 15 minutes)", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: Eternal Rank Scheduler


// =============================================================================
// ██  ACTION 8 — SEASONAL RESET (run at start of each new month)
// =============================================================================
// Trigger: Streamer.bot Timer (monthly) or manual run
//
// • Sets seasonal_points = 0, rank_id = 0 for ALL users in the current season.
// • Creates a new season record.
// • Lifetime points, lifetime rank, and lifetime stats are UNTOUCHED.
// =============================================================================
#region ACTION: Seasonal Reset

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("SEASONAL RESET (run at start of each new month)", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            int oldSeasonId = EconomyDb.CurrentSeasonId();
            string newSeasonName = $"Season {oldSeasonId + 1}";
    
            using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    // Close old season
                    using (var q = new SQLiteCommand("UPDATE seasons SET end_date=date('now') WHERE id=@sid;", conn, tx))
                    {
                        q.Parameters.AddWithValue("@sid", oldSeasonId);
                        q.ExecuteNonQuery();
                    }
                    // Create new season
                    using (var q = new SQLiteCommand("INSERT INTO seasons(name, start_date) VALUES(@n, date('now'));", conn, tx))
                    {
                        q.Parameters.AddWithValue("@n", newSeasonName);
                        q.ExecuteNonQuery();
                    }
                    int newSeasonId = (int)(long)new SQLiteCommand("SELECT last_insert_rowid();", conn, tx).ExecuteScalar();
    
                    // Migrate all users to new season with zeroed stats
                    using (var q = new SQLiteCommand(@"
                        INSERT INTO seasonal_stats (user_id, season_id, seasonal_points, rank_id, rank_change)
                        SELECT user_id, @nsid, 0, 0, 0
                        FROM users;", conn, tx))
                    {
                        q.Parameters.AddWithValue("@nsid", newSeasonId);
                        q.ExecuteNonQuery();
                    }
    
                    tx.Commit();
                }
            }
    
            CPH.WebsocketBroadcastString(JsonConvert.SerializeObject(new { @event = "season_reset", newSeason = newSeasonName }));
            CPH.SendMessage($"🔄 Season reset complete! Welcome to {newSeasonName}. All seasonal points zeroed. Grind starts NOW!");
            CPH.LogInfo($"[Economy] Seasonal reset: old={oldSeasonId}, new={newSeasonName}");
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("SEASONAL RESET (run at start of each new month)", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("SEASONAL RESET (run at start of each new month)", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: Seasonal Reset


// =============================================================================
// ██  ACTION 9 — CHAT MESSAGE WEBSOCKET PAYLOAD
// =============================================================================
// Trigger: Streamer.bot "Chat Message" event (fires on every chat message)
//
// Builds and broadcasts the full payload the OBS overlay consumes.
// =============================================================================
#region ACTION: Chat Message WebSocket Payload

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("CHAT MESSAGE WEBSOCKET PAYLOAD", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {    
            var    args     = CPH.GetArgs();
            string userId   = args.ContainsKey("userId")  ? args["userId"].ToString()  : "";
            string username = args.ContainsKey("user")    ? args["user"].ToString()    : "";
            string message  = args.ContainsKey("message") ? args["message"].ToString() : "";
    
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(message)) return false;
    
            // Skip bot messages (optional: add your bot's user ID here)
            // if (userId == "YOUR_BOT_ID") return false;
    
            EconomyDb.EnsureUser(userId, username);
            var stats = EconomyDb.GetStats(userId);
    
            var rank          = EconomyConfig.GetRank(stats.RankId);
            var lifetimePeak  = EconomyConfig.GetRank(stats.LifetimePeakRankId);
    
            var payload = JsonConvert.SerializeObject(new
            {
                @event                = "chat_message",
                userId,
                username,
                message,
                points                = stats.SeasonalPoints,
                rankId                = stats.RankId,
                rankName              = rank.Name,
                rankIcon              = $"icons/{rank.Name.ToLower()}.svg",
                rankColor             = rank.Color,
                lifetimePeakRankId    = stats.LifetimePeakRankId,
                lifetimePeakRankName  = lifetimePeak.Name,
                lifetimePeakRankColor = lifetimePeak.Color,   // ← used for "Legacy Aura" glow
            });
    
            CPH.WebsocketBroadcastString(payload);
            return true;
        }
        catch (Exception ex)
        {
            EconomyLogger.Fatal("CHAT MESSAGE WEBSOCKET PAYLOAD", "Action crashed!", ex, traceId);
            return false;
        }
        finally
        {
            EconomyLogger.Trace("CHAT MESSAGE WEBSOCKET PAYLOAD", "Action execution finished.", traceId);
        }
    }
}
#endregion ACTION: Chat Message WebSocket Payload


// =============================================================================
// ██  ACTION 10 — !rank [user]
// =============================================================================
// Trigger: Command "!rank"
// =============================================================================
#region ACTION: !rank

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        var    args     = CPH.GetArgs();
        string userId   = args["userId"].ToString();
        string username = args["user"].ToString();
        
        // Handle target user if provided
        string target = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim().Replace("@", "") : "";
        if (!string.IsNullOrEmpty(target))
        {
            // In a real Streamer.bot setup, you'd use CPH.TryGetUserId(target, out string tid)
            // For now, we'll assume the user is checking their own or we'd need a name-to-id lookup in DB.
            // Simplified: only check self if no exact match found.
        }

        EconomyDb.EnsureUser(userId, username);
        var stats = EconomyDb.GetStats(userId);
        var rank  = EconomyConfig.GetRank(stats.RankId);
        var peak  = EconomyConfig.GetRank(stats.LifetimePeakRankId);

        string msg = $"@{username} Rank: {rank.Name} | Points: {stats.SeasonalPoints.toLocaleString()} | Peak: {peak.Name} | Lifetime: {stats.LifetimePoints.toLocaleString()}";
        CPH.SendMessage(msg);
        return true;
    }
}
#endregion ACTION: !rank


// =============================================================================
// ██  ACTION 11 — !top
// =============================================================================
// Trigger: Command "!top"
// Shows Top 5 users by seasonal points.
// =============================================================================
#region ACTION: !top

// --- PASTE SHARED HELPER ABOVE THIS LINE ---

public class CPHInline
{
    public IInlineInvokeProxy CPH { get; set; }

    public bool Execute()
    {
        var top = new List<string>();
        using (var conn = new SQLiteConnection(EconomyConfig.ConnStr))
        {
            conn.Open();
            using (var q = new SQLiteCommand("SELECT username, seasonal_points, rank_id FROM v_season_leaderboard LIMIT 5;", conn))
            using (var r = q.ExecuteReader())
            {
                int pos = 1;
                while (r.Read())
                {
                    string name = r.GetString(0);
                    int pts     = r.GetInt32(1);
                    string rank = EconomyConfig.GetRank(r.GetInt32(2)).Name;
                    top.Add($"{pos}. {name} ({rank}) - {pts.toLocaleString()}");
                    pos++;
                }
            }
        }

        if (top.Count == 0)
        {
            CPH.SendMessage("Leaderboard is empty! Start chatting to earn points.");
        }
        else
        {
            CPH.SendMessage("🏆 TOP 5 SEASONAL: " + string.Join(" | ", top));
        }
        return true;
    }
}

// Extension to format numbers with commas
public static class Extensions
{
    public static string toLocaleString(this int val) => val.ToString("N0");
}
#endregion ACTION: !top
