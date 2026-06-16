using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using FCTracker.Data;
using FCTracker.Game;
using FCTracker.Windows;
using Lumina.Excel.Sheets;

namespace FCTracker;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/fctracker";
    private const string CommandAlias = "/fct";

    public Configuration Config { get; }
    public SharedStore? Store { get; private set; }
    private readonly WindowSystem windowSystem = new("FCTracker");
    private readonly MainWindow mainWindow;

    private DateTime lastPoll = DateTime.MinValue;
    private DateTime lastSharedRefresh = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SharedRefreshInterval = TimeSpan.FromSeconds(10);

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        InitStore();

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the FC Tracker window.",
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the FC Tracker window (alias for /fctracker).",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;
        Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        windowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private void OpenMain() => mainWindow.IsOpen = true;

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    // (Re)build the shared store from current settings and load records into Config.
    public void InitStore()
    {
        if (Config.UseSharedStorage)
        {
            Store = new SharedStore(true, Config.SharedStoragePathOverride);
            Config.Characters = Store.LoadAll();
            Log.Info($"FC Tracker using shared storage: {Store.Path_}");
        }
        else
        {
            Store = null;
            // fall back to whatever was serialized in the normal plugin config
        }
        lastSharedRefresh = DateTime.UtcNow;
    }

    // Persist one character. Shared mode merges into the shared file (multi-client
    // safe); otherwise we save the normal Dalamud config.
    public void PersistCharacter(CharacterRecord record)
    {
        if (Store != null)
            Store.UpsertCharacter(record);
        else
            Config.Save();
    }

    // Run an import from one or both external plugins, persist, and return a
    // human-readable summary for the UI.
    public string RunImport(bool autoRetainer, bool submarineTracker)
    {
        var summary = new System.Text.StringBuilder();

        // Operate on the current in-memory list (already loaded from shared store).
        var records = Config.Characters;

        if (autoRetainer)
        {
            var path = string.IsNullOrWhiteSpace(Config.AutoRetainerConfigPath)
                ? Importer.DefaultAutoRetainerConfig()
                : Config.AutoRetainerConfigPath;
            var r = Importer.ImportAutoRetainer(records, path);
            foreach (var m in r.Messages) summary.AppendLine(m);
        }

        if (submarineTracker)
        {
            var path = string.IsNullOrWhiteSpace(Config.SubmarineTrackerDbPath)
                ? Importer.DefaultSubmarineTrackerDb()
                : Config.SubmarineTrackerDbPath;
            var r = Importer.ImportSubmarineTracker(records, path);
            foreach (var m in r.Messages) summary.AppendLine(m);
        }

        // Persist every (possibly new/updated) record.
        foreach (var rec in records)
            PersistCharacter(rec);

        // Refresh in-memory view from the store so display is consistent.
        if (Store != null) Config.Characters = Store.LoadAll();
        else Config.Save();

        return summary.ToString();
    }

    // Remove a single character/FC entry. Works in both shared and local modes.
    public void DeleteCharacter(ulong contentId)
    {
        Config.Characters.RemoveAll(x => x.ContentId == contentId);
        if (Store != null)
        {
            Store.DeleteCharacter(contentId);
            Config.Characters = Store.LoadAll();
        }
        else
        {
            Config.Save();
        }
    }

    // Clear only the FC snapshot + vessels for a character, keeping the character
    // and its manual notes.
    public void ClearFcData(ulong contentId)
    {
        var rec = Config.Characters.Find(x => x.ContentId == contentId);
        if (rec == null) return;
        rec.Fc = null;
        rec.Airships.Clear();
        rec.Submersibles.Clear();
        rec.VesselsLastUpdatedUtc = null;
        rec.FirstSeenFcId = 0;
        PersistCharacter(rec);
        if (Store != null) Config.Characters = Store.LoadAll();
    }

    public void ClearAll()
    {
        Config.Characters.Clear();
        if (Store != null)
        {
            Store.ClearAll();
            Config.Characters = Store.LoadAll();
        }
        else
        {
            Config.Save();
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn) return;

        // Periodically re-read the shared file so this client sees other clients'
        // updates (their characters, their latest vessel timers, etc).
        if (Store != null && DateTime.UtcNow - lastSharedRefresh >= SharedRefreshInterval)
        {
            lastSharedRefresh = DateTime.UtcNow;
            try { Config.Characters = Store.LoadAll(); }
            catch (Exception ex) { Log.Error(ex, "FC Tracker shared refresh failed"); }
        }

        if (DateTime.UtcNow - lastPoll < PollInterval) return;
        lastPoll = DateTime.UtcNow;

        try { Poll(); }
        catch (Exception ex) { Log.Error(ex, "FC Tracker poll failed"); }
    }

    private void Poll()
    {
        var local = ObjectTable.LocalPlayer;
        if (local == null) return;

        var contentId = PlayerState.ContentId;
        if (contentId == 0) return;

        var name = local.Name.TextValue;
        var world = local.HomeWorld.Value.Name.ExtractText();
        var record = Config.GetOrCreate(contentId, name, world);

        var changed = false;

        // --- Free Company snapshot ---
        var fc = GameReader.ReadFreeCompany();
        if (fc != null)
        {
            var prev = record.Fc;

            fc.Tag = GameReader.ReadLocalPlayerFcTag(local);

            // Credits: only the FC window exposes this. When the window is closed the
            // scraper returns empty, so carry forward the last known value rather than
            // blanking it. A fresh successful read overwrites it (credits change).
            var credits = AddonScraper.TryScrapeFcCredits(GameGui, fc.Rank, fc.OnlineMembers, fc.TotalMembers);
            if (!string.IsNullOrEmpty(credits))
                fc.CreditsText = credits;
            else if (prev != null && !string.IsNullOrEmpty(prev.CreditsText) && prev.FreeCompanyId == fc.FreeCompanyId)
                fc.CreditsText = prev.CreditsText; // preserve last known

            // FC house check (works anywhere, not just at the house/workshop).
            try { fc.House = GameReader.ReadFreeCompanyHouse(DataManager); }
            catch (Exception ex) { Log.Error(ex, "FC house read failed"); }
            // If the house read momentarily failed but we had one before, keep it.
            if (fc.House == null && prev?.House != null && prev.FreeCompanyId == fc.FreeCompanyId)
                fc.House = prev.House;

            // First-registered = first time we ever saw this character in this FC.
            if (record.FirstSeenFcId != fc.FreeCompanyId)
            {
                record.FirstSeenFcId = fc.FreeCompanyId;
                record.FirstSeenInFcUtc = DateTime.UtcNow;
            }

            record.Fc = fc;
            changed = true;
        }

        // --- Workshop vessels (only inside the FC workshop) ---
        if (GameReader.TryReadWorkshopVessels(out var airships, out var subs))
        {
            record.Airships = airships;
            record.Submersibles = subs;
            record.VesselsLastUpdatedUtc = DateTime.UtcNow;
            changed = true;
        }

        if (changed) PersistCharacter(record);
    }
}
