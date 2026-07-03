using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FCTracker.Data;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Keyed by ContentId. List (not dict) keeps Newtonsoft serialization simple
    // and mirrors the AutoRetainer OfflineData pattern.
    public List<CharacterRecord> Characters = new();

    public bool OpenOnLogin = false;

    // When true, character records live in a single shared JSON file computed from
    // %APPDATA% directly (ignoring Dalamud's --roamingPath), so all clients on this
    // Windows user share data regardless of which roaming path launched them.
    public bool UseSharedStorage = true;

    // Optional override. Empty => default %APPDATA%\XIVLauncher\pluginConfigs\FCTracker\shared.json
    public string SharedStoragePathOverride = string.Empty;

    // Import source paths (empty => use computed defaults).
    public string AutoRetainerConfigPath = string.Empty;
    public string SubmarineTrackerDbPath = string.Empty;

    // View options.
    public bool GroupByFc = false;          // false = flat character list (default)
    public bool ShowAccountColumn = false;  // optional account-alias column/tag

    // Optional (off by default) fields that aren't cleanly readable:
    public bool ShowMemberFcRank = false;   // best-effort scrape of own FC rank
    public bool ShowFounderAndTime = false; // founder/original-winner + time-in-FC

    // Optional columns (checkmark in settings).
    public bool ShowLoginButton = false;    // door icon to log into a character (Lifestream)
    public bool ShowRegionColumn = false;   // region column in both views
    public bool SubsortByRegion = false;    // group rows by region, then secondary sort
    public bool ManualColumnResize = false; // true = draggable column borders; false = auto-fit

    // Per-column visibility for the FC table. Defaults match the prior layout.
    public bool ColTp = true;          // Lifestream-to-house button
    public bool ColLogin = true;       // login (door) button column (also needs ShowLoginButton)
    public bool ColRegion = false;     // mirrors ShowRegionColumn intent; kept separate per-toggle
    public bool ColDataCenter = false; // data center name (Aether, etc.)
    public bool ColWorld = true;
    public bool ColAccount = true;
    public bool ColSubRunner = true;
    public bool ColCharacter = false;  // character name (for no-FC rows; sub-runner on FC rows)
    public bool ColFc = true;
    public bool ColTag = true;
    public bool ColMembers = true;
    public bool ColLevel = true;
    public bool ColSubs = true;
    public bool ColReturns = true;     // submarine return timer (from AutoRetainer)
    public bool ColHouse = true;
    public bool ColCredits = true;
    public bool ColCustomName = false; // custom per-FC house/label name
    public bool ColOwner = false;      // FC owner character name (editable, defaults to first seen)
    public bool ColBidTimer = false;   // per-character housing bid eligibility countdown

    // Column display order (by name). Any columns missing here are appended in
    // default order; unknown names are ignored. Lets users reorder via settings.
    public List<string> ColumnOrder = new();

    // Per-FC custom names (keyed by the same stable FC key the UI uses).
    public Dictionary<string, string> CustomFcNames = new();

    // Per-FC owner character name (editable; defaults to the first character seen).
    // Keyed by the stable FC key.
    public Dictionary<string, string> FcOwnerNames = new();

    // Housing bid eligibility: 30 days from a start date. Per character (ContentId),
    // the start date defaults to first-seen but can be overridden. Stored as unix
    // seconds; 0 = use the character's FirstSeenInFcUtc.
    public Dictionary<ulong, long> BidStartOverrideUnix = new();
    public int BidEligibilityDays = 30;

    // Customizable colors (RGBA 0-1, stored as float[4]). Null = use default.
    public float[]? ColorTimer;      // sub return countdown / bid countdown text
    public float[]? ColorReady;      // "Back"/eligible (green)
    public float[]? ColorSubsFull;   // subs deployed/full
    public float[]? ColorSubsEmpty;  // subs 0/4 with a house (warning)
    public float[]? ColorLevelLow;   // FC level < 6
    public float[]? ColorDaysNew;    // days-in-FC <= 30

    // Account aliases: maps a detected Dalamud roaming path -> a friendly name.
    // (ServiceAccount turned out to be the wrong axis — it's the paid +chars tier,
    // not the user's separate game accounts, which correspond to roaming paths.)
    public Dictionary<string, string> AccountAliases = new();

    // When true, on login (if AutoRetainer multimode is enabled) briefly open the FC
    // window to refresh level/credits/house. Off by default — the window flickers
    // open on screen when this fires.
    public bool AutoOpenFcOnLogin = false;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);

    // Default semantic colors (RGBA).
    public static readonly float[] DefTimer = { 0.85f, 0.8f, 0.45f, 1f };
    public static readonly float[] DefReady = { 0.5f, 0.85f, 0.5f, 1f };
    public static readonly float[] DefSubsFull = { 0.5f, 0.85f, 0.5f, 1f };
    public static readonly float[] DefSubsEmpty = { 0.85f, 0.5f, 0.5f, 1f };
    public static readonly float[] DefLevelLow = { 0.85f, 0.5f, 0.5f, 1f };
    public static readonly float[] DefDaysNew = { 0.85f, 0.5f, 0.5f, 1f };

    public System.Numerics.Vector4 Col(float[]? stored, float[] def)
    {
        var c = stored ?? def;
        if (c.Length < 4) c = def;
        return new System.Numerics.Vector4(c[0], c[1], c[2], c[3]);
    }

    public CharacterRecord GetOrCreate(ulong contentId, string name, string world)
    {
        foreach (var c in Characters)
        {
            if (c.ContentId == contentId)
            {
                // keep name/world fresh
                if (!string.IsNullOrEmpty(name)) c.CharacterName = name;
                if (!string.IsNullOrEmpty(world)) c.WorldName = world;
                return c;
            }
        }

        var rec = new CharacterRecord
        {
            ContentId = contentId,
            CharacterName = name,
            WorldName = world,
        };
        Characters.Add(rec);
        return rec;
    }
}
