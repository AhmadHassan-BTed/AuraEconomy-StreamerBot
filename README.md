# AuraEconomy: Advanced Streamer.bot Gamification System

![Status](https://img.shields.io/badge/Status-Complete-success)
![Platform](https://img.shields.io/badge/Platform-Streamer.bot-blue)
![Database](https://img.shields.io/badge/Database-SQLite-lightgrey)

**AuraEconomy** is a high-performance, engagement-focused economy system for Twitch streamers. It combines a robust C# backend with a stunning Vanilla JS overlay to create a mini-RPG experience for your viewers.

---

## ✨ Key Features

*   **📈 Dynamic Ranking**: 10 distinct tiers from "Wood" to the prestigious "Eternal" rank.
*   **💠 Legacy Aura**: A permanent visual badge of honor. Viewers keep a colored glow based on their *highest-ever* rank, regardless of monthly resets.
*   **🎲 Integrated Betting**: Advanced escrow-based betting system (Win/Loss) with rank-based wager caps.
*   **📜 !annuncio System**: Premium point-sink command that broadcasts beautiful, animated messages on-screen.
*   **🔄 Seasonal Resets**: Monthly resets that clear seasonal points but preserve lifetime stats and "Aura" status.
*   **🛠️ Robust Accrual**: Verified 1-point-per-minute watch time system using Streamer.bot's native 'Present Viewers' engine for 100% reliability.
*   **⚡ High Performance**: SQLite database configured with WAL mode to allow simultaneous access by your bot and external tools (like Minecraft plugins).

---

## 📂 Project Structure

```text
/
├── docs/
│   └── ImplementationGuide.md    # Simple step-by-step setup for non-technical users
├── src/
│   ├── bot/
│   │   └── EconomySystem.cs      # Core C# logic for Streamer.bot
│   ├── database/
│   │   └── schema.sql            # SQLite database structure & views
│   └── overlay/
│       ├── Overlay.html          # Vanilla JS Chat & Notification Overlay
│       └── icons/                # Rank icons (SVG)
└── README.md                     # Project overview
```

---

## 🚀 Quick Start

1.  **Database**: Run `schema.sql` in a SQLite tool to create your database file.
2.  **Configuration**: Open `EconomySystem.cs` and set your `DB_PATH`.
3.  **Streamer.bot**: Create actions and paste the corresponding code regions from `EconomySystem.cs`. **Ensure "Watch Time" uses the 'Present Viewers' trigger.**
4.  **OBS**: Add `Overlay.html` as a Browser Source (1920x1080).

For detailed instructions, see the [Implementation Guide](docs/ImplementationGuide.md).

---

## 🛠️ Tech Stack

*   **Bot**: Streamer.bot (C# Inline)
*   **Storage**: SQLite (WAL Mode)
*   **Overlay**: Vanilla HTML5, CSS3 (Inter Font, Glassmorphism), JavaScript (WebSockets)

---

## 💎 Rank Thresholds

| Rank | Points | Bet Cap | Aura Color |
| :--- | :--- | :--- | :--- |
| **Wood** | 0 | 25 | #8B6914 |
| **Bronze** | 500 | 50 | #CD7F32 |
| **Silver** | 1,500 | 100 | #A8A9AD |
| ... | ... | ... | ... |
| **Eternal** | Top 3 | 10,000 | #FFE566 |

---

Developed with ❤️ for the streaming community.
