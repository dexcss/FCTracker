using System;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FCTracker.Game;

// FC credits aren't a clean struct field. In the FreeCompany window the credits
// figure (e.g. "2,567,502" or "416") is rendered as a text node in the header next
// to the rank, with no label. We scan all text nodes for integers and take the
// largest, but first exclude values we already know are NOT credits (the FC rank
// and the online/total member counts read from the proxy). That lets us capture
// small credit totals on alts without mistaking rank/member numbers for credits.
// Best-effort: only works while the FC window is open.
public static unsafe class AddonScraper
{
    public static string TryScrapeFcCredits(IGameGui gameGui, byte knownRank, int knownOnline, int knownTotal)
    {
        nint addonAddress = gameGui.GetAddonByName("FreeCompany", 1);
        if (addonAddress == nint.Zero) return string.Empty;

        var addon = (AtkUnitBase*)addonAddress;
        if (addon == null || !addon->IsVisible) return string.Empty;

        long best = -1;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;

            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Strip thousands separators / spaces and try to parse a pure integer.
            var cleaned = text.Replace(",", "").Replace(".", "").Replace(" ", "").Trim();
            if (cleaned.Length == 0) continue;
            if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                continue;

            // Exclude numbers we already know aren't credits.
            if (val == knownRank) continue;
            if (val == knownOnline) continue;
            if (val == knownTotal) continue;
            if (val <= 0) continue;

            if (val > best) best = val;
        }

        return best > 0 ? best.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;
    }
}

