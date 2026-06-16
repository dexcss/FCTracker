# FC Tracker

A Dalamud plugin (API 15) that tracks, per character, the Free Company you belong to and its details.

Open the window with `/fctracker` (alias `/fct`).

## Features

- **Sortable table** of all tracked characters: Character (with server), Free Company, Tag, Level, House (Yes/No), Subs (Yes/No/N-A), and FC Credits. Click any column header to sort; click a row to expand its full details below.
- **FC details:** name, tag, level, current master, online/total members, and FC credits. Credits are read from the in-game FC window and then persist, updating whenever you reopen the window.
- **House checker:** shows whether the FC owns an estate and, if so, the district, ward, plot, and world. Works anywhere — you don't have to be at the house.
- **Workshop vessels:** airships and submersibles with rank and build shorthand (e.g. `SSUC++`), captured while standing in your FC workshop.
- **First Registered** date per character, plus editable manual notes for founder / house-winner info the game doesn't expose.
- **Shared storage** (optional, on by default): all clients on the same Windows user share one dataset regardless of `--roamingPath`, with multi-client-safe merging.
- **Import** existing data from AutoRetainer and Submarine Tracker.

## Installing (custom repo)

1. In game: `/xlsettings` -> Experimental -> Custom Plugin Repositories.
2. Add: `https://raw.githubusercontent.com/dexcss/FCTracker/main/repo.json`
3. Save, then find "FC Tracker" in the plugin installer.

## Building from source

Requires the .NET 10 SDK and Dalamud (via XIVLauncher). From the repo root:

```
dotnet build FCTracker/FCTracker.csproj -c Release
```

The plugin is built against `Dalamud.NET.Sdk/15.0.0` (API 15). `Microsoft.Data.Sqlite`
is used only to read Submarine Tracker's database during import.

## Notes

- "First Registered" is the first time this plugin saw the character in the FC, not a
  true in-game join date (the game does not expose one).
- FC credits are only readable while the in-game FC window is open; the last value is
  remembered between sessions.
