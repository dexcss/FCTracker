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
