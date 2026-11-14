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
    private string _m3JoinerTackCsv = "";
    private string? _m3JoinerLastSentPayload;
    private long _m3JoinerPollAtMs, _m3HostTickAtMs;
    private readonly ConcurrentDictionary<byte, (string? Horse, string TackCsv)> _m3HostPeerHorses = new();
    private readonly ConcurrentDictionary<string, byte> _m3HostContextHorses = new(StringComparer.Ordinal);

    private void M3OnConnect()
    {
        _m3JoinerHorseName = null;
        _m3JoinerTackCsv = "";
        _m3JoinerLastSentPayload = null;
        _m3JoinerPollAtMs = 0;
        _m3HostTickAtMs = 0;
        _m3HostPeerHorses.Clear();
        _m3HostContextHorses.Clear();
    }

    private async Task M3OnDisconnectAsync()
    {
        _m3JoinerHorseName = null;
        _m3JoinerTackCsv = "";
        _m3JoinerLastSentPayload = null;
        _m3HostPeerHorses.Clear();
        foreach (var (horse, _) in _m3HostContextHorses.ToArray())
        {
            if (_m3HostContextHorses.TryRemove(horse, out _))
            {
                try { await _combat.Wo139ContextAsync(false, "crime_ignoredHorseTheft_Horse", horse); } catch { }
            }
        }
        try { await ExecLuaAsync("if KCD2MP_M3ReleaseAllPeerHorses then KCD2MP_M3ReleaseAllPeerHorses(\"disconnect\") end"); } catch { }
    }

    private void M3OnPeerDisconnected(byte ghostId)
    {
        _m3HostPeerHorses.TryRemove(ghostId, out _);
        if (W137Host)
            _ = ExecLuaAsync($"if KCD2MP_W160PeerHorse then KCD2MP_W160PeerHorse({ghostId}, \"-\", \"\") end");
    }

    private async Task M3TickAsync(bool host, bool joinerActive)
    {
        long now = Environment.TickCount64;
        if (host && LivePartners().Count > 0 && now - _m3HostTickAtMs >= 2_000)
        {
            _m3HostTickAtMs = now;
            _ = ExecLuaAsync("if KCD2MP_M3HostTick then KCD2MP_M3HostTick() end");
        }
        if (!joinerActive)
        {
            _m3JoinerLastSentPayload = null;
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
        _m3JoinerHorseName = horseName;
        _m3JoinerPollAtMs = Environment.TickCount64;
        if (!W137Joiner) return;

        if (horseName is null)
        {
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
            else
            {
                tack = M3ReadLatestSnapshotTackClasses();
            }
        }
        catch
        {
            tack = fallbackTack is { Count: > 0 } ? fallbackTack : M3ReadLatestSnapshotTackClasses();
        }

        string payload = M3HorseRules.FormatOwnHorseAsk(horseName, tack);
        if (payload == _m3JoinerLastSentPayload || !W137Joiner) return;
        _m3JoinerLastSentPayload = payload;
        _m3JoinerTackCsv = M3HorseRules.NormalizeTackCsv(tack);
        uint htok = Interlocked.Increment(ref _w137Tok);
        Console.WriteLine($"MP-WO160 joiner: its own horse is {horseName} (tack={(tack.Count == 0 ? "-" : _m3JoinerTackCsv)}) -- told to the host");
        await Wo137SendAsync(Protocol.QuestAskUp, Protocol.JoinTargetHost, Protocol.QuestAskOwnHorse, htok, payload);
    }

    private List<string> M3ReadLatestSnapshotTackClasses()
    {
        try
        {
            if (_joinedTag is not { Length: > 0 } tag) return [];
            var snaps = _henry.Snapshots(tag);
            if (snaps.Count == 0) return [];
            var parts = _henry.Load(snaps[0]);
            return M3HorseRules.ExtractEquippedTackClasses(parts);
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
        if (on) _m3HostContextHorses[horse] = 1;
        else _m3HostContextHorses.TryRemove(horse, out _);

        _ = Task.Run(async () =>
        {
            try
            {
                var res = await _combat.Wo139ContextAsync(on, "crime_ignoredHorseTheft_Horse", horse);
                Console.WriteLine($"MP-M3-HORSE host: {horse} {(on ? "marked legal for partner" : "restored")} -- crime_ignoredHorseTheft_Horse {(res is 1 or 0 ? "ok" : $"not set ({res?.ToString(CultureInfo.InvariantCulture) ?? "no answer"})")}");
            }
            catch { }
        });
    }

    private void M3OnLocalEmote(string? arg)
    {
        if (W137Joiner && arg?.Trim() == "whistle")
            _ = ExecLuaAsync("if KCD2MP_M3OnJoinerWhistle then KCD2MP_M3OnJoinerWhistle() end");
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
