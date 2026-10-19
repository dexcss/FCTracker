# FC Tracker

A Dalamud plugin (API 15) that tracks, per character, the Free Company you belong to and its details.

Open the window with `/fctracker` (alias `/fct`).

## Features

- **FC-centric sortable table** — one row per Free Company, built from your tracked characters (characters with no FC get their own loose rows). Click any header to sort; click a row to expand full details.
- **Columns** (all toggleable and reorderable in Settings → Columns): Lifestream-to-house button, AutoRetainer relog button, Region, Data Center, World, Account, Sub-runner, Character, FC Owner, Nickname, Free Company, Tag, Members, Level, Subs, Returns, Bid Eligible, House, Credits.
- **FC details:** name, tag, level, current master, online/total members, and FC credits. Credits are read from the in-game FC window and then persist, updating whenever you reopen the window. The FC row shows the FC's own home world, even when your tracked characters live on other worlds of the data center.
- **House checker:** shows whether the FC owns an estate and, if so, the district, ward, plot, and world. Works anywhere — you don't have to be at the house. The TP button sends Lifestream straight to the FC estate.
- **Workshop vessels:** airships and submersibles with rank, build shorthand (SubmarineTracker-style, e.g. `SSUC++`), and a live return countdown per vessel, captured while standing in your FC workshop.
- **Returns column:** submarine return timers keep working while you're away, via AutoRetainer's offline data.
- **Bid Eligible:** per-character housing bid countdown from an editable start date (per-FC day overrides supported), with Set start / Reset controls. Unknown dates show `-` and sort last.
- **House-winner auto-detect:** winning a housing lottery records the winner automatically (editable "Original winner" note).
- **Account aliases:** each game client (Dalamud roaming path) is detected and can be given a friendly name shown in the Account column.
- **Login button:** relogs to any tracked character — via AutoRetainer's relog IPC, the `/autoretainer relog` command on older builds, or Lifestream's ConnectAndLogin when AutoRetainer isn't installed.
- **Shared storage** (optional, on by default): all clients on the same Windows user share one dataset regardless of `--roamingPath`, with multi-client-safe merging.
- **Import** existing data from AutoRetainer (JSON) and Submarine Tracker (SQLite), without touching their files.
- **Custom colors** for timers, ready states, sub counts, low level, and new-member days (Settings → Colors).

## Installing (custom repo)

1. In game: `/xlsettings` -> Experimental -> Custom Plugin Repositories.
2. Add: `https://raw.githubusercontent.com/dexcss/DalamudPlugins/main/repo.json`
3. Save, then find "FC Tracker" in the plugin installer.

## Notes

- "First Registered" / "Days in FC" is the first time this plugin saw the character in the
  FC, not a true in-game join date (the game does not expose one).
- FC credits are only readable while the in-game FC window is open (or the FC credit shop
  agent has been opened); the last value is remembered between sessions.
- The member table lists only your own tracked characters — the total member count is real,
  but the full FC roster cannot be enumerated.
