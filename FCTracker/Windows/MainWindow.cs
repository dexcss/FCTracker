using System;
using System.Collections.Generic;
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
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 320) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(4000, 4000),
        };
    }

    // Deferred actions (applied after the draw loop).
    private ulong pendingDeleteCid;
    private ulong pendingClearFcCid;
    private bool pendingClearAll;

    private string search = string.Empty;
    private string expandedFcKey = "";

    // ---- FC sort ----
    private enum FcSortCol { Region, World, Account, SubRunner, Name, Tag, Members, Level, Subs, House, Credits }
    private FcSortCol fcSort = FcSortCol.Name;
    private bool fcSortAsc = true;

    // A grouped FC entry built from one or more of the user's tracked characters.
    private class FcGroup
    {
        public string Key = "";
        public ulong FcId;
        public string Name = "";
        public string Tag = "";
        public string Region = "";
        public string World = "";
        public ushort Members;           // total FC members (from the snapshot)
        public byte Level;
        public string Credits = "";
        public HouseRecord? House;
        public int SubCount;
        public bool SubsKnown;
        public string SubRunnerName = "";
        public string SubRunnerAccountKey = "";
        public string FallbackAccountKey = "";
        public List<CharacterRecord> TrackedMembers = new();

        public string EffectiveAccountKey =>
            !string.IsNullOrEmpty(SubRunnerAccountKey) ? SubRunnerAccountKey : FallbackAccountKey;
    }

    public override void Draw()
    {
        var config = plugin.Config;

        // Opportunistically pull AutoRetainer info (workshop runner flag, etc).
        plugin.EnrichFromAutoRetainer();

        DrawTopBar(config);

        if (config.Characters.Count == 0)
        {
            ImGui.TextWrapped(
                "No data yet. Log into a character that is in a Free Company. " +
                "Open the Free Company window to capture level/credits, and " +
                "stand in your FC workshop to capture airships and submersibles.");
            return;
        }

        var groups = BuildGroups(config.Characters);
        DrawFcTable(groups, config);

        ApplyPendingActions();
    }

    private void DrawTopBar(Configuration config)
    {
        var fcIds = new HashSet<ulong>();
        var houses = 0;
        foreach (var c in config.Characters)
        {
            if (c.Fc != null && c.Fc.FreeCompanyId != 0) fcIds.Add(c.Fc.FreeCompanyId);
            if (c.Fc?.House != null && c.Fc.House.HasHouse) houses++;
        }

        ImGui.TextDisabled($"{config.Characters.Count} characters · {fcIds.Count} FCs · {houses} houses");

        var gear = "Settings";
        var gearW = ImGui.CalcTextSize(gear).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - gearW);
        if (ImGui.SmallButton(gear))
            plugin.OpenSettings();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search FC, tag, world, or character (e.g. \"ultr\")...", ref search, 64);
        ImGuiHelpers.ScaledDummy(2f);
    }

    // ---- Build one FcGroup per FC from tracked characters ----
    private List<FcGroup> BuildGroups(List<CharacterRecord> chars)
    {
        var map = new Dictionary<string, FcGroup>();

        foreach (var c in chars)
        {
            var fc = c.Fc;
            var key = fc != null && fc.FreeCompanyId != 0 ? fc.FreeCompanyId.ToString()
                    : fc != null && !string.IsNullOrEmpty(fc.Name) ? "name:" + fc.Name
                    : "none:" + (string.IsNullOrEmpty(c.WorldName) ? "?" : c.WorldName);

            if (!map.TryGetValue(key, out var g))
            {
                map[key] = g = new FcGroup
                {
                    Key = key,
                    FcId = fc?.FreeCompanyId ?? 0,
                    Name = fc?.Name ?? $"Not in an FC ({(string.IsNullOrEmpty(c.WorldName) ? "?" : c.WorldName)})",
                    Tag = fc?.Tag ?? "",
                    Region = fc?.Region ?? "",
                    World = c.WorldName,
                    Members = fc?.TotalMembers ?? 0,
                    Level = fc?.Rank ?? 0,
                    Credits = fc?.CreditsText ?? "",
                    House = fc?.House,
                };
            }

            g.TrackedMembers.Add(c);

            if (c.VesselsLastUpdatedUtc != null)
            {
                g.SubsKnown = true;
                if (c.Submersibles.Count > g.SubCount) g.SubCount = c.Submersibles.Count;
            }

            if (c.IsWorkshopRunner)
            {
                g.SubRunnerName = string.IsNullOrEmpty(c.WorldName) ? c.CharacterName : $"{c.CharacterName} @ {c.WorldName}";
                if (!string.IsNullOrEmpty(c.AccountKey)) g.SubRunnerAccountKey = c.AccountKey;
            }
            if (string.IsNullOrEmpty(g.FallbackAccountKey) && !string.IsNullOrEmpty(c.AccountKey))
                g.FallbackAccountKey = c.AccountKey;
        }

        // Apply search filter on FC-level fields + tracked member names.
        var list = new List<FcGroup>();
        var q = search.Trim();
        foreach (var g in map.Values)
        {
            if (string.IsNullOrEmpty(q))
            {
                list.Add(g);
                continue;
            }
            var hay = $"{g.Region} {g.World} {g.Name} {g.Tag} {g.SubRunnerName} {AccountAliasLabel(g.EffectiveAccountKey)}";
            foreach (var m in g.TrackedMembers) hay += $" {m.CharacterName}";
            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                list.Add(g);
        }

        SortGroups(list);
        return list;
    }

    private void SortGroups(List<FcGroup> list)
    {
        Comparison<FcGroup> cmp = fcSort switch
        {
            FcSortCol.Region => (a, b) => string.Compare(a.Region, b.Region, StringComparison.OrdinalIgnoreCase),
            FcSortCol.World => (a, b) => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Account => (a, b) => string.Compare(AccountAliasLabel(a.EffectiveAccountKey), AccountAliasLabel(b.EffectiveAccountKey), StringComparison.OrdinalIgnoreCase),
            FcSortCol.SubRunner => (a, b) => string.Compare(a.SubRunnerName, b.SubRunnerName, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Tag => (a, b) => string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Members => (a, b) => a.Members.CompareTo(b.Members),
            FcSortCol.Level => (a, b) => a.Level.CompareTo(b.Level),
            FcSortCol.Subs => (a, b) => a.SubCount.CompareTo(b.SubCount),
            FcSortCol.House => (a, b) => (a.House?.HasHouse == true ? 1 : 0).CompareTo(b.House?.HasHouse == true ? 1 : 0),
            FcSortCol.Credits => (a, b) => CreditsVal(a.Credits).CompareTo(CreditsVal(b.Credits)),
            _ => (a, b) => 0,
        };

        list.Sort((a, b) =>
        {
            if (plugin.Config.SubsortByRegion && fcSort != FcSortCol.Region)
            {
                var rr = string.Compare(a.Region, b.Region, StringComparison.OrdinalIgnoreCase);
                if (rr != 0) return rr;
            }
            var r = cmp(a, b);
            return fcSortAsc ? r : -r;
        });
    }

    private static long CreditsVal(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        return long.TryParse(s.Replace(",", "").Replace(".", "").Replace(" ", ""), out var v) ? v : -1;
    }

    // ---- The single FC table ----
    private void DrawFcTable(List<FcGroup> groups, Configuration cfg)
    {
        var showRegion = cfg.ShowRegionColumn;
        var showLogin = cfg.ShowLoginButton;
        // Go(1) + optional Login + optional Region + 10 sortable columns
        // (World, Account, SubRunner, Name, Tag, Members, Level, Subs, House, Credits)
        var cols = 1 + (showLogin ? 1 : 0) + (showRegion ? 1 : 0) + 10;

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

        var detailOpen = !string.IsNullOrEmpty(expandedFcKey) && groups.Exists(x => x.Key == expandedFcKey);
        var outerSize = new Vector2(0, detailOpen ? ImGui.GetContentRegionAvail().Y * 0.5f : 0f);

        if (ImGui.BeginTable("##fctable", cols, flags, outerSize))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Go", ImGuiTableColumnFlags.WidthFixed, 28 * ImGuiHelpers.GlobalScale);
            if (showLogin)
                ImGui.TableSetupColumn("Login", ImGuiTableColumnFlags.WidthFixed, 28 * ImGuiHelpers.GlobalScale);
            if (showRegion)
                ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Account", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Sub-runner", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Tag", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("Subs", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("House", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Credits", ImGuiTableColumnFlags.WidthStretch, 1.0f);

            // Header row.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableNextColumn(); ImGui.TextDisabled(""); // Go
            if (showLogin) { ImGui.TableNextColumn(); ImGui.TextDisabled(""); }
            if (showRegion) DrawSortHeader(FcSortCol.Region, "Region");
            DrawSortHeader(FcSortCol.World, "World");
            DrawSortHeader(FcSortCol.Account, "Account");
            DrawSortHeader(FcSortCol.SubRunner, "Sub-runner");
            DrawSortHeader(FcSortCol.Name, "Free Company");
            DrawSortHeader(FcSortCol.Tag, "Tag");
            DrawSortHeader(FcSortCol.Members, "Members");
            DrawSortHeader(FcSortCol.Level, "Level");
            DrawSortHeader(FcSortCol.Subs, "Subs");
            DrawSortHeader(FcSortCol.House, "House");
            DrawSortHeader(FcSortCol.Credits, "Credits");

            foreach (var g in groups)
                DrawFcRow(g, cfg);

            ImGui.EndTable();
        }

        // Expanded detail full-width below the table, in a scroll child.
        if (!string.IsNullOrEmpty(expandedFcKey))
        {
            var open = groups.Find(x => x.Key == expandedFcKey);
            if (open != null)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                if (ImGui.BeginChild("##fcdetail", new Vector2(0, 0), false))
                    DrawFcDetail(open, cfg);
                ImGui.EndChild();
            }
        }
    }

    private void DrawSortHeader(FcSortCol col, string label)
    {
        ImGui.TableNextColumn();
        var arrow = fcSort == col ? (fcSortAsc ? " \u25B2" : " \u25BC") : "";
        if (ImGui.Selectable($"{label}{arrow}###fchdr{(int)col}"))
        {
            if (fcSort == col) fcSortAsc = !fcSortAsc;
            else
            {
                fcSort = col;
                fcSortAsc = col is FcSortCol.Region or FcSortCol.World or FcSortCol.Account
                    or FcSortCol.SubRunner or FcSortCol.Name or FcSortCol.Tag;
            }
        }
    }

    private void DrawFcRow(FcGroup g, Configuration cfg)
    {
        var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
        var red = new Vector4(0.85f, 0.5f, 0.5f, 1f);
        var green = new Vector4(0.5f, 0.85f, 0.5f, 1f);
        var hasHouse = g.House?.HasHouse == true;
        var isOpen = expandedFcKey == g.Key;

        ImGui.TableNextRow();

        // Go (Lifestream-to-house).
        ImGui.TableNextColumn();
        if (hasHouse && g.House != null && !g.House.IsApartment)
        {
            if (ImGui.SmallButton($"\u27A4###go{g.Key}"))
                PluginIpc.LifestreamGoToAddress(g.World, g.House.District, g.House.Ward, g.House.Plot);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Lifestream to {g.World} {g.House.District} W{g.House.Ward} P{g.House.Plot}");
        }
        else ImGui.TextDisabled("");

        // Login (door).
        if (cfg.ShowLoginButton)
        {
            ImGui.TableNextColumn();
            var target = g.TrackedMembers.Find(m => m.IsWorkshopRunner)
                         ?? (g.TrackedMembers.Count > 0 ? g.TrackedMembers[0] : null);
            if (target != null && !string.IsNullOrEmpty(target.CharacterName) && !string.IsNullOrEmpty(target.WorldName))
            {
                if (ImGui.SmallButton($"\uE03C###login{g.Key}"))
                    PluginIpc.LifestreamLogin(target.CharacterName, target.WorldName);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Log into {target.CharacterName} @ {target.WorldName} (Lifestream)");
            }
            else ImGui.TextDisabled("");
        }

        var tri = isOpen ? "\u25BC " : "\u25B6 ";

        // Region (carries the expand toggle when shown).
        if (cfg.ShowRegionColumn)
        {
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{tri}{(string.IsNullOrEmpty(g.Region) ? "-" : g.Region)}###fcrow{g.Key}",
                    isOpen, ImGuiSelectableFlags.SpanAllColumns))
                expandedFcKey = isOpen ? "" : g.Key;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(g.World) ? "-" : g.World);
        }
        else
        {
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{tri}{(string.IsNullOrEmpty(g.World) ? "-" : g.World)}###fcrow{g.Key}",
                    isOpen, ImGuiSelectableFlags.SpanAllColumns))
                expandedFcKey = isOpen ? "" : g.Key;
        }

        // Account
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(AccountAliasLabel(g.EffectiveAccountKey));

        // Sub-runner (name + account alias)
        ImGui.TableNextColumn();
        if (string.IsNullOrEmpty(g.SubRunnerName)) ImGui.TextDisabled("-");
        else ImGui.TextUnformatted($"{g.SubRunnerName}");

        // FC Name
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(g.Name);

        // Tag
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrEmpty(g.Tag) ? "-" : g.Tag);

        // Members
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(g.Members > 0 ? g.Members.ToString() : "-");

        // Level (red < 6)
        ImGui.TableNextColumn();
        if (g.Level == 0) ImGui.TextColored(grey, "-");
        else if (g.Level < 6) ImGui.TextColored(red, g.Level.ToString());
        else ImGui.TextUnformatted(g.Level.ToString());

        // Subs #/4
        ImGui.TableNextColumn();
        if (!hasHouse) ImGui.TextColored(grey, "-");
        else if (!g.SubsKnown) ImGui.TextColored(grey, "?/4");
        else ImGui.TextColored(g.SubCount == 0 ? red : green, $"{g.SubCount}/4");

        // House (short, no world)
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

    private static string ShortHouseAddress(HouseRecord? h)
    {
        if (h == null || !h.HasHouse) return "-";
        if (h.IsApartment) return $"{h.District} Apt (W{h.Ward})";
        return $"{h.District} W{h.Ward} P{h.Plot}";
    }

    // ---- FC detail (click-in) ----
    private void DrawFcDetail(FcGroup g, Configuration cfg)
    {
        ImGui.Indent(14 * ImGuiHelpers.GlobalScale);

        // Additional info: Original Winner (editable), first registered, last update.
        var holder = g.TrackedMembers[0];
        var winner = holder.ManualHouseWinner;
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText($"Original winner###winner{g.Key}", ref winner, 128))
        {
            holder.ManualHouseWinner = winner;
            plugin.PersistCharacter(holder);
        }

        ImGui.TextDisabled($"First registered: {ToLocal(EarliestFirstSeen(g))}");
        if (holder.Fc != null)
            ImGui.TextDisabled($"Last data update: {ToLocal(holder.Fc.LastUpdatedUtc)}");

        ImGuiHelpers.ScaledDummy(4f);

        // Characters table: all tracked members of this FC.
        ImGui.TextUnformatted("Characters");
        if (ImGui.BeginTable($"##fcmembers{g.Key}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Rank in FC");
            ImGui.TableSetupColumn("Days in FC");
            ImGui.TableSetupColumn("Account");
            ImGui.TableHeadersRow();

            foreach (var m in g.TrackedMembers)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var nm = string.IsNullOrEmpty(m.WorldName) ? m.CharacterName : $"{m.CharacterName} @ {m.WorldName}";
                ImGui.TextUnformatted(nm);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(!string.IsNullOrEmpty(m.MyFcRank) ? m.MyFcRank : "-");

                ImGui.TableNextColumn();
                var days = (int)(DateTime.UtcNow - m.FirstSeenInFcUtc).TotalDays;
                if (days <= 30) ImGui.TextColored(new Vector4(0.85f, 0.5f, 0.5f, 1f), days.ToString());
                else ImGui.TextUnformatted(days.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(m.AccountKey) ? "-" : AccountAliasLabel(m.AccountKey));
            }
            ImGui.EndTable();
        }

        ImGuiHelpers.ScaledDummy(4f);

        // Submersibles (ranks + builds) — from whichever tracked member has data.
        var subHolder = g.TrackedMembers.Find(m => m.Submersibles.Count > 0);
        if (subHolder != null)
            DrawVessels("Submersibles", subHolder.Submersibles, subHolder);

        // Airships too, if present (same backend).
        var airHolder = g.TrackedMembers.Find(m => m.Airships.Count > 0);
        if (airHolder != null)
            DrawVessels("Airships", airHolder.Airships, airHolder);

        ImGuiHelpers.ScaledDummy(4f);

        // Notes box (no longer hidden behind a dropdown).
        ImGui.TextUnformatted("Notes");
        var notes = holder.ManualNotes;
        if (ImGui.InputTextMultiline($"##notes{g.Key}", ref notes, 2048, new Vector2(-1, 80 * ImGuiHelpers.GlobalScale)))
        {
            holder.ManualNotes = notes;
            plugin.PersistCharacter(holder);
        }

        ImGui.Unindent(14 * ImGuiHelpers.GlobalScale);
    }

    private static DateTime EarliestFirstSeen(FcGroup g)
    {
        var earliest = DateTime.MaxValue;
        foreach (var m in g.TrackedMembers)
            if (m.FirstSeenInFcUtc < earliest) earliest = m.FirstSeenInFcUtc;
        return earliest == DateTime.MaxValue ? DateTime.UtcNow : earliest;
    }

    private void DrawVessels(string title, List<VesselRecord> vessels, CharacterRecord c)
    {
        if (vessels.Count == 0) return;
        if (!ImGui.CollapsingHeader($"{title} ({vessels.Count})###{title}{c.ContentId}")) return;

        if (c.VesselsLastUpdatedUtc.HasValue)
            ImGui.TextDisabled($"Updated: {ToLocal(c.VesselsLastUpdatedUtc.Value)} (stand in workshop to refresh)");

        if (ImGui.BeginTable($"##{title}tbl{c.ContentId}", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
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

    private static string ToLocal(DateTime utc) => utc.ToLocalTime().ToString("g");
}
