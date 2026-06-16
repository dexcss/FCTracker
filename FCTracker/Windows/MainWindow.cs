using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FCTracker.Data;

namespace FCTracker.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("FC Tracker###FCTrackerMain")
    {
        this.plugin = plugin;
        // Scale constraints by the global UI scale so the window behaves on 4K /
        // high-DPI where users run a large font scale. MaximumSize stays generous.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(4000, 4000),
        };
    }

    // Deferred actions (applied after the draw loop to avoid mutating the
    // collection while iterating it).
    private ulong pendingDeleteCid;
    private ulong pendingClearFcCid;
    private bool pendingClearAll;

    // Which character row is expanded (0 = none).
    private ulong expandedCid;

    // Current sort state.
    private enum SortCol { Character, FreeCompany, Tag, Level, House, Subs, Credits }
    private SortCol sortColumn = SortCol.Character;
    private bool sortAscending = true;

    public override void Draw()
    {
        var config = plugin.Config;

        DrawSettings(config);

        if (config.Characters.Count == 0)
        {
            ImGui.TextWrapped(
                "No data yet. Log into a character that is in a Free Company. " +
                "Open the Free Company window to capture rank/credits, and " +
                "stand in your FC workshop to capture airships and submersibles.");
            return;
        }

        var rows = SortRows(config.Characters);

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##fctable", 7, flags))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("Tag", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("House", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("Subs", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("Credits", ImGuiTableColumnFlags.WidthStretch, 1.0f);

            // Custom clickable header row (so we control sort cycling/arrows
            // without depending on the binding's sort-spec struct shape).
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            DrawSortHeader(SortCol.Character, "Character");
            DrawSortHeader(SortCol.FreeCompany, "Free Company");
            DrawSortHeader(SortCol.Tag, "Tag");
            DrawSortHeader(SortCol.Level, "Level");
            DrawSortHeader(SortCol.House, "House");
            DrawSortHeader(SortCol.Subs, "Subs");
            DrawSortHeader(SortCol.Credits, "Credits");

            foreach (var c in rows)
                DrawCharacterRow(c);

            ImGui.EndTable();
        }

        // Expanded detail renders full-width below the table (so it isn't clipped to
        // the first column). Only one row is expanded at a time.
        if (expandedCid != 0)
        {
            var expanded = config.Characters.Find(x => x.ContentId == expandedCid);
            if (expanded != null)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                DrawCharacterDetail(expanded);
            }
        }

        ApplyPendingActions();
    }

    private void DrawSortHeader(SortCol col, string label)
    {
        ImGui.TableNextColumn();
        var arrow = sortColumn == col ? (sortAscending ? " ^" : " v") : "";
        if (ImGui.Selectable($"{label}{arrow}###hdr{(int)col}"))
        {
            if (sortColumn == col)
            {
                sortAscending = !sortAscending;
            }
            else
            {
                sortColumn = col;
                // Text columns default A→Z (ascending). Level/House/Subs/Credits
                // default to the "high" side first (highest level, Yes, most credits).
                sortAscending = col is SortCol.Character or SortCol.FreeCompany or SortCol.Tag;
            }
        }
    }


    private System.Collections.Generic.List<CharacterRecord> SortRows(
        System.Collections.Generic.List<CharacterRecord> src)
    {
        var list = new System.Collections.Generic.List<CharacterRecord>(src);

        Comparison<CharacterRecord> cmp = sortColumn switch
        {
            SortCol.Character => (a, b) => string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase),
            SortCol.FreeCompany => (a, b) => string.Compare(a.Fc?.Name ?? "", b.Fc?.Name ?? "", StringComparison.OrdinalIgnoreCase),
            SortCol.Tag => (a, b) => string.Compare(a.Fc?.Tag ?? "", b.Fc?.Tag ?? "", StringComparison.OrdinalIgnoreCase),
            SortCol.Level => (a, b) => (a.Fc?.Rank ?? 0).CompareTo(b.Fc?.Rank ?? 0),
            SortCol.House => (a, b) => HouseSortValue(a).CompareTo(HouseSortValue(b)),
            SortCol.Subs => (a, b) => SubsSortValue(a).CompareTo(SubsSortValue(b)),
            SortCol.Credits => (a, b) => CreditsSortValue(a).CompareTo(CreditsSortValue(b)),
            _ => (a, b) => 0,
        };

        list.Sort((a, b) =>
        {
            var r = cmp(a, b);
            return sortAscending ? r : -r;
        });
        return list;
    }

    // House: Yes(2) > No(1) > unknown(0). Ascending shows No→Yes; descending Yes→No.
    private static int HouseSortValue(CharacterRecord c)
    {
        if (c.Fc?.House == null) return 0;
        return c.Fc.House.HasHouse ? 2 : 1;
    }

    // Subs: has subs(2) > none(1) > not checked(0).
    private static int SubsSortValue(CharacterRecord c)
    {
        if (c.VesselsLastUpdatedUtc == null) return 0;
        return c.Submersibles.Count > 0 ? 2 : 1;
    }

    private static long CreditsSortValue(CharacterRecord c)
    {
        var txt = c.Fc?.CreditsText;
        if (string.IsNullOrEmpty(txt)) return -1;
        var cleaned = txt.Replace(",", "").Replace(".", "").Replace(" ", "");
        return long.TryParse(cleaned, out var v) ? v : -1;
    }

    private void DrawCharacterRow(CharacterRecord c)
    {
        var fc = c.Fc;
        var isExpanded = expandedCid == c.ContentId;

        ImGui.TableNextRow();

        // Character (with server) — clickable to expand.
        ImGui.TableNextColumn();
        var arrow = isExpanded ? "v " : "> ";
        var charName = string.IsNullOrEmpty(c.CharacterName) ? c.ContentId.ToString() : c.CharacterName;
        var charLabel = string.IsNullOrEmpty(c.WorldName) ? charName : $"{charName} @ {c.WorldName}";
        if (ImGui.Selectable($"{arrow}{charLabel}###row{c.ContentId}", isExpanded,
                ImGuiSelectableFlags.SpanAllColumns))
            expandedCid = isExpanded ? 0 : c.ContentId;

        // Free Company
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(fc != null && !string.IsNullOrEmpty(fc.Name) ? fc.Name : "-");

        // Tag
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(fc != null && !string.IsNullOrEmpty(fc.Tag) ? fc.Tag : "-");

        // Level
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(fc != null && fc.Rank > 0 ? fc.Rank.ToString() : "-");

        // House
        ImGui.TableNextColumn();
        if (fc?.House == null)
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "?");
        else if (fc.House.HasHouse)
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1f), "Yes");
        else
            ImGui.TextColored(new Vector4(0.85f, 0.5f, 0.5f, 1f), "No");

        // Subs
        ImGui.TableNextColumn();
        if (c.VesselsLastUpdatedUtc == null)
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "N/A");
        else if (c.Submersibles.Count > 0)
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1f), "Yes");
        else
            ImGui.TextColored(new Vector4(0.85f, 0.5f, 0.5f, 1f), "No");

        // Credits
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(fc != null && !string.IsNullOrEmpty(fc.CreditsText) ? fc.CreditsText : "-");
    }




    private void ApplyPendingActions()
    {
        if (pendingDeleteCid != 0)
        {
            plugin.DeleteCharacter(pendingDeleteCid);
            pendingDeleteCid = 0;
        }
        if (pendingClearFcCid != 0)
        {
            plugin.ClearFcData(pendingClearFcCid);
            pendingClearFcCid = 0;
        }
        if (pendingClearAll)
        {
            plugin.ClearAll();
            pendingClearAll = false;
        }
    }

    private void DrawSettings(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Settings###settings"))
            return;

        var shared = config.UseSharedStorage;
        if (ImGui.Checkbox("Share data across all clients (same Windows user)", ref shared))
        {
            config.UseSharedStorage = shared;
            config.Save();
            plugin.InitStore();
        }
        ImGui.TextDisabled("Reads/writes a single file under %APPDATA%, independent of --roamingPath. " +
                           "Multi-client safe (merged per character).");

        if (config.UseSharedStorage && plugin.Store != null)
        {
            ImGui.TextDisabled($"Path: {plugin.Store.Path_}");
        }

        var ov = config.SharedStoragePathOverride;
        if (ImGui.InputText("Path override (optional)###pathoverride", ref ov, 512))
        {
            config.SharedStoragePathOverride = ov;
            config.Save();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.InitStore();
        ImGui.TextDisabled("Leave empty for the default. Point at a synced/network folder to share across machines.");

        ImGui.Separator();
        DrawImport(config);

        ImGui.Separator();
        if (ImGui.Button("Clear ALL tracked data") && ImGui.GetIO().KeyCtrl)
            pendingClearAll = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ctrl-click to wipe every tracked character and FC. Cannot be undone.");

        ImGui.Separator();
    }

    private string importSummary = string.Empty;

    private void DrawImport(Configuration config)
    {
        ImGui.TextUnformatted("Import existing data");
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
    }


    private void DrawCharacterDetail(CharacterRecord c)
    {
        if (c.Source != "Live")
            ImGui.TextDisabled($"(imported from {c.Source}" +
                               (c.ImportedAtUtc.HasValue ? $", {ToLocal(c.ImportedAtUtc.Value)}" : "") + ")");

        // Per-character management. Ctrl-click guards against accidental clicks.
        if (ImGui.Button($"Remove this character###del{c.ContentId}") && ImGui.GetIO().KeyCtrl)
            pendingDeleteCid = c.ContentId;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ctrl-click to remove this character/FC entry entirely. " +
                             "Note: the character you're currently logged into will be " +
                             "re-added on the next scan, since that's live data.");

        ImGui.SameLine();
        if (ImGui.Button($"Clear FC data only###clearfc{c.ContentId}") && ImGui.GetIO().KeyCtrl)
            pendingClearFcCid = c.ContentId;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ctrl-click to drop this character's FC snapshot and vessels, " +
                             "keeping the character and your manual notes.");

        ImGui.Separator();

        if (c.Fc == null)
        {
            ImGui.TextWrapped("No Free Company data captured for this character yet. " +
                              "Open the FC window in game to populate it.");
        }
        else
        {
            var fc = c.Fc;
            ImGui.TextWrapped($"Free Company: {fc.Name}  «{fc.Tag}»");
            ImGui.TextWrapped($"FC Level: {fc.Rank}");
            ImGui.TextWrapped($"Current Master: {fc.Master}");
            ImGui.TextWrapped($"Members: {fc.OnlineMembers} online / {fc.TotalMembers} total");

            DrawHouse(fc.House);

            if (!string.IsNullOrEmpty(fc.CreditsText))
                ImGui.TextWrapped($"FC Credits: {fc.CreditsText}");
            else
                ImGui.TextDisabled("FC Credits: open the FC window in-game to capture");

            ImGui.TextDisabled($"FC data updated: {ToLocal(fc.LastUpdatedUtc)}");
            ImGui.TextDisabled($"First Registered: {ToLocal(c.FirstSeenInFcUtc)}");
        }

        ImGui.Spacing();
        DrawManualMetadata(c);

        ImGui.Spacing();
        DrawVessels("Airships", c.Airships, c);
        DrawVessels("Submersibles", c.Submersibles, c);
    }


    private void DrawHouse(HouseRecord? house)
    {
        if (house == null)
        {
            ImGui.TextDisabled("House: not checked yet (log in / open FC to check).");
            return;
        }

        if (!house.HasHouse)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.5f, 0.5f, 1f), "House: none owned");
            return;
        }

        var loc = house.IsApartment
            ? $"{house.District} — Apartment (Ward {house.Ward})"
            : $"{house.District} — Ward {house.Ward}, Plot {house.Plot}";
        if (!string.IsNullOrEmpty(house.WorldName))
            loc += $" ({house.WorldName})";

        ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1f), $"House: {loc}");
    }

    private void DrawManualMetadata(CharacterRecord c)
    {
        if (!ImGui.CollapsingHeader("Manual notes (founder / house winner)"))
            return;

        ImGui.TextDisabled("The game cannot tell us who founded the FC or who won the " +
                           "house lottery. Record it yourself here; it is saved with this character.");

        var founder = c.ManualFounder;
        if (ImGui.InputText("Founder###founder", ref founder, 128))
        {
            c.ManualFounder = founder;
            plugin.PersistCharacter(c);
        }

        var house = c.ManualHouseWinner;
        if (ImGui.InputText("House winner###house", ref house, 128))
        {
            c.ManualHouseWinner = house;
            plugin.PersistCharacter(c);
        }

        var notes = c.ManualNotes;
        if (ImGui.InputTextMultiline("Notes###notes", ref notes, 1024, new Vector2(-1, 80 * ImGuiHelpers.GlobalScale)))
        {
            c.ManualNotes = notes;
            plugin.PersistCharacter(c);
        }
    }

    private void DrawVessels(string title, System.Collections.Generic.List<VesselRecord> vessels, CharacterRecord c)
    {
        if (vessels.Count == 0)
            return;

        if (!ImGui.CollapsingHeader($"{title} ({vessels.Count})###{title}"))
            return;

        if (c.VesselsLastUpdatedUtc.HasValue)
            ImGui.TextDisabled($"Updated: {ToLocal(c.VesselsLastUpdatedUtc.Value)} (stand in workshop to refresh)");

        if (ImGui.BeginTable($"##{title}tbl", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Rank");
            ImGui.TableSetupColumn("Build");
            ImGui.TableHeadersRow();

            foreach (var v in vessels)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(v.Name);
                ImGui.TableNextColumn(); ImGui.Text(v.RankId.ToString());
                ImGui.TableNextColumn(); ImGui.Text(v.Build);
            }
            ImGui.EndTable();
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    private static string ToLocal(DateTime utc) => utc.ToLocalTime().ToString("g");
}
