using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FCTracker.Data;

namespace FCTracker.Windows;

public class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private string importSummary = string.Empty;

    private static readonly Vector4 Red = new(0.85f, 0.35f, 0.35f, 1f);

    public SettingsWindow(Plugin plugin)
        : base("FC Tracker — Settings###FCTrackerSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(4000, 4000),
        };
    }

    public override void Draw()
    {
        var config = plugin.Config;

        // --- View ---
        ImGui.TextUnformatted("View");
        ImGui.Separator();

        var showRank = config.ShowMemberFcRank;
        if (ImGui.Checkbox("Capture member FC rank (experimental)", ref showRank))
        {
            config.ShowMemberFcRank = showRank;
            config.Save();
        }
        ImGui.TextDisabled("The game doesn't expose a member's rank cleanly. This scrapes the FC " +
                           "window and only recognises default rank names (Master/Officer/Member/etc.); " +
                           "custom-renamed ranks will show blank. Shown in each FC's character table.");

        var subsort = config.SubsortByRegion;
        if (ImGui.Checkbox("Sub-sort by region (group regions together, then sort within)", ref subsort))
        {
            config.SubsortByRegion = subsort;
            config.Save();
        }
        ImGui.TextDisabled("With this on, sorting by e.g. World keeps NA worlds together, then EU, etc. " +
                           "(Enable the Region column below to show it.)");

        ImGuiHelpers.ScaledDummy(6f);

        // --- Columns ---
        ImGui.TextUnformatted("Columns");
        ImGui.Separator();
        ImGui.TextDisabled("Check to show. Use the arrows to reorder (top = leftmost).");

        var order = MainWindow.OrderedColumnNames(config);
        var moved = false;
        for (var i = 0; i < order.Count && !moved; i++)
        {
            var name = order[i];
            ImGui.PushID($"col{i}");

            var enabled = GetColEnabled(config, name);
            if (ImGui.Checkbox(name, ref enabled))
            {
                SetColEnabled(config, name, enabled);
                config.Save();
            }

            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60 * ImGuiHelpers.GlobalScale);
            if (ImGui.ArrowButton("up", ImGuiDir.Up) && i > 0)
            {
                MoveColumn(config, order, i, i - 1);
                config.Save();
                moved = true;
            }
            ImGui.SameLine();
            if (ImGui.ArrowButton("down", ImGuiDir.Down) && i < order.Count - 1)
            {
                MoveColumn(config, order, i, i + 1);
                config.Save();
                moved = true;
            }

            ImGui.PopID();
        }

        ImGuiHelpers.ScaledDummy(6f);

        // --- Accounts ---
        ImGui.TextUnformatted("Accounts");
        ImGui.Separator();
        ImGui.TextWrapped("Each game client runs under a Dalamud roaming path (one per game account). " +
                          "Give each detected path a friendly alias; it shows in the Account column and " +
                          "for each FC's sub-runner.");

        // Gather distinct roaming paths across tracked characters, plus this client's.
        var paths = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(plugin.AccountKey)) paths.Add(plugin.AccountKey);
        foreach (var c in config.Characters)
            if (!string.IsNullOrEmpty(c.AccountKey)) paths.Add(c.AccountKey);

        if (paths.Count == 0)
        {
            ImGui.TextDisabled("No accounts detected yet. Log into characters to populate them.");
        }
        else if (ImGui.BeginTable("##aliases", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Roaming path");
            ImGui.TableSetupColumn("Alias", ImGuiTableColumnFlags.WidthFixed, 180 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var path in paths)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var thisOne = path == plugin.AccountKey ? " (this client)" : "";
                ImGui.TextWrapped(path + thisOne);
                ImGui.TableNextColumn();
                config.AccountAliases.TryGetValue(path, out var alias);
                alias ??= string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText($"###alias{path.GetHashCode()}", ref alias, 64))
                {
                    config.AccountAliases[path] = alias;
                    config.Save();
                }
            }
            ImGui.EndTable();
        }

        ImGuiHelpers.ScaledDummy(4f);
        var autoOpen = config.AutoOpenFcOnLogin;
        if (ImGui.Checkbox("Auto-open FC window on login when AutoRetainer multimode is on", ref autoOpen))
        {
            config.AutoOpenFcOnLogin = autoOpen;
            config.Save();
        }
        ImGui.TextDisabled("Briefly opens (and closes) the FC window a few seconds after login to refresh " +
                           "level/credits/house. The window flickers on screen when this fires. Off by default.");

        ImGuiHelpers.ScaledDummy(6f);

        // --- Shared storage ---
        ImGui.TextUnformatted("Storage");
        ImGui.Separator();

        var shared = config.UseSharedStorage;
        if (ImGui.Checkbox("Share data across all clients (same Windows user)", ref shared))
        {
            config.UseSharedStorage = shared;
            config.Save();
            plugin.InitStore();
        }
        ImGui.TextDisabled("Reads/writes a single file under %APPDATA%, independent of --roamingPath.");

        if (config.UseSharedStorage && plugin.Store != null)
            ImGui.TextDisabled($"Path: {plugin.Store.Path_}");

        var ov = config.SharedStoragePathOverride;
        if (ImGui.InputText("Path override (optional)###pathoverride", ref ov, 512))
        {
            config.SharedStoragePathOverride = ov;
            config.Save();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.InitStore();

        ImGuiHelpers.ScaledDummy(6f);

        // --- Import ---
        ImGui.TextUnformatted("Import existing data");
        ImGui.Separator();
        ImGui.TextDisabled("Pulls FC and vessel data already collected by other plugins. " +
                           "Your own live data is never overwritten.");

        var arPath = config.AutoRetainerConfigPath;
        if (ImGui.InputTextWithHint("AutoRetainer config###arpath",
                Importer.DefaultAutoRetainerConfig(), ref arPath, 512))
        {
            config.AutoRetainerConfigPath = arPath;
            config.Save();
        }

        var stPath = config.SubmarineTrackerDbPath;
        if (ImGui.InputTextWithHint("SubmarineTracker db###stpath",
                Importer.DefaultSubmarineTrackerDb(), ref stPath, 512))
        {
            config.SubmarineTrackerDbPath = stPath;
            config.Save();
        }

        if (ImGui.Button("Import from AutoRetainer"))
            importSummary = plugin.RunImport(autoRetainer: true, submarineTracker: false);
        ImGui.SameLine();
        if (ImGui.Button("Import from SubmarineTracker"))
            importSummary = plugin.RunImport(autoRetainer: false, submarineTracker: true);
        ImGui.SameLine();
        if (ImGui.Button("Import both"))
            importSummary = plugin.RunImport(autoRetainer: true, submarineTracker: true);

        if (!string.IsNullOrEmpty(importSummary))
        {
            ImGui.TextDisabled("Result:");
            ImGui.TextWrapped(importSummary);
        }

        ImGuiHelpers.ScaledDummy(8f);

        // --- Danger zone: reset everything with two confirmations ---
        ImGui.TextUnformatted("Danger zone");
        ImGui.Separator();
        DrawResetEverything();
    }

    private static bool GetColEnabled(Configuration c, string name) => name switch
    {
        "TP" => c.ColTp,
        "LOG" => c.ColLogin,
        "Region" => c.ColRegion,
        "World" => c.ColWorld,
        "Account" => c.ColAccount,
        "Sub-runner" => c.ColSubRunner,
        "Custom name" => c.ColCustomName,
        "Free Company" => c.ColFc,
        "Tag" => c.ColTag,
        "Members" => c.ColMembers,
        "Level" => c.ColLevel,
        "Subs" => c.ColSubs,
        "House" => c.ColHouse,
        "Credits" => c.ColCredits,
        _ => false,
    };

    private static void SetColEnabled(Configuration c, string name, bool v)
    {
        switch (name)
        {
            case "TP": c.ColTp = v; break;
            case "LOG": c.ColLogin = v; break;
            case "Region": c.ColRegion = v; break;
            case "World": c.ColWorld = v; break;
            case "Account": c.ColAccount = v; break;
            case "Sub-runner": c.ColSubRunner = v; break;
            case "Custom name": c.ColCustomName = v; break;
            case "Free Company": c.ColFc = v; break;
            case "Tag": c.ColTag = v; break;
            case "Members": c.ColMembers = v; break;
            case "Level": c.ColLevel = v; break;
            case "Subs": c.ColSubs = v; break;
            case "House": c.ColHouse = v; break;
            case "Credits": c.ColCredits = v; break;
        }
    }

    // Persists the current visible order into config.ColumnOrder, with the moved item
    // shifted from -> to.
    private static void MoveColumn(Configuration c, System.Collections.Generic.List<string> order, int from, int to)
    {
        if (from < 0 || from >= order.Count || to < 0 || to >= order.Count) return;
        var item = order[from];
        order.RemoveAt(from);
        order.Insert(to, item);
        c.ColumnOrder = new System.Collections.Generic.List<string>(order);
    }

    private void DrawResetEverything()
    {
        const string popup1 = "##reset_confirm1";
        const string popup2 = "##reset_confirm2";

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.15f, 0.15f, 1f));
        if (ImGui.Button("Reset Everything"))
            ImGui.OpenPopup(popup1);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("Wipes every tracked character, FC, and note.");

        // First confirmation.
        if (ImGui.BeginPopup(popup1))
        {
            ImGui.TextColored(Red, "ARE YOU SURE? This deletes ALL tracked data.");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
            if (ImGui.Button("Yes, continue###reset_yes1"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.OpenPopup(popup2);
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel###reset_cancel1"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // Second confirmation (separate popup so it's a distinct, deliberate click).
        if (ImGui.BeginPopup(popup2))
        {
            ImGui.TextColored(Red, "FINAL CONFIRMATION. This cannot be undone.");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.05f, 0.05f, 1f));
            if (ImGui.Button("Delete everything###reset_yes2"))
            {
                plugin.ClearAll();
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel###reset_cancel2"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}
