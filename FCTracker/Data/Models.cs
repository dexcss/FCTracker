using System;
using System.Collections.Generic;

namespace FCTracker.Data;

// A single airship or submersible belonging to the FC workshop.
public class VesselRecord
{
    public string Name = string.Empty;
    public byte RankId;
    public uint CurrentExp;
    public uint NextLevelExp;
    // Unix seconds (0 == not deployed / idle).
    public uint RegisterTime;
    public uint ReturnTime;

    // Equipped parts. Ids are SubmarinePart / AirshipExplorationPart sheet keys.
    public ushort HullId;
    public ushort SternId;
    public ushort BowId;
    public ushort BridgeId;

    // Community-standard build shorthand, e.g. "SSUC++". Computed from the part ids
    // the same way Submarine Tracker does it (no sheet lookup needed).
    public string Build =>
        PartLetter(HullId) + PartLetter(SternId) + PartLetter(BowId) + PartLetter(BridgeId);

    private static string PartLetter(ushort partId)
    {
        if (partId == 0) return "-";
        // Parts are grouped in fours per tier; +/++ denote the higher unlock tiers.
        return ((partId - 1) / 4) switch
        {
            0 => "S",
            1 => "U",
            2 => "W",
            3 => "C",
            4 => "Y",
            5 or 6 or 7 or 8 or 9 => PartLetter((ushort)(partId - 20)) + "+",
            _ => "?",
        };
    }

    public bool IsDeployed => ReturnTime != 0;

    public DateTime ReturnTimeUtc => ReturnTime == 0
        ? DateTime.MinValue
        : DateTime.UnixEpoch.AddSeconds(ReturnTime);

    public TimeSpan TimeUntilReturn
    {
        get
        {
            if (ReturnTime == 0) return TimeSpan.Zero;
            var remaining = ReturnTimeUtc - DateTime.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }
}

// One rank in the FC hierarchy (from InfoProxyFreeCompany.RankData).
public class RankRecord
{
    public string Name = string.Empty;
    public byte RankNumber;
    public ushort MemberCount;

    // Decoded permission groups (stored as readable strings so the config stays
    // human-readable and patch changes can't corrupt an enum).
    public string BasicSettings = string.Empty;
    public string ChestItems1 = string.Empty;
    public string ChestItems2 = string.Empty;
    public string ChestItems3 = string.Empty;
    public string ChestItems4 = string.Empty;
    public string ChestItems5 = string.Empty;
    public string ChestCrystals = string.Empty;
    public string ChestGil = string.Empty;
    public string Housing = string.Empty;
    public string Workshop = string.Empty;
}

// FC estate / house info (from HousingManager.GetOwnedHouseId).
public class HouseRecord
{
    public bool HasHouse;
    public ushort TerritoryTypeId;
    public string District = string.Empty;  // resolved name, e.g. "Mist", "Empyreum"
    public byte Plot;                        // 1-based plot number for display
    public byte Ward;                        // 1-based ward number for display
    public ushort WorldId;
    public string WorldName = string.Empty;
    public bool IsApartment;
    public DateTime LastCheckedUtc = DateTime.UtcNow;
}

// A snapshot of one Free Company at a point in time.
public class FreeCompanySnapshot
{
    public ulong FreeCompanyId;
    public string Name = string.Empty;
    public string Tag = string.Empty;          // 5-char company tag (from local player)
    public string Master = string.Empty;        // current FC master (top rank holder)
    public byte Rank;                           // FC level (1-30); the game's "Rank"
    public ushort OnlineMembers;
    public ushort TotalMembers;
    public ushort HomeWorldId;
    public byte GrandCompany;

    // Best-effort, scraped from the FC window addon when available (may be empty).
    public string PointsText = string.Empty;    // e.g. "Company Points: 1234"
    public string CreditsText = string.Empty;

    public DateTime LastUpdatedUtc = DateTime.UtcNow;

    // Full rank ladder (named ranks, numbers, member counts, permissions).
    public List<RankRecord> Ranks = new();

    // Where this snapshot came from. "Live" = read from game by this plugin.
    public string Source = "Live";

    // FC estate, if the FC owns one (checked from the game).
    public HouseRecord? House;
}

// Everything we know for a single character (keyed by ContentId / CID).
public class CharacterRecord
{
    public ulong ContentId;
    public string CharacterName = string.Empty;
    public string WorldName = string.Empty;

    // When we FIRST saw this character in this FC. This is the only "membership
    // duration" we can offer — the game does not expose a real join date, so we
    // measure from first observation by this plugin.
    public DateTime FirstSeenInFcUtc = DateTime.UtcNow;
    public ulong FirstSeenFcId;

    public FreeCompanySnapshot? Fc;

    public List<VesselRecord> Airships = new();
    public List<VesselRecord> Submersibles = new();
    public DateTime? VesselsLastUpdatedUtc;

    // --- User-entered metadata the game cannot provide ---
    // Founder / who-won-the-house etc. Free text, never read from the game.
    public string ManualFounder = string.Empty;
    public string ManualHouseWinner = string.Empty;
    public string ManualNotes = string.Empty;

    public TimeSpan MembershipDuration => DateTime.UtcNow - FirstSeenInFcUtc;

    // "Live" if this plugin observed the character; otherwise the importer name
    // (e.g. "AutoRetainer", "SubmarineTracker"). Lets the UI show provenance and
    // lets live data take precedence over imported data on merge.
    public string Source = "Live";
    public DateTime? ImportedAtUtc;
}
