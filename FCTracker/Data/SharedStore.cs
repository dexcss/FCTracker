using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace FCTracker.Data;

// A machine-wide (per Windows user) store for character records.
//
// The path is computed from %APPDATA% DIRECTLY, not from Dalamud's plugin config
// directory. This is deliberate: launching with --roamingPath moves Dalamud's
// roaming folder, which is why per-install configs don't share. By resolving
// %APPDATA%\XIVLauncher\pluginConfigs\FCTracker\shared.json ourselves, every client
// on the same Windows user reads/writes the same file no matter what roaming path
// launched it.
//
// Concurrency model (for multiboxing):
//   - All writes go through Save(), which takes an exclusive OS file lock with
//     retry/backoff, RE-READS the current file, merges the caller's record by
//     ContentId (last-write-wins per character, never clobbering other characters'
//     entries), then writes back and releases the lock.
//   - Reads also retry on transient sharing violations.
public sealed class SharedStore
{
    private readonly string path;
    private static readonly object GateLock = new();

    public SharedStore(bool useShared, string overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            path = overridePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            path = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "FCTracker", "shared.json");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public string Path_ => path;

    private sealed class StoreFile
    {
        public int Version = 1;
        public List<CharacterRecord> Characters = new();
    }

    // Read all records (best-effort; returns empty on missing/corrupt file).
    public List<CharacterRecord> LoadAll()
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return new();
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var sr = new StreamReader(fs);
                    var json = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json)) return new();
                    var data = JsonConvert.DeserializeObject<StoreFile>(json);
                    return data?.Characters ?? new();
                }
                catch (IOException)
                {
                    Thread.Sleep(25 + attempt * 25); // another client holds it; back off
                }
                catch (Exception)
                {
                    return new(); // corrupt/unreadable — don't crash the game loop
                }
            }
            return new();
        }
    }

    // Merge a single character's record into the shared file under an exclusive lock.
    public void UpsertCharacter(CharacterRecord record)
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    // Open with exclusive access — no other process may read/write
                    // while we read-merge-write.
                    using var fs = new FileStream(
                        path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    StoreFile store;
                    using (var sr = new StreamReader(fs, leaveOpen: true))
                    {
                        var json = sr.ReadToEnd();
                        store = string.IsNullOrWhiteSpace(json)
                            ? new StoreFile()
                            : (JsonConvert.DeserializeObject<StoreFile>(json) ?? new StoreFile());
                    }

                    // Merge by ContentId (preserve everyone else's data).
                    var idx = store.Characters.FindIndex(x => x.ContentId == record.ContentId);
                    if (idx >= 0) store.Characters[idx] = record;
                    else store.Characters.Add(record);

                    var outJson = JsonConvert.SerializeObject(store, Formatting.Indented);

                    fs.SetLength(0);
                    fs.Position = 0;
                    using (var sw = new StreamWriter(fs, leaveOpen: true))
                    {
                        sw.Write(outJson);
                        sw.Flush();
                    }
                    fs.Flush(true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(25 + attempt * 25); // contended; retry
                }
                catch (Exception)
                {
                    return; // never throw into the framework update loop
                }
            }
        }
    }

    // Remove a character entirely from the shared file, under an exclusive lock.
    public void DeleteCharacter(ulong contentId)
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using var fs = new FileStream(
                        path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    StoreFile store;
                    using (var sr = new StreamReader(fs, leaveOpen: true))
                    {
                        var json = sr.ReadToEnd();
                        store = string.IsNullOrWhiteSpace(json)
                            ? new StoreFile()
                            : (JsonConvert.DeserializeObject<StoreFile>(json) ?? new StoreFile());
                    }

                    store.Characters.RemoveAll(x => x.ContentId == contentId);

                    var outJson = JsonConvert.SerializeObject(store, Formatting.Indented);
                    fs.SetLength(0);
                    fs.Position = 0;
                    using (var sw = new StreamWriter(fs, leaveOpen: true))
                    {
                        sw.Write(outJson);
                        sw.Flush();
                    }
                    fs.Flush(true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(25 + attempt * 25);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    // Wipe all records.
    public void ClearAll()
    {
        lock (GateLock)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sw = new StreamWriter(fs);
                sw.Write(JsonConvert.SerializeObject(new StoreFile(), Formatting.Indented));
            }
            catch { /* ignore */ }
        }
    }
}
