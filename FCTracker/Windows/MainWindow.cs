using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FCTracker.Data;
using FCTracker.Game;

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

        // Opportunistically pull AutoRetainer service-account info for tracked chars.
        plugin.EnrichFromAutoRetainer();

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
    private enum FcSortCol { Region, World, Account, Name, Tag, Level, Subs, House, Credits }
    private FcSortCol fcSort = FcSortCol.Name;
    private bool fcSortAsc = true;
    private ulong expandedFcId;

    // A grouped FC entry built from one or more of the user's characters.
    private class FcGroup
    {
        public ulong FcId;
        public string Name = "";
        public string Tag = "";
        public string Region = "";
        public string World = "";
        public byte Level;
        public string Credits = "";
        public HouseRecord? House;
        public int SubCount;          // best subs count among members
        public bool SubsKnown;
        public string SubRunnerAccountKey = ""; // roaming path of the WorkshopEnabled char
        public string SubRunnerName = "";
        public System.Collections.Generic.List<CharacterRecord> Members = new();
    }

    private void DrawGrouped(System.Collections.Generic.List<CharacterRecord> rows)
    {
        // Build FC groups keyed by FCID (fall back to name for safety).
        var map = new System.Collections.Generic.Dictionary<string, FcGroup>();
        foreach (var c in rows)
        {
            var fc = c.Fc;
            var key = fc != null && fc.FreeCompanyId != 0 ? fc.FreeCompanyId.ToString()
                    : fc != null && !string.IsNullOrEmpty(fc.Name) ? "name:" + fc.Name
                    : "none:" + (string.IsNullOrEmpty(c.WorldName) ? "?" : c.WorldName);

            if (!map.TryGetValue(key, out var g))
            {
                map[key] = g = new FcGroup
                {
                    FcId = fc?.FreeCompanyId ?? 0,
                    Name = fc?.Name ?? $"Not in an FC ({(string.IsNullOrEmpty(c.WorldName) ? "?" : c.WorldName)})",
                    Tag = fc?.Tag ?? "",
                    Region = fc?.Region ?? "",
                    World = c.WorldName,
                    Level = fc?.Rank ?? 0,
                    Credits = fc?.CreditsText ?? "",
                    House = fc?.House,
                };
            }
            g.Members.Add(c);

            // best-known subs among the FC's tracked members
            if (c.VesselsLastUpdatedUtc != null)
            {
                g.SubsKnown = true;
                if (c.Submersibles.Count > g.SubCount) g.SubCount = c.Submersibles.Count;
            }
            // subrunner = the FC member flagged WorkshopEnabled in AutoRetainer.
            if (c.IsWorkshopRunner)
            {
                g.SubRunnerAccountKey = c.AccountKey;
                g.SubRunnerName = string.IsNullOrEmpty(c.WorldName) ? c.CharacterName : $"{c.CharacterName} @ {c.WorldName}";
            }
        }

        var groups = new System.Collections.Generic.List<FcGroup>(map.Values);
        SortFcGroups(groups);

        var cfg = plugin.Config;
        var cols = 10;
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

        if (!ImGui.BeginTable("##fcgrouptable", cols, flags))
            return;

        // Frozen header row.
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Go", ImGuiTableColumnFlags.WidthFixed, 28 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Account", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Tag", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Subs", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("House", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Credits", ImGuiTableColumnFlags.WidthStretch, 1.0f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn(); // Go (no sort)
        ImGui.TextDisabled("");
        DrawFcSortHeader(FcSortCol.Region, "Region");
        DrawFcSortHeader(FcSortCol.World, "World");
        DrawFcSortHeader(FcSortCol.Account, "Account");
        DrawFcSortHeader(FcSortCol.Name, "Free Company");
        DrawFcSortHeader(FcSortCol.Tag, "Tag");
        DrawFcSortHeader(FcSortCol.Level, "Level");
        DrawFcSortHeader(FcSortCol.Subs, "Subs");
        DrawFcSortHeader(FcSortCol.House, "House");
        DrawFcSortHeader(FcSortCol.Credits, "Credits");

        foreach (var g in groups)
            DrawFcRow(g, cfg);

        ImGui.EndTable();

        // Expanded FC detail renders full-width BELOW the table so it isn't clipped
        // into the first column.
        if (expandedFcId != 0)
        {
            var open = groups.Find(x => x.FcId == expandedFcId);
            if (open != null)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                DrawFcDetail(open, cfg);
            }
        }
    }

    private void DrawFcSortHeader(FcSortCol col, string label)
    {
        ImGui.TableNextColumn();
        var arrow = fcSort == col ? (fcSortAsc ? " \u25B2" : " \u25BC") : "";
        if (ImGui.Selectable($"{label}{arrow}###fchdr{(int)col}"))
        {
            if (fcSort == col) fcSortAsc = !fcSortAsc;
            else { fcSort = col; fcSortAsc = col is FcSortCol.Region or FcSortCol.World or FcSortCol.Account or FcSortCol.Name or FcSortCol.Tag; }
        }
    }

    private void SortFcGroups(System.Collections.Generic.List<FcGroup> list)
    {
        Comparison<FcGroup> cmp = fcSort switch
        {
            FcSortCol.Region => (a, b) => string.Compare(a.Region, b.Region, StringComparison.OrdinalIgnoreCase),
            FcSortCol.World => (a, b) => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Account => (a, b) => string.Compare(AccountAliasLabel(a.SubRunnerAccountKey), AccountAliasLabel(b.SubRunnerAccountKey), StringComparison.OrdinalIgnoreCase),
            FcSortCol.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Tag => (a, b) => string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Level => (a, b) => a.Level.CompareTo(b.Level),
            FcSortCol.Subs => (a, b) => a.SubCount.CompareTo(b.SubCount),
            FcSortCol.House => (a, b) => (a.House?.HasHouse == true ? 1 : 0).CompareTo(b.House?.HasHouse == true ? 1 : 0),
            FcSortCol.Credits => (a, b) => CreditsVal(a.Credits).CompareTo(CreditsVal(b.Credits)),
            _ => (a, b) => 0,
        };
        list.Sort((a, b) => { var r = cmp(a, b); return fcSortAsc ? r : -r; });
    }

    private static long CreditsVal(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        return long.TryParse(s.Replace(",", "").Replace(".", "").Replace(" ", ""), out var v) ? v : -1;
    }

    private void DrawFcRow(FcGroup g, Configuration cfg)
    {
        var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
        var red = new Vector4(0.85f, 0.5f, 0.5f, 1f);
        var green = new Vector4(0.5f, 0.85f, 0.5f, 1f);
        var hasHouse = g.House?.HasHouse == true;
        var isOpen = expandedFcId == g.FcId && g.FcId != 0;

        ImGui.TableNextRow();

        // Go (Lifestream) button — only when the FC has a house.
        ImGui.TableNextColumn();
        if (hasHouse && g.House != null && !g.House.IsApartment)
        {
            if (ImGui.SmallButton($"\u27A4###go{g.FcId}")) // ➤ runner-ish glyph
                PluginIpc.LifestreamGoToAddress(g.World, g.House.District, g.House.Ward, g.House.Plot);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Lifestream to {g.World} {g.House.District} W{g.House.Ward} P{g.House.Plot}");
        }
        else
        {
            ImGui.TextDisabled("");
        }

        // Region (click to expand the FC).
        ImGui.TableNextColumn();
        var tri = isOpen ? "\u25BC " : "\u25B6 ";
        if (ImGui.Selectable($"{tri}{(string.IsNullOrEmpty(g.Region) ? "-" : g.Region)}###fcrow{g.FcId}",
                isOpen, ImGuiSelectableFlags.SpanAllColumns))
            expandedFcId = isOpen ? 0 : g.FcId;

        // World
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrEmpty(g.World) ? "-" : g.World);

        // Account alias
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(AccountAliasLabel(g.SubRunnerAccountKey));

        // FC Name
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(g.Name);

        // Tag
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrEmpty(g.Tag) ? "-" : g.Tag);

        // Level (red if < 6)
        ImGui.TableNextColumn();
        if (g.Level == 0) ImGui.TextColored(grey, "-");
        else if (g.Level < 6) ImGui.TextColored(red, g.Level.ToString());
        else ImGui.TextUnformatted(g.Level.ToString());

        // Subs #/4
        ImGui.TableNextColumn();
        if (!hasHouse) ImGui.TextColored(grey, "-");
        else if (!g.SubsKnown) ImGui.TextColored(grey, "?/4");
        else ImGui.TextColored(g.SubCount == 0 ? red : green, $"{g.SubCount}/4");

        // House address (short)
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(ShortHouseAddress(g.House));

        // Credits
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrEmpty(g.Credits) ? "-" : g.Credits);
    }

    private string AccountAliasLabel(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return "-";
        return plugin.Config.AccountAliases.TryGetValue(accountKey, out var a) && !string.IsNullOrEmpty(a)
            ? a : "(unnamed)";
    }

    private void DrawFcDetail(FcGroup g, Configuration cfg)
    {
        ImGui.Indent(14 * ImGuiHelpers.GlobalScale);

        // Additional info section.
        var runnerLabel = string.IsNullOrEmpty(g.SubRunnerName)
            ? AccountAliasLabel(g.SubRunnerAccountKey)
            : $"{g.SubRunnerName}  ({AccountAliasLabel(g.SubRunnerAccountKey)})";
        ImGui.TextWrapped($"Sub-runner: {runnerLabel}");

        // Original winner — editable, pulled/stored on the first member.
        var holder = g.Members[0];
        var winner = holder.ManualHouseWinner;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText($"Original winner###winner{g.FcId}", ref winner, 128))
        {
            holder.ManualHouseWinner = winner;
            plugin.PersistCharacter(holder);
        }

        ImGui.TextDisabled($"First registered: {ToLocal(EarliestFirstSeen(g))}");
        if (g.Members[0].Fc != null)
            ImGui.TextDisabled($"Last data update: {ToLocal(g.Members[0].Fc!.LastUpdatedUtc)}");

        // Submersibles dropdown (ranks + builds) — use the member with sub data.
        var subHolder = g.Members.Find(m => m.Submersibles.Count > 0);
        if (subHolder != null)
            DrawVessels("Submersibles", subHolder.Submersibles, subHolder);

        // Characters table.
        ImGuiHelpers.ScaledDummy(4f);
        if (ImGui.BeginTable($"##fcmembers{g.FcId}", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Rank in FC");
            ImGui.TableSetupColumn("Days in FC");
            ImGui.TableHeadersRow();

            foreach (var m in g.Members)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var nm = string.IsNullOrEmpty(m.WorldName) ? m.CharacterName : $"{m.CharacterName} @ {m.WorldName}";
                if (ImGui.Selectable($"{nm}###fcm{m.ContentId}", expandedCid == m.ContentId))
                    expandedCid = expandedCid == m.ContentId ? 0 : m.ContentId;

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(cfg.ShowMemberFcRank && !string.IsNullOrEmpty(m.MyFcRank) ? m.MyFcRank : "-");

                ImGui.TableNextColumn();
                var days = (int)(DateTime.UtcNow - m.FirstSeenInFcUtc).TotalDays;
                if (days <= 30) ImGui.TextColored(new Vector4(0.85f, 0.5f, 0.5f, 1f), days.ToString());
                else ImGui.TextUnformatted(days.ToString());
            }
            ImGui.EndTable();
        }

        // Per-character detail when a member is clicked.
        if (expandedCid != 0)
        {
            var m = g.Members.Find(x => x.ContentId == expandedCid);
            if (m != null)
            {
                ImGui.Separator();
                DrawCharacterDetail(m);
            }
        }

        // Account alias quick-edit hint.
        if (!string.IsNullOrEmpty(g.SubRunnerAccountKey) && !plugin.Config.AccountAliases.ContainsKey(g.SubRunnerAccountKey))
        {
            ImGuiHelpers.ScaledDummy(2f);
            ImGui.TextDisabled("Tip: name this account in Settings → Accounts.");
        }

        ImGui.Unindent(14 * ImGuiHelpers.GlobalScale);
    }

    private static DateTime EarliestFirstSeen(FcGroup g)
    {
        var earliest = DateTime.MaxValue;
        foreach (var m in g.Members)
            if (m.FirstSeenInFcUtc < earliest) earliest = m.FirstSeenInFcUtc;
        return earliest == DateTime.MaxValue ? DateTime.UtcNow : earliest;
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

    // Subs "#/4": "-" if no house; red "0/4" if has house but no subs.
    private static (string text, Vector4 color) SubsCell(CharacterRecord c)
    {
        var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
        var red = new Vector4(0.85f, 0.5f, 0.5f, 1f);
        var green = new Vector4(0.5f, 0.85f, 0.5f, 1f);

        var hasHouse = c.Fc?.House?.HasHouse == true;
        if (!hasHouse) return ("-", grey);

        if (c.VesselsLastUpdatedUtc == null) return ("?/4", grey);
        var n = c.Submersibles.Count;
        return ($"{n}/4", n == 0 ? red : green);
    }

    // Short house address: "Mist W1 P1" (no world — it's shown separately).
    private static string ShortHouseAddress(HouseRecord? h)
    {
        if (h == null || !h.HasHouse) return "-";
        if (h.IsApartment) return $"{h.District} Apt (W{h.Ward})";
        return $"{h.District} W{h.Ward} P{h.Plot}";
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
            if (plugin.Config.ShowMemberFcRank && !string.IsNullOrEmpty(c.MyFcRank))
                ImGui.TextWrapped($"Your rank in FC: {c.MyFcRank}");
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
