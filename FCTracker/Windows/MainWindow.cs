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
    private string search = string.Empty;

    // Current sort state.
    private enum SortCol { Character, World, FreeCompany, Tag, Level, House, Subs, Credits }
    private SortCol sortColumn = SortCol.Character;
    private bool sortAscending = true;

    public override void Draw()
    {
        var config = plugin.Config;

        // Top bar: search + gear (settings) on the right.
        DrawTopBar(config);

        if (config.Characters.Count == 0)
        {
            ImGui.TextWrapped(
                "No data yet. Log into a character that is in a Free Company. " +
                "Open the Free Company window to capture level/credits, and " +
                "stand in your FC workshop to capture airships and submersibles.");
            return;
        }

        var filtered = Filter(config.Characters);

        if (config.GroupByFc)
            DrawGrouped(filtered);
        else
            DrawFlat(filtered);

        ApplyPendingActions();
    }

    private void DrawTopBar(Configuration config)
    {
        // Counts.
        var fcIds = new System.Collections.Generic.HashSet<ulong>();
        var houses = 0;
        foreach (var c in config.Characters)
        {
            if (c.Fc != null && c.Fc.FreeCompanyId != 0) fcIds.Add(c.Fc.FreeCompanyId);
            if (c.Fc?.House != null && c.Fc.House.HasHouse) houses++;
        }

        ImGui.TextDisabled($"{config.Characters.Count} characters · {fcIds.Count} FCs · {houses} houses");

        // Gear button, right-aligned.
        var gear = "Settings";
        var gearW = ImGui.CalcTextSize(gear).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - gearW);
        if (ImGui.SmallButton(gear))
            plugin.OpenSettings();

        // Search bar.
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search character, FC, tag, or world (e.g. \"ultr\")...", ref search, 64);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private System.Collections.Generic.List<CharacterRecord> Filter(
        System.Collections.Generic.List<CharacterRecord> src)
    {
        if (string.IsNullOrWhiteSpace(search))
            return SortRows(src);

        var q = search.Trim();
        var outList = new System.Collections.Generic.List<CharacterRecord>();
        foreach (var c in src)
        {
            var hay = $"{c.CharacterName} {c.WorldName} {c.Fc?.Name} {c.Fc?.Tag}";
            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                outList.Add(c);
        }
        return SortRows(outList);
    }

    // ---- Flat list (default) ----
    private void DrawFlat(System.Collections.Generic.List<CharacterRecord> rows)
    {
        var cols = plugin.Config.ShowAccountColumn ? 9 : 8;
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

        if (ImGui.BeginTable("##fctable", cols, flags))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Tag", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("House", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("Subs", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("Credits", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            if (plugin.Config.ShowAccountColumn)
                ImGui.TableSetupColumn("Account", ImGuiTableColumnFlags.WidthStretch, 1.0f);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            DrawSortHeader(SortCol.Character, "Character");
            DrawSortHeader(SortCol.World, "World");
            DrawSortHeader(SortCol.FreeCompany, "Free Company");
            DrawSortHeader(SortCol.Tag, "Tag");
            DrawSortHeader(SortCol.Level, "Level");
            DrawSortHeader(SortCol.House, "House");
            DrawSortHeader(SortCol.Subs, "Subs");
            DrawSortHeader(SortCol.Credits, "Credits");
            if (plugin.Config.ShowAccountColumn)
            {
                ImGui.TableNextColumn();
                ImGui.TextDisabled("Account");
            }

            foreach (var c in rows)
                DrawCharacterRow(c);

            ImGui.EndTable();
        }
    }

    // ---- Grouped by FC ----
    private void DrawGrouped(System.Collections.Generic.List<CharacterRecord> rows)
    {
        // Bucket by FC name (characters with no FC go under "Not in an FC").
        var groups = new System.Collections.Generic.SortedDictionary<string,
            System.Collections.Generic.List<CharacterRecord>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in rows)
        {
            var key = c.Fc != null && !string.IsNullOrEmpty(c.Fc.Name) ? c.Fc.Name : "Not in an FC";
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new();
            list.Add(c);
        }

        foreach (var g in groups)
        {
            var first = g.Value[0].Fc;
            var header = first != null && !string.IsNullOrEmpty(first.Tag)
                ? $"{g.Key}  «{first.Tag}»  ({g.Value.Count})"
                : $"{g.Key}  ({g.Value.Count})";

            if (ImGui.CollapsingHeader($"{header}###grp{g.Key}"))
            {
                ImGui.Indent(16 * ImGuiHelpers.GlobalScale);
                foreach (var c in g.Value)
                {
                    var name = string.IsNullOrEmpty(c.WorldName) ? c.CharacterName : $"{c.CharacterName} @ {c.WorldName}";
                    var tri = expandedCid == c.ContentId ? "\u25BC " : "\u25B6 "; // ▼ / ▶
                    if (ImGui.Selectable($"{tri}{name}###grow{c.ContentId}", expandedCid == c.ContentId))
                        expandedCid = expandedCid == c.ContentId ? 0 : c.ContentId;
                    if (expandedCid == c.ContentId)
                    {
                        ImGui.Indent(12 * ImGuiHelpers.GlobalScale);
                        DrawCharacterDetail(c);
                        ImGui.Unindent(12 * ImGuiHelpers.GlobalScale);
                    }
                }
                ImGui.Unindent(16 * ImGuiHelpers.GlobalScale);
            }
        }
    }

    private void DrawSortHeader(SortCol col, string label)
    {
        ImGui.TableNextColumn();
        var arrow = sortColumn == col ? (sortAscending ? " \u25B2" : " \u25BC") : ""; // ▲ / ▼
        if (ImGui.Selectable($"{label}{arrow}###hdr{(int)col}"))
        {
            if (sortColumn == col)
                sortAscending = !sortAscending;
            else
            {
                sortColumn = col;
                // Text columns default A→Z; numeric/boolean default to "high" first.
                sortAscending = col is SortCol.Character or SortCol.World or SortCol.FreeCompany or SortCol.Tag;
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
            SortCol.World => (a, b) => string.Compare(a.WorldName, b.WorldName, StringComparison.OrdinalIgnoreCase),
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

    private static int HouseSortValue(CharacterRecord c)
    {
        if (c.Fc?.House == null) return 0;
        return c.Fc.House.HasHouse ? 2 : 1;
    }

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

    private string AccountLabel(CharacterRecord c)
    {
        if (string.IsNullOrEmpty(c.AccountKey)) return "-";
        return plugin.Config.AccountAliases.TryGetValue(c.AccountKey, out var alias) && !string.IsNullOrEmpty(alias)
            ? alias
            : "(unnamed)";
    }

    private void DrawCharacterRow(CharacterRecord c)
    {
        var fc = c.Fc;
        var isExpanded = expandedCid == c.ContentId;

        ImGui.TableNextRow();

        // Character — clickable triangle to expand.
        ImGui.TableNextColumn();
        var tri = isExpanded ? "\u25BC " : "\u25B6 "; // ▼ / ▶
        var charName = string.IsNullOrEmpty(c.CharacterName) ? c.ContentId.ToString() : c.CharacterName;
        if (ImGui.Selectable($"{tri}{charName}###row{c.ContentId}", isExpanded,
                ImGuiSelectableFlags.SpanAllColumns))
            expandedCid = isExpanded ? 0 : c.ContentId;

        // World
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrEmpty(c.WorldName) ? "-" : c.WorldName);

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

        // Account (optional)
        if (plugin.Config.ShowAccountColumn)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(AccountLabel(c));
        }

        // Per-row expanded detail, directly beneath this row (spans full width).
        if (isExpanded)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawCharacterDetail(c);
        }
    }

    private void ApplyPendingActions()
    {
        if (pendingDeleteCid != 0)
        {
            plugin.DeleteCharacter(pendingDeleteCid);
            if (expandedCid == pendingDeleteCid) expandedCid = 0;
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
