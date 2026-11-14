// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>
/// Milestone 3: agent orchestration for joiner horse ownership reporting, live + snapshot tack class sync,
/// host <c>crime_ignoredHorseTheft_Horse</c> context toggling, save-safe temporary tack stripping,
/// joiner far-whistle stream hold, and peer/session disconnect cleanup.
/// </summary>
public partial class GameBridge
{
    private string? _m3JoinerHorseName;
    private string? _m3JoinerHorseAlias;
    private string? _m3JoinerTackHorse;
    private string _m3JoinerTackCsv = "";
    private string? _m3JoinerLastSentPayload;
    private string? _m3JoinerSeededTag;
    private bool _m3WasJoinerActive;
    private long _m3JoinerPollAtMs, _m3HostTickAtMs;
    private readonly ConcurrentDictionary<byte, (string? Horse, string TackCsv)> _m3HostPeerHorses = new();
    private readonly ConcurrentDictionary<string, bool> _m3HostContextWanted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _m3HostContextApplied = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _m3ContextGate = new(1, 1);

    private void M3OnConnect()
    {
        _m3JoinerHorseName = null;
        _m3JoinerHorseAlias = null;
        _m3JoinerTackHorse = null;
        _m3JoinerTackCsv = "";
        _m3JoinerLastSentPayload = null;
        _m3JoinerSeededTag = null;
        _m3WasJoinerActive = false;
        _m3JoinerPollAtMs = 0;
        _m3HostTickAtMs = 0;
        _m3HostPeerHorses.Clear();
        _m3HostContextWanted.Clear();
        _m3HostContextApplied.Clear();
    }

    private async Task M3OnDisconnectAsync()
    {
        _m3JoinerHorseName = null;
        _m3JoinerHorseAlias = null;
        _m3JoinerTackHorse = null;
        _m3JoinerTackCsv = "";
        _m3JoinerLastSentPayload = null;
        _m3JoinerSeededTag = null;
        _m3WasJoinerActive = false;
        _m3HostPeerHorses.Clear();
        _m3HostContextWanted.Clear();
        await M3ReconcileHostContextsAsync();
        try { await ExecLuaAsync("if KCD2MP_M3ReleaseAllPeerHorses then KCD2MP_M3ReleaseAllPeerHorses(\"disconnect\") end"); } catch { }
    }

    private void M3OnPeerDisconnected(byte ghostId)
    {
        _m3HostPeerHorses.TryRemove(ghostId, out _);
        if (W137Host)
            _ = ExecLuaAsync($"if KCD2MP_W160PeerHorse then KCD2MP_W160PeerHorse({ghostId}, \"-\", \"\") end");
    }

    /// <summary>H4 + C1: Joiner's own companion horse (and its WO-217 lh* alias) is never driven by Host's NpcState stream.</summary>
    internal bool M3DropOwnHorseStream(string npcName)
    {
        if (!W137JoinerSession || _m3JoinerHorseName is not { Length: > 0 } own) return false;
        if (string.Equals(npcName, own, StringComparison.OrdinalIgnoreCase)) return true;
        return _m3JoinerHorseAlias is { Length: > 0 } al && string.Equals(npcName, al, StringComparison.OrdinalIgnoreCase);
    }

    private async Task M3LiftNativeHoldOnHorseAsync(string key)
    {
        if (_nativeBound.TryRemove(key, out _))
            NativeBindInOrder(() => _combat.NpcBindAsync(false, 0, 0, 0, 0, 0, 0, key));
        if (_w131Guarded.TryRemove(key, out uint eid))
        {
            try { await _combat.Wo131CopyGuardAsync(false, eid); } catch { }
            await Wo132DiscardAsync(false, eid);
        }
        if (_w132Engaged.TryRemove(key, out var eg))
        {
            try { await _combat.Wo132EngageAsync(false, eg.Eid, default); } catch { }
        }
    }

    private void M3SetJoinerOwnHorse(string? horseName)
    {
        _m3JoinerHorseName = horseName;
        _m3JoinerHorseAlias = M3HorseRules.LongAlias(horseName);
        if (horseName is { Length: > 0 } hn)
        {
            _ = M3LiftNativeHoldOnHorseAsync(hn);
            if (_m3JoinerHorseAlias is { Length: > 0 } al)
                _ = M3LiftNativeHoldOnHorseAsync(al);
        }
    }

    private bool M3TrySeedFromLatestSnapshot(string tag)
    {
        try
        {
            var snaps = _henry.Snapshots(tag);
            if (snaps.Count == 0) return false;
            var parts = _henry.Load(snaps[0]);
            if (!M3HorseRules.TryReadSnapshotHorseNameAndTack(parts, out var snapHorse, out var snapTack))
                return false;
            _m3JoinerSeededTag = tag;
            M3SetJoinerOwnHorse(snapHorse);
            _m3JoinerTackHorse = snapHorse;
            _m3JoinerTackCsv = snapHorse is null ? "" : M3HorseRules.NormalizeTackCsv(snapTack);
            _m3JoinerLastSentPayload = null;
            return true;
        }
        catch { return false; }
    }

    private async Task M3TickAsync(bool host, bool joiner, bool joinerActive)
    {
        long now = Environment.TickCount64;
        if (host && LivePartners().Count > 0 && now - _m3HostTickAtMs >= 2_000)
        {
            _m3HostTickAtMs = now;
            _ = ExecLuaAsync("if KCD2MP_M3HostTick then KCD2MP_M3HostTick() end");
            await M3ReconcileHostContextsAsync();
        }
        if (!joiner)
        {
            _m3JoinerSeededTag = null;
            _m3WasJoinerActive = false;
            _m3JoinerLastSentPayload = null;
            return;
        }
        if (_joinedTag is { Length: > 0 } tag && _m3JoinerSeededTag != tag)
            M3TrySeedFromLatestSnapshot(tag);

        if (!joinerActive)
        {
            _m3WasJoinerActive = false;
            _m3JoinerLastSentPayload = null;
            return;
        }
        if (!_m3WasJoinerActive)
        {
            _m3WasJoinerActive = true;
            _m3JoinerLastSentPayload = null;
            if (_m3JoinerSeededTag is null && _joinedTag is { Length: > 0 } tag2)
                M3TrySeedFromLatestSnapshot(tag2);

            await ExecLuaAsync($"if KCD2MP_M3SeedOwnHorse then KCD2MP_M3SeedOwnHorse(\"{EscapeLua(_m3JoinerHorseName ?? "-")}\") end");
            _m3JoinerPollAtMs = now;
            if (_m3JoinerHorseName is { Length: > 0 } seededName)
            {
                var seededTack = _m3JoinerTackHorse == seededName && _m3JoinerTackCsv.Length > 0
                    ? _m3JoinerTackCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    : null;
                await M3RefreshAndSendJoinerOwnHorseAsync(seededName, seededTack);
            }
            else if (_m3JoinerSeededTag is not null)
            {
                _m3JoinerLastSentPayload = "-";
                uint htok = Interlocked.Increment(ref _w137Tok);
                await Wo137SendAsync(Protocol.QuestAskUp, Protocol.JoinTargetHost, Protocol.QuestAskOwnHorse, htok, "-");
            }
            return;
        }
        if (_m3JoinerHorseName is { Length: > 0 } hName && now - _m3JoinerPollAtMs >= 5_000)
        {
            _m3JoinerPollAtMs = now;
            await M3RefreshAndSendJoinerOwnHorseAsync(hName);
        }
    }

    private void M3OnLocalOwnHorseEvent(string? arg)
    {
        if (!M3HorseRules.TryParseOwnHorseAsk(arg, out var horseName, out var luaTack)) return;
        M3SetJoinerOwnHorse(horseName);
        _m3JoinerPollAtMs = Environment.TickCount64;
        if (!W137Joiner) return;

        if (horseName is null)
        {
            _m3JoinerTackHorse = null;
            _m3JoinerTackCsv = "";
            if (_m3JoinerLastSentPayload == "-") return;
            _m3JoinerLastSentPayload = "-";
            uint htok = Interlocked.Increment(ref _w137Tok);
            Console.WriteLine("MP-WO160 joiner: owns no horse now -- told to the host");
            _ = Wo137SendAsync(Protocol.QuestAskUp, Protocol.JoinTargetHost, Protocol.QuestAskOwnHorse, htok, "-");
            return;
        }

        _ = M3RefreshAndSendJoinerOwnHorseAsync(horseName, luaTack);
    }

    private async Task M3RefreshAndSendJoinerOwnHorseAsync(string horseName, List<string>? fallbackTack = null)
    {
        if (!W137Joiner || _m3JoinerHorseName != horseName) return;
        List<string> tack = [];
        try
        {
            var worn = await Wo216OutfitOfAsync(horseName);
            if (worn is not null)
            {
                tack = worn.Select(ExtraRow.ClassText).ToList();
            }
            else if (fallbackTack is { Count: > 0 })
            {
                tack = fallbackTack;
            }
            else if (_m3JoinerTackHorse == horseName && _m3JoinerTackCsv.Length > 0)
            {
                tack = _m3JoinerTackCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else
            {
                tack = M3ReadLatestSnapshotTackClasses(horseName);
            }
        }
        catch
        {
            tack = fallbackTack is { Count: > 0 }
                ? fallbackTack
                : (_m3JoinerTackHorse == horseName && _m3JoinerTackCsv.Length > 0
                    ? _m3JoinerTackCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    : M3ReadLatestSnapshotTackClasses(horseName));
        }

        string payload = M3HorseRules.FormatOwnHorseAsk(horseName, tack);
        _m3JoinerTackHorse = horseName;
        _m3JoinerTackCsv = M3HorseRules.NormalizeTackCsv(tack);
        if (payload == _m3JoinerLastSentPayload || !W137Joiner) return;
        _m3JoinerLastSentPayload = payload;
        uint htok = Interlocked.Increment(ref _w137Tok);
        Console.WriteLine($"MP-WO160 joiner: its own horse is {horseName} (tack={(tack.Count == 0 ? "-" : _m3JoinerTackCsv)}) -- told to the host");
        await Wo137SendAsync(Protocol.QuestAskUp, Protocol.JoinTargetHost, Protocol.QuestAskOwnHorse, htok, payload);
    }

    private List<string> M3ReadLatestSnapshotTackClasses(string expectedHorseName)
    {
        try
        {
            if (_joinedTag is not { Length: > 0 } tag) return [];
            var snaps = _henry.Snapshots(tag);
            if (snaps.Count == 0) return [];
            var parts = _henry.Load(snaps[0]);
            if (!M3HorseRules.TryReadSnapshotHorseNameAndTack(parts, out var snapHorse, out var snapTack))
                return [];
            return string.Equals(snapHorse, expectedHorseName, StringComparison.OrdinalIgnoreCase) ? snapTack : [];
        }
        catch { return []; }
    }

    private void M3OnHostOwnHorseAsk(byte src, string? horseName, List<string> tackClasses)
    {
        string tackCsv = M3HorseRules.NormalizeTackCsv(tackClasses);
        if (horseName is null)
        {
            _m3HostPeerHorses.TryRemove(src, out _);
            Console.WriteLine($"MP-WO160 host: ghost {src} now owns no horse -- released in this world");
            _ = ExecLuaAsync($"if KCD2MP_W160PeerHorse then KCD2MP_W160PeerHorse({src}, \"-\", \"\") end");
            return;
        }
        _m3HostPeerHorses[src] = (horseName, tackCsv);
        Console.WriteLine($"MP-WO160 host: ghost {src}'s own horse is {horseName} (tack={(tackCsv.Length == 0 ? "-" : tackCsv)}) -- marked in this world");
        _ = ExecLuaAsync($"if KCD2MP_W160PeerHorse then KCD2MP_W160PeerHorse({src}, \"{EscapeLua(horseName)}\", \"{tackCsv}\") end");
    }

    private void M3OnPeerHorseContextEvent(string? arg)
    {
        if (!W137Host || !M3HorseRules.TryParsePeerHorseContextEvent(arg, out string horse, out bool on)) return;
        if (on) _m3HostContextWanted[horse] = true;
        else _m3HostContextWanted.TryRemove(horse, out _);
        _ = M3ReconcileHostContextsAsync();
    }

    private async Task M3ReconcileHostContextsAsync()
    {
        await _m3ContextGate.WaitAsync();
        try
        {
            var keys = _m3HostContextWanted.Keys.Union(_m3HostContextApplied.Keys, StringComparer.Ordinal).ToList();
            foreach (var horse in keys)
            {
                bool want = _m3HostContextWanted.TryGetValue(horse, out bool w) && w;
                bool have = _m3HostContextApplied.TryGetValue(horse, out bool a) && a;
                if (want == have) continue;
                try
                {
                    var res = await _combat.Wo139ContextAsync(want, "crime_ignoredHorseTheft_Horse", horse);
                    if (res is 1 or 0)
                    {
                        if (want) _m3HostContextApplied[horse] = true;
                        else _m3HostContextApplied.TryRemove(horse, out _);
                    }
                    Console.WriteLine($"MP-M3-HORSE host: {horse} {(want ? "marked legal for partner" : "restored")} -- crime_ignoredHorseTheft_Horse {(res is 1 or 0 ? "ok" : $"not set ({res?.ToString(CultureInfo.InvariantCulture) ?? "no answer"})")}");
                }
                catch { }
            }
        }
        finally
        {
            _m3ContextGate.Release();
        }
    }

    private void M3OnLocalEmote(string? arg)
    {
        if (W137Joiner && arg?.Trim() == "whistle")
            _ = ExecLuaAsync("if KCD2MP_M3OnJoinerWhistle then KCD2MP_M3OnJoinerWhistle() end");
    }

    private void M3OnHostSaveInitiated(string _line)
    {
        if (!W137Host || _m3HostPeerHorses.IsEmpty) return;
        _ = ExecLuaAsync("if KCD2MP_M3BeforeHostSave then KCD2MP_M3BeforeHostSave() end");
    }

    private async Task M3BeforeHostSaveAsync()
    {
        if (!W137Host || _m3HostPeerHorses.IsEmpty) return;
        await ExecLuaAsync("if KCD2MP_M3BeforeHostSave then KCD2MP_M3BeforeHostSave() end");
        await Task.Delay(75);
    }

    private async Task M3AfterHostSaveAsync()
    {
        if (!W137Host || _m3HostPeerHorses.IsEmpty) return;
        await ExecLuaAsync("if KCD2MP_M3AfterHostSave then KCD2MP_M3AfterHostSave() end");
    }
}
