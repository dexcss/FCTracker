using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FCTracker.Game;

// Thin wrappers around AutoRetainer and Lifestream IPC. We call the raw IPC
// subscriber strings directly (verified against AutoRetainerAPI and Lifestream's
// IPCProvider) so we don't have to bundle their assemblies. Every call is guarded:
// if the other plugin isn't installed/loaded, these no-op or return defaults.
public static class PluginIpc
{
    private static IDalamudPluginInterface Pi => Plugin.PluginInterface;

    // ---- AutoRetainer ----

    // All known character CIDs registered in AutoRetainer.
    public static List<ulong> GetRegisteredCharacters()
    {
        try
        {
            return Pi.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc()
                   ?? new List<ulong>();
        }
        catch { return new List<ulong>(); }
    }

    // Reads AutoRetainer's OfflineCharacterData for a CID and pulls just the fields
    // we need. AR returns its own type; we access it dynamically via reflection on
    // the boxed object to avoid referencing AR's assembly.
    public static ArCharInfo? GetArCharInfo(ulong cid)
    {
        try
        {
            var data = Pi.GetIpcSubscriber<ulong, object>("AutoRetainer.GetOfflineCharacterData")
                .InvokeFunc(cid);
            if (data == null) return null;

            var t = data.GetType();
            ulong fcid = GetULong(t, data, "FCID");
            bool workshopEnabled = GetBool(t, data, "WorkshopEnabled");
            string name = GetString(t, data, "Name");
            string world = GetString(t, data, "World");

            // Submarine return times: OfflineSubmarineData is a List<OfflineVesselData>
            // where each has a uint ReturnTime (unix seconds). Read them reflectively.
            var subReturns = new List<uint>();
            try
            {
                if (t.GetField("OfflineSubmarineData")?.GetValue(data) is System.Collections.IEnumerable subs)
                {
                    foreach (var s in subs)
                    {
                        var rt = s.GetType().GetField("ReturnTime")?.GetValue(s);
                        if (rt is uint u) subReturns.Add(u);
                    }
                }
            }
            catch { /* ignore */ }

            return new ArCharInfo
            {
                Cid = cid,
                FcId = fcid,
                WorkshopEnabled = workshopEnabled,
                Name = name,
                World = world,
                SubReturnTimes = subReturns,
            };
        }
        catch { return null; }
    }

    public static bool IsAutoRetainerAvailable()
    {
        try
        {
            // Cheap probe: GetRegisteredCIDs exists when AR is loaded.
            Pi.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    // Confirmed in AutoRetainer's IPC.cs: registers "AutoRetainer.GetMultiModeEnabled".
    public static bool GetMultiModeEnabled()
    {
        try
        {
            return Pi.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc();
        }
        catch { return false; }
    }

    // ---- Lifestream ----

    // Sends the player to a housing address. Lifestream's ExecuteCommand runs
    // "/li <args>"; the documented format is "<World> <District> <Ward> <Plot>"
    // with plain numbers (e.g. "Ultros Mist 1 1").
    public static bool LifestreamGoToAddress(string world, string district, int ward, int plot)
    {
        try
        {
            var args = $"{world} {district} {ward} {plot}";
            Pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(args);
            return true;
        }
        catch { return false; }
    }

    public static bool IsLifestreamAvailable()
    {
        try
        {
            Pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    // Logs into a character from anywhere (connects, opens chara-select, logs in).
    // Confirmed in Lifestream's IPCProvider: ConnectAndLogin(name, homeWorld).
    // Logs into a character via Lifestream. Returns a human-readable result so the
    // caller can surface why it failed (Lifestream silently returns false when its
    // own CanAutoLogin() is not satisfied or it's busy).
    public static string LifestreamLoginDiagnostic(string charaName, string homeWorld)
    {
        try
        {
            // Probe Lifestream presence.
            try { Pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
            catch { return "Lifestream not installed / IPC unavailable."; }

            bool busy = false;
            try { busy = Pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); } catch { }
            if (busy) return "Lifestream is busy right now — try again in a moment.";

            bool canAuto = true;
            try { canAuto = Pi.GetIpcSubscriber<bool>("Lifestream.CanAutoLogin").InvokeFunc(); } catch { }
            if (!canAuto) return "Lifestream auto-login isn't available (enable/allow auto-login in Lifestream settings, and be at a state where it can act).";

            var ok = Pi.GetIpcSubscriber<string, string, bool>("Lifestream.ConnectAndLogin")
                .InvokeFunc(charaName, homeWorld);
            return ok ? "" : "Lifestream declined the login (busy or auto-login prerequisites not met).";
        }
        catch (Exception ex)
        {
            return $"Login call failed: {ex.Message}";
        }
    }

    public static bool LifestreamLogin(string charaName, string homeWorld)
    {
        try
        {
            return Pi.GetIpcSubscriber<string, string, bool>("Lifestream.ConnectAndLogin")
                .InvokeFunc(charaName, homeWorld);
        }
        catch { return false; }
    }

    // ---- reflection helpers (AR's OfflineCharacterData fields are public) ----
    private static ulong GetULong(Type t, object o, string field)
        => t.GetField(field)?.GetValue(o) is ulong v ? v : 0;
    private static bool GetBool(Type t, object o, string field)
        => t.GetField(field)?.GetValue(o) is bool v && v;
    private static string GetString(Type t, object o, string field)
        => t.GetField(field)?.GetValue(o) as string ?? string.Empty;
}

public class ArCharInfo
{
    public ulong Cid;
    public ulong FcId;
    public bool WorkshopEnabled;
    public string Name = string.Empty;
    public string World = string.Empty;
    public List<uint> SubReturnTimes = new();
}
