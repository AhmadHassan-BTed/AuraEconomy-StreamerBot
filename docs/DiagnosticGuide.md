#  AuraEconomy: Setup Verification & Diagnostic Guide (Windows 11)

If you are not seeing points being awarded or log files are missing, follow these exact steps to verify your setup and collect the files needed for support.

---

### 1. Verify Folder Permissions
Streamer.bot needs permission to write to your `C:` drive.
1.  Open **File Explorer** and go to `C:\`.
2.  Ensure you have a folder named `StreamerBot`.
3.  Inside, ensure there is a `Data` folder, and inside that, a `Logs` folder.
    *   Path should be: `C:\StreamerBot\Data\Logs`
4.  **Action**: If these don't exist, create them manually.

---

### 2. Force a Code Compile & Test
Even if you are offline, you can force the bot to "wake up" and create a log file.
1.  Open **Streamer.bot**.
2.  Go to the **Actions** tab and find your **Watch Time** action.
3.  In the **Sub-Actions** pane (bottom right), double-click the **Execute C# Code** block.
4.  In the editor window:
    *   Click the **Compile** button. It should say "Compiled Successfully" in green.
    *   Click **Save** (or Close if it asks to save).
5.  **Right-click** the **Execute C# Code** sub-action and select **Test**.
    *   *This "Test" run is what triggers the creation of the log file.*

---

### 3. Check the Internal "Black Box" Log
If the file in Step 1 is still missing, Streamer.bot will tell us why in its own internal log.
1.  Look at the bottom of the main Streamer.bot window.
2.  Click on the **Log** tab (next to 'Status').
3.  Look for any yellow or red lines starting with `[AuraEconomy]`.
4.  **Common Errors**:
    *   `Logger failed: Access to the path is denied`: You need to run Streamer.bot as Administrator.
    *   `Logger failed: Could not find a part of the path`: You missed a folder in Step 1.

---

### 4. Verify References
The bot needs specific "drivers" to talk to the database.
1.  In the C# Editor (from Step 2), click the **Settings** tab at the top.
2.  Click **References**.
3.  Verify that both of these are in the list:
    *   `System.Data.SQLite.dll`
    *   `Newtonsoft.Json.dll`
4.  If `System.Data.SQLite.dll` is missing, the code will never run.

---

### 5. Collecting Files for Support
If you've done the above and it's still not working, please send these **two** files:

1.  **The Database**: `C:\StreamerBot\Data\economy.db`
2.  **The Log**: `C:\StreamerBot\Data\Logs\economy_session_XXXXXXXX_XXXXXX.log` (Send the newest one).

---

> [!TIP]
> If you can't find the `.log` file, take a screenshot of your **Streamer.bot Log tab** (from Step 3) instead.
