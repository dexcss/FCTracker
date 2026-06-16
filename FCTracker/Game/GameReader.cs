using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FCTracker.Data;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace FCTracker.Game;

// All native reads live here. Everything is null-checked because these structures
// are only populated when the relevant game systems are loaded:
//   - InfoProxyFreeCompany: populated once the FC info has been requested (FC window
//     opened at least once, or on login for your own FC).
//   - WorkshopTerritory: only non-null while you are physically inside your FC's
//     workshop (the airship/submersible voyage panel area).
public static unsafe class GameReader
{
    public static FreeCompanySnapshot? ReadFreeCompany()
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return null;

        var proxy = agent->InfoProxyFreeCompany;
        if (proxy == null) return null;
        if (proxy->Id == 0) return null; // not in / not loaded

        var snap = new FreeCompanySnapshot
        {
            FreeCompanyId = proxy->Id,
            Name = proxy->NameString,
            Master = proxy->MasterString,
            Rank = proxy->Rank,
            OnlineMembers = proxy->OnlineMembers,
            TotalMembers = proxy->TotalMembers,
            HomeWorldId = proxy->HomeWorldId,
            GrandCompany = (byte)proxy->GrandCompany,
            LastUpdatedUtc = DateTime.UtcNow,
        };

        return snap;
    }

    // The 5-char FC tag lives on the local player Character, not the proxy.
    public static string ReadLocalPlayerFcTag(Dalamud.Game.ClientState.Objects.Types.IGameObject? local)
    {
        if (local == null) return string.Empty;

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)local.Address;
        if (chara == null) return string.Empty;
        return chara->FreeCompanyTagString;
    }

    public static bool TryReadWorkshopVessels(
        out List<VesselRecord> airships,
        out List<VesselRecord> submersibles)
    {
        airships = new();
        submersibles = new();

        var housing = HousingManager.Instance();
        if (housing == null) return false;

        var workshop = housing->WorkshopTerritory;
        if (workshop == null) return false; // not inside the FC workshop

        // Airships
        var airshipData = workshop->Airship;
        int airshipCount = airshipData.AirshipCount;
        for (var i = 0; i < airshipCount && i < 4; i++)
        {
            var v = airshipData.Data[i];
            // Airship _name is declared with isString:true → generated 'Name' is a string.
            airships.Add(new VesselRecord
            {
                Name = v.Name.ToString(),
                RankId = v.RankId,
                CurrentExp = v.CurrentExp,
                NextLevelExp = v.NextLevelExp,
                RegisterTime = v.RegisterTime,
                ReturnTime = v.ReturnTime,
                HullId = v.HullId,
                SternId = v.SternId,
                BowId = v.BowId,
                BridgeId = v.BridgeId,
            });
        }

        // Submersibles — iterate the 4 slots; a registered sub has a non-empty name.
        var subData = workshop->Submersible;
        for (var i = 0; i < 4; i++)
        {
            var v = subData.Data[i];
            // Submersible _name is a plain byte FixedSizeArray (not isString) → the
            // generated 'Name' is a Span<byte>. Decode the null-terminated UTF8.
            var name = DecodeCString(v.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;
            submersibles.Add(new VesselRecord
            {
                Name = name,
                RankId = v.RankId,
                CurrentExp = v.CurrentExp,
                NextLevelExp = v.NextLevelExp,
                RegisterTime = v.RegisterTime,
                ReturnTime = v.ReturnTime,
                HullId = v.HullId,
                SternId = v.SternId,
                BowId = v.BowId,
                BridgeId = v.BridgeId,
            });
        }

        return airships.Count > 0 || submersibles.Count > 0;
    }

    // Decode a null-terminated UTF8 string from a byte span (used for inline
    // struct strings that aren't declared with isString).
    // Resolves a world name to a short region code (JP/NA/EU/OCE/CN/KR/CLD/TCN/DEV)
    // via World -> DataCenter -> Region. Uses the region's Name string mapped through
    // the requested aliases, with a RowId fallback.
    public static string ResolveRegionCode(IDataManager data, string worldName)
    {
        if (string.IsNullOrEmpty(worldName)) return "";
        try
        {
            var worlds = data.GetExcelSheet<Lumina.Excel.Sheets.World>();
            if (worlds == null) return "";

            foreach (var w in worlds)
            {
                if (!w.Name.ExtractText().Equals(worldName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dcRef = w.DataCenter;
                var regionId = (byte)dcRef.Value.Region.RowId; // RowRef -> row id

                // Try the region's name string for the alias table.
                var regionName = string.Empty;
                try
                {
                    var dcGroup = data.GetExcelSheet<Lumina.Excel.Sheets.WorldDCGroupType>()
                        ?.GetRowOrDefault((uint)regionId);
                    if (dcGroup.HasValue) regionName = dcGroup.Value.Name.ExtractText();
                }
                catch { /* ignore */ }

                return RegionAlias(regionName, regionId);
            }
        }
        catch { /* ignore */ }
        return "";
    }

    private static string RegionAlias(string regionName, byte regionId)
    {
        // Name-based aliases (as specified).
        switch (regionName)
        {
            case "": break; // fall through to id map (empty name handled below)
            case "Japan": return "JP";
            case "North America": return "NA";
            case "Europe": return "EU";
            case "Oceania": return "OCE";
            case "China": return "CN";
            case "Korea": return "KR";
            case "NA Cloud": return "CLD";
            case "Traditional Chinese regions": return "TCN";
        }

        // Fallback by well-known region row ids.
        return regionId switch
        {
            1 => "JP",
            2 => "NA",
            3 => "EU",
            4 => "OCE",
            5 => "CN",
            6 => "KR",
            7 => "CLD",
            _ => string.IsNullOrEmpty(regionName) ? "DEV" : regionName,
        };
    }

    // Opens the Free Company window via its agent (AgentInterface.Show). Used by the
    // optional auto-open-on-login flow; the window is briefly visible on screen.
    public static void OpenFreeCompanyWindow()
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;
        if (!agent->IsAgentActive())
            agent->Show();
    }

    // Closes the FreeCompany addon if it's open.
    public static void CloseFreeCompanyWindow(IGameGui gameGui)
    {
        nint addr = gameGui.GetAddonByName("FreeCompany", 1);
        if (addr == nint.Zero) return;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addr;
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }

    // BEST-EFFORT: read the logged-in character's own FC rank from the FreeCompany
    // window's member context. There is no clean struct field for a member's rank,
    // so this scrapes text nodes and may return empty. Gated behind a setting.
    public static string TryReadOwnFcRank(IGameGui gameGui, string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return string.Empty;

        // The FreeCompany window's INFO tab shows the viewing character's rank.
        nint addr = gameGui.GetAddonByName("FreeCompany", 1);
        if (addr == nint.Zero) return string.Empty;

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addr;
        if (addon == null || !addon->IsVisible) return string.Empty;

        // Collect text nodes; only accept text that matches a known default FC rank
        // name to avoid surfacing unrelated labels. Custom-renamed ranks won't match
        // and will simply return empty (better than showing the wrong thing).
        string[] known = { "Master", "Officer", "Member", "Recruit", "Veteran" };
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text)
                continue;
            var t = ((FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode*)node)->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(t)) continue;

            var trimmed = t.Trim();
            foreach (var k in known)
            {
                if (trimmed.Equals(k, StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }
        }

        return string.Empty;
    }

    // Reads the prompt text from a SelectYesno addon (used for house-winner detect).
    public static string ReadSelectYesnoPrompt(nint addonPtr)
    {
        if (addonPtr == nint.Zero) return string.Empty;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr;
        if (addon == null) return string.Empty;

        // SelectYesno's prompt is a text node; scan for the first non-empty one.
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text)
                continue;
            var t = ((FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(t) && t.Length > 10)
                return t;
        }
        return string.Empty;
    }

    private static string DecodeCString(ReadOnlySpan<byte> span)
    {
        var len = span.IndexOf((byte)0);
        if (len < 0) len = span.Length;
        if (len == 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(span.Slice(0, len));
    }

    // Reads the FC's owned estate. Works anywhere (does not require being in the
    // workshop or at the house) because GetOwnedHouseId queries owned-house state.
    public static HouseRecord ReadFreeCompanyHouse(IDataManager data)
    {
        var rec = new HouseRecord { LastCheckedUtc = DateTime.UtcNow };

        var houseId = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);

        // "No house" can come back as either Id == 0 OR a sentinel where the fields
        // are all-ones (TerritoryTypeId 0xFFFF, ward/plot 0xFF). A real plot has a
        // valid housing territory id (small number) and a plausible ward/plot.
        var noHouse =
            houseId.Id == 0
            || houseId.TerritoryTypeId == 0xFFFF
            || houseId.TerritoryTypeId == 0
            || houseId.WardIndex == 0x3F          // 6-bit all-ones
            || houseId.PlotIndex == 0x7F;         // 7-bit all-ones

        if (noHouse)
        {
            rec.HasHouse = false;
            return rec;
        }

        rec.HasHouse = true;
        rec.TerritoryTypeId = houseId.TerritoryTypeId;
        rec.Ward = (byte)(houseId.WardIndex + 1); // WardIndex is 0-based
        rec.Plot = (byte)(houseId.PlotIndex + 1); // PlotIndex is 0-based
        rec.IsApartment = houseId.IsApartment;
        rec.WorldId = houseId.WorldId;

        // Resolve world name.
        try
        {
            var world = data.GetExcelSheet<Lumina.Excel.Sheets.World>().GetRowOrDefault(houseId.WorldId);
            if (world.HasValue) rec.WorldName = world.Value.Name.ExtractText();
        }
        catch { /* ignore */ }

        // Resolve district name from the housing territory's PlaceName.
        rec.District = ResolveDistrict(data, houseId.TerritoryTypeId);

        return rec;
    }

    private static string ResolveDistrict(IDataManager data, ushort territoryTypeId)
    {
        try
        {
            var tt = data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territoryTypeId);
            if (tt.HasValue)
            {
                var place = tt.Value.PlaceName.Value.Name.ExtractText();
                if (!string.IsNullOrEmpty(place)) return place;
            }
        }
        catch { /* ignore */ }
        return $"Territory {territoryTypeId}";
    }
}
