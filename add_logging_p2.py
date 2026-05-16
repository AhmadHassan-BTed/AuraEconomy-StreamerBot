import re

def process_file():
    with open('src/bot/EconomySystem.cs', 'r', encoding='utf-8') as f:
        code = f.read()

    # Find all Execute() methods that aren't WatchTime
    def replace_execute(match):
        action_comment = match.group(1) # Action name
        if "Watch Time" in action_comment:
            return match.group(0) # Already instrumented Watch Time
        
        inner_code = match.group(2)
        
        # Simple injection at start and end of method, wrapping in try-catch
        action_name = action_comment.split("—")[1].strip() if "—" in action_comment else action_comment.strip()
        
        replacement = f'''
    public bool Execute()
    {{
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("{action_name}", $">>> ACTION TRIGGERED <<<", traceId);
        try
        {{
            {inner_code}
            EconomyLogger.Info("{action_name}", $"Action completed successfully.", traceId);
            return true;
        }}
        catch (Exception ex)
        {{
            EconomyLogger.Fatal("{action_name}", $"Action failed with exception.", ex, traceId);
            return false;
        }}
    }}
'''
        # We need to make sure the inner code returns properly, or let the try block fall through.
        # Actually, inner_code might have multiple return statements. A simple wrap is fine.
        return match.group(0).replace(f'public bool Execute()\n    {{\n{inner_code}\n    }}', replacement)

    # Let's do something simpler: Just find all public bool Execute() blocks and replace them if they don't have traceId
    
    parts = re.split(r'(// =============================================================================\n// ██  ACTION \d+ — [^\n]+)', code)
    
    new_code = parts[0]
    
    for i in range(1, len(parts), 2):
        action_header = parts[i]
        action_body = parts[i+1]
        
        if "ACTION 1" in action_header:
            new_code += action_header + action_body
            continue
            
        # Extract action name
        match = re.search(r'ACTION \d+ — ([^\n]+)', action_header)
        action_name = match.group(1).strip() if match else "UnknownAction"
        
        # Replace Execute()
        execute_match = re.search(r'public bool Execute\(\)\s*\{([\s\S]*?)\n    \}', action_body)
        if execute_match and "string traceId" not in execute_match.group(1):
            inner_code = execute_match.group(1)
            # Indent inner code for the try block
            indented_inner = "\n".join("    " + line for line in inner_code.split("\n"))
            
            replacement = f'''public bool Execute()
    {{
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        EconomyLogger.Info("{action_name}", ">>> ACTION TRIGGERED <<<", traceId);
        try
        {{{indented_inner}
        }}
        catch (Exception ex)
        {{
            EconomyLogger.Fatal("{action_name}", "Action crashed!", ex, traceId);
            return false;
        }}
        finally
        {{
            EconomyLogger.Trace("{action_name}", "Action execution finished.", traceId);
        }}
    }}'''
            action_body = action_body.replace(execute_match.group(0), replacement)
            
        new_code += action_header + action_body

    # Now instrument EconomyDb.Open and CurrentSeasonId
    open_replacement = '''
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
'''
    new_code = re.sub(r'private static SQLiteConnection Open\(\)\s*\{.*?return conn;\s*\}', open_replacement.strip(), new_code, flags=re.DOTALL)
    
    # We must fix calls to Open() in the rest of EconomyDb
    new_code = new_code.replace('Open()', 'Open(traceId)')
    
    # Let's write it back
    with open('src/bot/EconomySystem.cs', 'w', encoding='utf-8') as f:
        f.write(new_code)

if __name__ == '__main__':
    process_file()
