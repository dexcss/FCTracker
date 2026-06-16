using System;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FCTracker.Game;

// FC credits aren't a clean struct field. Two sources, neither always available:
//  1) The FreeCompany window header renders the credits number (no label). We scan
//     text nodes for the largest integer (excluding known rank/member values).
//     Available whenever the FC window is open.
//  2) AutoRetainer-style: the FreeCompanyCreditShop agent holds the credits as an
//     int at +256, but only once you've opened the FC Credit shop NPC.
// Hybrid: try the agent first (it's an exact int), fall back to the window scrape.
public static unsafe class AddonScraper
{
    // Returns the credits as a formatted string, or empty if neither source has it.
    public static string TryReadFcCredits(IGameGui gameGui, byte knownRank, int knownOnline, int knownTotal)
    {
        var agentVal = TryReadCreditsAgent();
        if (agentVal > 0)
            return agentVal.ToString("N0", CultureInfo.InvariantCulture);

        return TryScrapeFcCreditsWindow(gameGui, knownRank, knownOnline, knownTotal);
    }

    private static long TryReadCreditsAgent()
    {
        try
        {
            var module = AgentModule.Instance();
            if (module == null) return 0;
            var agent = module->GetAgentByInternalId(AgentId.FreeCompanyCreditShop);
            if (agent == null || !agent->IsAgentActive()) return 0;
            var credits = *(int*)((nint)agent + 256);
            return credits > 0 ? credits : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string TryScrapeFcCreditsWindow(IGameGui gameGui, byte knownRank, int knownOnline, int knownTotal)
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

            var cleaned = text.Replace(",", "").Replace(".", "").Replace(" ", "").Trim();
            if (cleaned.Length == 0) continue;
            if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                continue;

            if (val == knownRank) continue;
            if (val == knownOnline) continue;
            if (val == knownTotal) continue;
            if (val <= 0) continue;

            if (val > best) best = val;
        }

        return best > 0 ? best.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;
    }
}

