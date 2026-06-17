using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
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
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/fctracker";
    private const string CommandAlias = "/fct";

    public Configuration Config { get; }
    public SharedStore? Store { get; private set; }
    private readonly WindowSystem windowSystem = new("FCTracker");
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;

    // The Dalamud roaming path this client runs under — used to tag characters by
    // account. Independent of which character is logged in.
    public string AccountKey { get; }

    private DateTime lastPoll = DateTime.MinValue;
    private DateTime lastSharedRefresh = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SharedRefreshInterval = TimeSpan.FromSeconds(10);

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        // The plugin config directory sits under the active roaming path; its parent
        // chain identifies the account/client. We use the configfile's directory
        // root as a stable key per roaming path.
        AccountKey = ComputeAccountKey();

        InitStore();

        mainWindow = new MainWindow(this);
        settingsWindow = new SettingsWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);

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
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Framework.Update += OnUpdate;
        ClientState.Login += OnLogin;

        // House-winner auto-detect: when the lottery results SelectYesno appears
        // after a housing placard, check for a "Congratulations" payload.
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(OnSelectYesno);
        Framework.Update -= OnUpdate;
        ClientState.Login -= OnLogin;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        windowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private static string ComputeAccountKey()
    {
        try
        {
            // e.g. ...\XIVLauncher\pluginConfigs  (or a custom --roamingPath root).
            var dir = PluginInterface.GetPluginConfigDirectory();
            // Two levels up from pluginConfigs\FCTracker is the roaming root.
            var pluginConfigs = System.IO.Directory.GetParent(dir)?.FullName ?? dir;
            var roamingRoot = System.IO.Directory.GetParent(pluginConfigs)?.FullName ?? pluginConfigs;
            return roamingRoot;
        }
        catch
        {
            return "default";
        }
    }

    public void OpenSettings() => settingsWindow.IsOpen = true;

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

    // Enrich records with AutoRetainer service-account info for ALL registered
    // characters (not just the logged-in one), so account aliases resolve even for
    // alts not currently online. Throttled by the caller. Best-effort.
    private DateTime lastArEnrich = DateTime.MinValue;
    public void EnrichFromAutoRetainer(bool force = false)
    {
        if (!force && DateTime.UtcNow - lastArEnrich < TimeSpan.FromSeconds(30)) return;
        lastArEnrich = DateTime.UtcNow;

        try
        {
            if (!PluginIpc.IsAutoRetainerAvailable()) return;
            var cids = PluginIpc.GetRegisteredCharacters();
            var dirty = false;
            foreach (var cid in cids)
            {
                var info = PluginIpc.GetArCharInfo(cid);
                if (info == null) continue;
                var rec = Config.Characters.Find(x => x.ContentId == cid);
                if (rec == null) continue; // only enrich characters we already track
                if (rec.IsWorkshopRunner != info.WorkshopEnabled)
                {
                    rec.IsWorkshopRunner = info.WorkshopEnabled;
                    dirty = true;
                }
            }
            if (dirty && Store == null) Config.Save();
        }
        catch (Exception ex) { Log.Error(ex, "FC Tracker AR enrich failed"); }
    }

    private DateTime pendingFcOpenAt = DateTime.MaxValue;

    private void OnLogin()
    {
        // Reset per-login timers so the poll re-reads fresh.
        lastArEnrich = DateTime.MinValue;

        if (!Config.AutoOpenFcOnLogin) return;

        // Only when AutoRetainer multimode is enabled (confirmed IPC).
        if (!PluginIpc.GetMultiModeEnabled()) return;

        // Defer the open a few seconds so the world/UI settles after login.
        pendingFcOpenAt = DateTime.UtcNow.AddSeconds(8);
    }

    private void TryScheduledFcOpen()
    {
        if (DateTime.UtcNow < pendingFcOpenAt) return;
        pendingFcOpenAt = DateTime.MaxValue; // one-shot

        try
        {
            if (!ClientState.IsLoggedIn) return;
            GameReader.OpenFreeCompanyWindow();
            // Actively poll for credits while open, then close. Up to ~6s.
            fcReadDeadline = DateTime.UtcNow.AddSeconds(6);
            fcReadGotCredits = false;
        }
        catch (Exception ex) { Log.Error(ex, "FC auto-open failed"); }
    }

    private DateTime fcReadDeadline = DateTime.MaxValue;
    private bool fcReadGotCredits;
    private void TryScheduledFcRead()
    {
        if (fcReadDeadline == DateTime.MaxValue) return;

        var local = ObjectTable.LocalPlayer;
        var cid = PlayerState.ContentId;
        if (local != null && cid != 0)
        {
            var fc = GameReader.ReadFreeCompany();
            if (fc != null)
            {
                var rec = Config.Characters.Find(x => x.ContentId == cid);
                var credits = AddonScraper.TryReadFcCredits(GameGui, fc.Rank, fc.OnlineMembers, fc.TotalMembers);
                if (!string.IsNullOrEmpty(credits) && rec?.Fc != null)
                {
                    rec.Fc.CreditsText = credits;
                    PersistCharacter(rec);
                    fcReadGotCredits = true;
                }
            }
        }

        // Close once we have credits, or when the deadline passes.
        if (fcReadGotCredits || DateTime.UtcNow >= fcReadDeadline)
        {
            fcReadDeadline = DateTime.MaxValue;
            try { GameReader.CloseFreeCompanyWindow(GameGui); }
            catch (Exception ex) { Log.Error(ex, "FC auto-close failed"); }
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn) return;

        TryScheduledFcOpen();
        TryScheduledFcRead();

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
        record.AccountKey = AccountKey; // roaming path this client runs under

        // Pull AutoRetainer's workshop-runner flag for this character (defines the
        // FC's sub-runner). Best-effort; no-op if AR isn't loaded.
        var ar = PluginIpc.GetArCharInfo(contentId);
        if (ar != null)
            record.IsWorkshopRunner = ar.WorkshopEnabled;

        var changed = false;

        // --- Free Company snapshot ---
        var fc = GameReader.ReadFreeCompany();
        if (fc != null)
        {
            var prev = record.Fc;

            fc.Tag = GameReader.ReadLocalPlayerFcTag(local);
            fc.Region = GameReader.ResolveRegionCode(DataManager, world);

            // Credits: hybrid read (AR credit-shop agent if open, else FC window).
            // When neither is available, carry forward the last known value.
            var credits = AddonScraper.TryReadFcCredits(GameGui, fc.Rank, fc.OnlineMembers, fc.TotalMembers);
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

            // Optional best-effort own-rank scrape.
            if (Config.ShowMemberFcRank)
            {
                var rank = GameReader.TryReadOwnFcRank(GameGui, name);
                if (!string.IsNullOrEmpty(rank) && rank != record.MyFcRank)
                {
                    record.MyFcRank = rank;
                }
            }
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

    // House-winner auto-detect. When you win a housing lottery, interacting with the
    // results placard opens a SelectYesno whose prompt congratulates you on the win.
    // The HousingSignBoard addon may or may not still be open by the time this fires,
    // so we treat it as a hint, not a hard requirement, and match a few win phrases.
    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        try
        {
            var text = GameReader.ReadSelectYesnoPrompt(args.Addon);
            if (string.IsNullOrEmpty(text)) return;

            // Must look like a housing-lottery win. Require a win word AND a housing
            // context word so unrelated "Congratulations" prompts don't trigger.
            var lower = text.ToLowerInvariant();
            var winWord = lower.Contains("congratulations") || lower.Contains("won the lottery")
                          || lower.Contains("winning");
            var housingWord = lower.Contains("plot") || lower.Contains("estate")
                              || lower.Contains("house") || lower.Contains("land")
                              || lower.Contains("residential");

            // If the sign board is open, that alone is enough of a housing context.
            nint signboard = GameGui.GetAddonByName("HousingSignBoard", 1);
            var signboardOpen = signboard != nint.Zero;

            if (!winWord) return;
            if (!housingWord && !signboardOpen) return;

            var contentId = PlayerState.ContentId;
            if (contentId == 0) return;
            var rec = Config.Characters.Find(x => x.ContentId == contentId);
            if (rec == null) return;

            var winner = string.IsNullOrEmpty(rec.WorldName)
                ? rec.CharacterName
                : $"{rec.CharacterName} @ {rec.WorldName}";

            var stamp = $"{winner} (auto-detected {DateTime.Now:yyyy-MM-dd})";

            // Store on the character record, and only if not manually set already.
            if (string.IsNullOrEmpty(rec.ManualHouseWinner))
            {
                rec.ManualHouseWinner = stamp;
                PersistCharacter(rec);
                Log.Info($"FC Tracker: auto-recorded house winner: {winner}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FC Tracker house-winner detect failed");
        }
    }
}
