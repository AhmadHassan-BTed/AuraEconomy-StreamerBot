# 🚀 Stream Gamification & Economy System: Setup Guide

Welcome! This system adds a powerful "Pure Economy" and ranking system to your stream. Viewers earn points just by watching, can bet on your games, and reach legendary ranks like **Eternal**.

---

## 🛠️ Step 1: Preparing the Database
Think of this as the "brain" of your system where all points and ranks are stored.

1.  **Download a Database Tool**: Download [DB Browser for SQLite](https://sqlitebrowser.org/) (it’s free and easy).
2.  **Create the Database**: 
    *   Open DB Browser.
    *   Click `File` -> `New Database` and save it somewhere safe (e.g., `C:\StreamerBot\Data\economy.db`).
    *   Go to the `Execute SQL` tab, copy the entire content of the `schema.sql` file provided, and click the **Play** button.
3.  **Link the Bot**: 
    *   Open the `EconomySystem.cs` file.
    *   Look for line 49: `public const string DB_PATH = @"..."`. 
    *   Change the path inside the quotes to match where you saved your database (e.g., `@"C:\StreamerBot\Data\economy.db"`). **Keep the `@` symbol!**

---

## 🤖 Step 2: Streamer.bot Configuration
We need to give Streamer.bot the "tools" it needs to read the database.

1.  **Add the "Driver"**:
    *   In Streamer.bot, go to `C# Compiler` -> `References`.
    *   Right-click and add `System.Data.SQLite.dll` (this is the "driver" you need to download—usually found in the SQLite download package).
    *   Ensure `Newtonsoft.Json.dll` is also in the list (it usually is by default).

2.  **The "Helper" Rule**:
    *   Inside `EconomySystem.cs`, you'll see a section called `SHARED HELPER`. 
    *   **Crucial**: Whenever you create a C# code block in Streamer.bot, you *must* copy that "Helper" section and paste it at the very top of the editor.

---

## ⚡ Step 3: Setting Up Your Actions
You need to create "Actions" in Streamer.bot for each feature. For every action, add a **Core -> C# -> Execute C# Code** sub-action.

| Feature | Streamer.bot Trigger | Code to Copy from `EconomySystem.cs` |
| :--- | :--- | :--- |
| **Watch Time** | Trigger: **Twitch -> General -> Present Viewers** | `SHARED HELPER` + `ACTION 1` |
| **Chat Alerts** | Twitch -> Chat Message | `SHARED HELPER` + `ACTION 9` |
| **!annuncio** | Command: `!annuncio` | `SHARED HELPER` + `ACTION 2` |
| **Betting** | Commands: `!startbet`, `!bet`, `!lockbet`, `!resolvebet` | `SHARED HELPER` + `ACTIONS 3 to 6` (in separate actions) |
| **Leaderboard** | Timer (15 min) | `SHARED HELPER` + `ACTION 7` |
| **Monthly Reset** | Manual / Monthly Timer | `SHARED HELPER` + `ACTION 8` |
| **Check Rank** | Command: `!rank` | `SHARED HELPER` + `ACTION 10` |
| **Top 5 List** | Command: `!top` | `SHARED HELPER` + `ACTION 11` |

> [!IMPORTANT]
> **Setting Up the Watch Time Action (Crucial for Points!)**
> To ensure viewers get points every minute:
> 1. In Streamer.bot, go to your **Watch Time** action.
> 2. Right-click in the **Triggers** box.
> 3. Navigate to **Twitch -> General -> Present Viewers**.
> 4. Go to **Platforms -> Twitch -> Settings**. Under the "Present Viewers" section, ensure it is enabled and set to update every **1 minute**.

> [!TIP]
> **Don't forget the WebSocket!** Go to `Servers/Clients` -> `WebSocket Server`. Set Port to `8080`, check `Auto Start`, and click `Start`. This sends the data to your OBS overlay.

---

## 📺 Step 4: Adding the OBS Overlay
Make your stream look professional with the dynamic chat and notifications.

1.  **Add Source**: In OBS, add a new **Browser Source**.
2.  **File**: Check "Local File" and select `Overlay.html`.
3.  **Size**: Set Width to `1920` and Height to `1080`.
4.  **Icons**: 
    *   In the same folder as `Overlay.html`, create a folder named `icons`.
    *   Put your rank icons inside (e.g., `wood.svg`, `bronze.svg`, etc.). The names must be lowercase.

---

## 💎 How Ranks Work
The system automatically handles ranks based on points:
*   **Wood to Grandmaster**: Based on seasonal points thresholds (e.g., 500 for Bronze).
*   **Eternal**: The prestigious **Top 3** users! They get a special golden glow in chat.
*   **Legacy Aura**: If a user reached "Master" last season but is currently "Wood", their name will still have a purple glow—a permanent badge of honor!

---

## ❓ Need to Change Costs or Ranks?
Open `EconomySystem.cs` and look at the `EconomyConfig` section at the top. You can easily change:
*   `ANNUNCIO_COST`: How much `!annuncio` costs.
*   `WATCH_TIME_POINTS`: Points earned per minute.
*   `RANKS`: Point requirements for each rank.

**Enjoy your new economy system!** 🚀
