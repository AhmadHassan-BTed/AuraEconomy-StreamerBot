import re

def process_file():
    with open('src/bot/EconomySystem.cs', 'r', encoding='utf-8') as f:
        code = f.read()

    # 1. Modify EconomyConfig
    code = code.replace(
        'public const string DB_PATH  = @"C:\\StreamerBot\\Data\\economy.db";',
        'public const string LOG_DIR  = @"C:\\StreamerBot\\Data\\Logs";\n    public const string DB_PATH  = @"C:\\StreamerBot\\Data\\economy.db";'
    )

    # 2. Inject EconomyLogger right before RankDef
    logger_code = '''
public static class EconomyLogger
{
    private static readonly object _lock = new object();
    private static string _sessionFile;
    private static string _sessionId;

    public enum LogLevel { TRACE, DEBUG, INFO, WARN, ERROR, FATAL }

    static EconomyLogger()
    {
        try
        {
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!System.IO.Directory.Exists(EconomyConfig.LOG_DIR))
                System.IO.Directory.CreateDirectory(EconomyConfig.LOG_DIR);
            
            _sessionFile = System.IO.Path.Combine(EconomyConfig.LOG_DIR, $"economy_session_{DateTime.Now:yyyyMMdd_HHmmss}_{_sessionId}.log");

            Log(LogLevel.INFO, "SYSTEM", "===================================================");
            Log(LogLevel.INFO, "SYSTEM", $"EconomyLogger started. Session ID: {_sessionId}");
            Log(LogLevel.INFO, "SYSTEM", $"Runtime: {DateTime.Now:O}");
            Log(LogLevel.INFO, "SYSTEM", $"Log File Path: {_sessionFile}");
            Log(LogLevel.INFO, "SYSTEM", "===================================================");
        }
        catch { /* Fallback */ }
    }

    public static void Log(LogLevel level, string component, string message, string stateSnapshot = null, Exception ex = null, string traceId = null)
    {
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string tid = string.IsNullOrEmpty(traceId) ? "" : $"[Trace:{traceId}] ";
            string state = string.IsNullOrEmpty(stateSnapshot) ? "" : $"\\n    -> State: {stateSnapshot}";
            string exception = ex != null ? $"\\n    -> Exception: {ex.Message}\\n    -> StackTrace: {ex.StackTrace}" : "";
            
            string logLine = $"[{timestamp}] [{level}] [{component}] {tid}{message}{state}{exception}";
            
            lock (_lock)
            {
                System.IO.File.AppendAllText(_sessionFile, logLine + Environment.NewLine);
            }
        }
        catch { /* Fail silently */ }
    }

    public static void Trace(string component, string msg, string traceId=null, string state=null) => Log(LogLevel.TRACE, component, msg, state, null, traceId);
    public static void Debug(string component, string msg, string traceId=null, string state=null) => Log(LogLevel.DEBUG, component, msg, state, null, traceId);
    public static void Info(string component, string msg, string traceId=null, string state=null)  => Log(LogLevel.INFO, component, msg, state, null, traceId);
    public static void Warn(string component, string msg, string traceId=null, string state=null)  => Log(LogLevel.WARN, component, msg, state, null, traceId);
    public static void Error(string component, string msg, Exception ex=null, string traceId=null, string state=null) => Log(LogLevel.ERROR, component, msg, state, ex, traceId);
    public static void Fatal(string component, string msg, Exception ex=null, string traceId=null, string state=null) => Log(LogLevel.FATAL, component, msg, state, ex, traceId);
}
'''
    code = code.replace('public struct RankDef', logger_code + '\npublic struct RankDef')

    # 3. Add explicit logging to EconomyDb.EnsureUser
    ensure_user_replacement = '''
    public static void EnsureUser(string userId, string username, string traceId = null)
    {
        EconomyLogger.Debug("EconomyDb", $"EnsureUser called", traceId, $"uid={userId}, uname={username}");
        try
        {
            int sid = CurrentSeasonId();
            using (var c = Open())
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
'''
    code = re.sub(r'public static void EnsureUser\(string userId, string username\).*?tx\.Commit\(\);\s*\}\s*\}', ensure_user_replacement.strip(), code, flags=re.DOTALL)

    # 4. Add logging to EconomyDb.AdjustPoints
    adjust_points_replacement = '''
    public static bool AdjustPoints(string userId, int delta, string traceId = null)
    {
        EconomyLogger.Debug("EconomyDb", $"AdjustPoints called", traceId, $"uid={userId}, delta={delta}");
        try
        {
            int sid = CurrentSeasonId();
            using (var c = Open())
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
'''
    code = re.sub(r'public static bool AdjustPoints\(string userId, int delta\).*?return true;\s*\}', adjust_points_replacement.strip(), code, flags=re.DOTALL)

    # 5. Add explicit logging to EconomyDb.RecalcRank
    recalc_rank_replacement = '''
    public static RankDelta RecalcRank(string userId, string traceId = null)
    {
        EconomyLogger.Trace("EconomyDb", $"RecalcRank called", traceId, $"uid={userId}");
        try
        {
            int sid = CurrentSeasonId();
            using (var c = Open())
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
'''
    code = re.sub(r'public static RankDelta RecalcRank\(string userId\).*?return new RankDelta \{ OldRankId=oldRank, NewRankId=newRank, Delta=delta \};\s*\}\s*\}', recalc_rank_replacement.strip(), code, flags=re.DOTALL)

    # 6. Instrument Watch Time Action (Action 1)
    watch_time_execute = '''
    public bool Execute()
    {
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
'''
    code = re.sub(r'#region ACTION: Watch Time.*?public bool Execute\(\)\s*\{.*?}.*?private Dictionary<string, object> args => CPH\.GetArgs\(\);\s*\}', 
                  '#region ACTION: Watch Time\n\n// --- PASTE SHARED HELPER ABOVE THIS LINE IN STREAMER.BOT ---\n\npublic class CPHInline\n{\n    public IInlineInvokeProxy CPH { get; set; }\n' + watch_time_execute + '\n    private Dictionary<string, object> args => CPH.GetArgs();\n}', 
                  code, flags=re.DOTALL)


    # Write the modified code back
    with open('src/bot/EconomySystem.cs', 'w', encoding='utf-8') as f:
        f.write(code)

if __name__ == '__main__':
    process_file()
