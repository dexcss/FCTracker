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

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);

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
