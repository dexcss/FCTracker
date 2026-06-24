using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace FCTracker.Data;

// Reads FC / vessel data that AutoRetainer and SubmarineTracker have already
// collected, and merges it into our own CharacterRecord list.
//
// Merge policy: a record we captured live (Source == "Live") is never overwritten
// by an import. Imported records fill gaps and update other imported records.
public static class Importer
{
    public class ImportResult
    {
        public int CharactersAdded;
        public int CharactersUpdated;
        public int VesselsImported;
        public readonly List<string> Messages = new();
        public bool AnyError;
    }

    // ---- Path discovery ---------------------------------------------------
    // Both plugins store under %APPDATA%\XIVLauncher\pluginConfigs\<Name>\, which
    // is independent of --roamingPath (same reasoning as our own SharedStore).
    private static string PluginConfigsRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "XIVLauncher", "pluginConfigs");
    }

    public static string DefaultAutoRetainerConfig()
        => Path.Combine(PluginConfigsRoot(), "AutoRetainer", "DefaultConfig.json");

    public static string DefaultSubmarineTrackerDb()
        => Path.Combine(PluginConfigsRoot(), "SubmarineTracker", "submarine-sqlite.db");

    // Given an import file path like …\<roamingRoot>\pluginConfigs\<Plugin>\file,
    // walk up to the roaming root so it matches our own AccountKey scheme. Returns
    // "" if the path doesn't look like a plugin-config path.
    private static string AccountKeyFromConfigPath(string filePath, string pluginFolder)
    {
        try
        {
            // filePath -> …\pluginConfigs\<pluginFolder>\<file>
            var pluginDir = Path.GetDirectoryName(filePath);                 // …\<pluginFolder>
            var pluginConfigs = Directory.GetParent(pluginDir!)?.FullName;   // …\pluginConfigs
            var roamingRoot = Directory.GetParent(pluginConfigs!)?.FullName; // roaming root
            return roamingRoot ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // ---- AutoRetainer (JSON) ---------------------------------------------
    public static ImportResult ImportAutoRetainer(List<CharacterRecord> target, string path, Dalamud.Plugin.Services.IDataManager? data = null)
    {
        var res = new ImportResult();
        try
        {
            if (!File.Exists(path))
            {
                res.Messages.Add($"AutoRetainer config not found at {path}");
                res.AnyError = true;
                return res;
            }

            // Derive the account key from the config's roaming root (…\XIVLauncher\
            // pluginConfigs\AutoRetainer\DefaultConfig.json -> roaming root), so
            // imported characters are tagged with the account they came from.
            var importAccountKey = AccountKeyFromConfigPath(path, "AutoRetainer");

            var root = JObject.Parse(File.ReadAllText(path));

            // FCData: dictionary keyed by FC id -> { Name, FCPoints, ... }
            var fcData = root["FCData"] as JObject;

            // OfflineData: array of characters with CID/Name/World/FCID + vessels.
            var offline = root["OfflineData"] as JArray;
            if (offline == null)
            {
                res.Messages.Add("AutoRetainer config had no OfflineData array.");
                res.AnyError = true;
                return res;
            }

            foreach (var ch in offline)
            {
                var cid = (ulong?)ch["CID"] ?? 0;
                if (cid == 0) continue;

                var name = (string?)ch["Name"] ?? string.Empty;
                var world = (string?)ch["World"] ?? string.Empty;
                var fcId = (ulong?)ch["FCID"] ?? 0;

                var (rec, isNew) = GetOrCreateForImport(target, cid, name, world, "AutoRetainer");

                // Tag with the originating account (so filler accounts you can't log
                // into still show an account), unless a live capture already set one.
                if (string.IsNullOrEmpty(rec.AccountKey) && !string.IsNullOrEmpty(importAccountKey))
                    rec.AccountKey = importAccountKey;

                if (fcId != 0)
                {
                    var fcName = string.Empty;
                    var fcCreditsText = string.Empty;
                    if (fcData?[fcId.ToString()] is JObject fc)
                    {
                        fcName = (string?)fc["Name"] ?? string.Empty;
                        var pts = (long?)fc["FCPoints"] ?? 0;
                        if (pts > 0) fcCreditsText = $"{pts:N0}";
                    }

                    // Never clobber a live record's FC snapshot, but we can fill an
                    // empty/imported one.
                    if (rec.Source != "Live" && (rec.Fc == null || rec.Fc.Source != "Live"))
                    {
                        rec.Fc ??= new FreeCompanySnapshot();
                        rec.Fc.FreeCompanyId = fcId;
                        if (!string.IsNullOrEmpty(fcName)) rec.Fc.Name = fcName;
                        if (!string.IsNullOrEmpty(fcCreditsText)) rec.Fc.CreditsText = fcCreditsText;
                        rec.Fc.Source = "AutoRetainer";
                        // Resolve region from the world via Lumina if we can.
                        if (data != null && string.IsNullOrEmpty(rec.Fc.Region) && !string.IsNullOrEmpty(world))
                            rec.Fc.Region = Game.GameReader.ResolveRegionCode(data, world);
                        if (data != null && string.IsNullOrEmpty(rec.Fc.DataCenter) && !string.IsNullOrEmpty(world))
                            rec.Fc.DataCenter = Game.GameReader.ResolveDataCenter(data, world);
                        if (rec.FirstSeenFcId == 0) rec.FirstSeenFcId = fcId;
                    }

                    // Vessels are only meaningful while in the FC.
                    ImportArVessels(ch["OfflineAirshipData"] as JArray, rec.Airships, res);
                    ImportArVessels(ch["OfflineSubmarineData"] as JArray, rec.Submersibles, res);
                    rec.VesselsLastUpdatedUtc ??= DateTime.UtcNow;
                }
                else
                {
                    // Character is NOT currently in an FC (e.g. left, but AR still
                    // keeps them assigned to subs for a future rejoin). Don't carry
                    // a stale FC tag or "has subs" state for an imported record.
                    if (rec.Source != "Live" && (rec.Fc == null || rec.Fc.Source != "Live"))
                    {
                        rec.Fc = null;
                        rec.Airships.Clear();
                        rec.Submersibles.Clear();
                        rec.VesselsLastUpdatedUtc = null;
                    }
                }

                if (isNew) res.CharactersAdded++; else res.CharactersUpdated++;
            }

            res.Messages.Add($"AutoRetainer: {res.CharactersAdded} added, {res.CharactersUpdated} updated.");
        }
        catch (Exception ex)
        {
            res.AnyError = true;
            res.Messages.Add($"AutoRetainer import failed: {ex.Message}");
        }
        return res;
    }

    private static void ImportArVessels(JArray? arr, List<VesselRecord> into, ImportResult res)
    {
        if (arr == null) return;
        foreach (var v in arr)
        {
            var name = (string?)v["Name"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (into.Exists(x => x.Name == name)) continue; // don't duplicate live data

            // AutoRetainer's OfflineVesselData uses ReturnTime (unix) when deployed.
            var ret = (uint?)v["ReturnTime"] ?? 0;
            into.Add(new VesselRecord { Name = name, ReturnTime = ret });
            res.VesselsImported++;
        }
    }

    // ---- SubmarineTracker (SQLite) ---------------------------------------
    public static ImportResult ImportSubmarineTracker(List<CharacterRecord> target, string dbPath, Dalamud.Plugin.Services.IDataManager? data = null)
    {
        var res = new ImportResult();
        try
        {
            var importAccountKey = AccountKeyFromConfigPath(dbPath, "SubmarineTracker");
            if (!File.Exists(dbPath))
            {
                res.Messages.Add($"SubmarineTracker database not found at {dbPath}");
                res.AnyError = true;
                return res;
            }

            // Read-only, and copy to temp so we never lock the live db while ST runs.
            var temp = Path.Combine(Path.GetTempPath(), $"fctracker_st_{Guid.NewGuid():N}.db");
            File.Copy(dbPath, temp, true);

            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = temp,
                    Mode = SqliteOpenMode.ReadOnly,
                };
                using var conn = new SqliteConnection(csb.ToString());
                conn.Open();

                // freecompany: FreeCompanyId(BLOB msgpack u64), FreeCompanyTag, World, CharacterName
                var fcs = new Dictionary<ulong, (string tag, string world, string chara)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT FreeCompanyId, FreeCompanyTag, World, CharacterName FROM freecompany";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var id = DecodeMsgpackU64((byte[])r["FreeCompanyId"]);
                        if (id == 0) continue;
                        fcs[id] = (
                            r["FreeCompanyTag"] as string ?? string.Empty,
                            r["World"] as string ?? string.Empty,
                            r["CharacterName"] as string ?? string.Empty);
                    }
                }

                // submarine: per-FC vessels with Name, Rank, Return, CExp, NExp
                var subsByFc = new Dictionary<ulong, List<VesselRecord>>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT FreeCompanyId, Name, Rank, Return, CExp, NExp, SubmarineId FROM submarine";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var id = DecodeMsgpackU64((byte[])r["FreeCompanyId"]);
                        if (id == 0) continue;
                        if (!subsByFc.TryGetValue(id, out var list))
                            subsByFc[id] = list = new();

                        list.Add(new VesselRecord
                        {
                            Name = r["Name"] as string ?? string.Empty,
                            RankId = (byte)Convert.ToInt32(r["Rank"]),
                            ReturnTime = (uint)Convert.ToInt64(r["Return"]),
                            CurrentExp = (uint)Convert.ToInt64(r["CExp"]),
                            NextLevelExp = (uint)Convert.ToInt64(r["NExp"]),
                            RegisterTime = (uint)Convert.ToInt64(r["SubmarineId"]),
                        });
                    }
                }

                // SubmarineTracker keys by FC, not character. We attach each FC to a
                // synthetic record keyed by the FC id (so it shows even if we have no
                // matching character), and also try to match the recorded CharacterName.
                foreach (var (fcId, info) in fcs)
                {
                    // Try to find an existing character in the same FC; otherwise make
                    // a synthetic per-FC record.
                    CharacterRecord rec;
                    bool isNew;
                    var existing = target.Find(x => x.Fc != null && x.Fc.FreeCompanyId == fcId);
                    if (existing != null)
                    {
                        rec = existing;
                        isNew = false;
                    }
                    else
                    {
                        // synthetic CID derived from FC id so re-imports are idempotent
                        (rec, isNew) = GetOrCreateForImport(
                            target, SyntheticCid(fcId), info.chara, info.world, "SubmarineTracker");
                    }

                    if (string.IsNullOrEmpty(rec.AccountKey) && !string.IsNullOrEmpty(importAccountKey))
                        rec.AccountKey = importAccountKey;

                    if (rec.Source != "Live" && (rec.Fc == null || rec.Fc.Source != "Live"))
                    {
                        rec.Fc ??= new FreeCompanySnapshot();
                        rec.Fc.FreeCompanyId = fcId;
                        if (string.IsNullOrEmpty(rec.Fc.Tag)) rec.Fc.Tag = info.tag;
                        rec.Fc.Source = "SubmarineTracker";
                        if (data != null && string.IsNullOrEmpty(rec.Fc.Region) && !string.IsNullOrEmpty(info.world))
                            rec.Fc.Region = Game.GameReader.ResolveRegionCode(data, info.world);
                        if (data != null && string.IsNullOrEmpty(rec.Fc.DataCenter) && !string.IsNullOrEmpty(info.world))
                            rec.Fc.DataCenter = Game.GameReader.ResolveDataCenter(data, info.world);
                        if (rec.FirstSeenFcId == 0) rec.FirstSeenFcId = fcId;
                    }

                    if (subsByFc.TryGetValue(fcId, out var subs))
                    {
                        foreach (var s in subs)
                        {
                            if (rec.Submersibles.Exists(x => x.Name == s.Name)) continue;
                            rec.Submersibles.Add(s);
                            res.VesselsImported++;
                        }
                        rec.VesselsLastUpdatedUtc ??= DateTime.UtcNow;
                    }

                    if (isNew) res.CharactersAdded++; else res.CharactersUpdated++;
                }

                res.Messages.Add($"SubmarineTracker: {fcs.Count} FCs, {res.VesselsImported} submersibles imported.");
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            res.AnyError = true;
            res.Messages.Add($"SubmarineTracker import failed: {ex.Message}");
        }
        return res;
    }

    // ---- helpers ----------------------------------------------------------

    // SubmarineTracker stores the FC id as a MessagePack-encoded uint64. We only
    // need to decode that one value, so we read the msgpack integer formats
    // directly rather than pulling in the MessagePack library.
    private static ulong DecodeMsgpackU64(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return 0;
        var b0 = bytes[0];

        // positive fixint
        if (b0 <= 0x7f) return b0;
        switch (b0)
        {
            case 0xcc: return bytes[1];                                   // uint8
            case 0xcd: return (ulong)((bytes[1] << 8) | bytes[2]);        // uint16
            case 0xce: return ((ulong)bytes[1] << 24) | ((ulong)bytes[2] << 16)
                            | ((ulong)bytes[3] << 8) | bytes[4];           // uint32
            case 0xcf:                                                     // uint64
                ulong v = 0;
                for (var i = 1; i <= 8; i++) v = (v << 8) | bytes[i];
                return v;
        }
        // fallback: treat as little-endian raw u64 if it's exactly 8 bytes
        if (bytes.Length == 8) return BitConverter.ToUInt64(bytes, 0);
        return 0;
    }

    // Deterministic synthetic CID for FC-only records (high bit set to avoid
    // colliding with real content ids).
    private static ulong SyntheticCid(ulong fcId) => 0x8000_0000_0000_0000UL | (fcId & 0x7FFF_FFFF_FFFF_FFFFUL);

    private static (CharacterRecord rec, bool isNew) GetOrCreateForImport(
        List<CharacterRecord> target, ulong cid, string name, string world, string source)
    {
        var existing = target.Find(x => x.ContentId == cid);
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(name)) existing.CharacterName = name;
            if (!string.IsNullOrEmpty(world)) existing.WorldName = world;
            existing.ImportedAtUtc = DateTime.UtcNow;
            return (existing, false);
        }

        var rec = new CharacterRecord
        {
            ContentId = cid,
            CharacterName = name,
            WorldName = world,
            Source = source,
            ImportedAtUtc = DateTime.UtcNow,
        };
        target.Add(rec);
        return (rec, true);
    }
}
