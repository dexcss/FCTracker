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
    private readonly List<ulong> pendingClearFcCids = new();
    private bool pendingClearAll;

    private string search = string.Empty;
    private string expandedFcKey = "";

    // Transient feedback from the login button (shown briefly in the top bar).
    private string loginResult = "";
    private DateTime loginResultUntil = DateTime.MinValue;

    // ---- FC sort ----
    private enum FcSortCol { Region, World, Account, SubRunner, Character, CustomName, Name, Tag, Members, Level, Subs, Returns, House, Credits }
    private FcSortCol fcSort = FcSortCol.Name;
    private bool fcSortAsc = true;

    // A grouped FC entry built from one or more of the user's tracked characters.
    private class FcGroup
    {
        public string Key = "";
        public bool IsLooseCharacter;  // true = a character with no FC (its own row)
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
        public uint SubReturnUnix;        // latest sub return (unix secs), from AR
        public bool SubReturnKnown;
        public string SubRunnerName = "";
        public string FirstCharacterName = "";  // first character seen attached to this FC
        public bool SubRunnerIsExplicit;  // true if from AR WorkshopEnabled (vs. sub-activity fallback)
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

        // Transient login feedback.
        if (!string.IsNullOrEmpty(loginResult) && DateTime.UtcNow < loginResultUntil)
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.4f, 1f), loginResult);
        else if (DateTime.UtcNow >= loginResultUntil)
            loginResult = "";

        ImGuiHelpers.ScaledDummy(2f);
    }

    // ---- Build one FcGroup per FC from tracked characters ----
    private List<FcGroup> BuildGroups(List<CharacterRecord> chars)
    {
        var map = new Dictionary<string, FcGroup>();

        foreach (var c in chars)
        {
            var fc = c.Fc;
            var hasFc = fc != null && (fc.FreeCompanyId != 0 || !string.IsNullOrEmpty(fc.Name));
            // Characters in an FC group under that FC. Characters with NO FC each get
            // their own row (keyed by content id) so every account's loose characters
            // are individually visible.
            var key = fc != null && fc.FreeCompanyId != 0 ? fc.FreeCompanyId.ToString()
                    : fc != null && !string.IsNullOrEmpty(fc.Name) ? "name:" + fc.Name
                    : "none:" + c.ContentId;

            if (!map.TryGetValue(key, out var g))
            {
                map[key] = g = new FcGroup
                {
                    Key = key,
                    IsLooseCharacter = !hasFc,
                    FcId = fc?.FreeCompanyId ?? 0,
                    Name = fc?.Name ?? "(no FC)",
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
            if (string.IsNullOrEmpty(g.FirstCharacterName) && !string.IsNullOrEmpty(c.CharacterName))
                g.FirstCharacterName = string.IsNullOrEmpty(c.WorldName) ? c.CharacterName : $"{c.CharacterName} @ {c.WorldName}";

            if (c.VesselsLastUpdatedUtc != null)
            {
                g.SubsKnown = true;
                if (c.Submersibles.Count > g.SubCount) g.SubCount = c.Submersibles.Count;

                // Latest return time from our own captured submarine data. A deployed
                // sub has ReturnTime > 0; an idle/home sub is 0. Track the max so the
                // column shows when the last one is back.
                foreach (var v in c.Submersibles)
                {
                    if (v.ReturnTime > g.SubReturnUnix)
                    {
                        g.SubReturnUnix = v.ReturnTime;
                        g.SubReturnKnown = true;
                    }
                    // Even an idle sub means we *know* the return state (they're home).
                    g.SubReturnKnown = true;
                }
            }

            // Does this character have submarines (captured workshop data OR an AR
            // return time)? Either is good evidence they're the sub-runner.
            var hasSubs = (c.VesselsLastUpdatedUtc != null && c.Submersibles.Count > 0) || c.SubReturnUnix > 0;

            if (c.IsWorkshopRunner)
            {
                // Explicit AR workshop-runner always wins.
                g.SubRunnerName = string.IsNullOrEmpty(c.WorldName) ? c.CharacterName : $"{c.CharacterName} @ {c.WorldName}";
                g.SubRunnerIsExplicit = true;
                if (!string.IsNullOrEmpty(c.AccountKey)) g.SubRunnerAccountKey = c.AccountKey;
                if (c.SubReturnUnix > g.SubReturnUnix) { g.SubReturnUnix = c.SubReturnUnix; }
                if (c.SubReturnUnix > 0) g.SubReturnKnown = true;
            }
            else if (!g.SubRunnerIsExplicit && hasSubs)
            {
                // Fallback: no AR-flagged runner — use whoever has subs. If several,
                // prefer the one with the latest known return (most recently sent);
                // characters with only captured data (no return time) fill in if none
                // have a return time yet.
                if (string.IsNullOrEmpty(g.SubRunnerName) || c.SubReturnUnix > g.SubReturnUnix)
                {
                    g.SubRunnerName = string.IsNullOrEmpty(c.WorldName) ? c.CharacterName : $"{c.CharacterName} @ {c.WorldName}";
                    if (!string.IsNullOrEmpty(c.AccountKey)) g.SubRunnerAccountKey = c.AccountKey;
                }
            }

            // Track the FC's latest sub return regardless of who the runner is.
            if (c.SubReturnUnix > g.SubReturnUnix)
            {
                g.SubReturnUnix = c.SubReturnUnix;
                g.SubReturnKnown = true;
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
            FcSortCol.Character => (a, b) => string.Compare(a.FirstCharacterName, b.FirstCharacterName, StringComparison.OrdinalIgnoreCase),
            FcSortCol.CustomName => (a, b) => string.Compare(CustomName(a), CustomName(b), StringComparison.OrdinalIgnoreCase),
            FcSortCol.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Tag => (a, b) => string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase),
            FcSortCol.Members => (a, b) => a.Members.CompareTo(b.Members),
            FcSortCol.Level => (a, b) => a.Level.CompareTo(b.Level),
            FcSortCol.Subs => (a, b) => a.SubCount.CompareTo(b.SubCount),
            FcSortCol.Returns => (a, b) => ReturnSortVal(a).CompareTo(ReturnSortVal(b)),
            FcSortCol.House => (a, b) => (a.House?.HasHouse == true ? 1 : 0).CompareTo(b.House?.HasHouse == true ? 1 : 0),
            FcSortCol.Credits => (a, b) => CreditsVal(a.Credits).CompareTo(CreditsVal(b.Credits)),
            _ => (a, b) => 0,
        };

        list.Sort((a, b) =>
        {
            // FC rows always come before loose (no-FC) character rows.
            if (a.IsLooseCharacter != b.IsLooseCharacter)
                return a.IsLooseCharacter ? 1 : -1;

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

    // Sort key for the Returns column: unknown sorts last; otherwise by return time
    // (soonest = smallest). "Back" (past) times naturally sort before pending ones.
    private static long ReturnSortVal(FcGroup g)
        => g.SubReturnKnown ? g.SubReturnUnix : long.MaxValue;

    // Column identity for visibility + ordering.
    private enum Col { Tp, Login, Region, World, Account, SubRunner, Character, CustomName, Fc, Tag, Members, Level, Subs, Returns, House, Credits }

    // Stable string name per column (used for the saved order list).
    private static readonly (Col col, string name)[] AllColumns =
    {
        (Col.Tp, "TP"), (Col.Login, "LOG"), (Col.Region, "Region"), (Col.World, "World"),
        (Col.Account, "Account"), (Col.SubRunner, "Sub-runner"), (Col.Character, "Character"),
        (Col.CustomName, "Custom name"),
        (Col.Fc, "Free Company"), (Col.Tag, "Tag"), (Col.Members, "Members"), (Col.Level, "Level"),
        (Col.Subs, "Subs"), (Col.Returns, "Returns"), (Col.House, "House"), (Col.Credits, "Credits"),
    };

    private static bool IsColEnabled(Col c, Configuration cfg) => c switch
    {
        Col.Tp => cfg.ColTp,
        Col.Login => cfg.ColLogin,
        Col.Region => cfg.ColRegion,
        Col.World => cfg.ColWorld,
        Col.Account => cfg.ColAccount,
        Col.SubRunner => cfg.ColSubRunner,
        Col.Character => cfg.ColCharacter,
        Col.CustomName => cfg.ColCustomName,
        Col.Fc => cfg.ColFc,
        Col.Tag => cfg.ColTag,
        Col.Members => cfg.ColMembers,
        Col.Level => cfg.ColLevel,
        Col.Subs => cfg.ColSubs,
        Col.Returns => cfg.ColReturns,
        Col.House => cfg.ColHouse,
        Col.Credits => cfg.ColCredits,
        _ => false,
    };

    // The full column order (enabled or not), honoring the user's saved order with
    // any unlisted columns appended in default order.
    private static List<Col> OrderedAllColumns(Configuration cfg)
    {
        var byName = new Dictionary<string, Col>();
        foreach (var (col, name) in AllColumns) byName[name] = col;

        var result = new List<Col>();
        var seen = new HashSet<Col>();
        foreach (var name in cfg.ColumnOrder)
            if (byName.TryGetValue(name, out var col) && seen.Add(col))
                result.Add(col);
        foreach (var (col, _) in AllColumns)
            if (seen.Add(col)) result.Add(col);
        return result;
    }

    // Public, string-based view of the ordered columns for the settings UI.
    public static List<string> OrderedColumnNames(Configuration cfg)
    {
        var nameByCol = new Dictionary<Col, string>();
        foreach (var (col, name) in AllColumns) nameByCol[col] = name;
        var outList = new List<string>();
        foreach (var c in OrderedAllColumns(cfg)) outList.Add(nameByCol[c]);
        return outList;
    }

    private List<Col> EnabledColumns(Configuration cfg)
    {
        var list = new List<Col>();
        foreach (var c in OrderedAllColumns(cfg))
            if (IsColEnabled(c, cfg)) list.Add(c);
        return list;
    }

    // First sortable (non-button) column carries the expand triangle.
    private static bool IsButtonCol(Col c) => c is Col.Tp or Col.Login;

    // ---- The single FC table ----
    private void DrawFcTable(List<FcGroup> groups, Configuration cfg)
    {
        var columns = EnabledColumns(cfg);
        if (columns.Count == 0) { ImGui.TextDisabled("All columns are hidden — enable some in Settings."); return; }

        // The first non-button column owns the expand triangle.
        var triCol = columns.Find(c => !IsButtonCol(c));

        // Auto-fit (default): stretch columns always redistribute to fit the window.
        // Manual: add Resizable so borders can be dragged (widths then persist and
        // won't auto-reflow — that's the trade-off, which is why it's a choice).
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (cfg.ManualColumnResize)
            flags |= ImGuiTableFlags.Resizable;
        else
            flags |= ImGuiTableFlags.NoSavedSettings; // don't cache stale widths in auto mode

        var detailOpen = !string.IsNullOrEmpty(expandedFcKey) && groups.Exists(x => x.Key == expandedFcKey);
        var outerSize = new Vector2(0, detailOpen ? ImGui.GetContentRegionAvail().Y * 0.5f : 0f);

        if (ImGui.BeginTable("##fctable", columns.Count, flags, outerSize))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            foreach (var c in columns)
                SetupColumn(c);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            foreach (var c in columns)
                HeaderCell(c);

            foreach (var g in groups)
                DrawFcRow(g, cfg, columns, triCol);

            ImGui.EndTable();
        }

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

    private void SetupColumn(Col c)
    {
        var s = ImGuiHelpers.GlobalScale;
        switch (c)
        {
            case Col.Tp: ImGui.TableSetupColumn("TP", ImGuiTableColumnFlags.WidthFixed, 30 * s); break;
            case Col.Login: ImGui.TableSetupColumn("LOG", ImGuiTableColumnFlags.WidthFixed, 34 * s); break;
            case Col.Region: ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthStretch, 0.7f); break;
            case Col.World: ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1.1f); break;
            case Col.Account: ImGui.TableSetupColumn("Account", ImGuiTableColumnFlags.WidthStretch, 1.1f); break;
            case Col.SubRunner: ImGui.TableSetupColumn("Sub-runner", ImGuiTableColumnFlags.WidthStretch, 1.8f); break;
            case Col.Character: ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.8f); break;
            case Col.CustomName: ImGui.TableSetupColumn("Nickname", ImGuiTableColumnFlags.WidthStretch, 1.4f); break;
            case Col.Fc: ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 2.0f); break;
            case Col.Tag: ImGui.TableSetupColumn("Tag", ImGuiTableColumnFlags.WidthStretch, 0.7f); break;
            case Col.Members: ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 0.7f); break;
            case Col.Level: ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthStretch, 0.6f); break;
            case Col.Subs: ImGui.TableSetupColumn("Subs", ImGuiTableColumnFlags.WidthStretch, 0.6f); break;
            case Col.Returns: ImGui.TableSetupColumn("Returns", ImGuiTableColumnFlags.WidthStretch, 1.0f); break;
            case Col.House: ImGui.TableSetupColumn("House", ImGuiTableColumnFlags.WidthStretch, 1.5f); break;
            case Col.Credits: ImGui.TableSetupColumn("Credits", ImGuiTableColumnFlags.WidthStretch, 1.0f); break;
        }
    }

    private void HeaderCell(Col c)
    {
        switch (c)
        {
            case Col.Tp: ImGui.TableNextColumn(); ImGui.TextDisabled("TP"); break;
            case Col.Login: ImGui.TableNextColumn(); ImGui.TextDisabled("LOG"); break;
            case Col.Region: DrawSortHeader(FcSortCol.Region, "Region"); break;
            case Col.World: DrawSortHeader(FcSortCol.World, "World"); break;
            case Col.Account: DrawSortHeader(FcSortCol.Account, "Account"); break;
            case Col.SubRunner: DrawSortHeader(FcSortCol.SubRunner, "Sub-runner"); break;
            case Col.Character: DrawSortHeader(FcSortCol.Character, "Character"); break;
            case Col.CustomName: DrawSortHeader(FcSortCol.CustomName, "Nickname"); break;
            case Col.Fc: DrawSortHeader(FcSortCol.Name, "Free Company"); break;
            case Col.Tag: DrawSortHeader(FcSortCol.Tag, "Tag"); break;
            case Col.Members: DrawSortHeader(FcSortCol.Members, "Members"); break;
            case Col.Level: DrawSortHeader(FcSortCol.Level, "Level"); break;
            case Col.Subs: DrawSortHeader(FcSortCol.Subs, "Subs"); break;
            case Col.Returns: DrawSortHeader(FcSortCol.Returns, "Returns"); break;
            case Col.House: DrawSortHeader(FcSortCol.House, "House"); break;
            case Col.Credits: DrawSortHeader(FcSortCol.Credits, "Credits"); break;
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
                    or FcSortCol.SubRunner or FcSortCol.Character or FcSortCol.CustomName or FcSortCol.Name or FcSortCol.Tag;
            }
        }
    }

    private void DrawFcRow(FcGroup g, Configuration cfg, List<Col> columns, Col triCol)
    {
        var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
        var red = new Vector4(0.85f, 0.5f, 0.5f, 1f);
        var green = new Vector4(0.5f, 0.85f, 0.5f, 1f);
        var hasHouse = g.House?.HasHouse == true;
        var isOpen = expandedFcKey == g.Key;
        var tri = isOpen ? "\u25BC " : "\u25B6 ";

        ImGui.TableNextRow();

        foreach (var c in columns)
        {
            ImGui.TableNextColumn();
            switch (c)
            {
                case Col.Tp:
                    if (hasHouse && g.House != null && !g.House.IsApartment)
                    {
                        if (ImGui.SmallButton($"\u27A4###go{g.Key}"))
                            PluginIpc.LifestreamGoToAddress(g.World, g.House.District, g.House.Ward, g.House.Plot);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Lifestream to {g.World} {g.House.District} W{g.House.Ward} P{g.House.Plot}");
                    }
                    else ImGui.TextDisabled("");
                    break;

                case Col.Login:
                {
                    var target = g.TrackedMembers.Find(m => m.IsWorkshopRunner)
                                 ?? (g.TrackedMembers.Count > 0 ? g.TrackedMembers[0] : null);
                    if (target != null && !string.IsNullOrEmpty(target.CharacterName) && !string.IsNullOrEmpty(target.WorldName))
                    {
                        if (ImGui.SmallButton($"{LoginGlyph}###login{g.Key}"))
                        {
                            var r = plugin.RelogTo(target.CharacterName, target.WorldName);
                            loginResult = string.IsNullOrEmpty(r)
                                ? $"Relogging to {target.CharacterName} @ {target.WorldName}..."
                                : r;
                            loginResultUntil = DateTime.UtcNow.AddSeconds(8);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Log into {target.CharacterName} @ {target.WorldName} (via AutoRetainer relog)");
                    }
                    else ImGui.TextDisabled("");
                    break;
                }

                case Col.Region:
                    ExpandableCell(g, c == triCol, isOpen, tri, string.IsNullOrEmpty(g.Region) ? "-" : g.Region);
                    break;
                case Col.World:
                    ExpandableCell(g, c == triCol, isOpen, tri, string.IsNullOrEmpty(g.World) ? "-" : g.World);
                    break;
                case Col.Account:
                    ExpandableCell(g, c == triCol, isOpen, tri, AccountAliasLabel(g.EffectiveAccountKey));
                    break;
                case Col.SubRunner:
                    ExpandableCell(g, c == triCol, isOpen, tri, string.IsNullOrEmpty(g.SubRunnerName) ? "-" : g.SubRunnerName);
                    break;
                case Col.Character:
                    ExpandableCell(g, c == triCol, isOpen, tri, string.IsNullOrEmpty(g.FirstCharacterName) ? "-" : g.FirstCharacterName);
                    break;
                case Col.CustomName:
                    ExpandableCell(g, c == triCol, isOpen, tri, CustomName(g));
                    break;
                case Col.Fc:
                    ExpandableCell(g, c == triCol, isOpen, tri, g.Name);
                    break;
                case Col.Tag:
                    ExpandableCell(g, c == triCol, isOpen, tri, string.IsNullOrEmpty(g.Tag) ? "-" : g.Tag);
                    break;
                case Col.Members:
                    ExpandableCell(g, c == triCol, isOpen, tri, g.Members > 0 ? g.Members.ToString() : "-");
                    break;

                case Col.Level:
                    if (g.Level == 0) ImGui.TextColored(grey, "-");
                    else if (g.Level < 6) ImGui.TextColored(red, g.Level.ToString());
                    else ImGui.TextUnformatted(g.Level.ToString());
                    break;

                case Col.Subs:
                    if (!hasHouse) ImGui.TextColored(grey, "-");
                    else if (!g.SubsKnown) ImGui.TextColored(grey, "?/4");
                    else ImGui.TextColored(g.SubCount == 0 ? red : green, $"{g.SubCount}/4");
                    break;

                case Col.Returns:
                    if (!g.SubReturnKnown)
                    {
                        ImGui.TextColored(grey, "-");
                    }
                    else if (g.SubReturnUnix == 0)
                    {
                        // Subs known but none deployed -> they're home.
                        ImGui.TextColored(green, "Back");
                    }
                    else
                    {
                        var remaining = g.SubReturnUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (remaining <= 0) ImGui.TextColored(green, "Back");
                        else ImGui.TextColored(new Vector4(0.85f, 0.8f, 0.45f, 1f), FormatCountdown(remaining));
                    }
                    break;

                case Col.House:
                    ImGui.TextUnformatted(ShortHouseAddress(g.House));
                    break;

                case Col.Credits:
                    ImGui.TextUnformatted(string.IsNullOrEmpty(g.Credits) ? "-" : g.Credits);
                    break;
            }
        }
    }

    // Renders a text cell that, if it's the designated trigger column, doubles as the
    // row-expand toggle (full-width selectable with the ▶/▼ triangle).
    private void ExpandableCell(FcGroup g, bool isTrigger, bool isOpen, string tri, string text)
    {
        if (isTrigger)
        {
            if (ImGui.Selectable($"{tri}{text}###fcrow{g.Key}", isOpen, ImGuiSelectableFlags.SpanAllColumns))
                expandedFcKey = isOpen ? "" : g.Key;
        }
        else
        {
            ImGui.TextUnformatted(text);
        }
    }

    private string CustomName(FcGroup g)
    {
        return plugin.Config.CustomFcNames.TryGetValue(g.Key, out var n) && !string.IsNullOrEmpty(n) ? n : "-";
    }

    // Login glyph: door from the game icon font, with a safe fallback if it boxes.
    // \uE05D is the SeIconChar door/log-out glyph; if the icon font isn't active it
    // renders as a box, so users can fall back to the prior arrow via this constant.
    private const string LoginGlyph = "\uE05D";

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

        // Additional info: Custom name, Original Winner (editable), first reg, last update.
        var holder = g.TrackedMembers[0];

        plugin.Config.CustomFcNames.TryGetValue(g.Key, out var custom);
        custom ??= string.Empty;
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText($"Nickname###custom{g.Key}", ref custom, 64))
        {
            if (string.IsNullOrEmpty(custom)) plugin.Config.CustomFcNames.Remove(g.Key);
            else plugin.Config.CustomFcNames[g.Key] = custom;
            plugin.Config.Save();
        }

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

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();

        // Per-FC delete: clears this FC's tracked data for all its tracked members.
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 1f));
        if (ImGui.Button($"Remove this FC's data###delfc{g.Key}"))
            ImGui.OpenPopup($"##confirmdelfc{g.Key}");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("Clears tracked FC/sub/house data for this FC. Re-populates if you log in again.");

        if (ImGui.BeginPopup($"##confirmdelfc{g.Key}"))
        {
            ImGui.TextUnformatted($"Remove tracked data for \"{g.Name}\"?");
            ImGui.TextDisabled("This clears the FC data on all your characters tracked in it.");
            ImGuiHelpers.ScaledDummy(2f);
            if (ImGui.Button("Yes, remove"))
            {
                foreach (var m in g.TrackedMembers)
                    pendingClearFcCids.Add(m.ContentId);
                expandedFcKey = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
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
        if (pendingClearFcCids.Count > 0)
        {
            foreach (var cid in pendingClearFcCids)
                plugin.ClearFcData(cid);
            pendingClearFcCids.Clear();
        }
        if (pendingClearAll)
        {
            plugin.ClearAll();
            pendingClearAll = false;
        }
    }

    private static string FormatCountdown(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static string ToLocal(DateTime utc) => utc.ToLocalTime().ToString("g");
}
