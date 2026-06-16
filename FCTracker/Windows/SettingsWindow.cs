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

        var group = config.GroupByFc;
        if (ImGui.Checkbox("Group by Free Company (characters nested under each FC)", ref group))
        {
            config.GroupByFc = group;
            config.Save();
        }

        var showAcct = config.ShowAccountColumn;
        if (ImGui.Checkbox("Show account column (uses aliases below)", ref showAcct))
        {
            config.ShowAccountColumn = showAcct;
            config.Save();
        }

        ImGuiHelpers.ScaledDummy(6f);

        // --- Accounts ---
        ImGui.TextUnformatted("Accounts");
        ImGui.Separator();
        ImGui.TextWrapped("Each game client runs under a Dalamud roaming path. The plugin " +
                          "auto-detects the path; give it a friendly alias here. (The launcher's " +
                          "account name isn't available to plugins, so the alias is yours to set.)");

        ImGui.TextDisabled($"This client's path: {plugin.AccountKey}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Add alias for this client"))
        {
            if (!config.AccountAliases.ContainsKey(plugin.AccountKey))
            {
                config.AccountAliases[plugin.AccountKey] = "My account";
                config.Save();
            }
        }

        if (config.AccountAliases.Count > 0)
        {
            if (ImGui.BeginTable("##aliases", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Roaming path");
                ImGui.TableSetupColumn("Alias", ImGuiTableColumnFlags.WidthFixed, 180 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                string? removeKey = null;
                foreach (var kv in config.AccountAliases)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(kv.Key);
                    ImGui.TableNextColumn();
                    var alias = kv.Value;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText($"###alias{kv.Key.GetHashCode()}", ref alias, 64))
                    {
                        config.AccountAliases[kv.Key] = alias;
                        config.Save();
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"x###rm{kv.Key.GetHashCode()}"))
                        removeKey = kv.Key;
                }
                ImGui.EndTable();

                if (removeKey != null)
                {
                    config.AccountAliases.Remove(removeKey);
                    config.Save();
                }
            }
        }

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
