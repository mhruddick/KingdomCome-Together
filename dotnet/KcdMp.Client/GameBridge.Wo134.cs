// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

// WO-134: world items -- the agent's half (docs/WO-134-findings.md).
//
//   * NPC bodies are shared, in the host's world. The joiner's loot is a request:
//     LootAsk BodyOpen -> the host's mod reads its body -> LootHost BodyState (the
//     items, worn ones marked from the host's EquipmentManager) -> the joiner's copy
//     is set to that list and the loot screen opens on it. Every take he makes goes
//     back as BodyTake; the host moves it out of its body or answers "gone" (the
//     joiner's mod takes it back off Henry). The host's own looting reaches every
//     joiner as BodyState "update" (inventory and look).
//   * Loose world items are per world: ItemTake asks the host (class + position),
//     ItemResult answers, ItemGone tells every other joiner.
//   * Chests are per player, remembered per world: the mod reports every take and
//     put (w134_chest); the host keeps its ledger in ChestLedgerStore (a copy paired
//     with every host save), the joiner keeps his with his Henry snapshots
//     (HenryStore chest sidecars). After a join the host sends its ledger (LootHost
//     Ledger) and the joiner's mod applies both (KCD2MP_W134ChestApply).
// Player drops (WO-48, 0x32-0x35) are not touched by anything here.
public partial class GameBridge
{
    private volatile bool _w134Connected;
    private readonly ChestLedgerStore _chestStore = new(ChestLedgerStore.DefaultRoot());
    private readonly object _w134Gate = new();
    private Wo134Rules.Ledger? _w134HostLedger;       // the host's running ledger for _w134HostTag
    private string? _w134HostTag;
    private readonly List<Wo134Rules.Entry> _w134HostPending = [];   // host takes before the world is identified
    private bool _w134HostDirty;
    private Wo134Rules.Ledger _w134JoinerLedger = new(); // the joiner's, for _joinedTag
    private HenryStore.Snapshot? _w134RestoreSnap;       // the snapshot a join restored (its ledger comes with it)
    private string? _w134ApplyTag;                       // a join loaded: apply once the host's ledger is in
    private DateTime _w134ApplySinceUtc;
    private readonly SortedDictionary<int, List<Wo134Rules.Entry>> _w134RxParts = new();
    private int _w134RxN;
    private Wo134Rules.Ledger? _w134RxLedger;
    private readonly ConcurrentDictionary<string, (long AtMs, Guid[]? Worn)> _w134Worn = new(StringComparer.OrdinalIgnoreCase);
    private long _w134AsksIn, _w134AsksOut, _w134HostOut, _w134HostIn, _w134Dropped;

    private bool Wo134JoinerRole => _combatRoleApplied && !_isDamageAuthority && _hostAuthority;
    private bool Wo134HostRole => _combatRoleApplied && _isDamageAuthority;

    private void Wo134OnConnect(CancellationToken ct)
    {
        _w134Connected = true;
        _w134Worn.Clear();
        _ = Wo134LoopAsync(ct);
    }

    private async Task Wo134OnDisconnectAsync()
    {
        _w134Connected = false;
        Wo134FlushHost();
        try { await _transport.ExecuteNowAsync("if KCD2MP_W134Tick then KCD2MP_W134Tick(false, false, false, 0) end"); } catch { }
    }

    private async Task Wo134LoopAsync(CancellationToken ct)
    {
        long lastStats = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch { return; }
            try
            {
                if (_where == GameWhere.Menu || Wo136Holding) continue;   // WO-136: bodies and chests wait for the world
                bool joiner = Wo134JoinerRole;
                bool host = Wo134HostRole;
                bool shared = joiner ? JoinerSharedEffective : _sharedWorld;
                int peers = LivePartners().Count;   // WO-144
                _ = ExecLuaAsync($"if KCD2MP_W134Tick then KCD2MP_W134Tick({B(joiner)}, {B(host)}, {B(shared)}, {peers}) end");
                Wo134FlushHost();
                Wo135HostTick();   // WO-135: the host world's build to every joiner
                await Wo134ApplyIfReadyAsync(timeout: false);
                if (Environment.TickCount64 - lastStats >= 60_000)
                {
                    lastStats = Environment.TickCount64;
                    Console.WriteLine($"MP-WO134-STATS joiner={B(joiner)} host={B(host)} shared={B(shared)} peers={peers} asks_out={_w134AsksOut} asks_in={_w134AsksIn} host_out={_w134HostOut} host_in={_w134HostIn} dropped={_w134Dropped} host_ledger={_w134HostLedger?.Entries.Count ?? -1} joiner_ledger={_w134JoinerLedger.Entries.Count}");
                    Console.WriteLine(Wo135StatsLine());   // WO-135
                }
            }
            catch (Exception ex) { Console.WriteLine($"MP-WO134 tick failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static string B(bool v) => v ? "true" : "false";

    private async Task Wo134SendAsync(byte type, byte target, byte kind, uint tok, string text)
    {
        if (type == Protocol.LootAskUp && Wo140HoldOutbound($"a loot ask ({Protocol.LootAskName(kind)})")) return;   // WO-140: its own world
        try
        {
            await WriteJoinAsync(new LootMsg(kind, tok, text).BuildUp(type, target));
            if (type == Protocol.LootAskUp) Interlocked.Increment(ref _w134AsksOut); else Interlocked.Increment(ref _w134HostOut);
        }
        catch (Exception ex) { Console.WriteLine($"MP-WO134 {(type == Protocol.LootAskUp ? Protocol.LootAskName(kind) : Protocol.LootHostName(kind))} not sent: {ex.Message}"); }
    }

    private List<byte> Wo134Peers() => LivePartners();   // WO-144: was every name ever seen (the phantom partner)

    // ---------------------------------------------------------------- events from the mod

    private void Wo134OnEvent(string name, string? arg)
    {
        var f = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        uint U(string s) => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : 0;
        switch (name)
        {
            // ---- joiner -> host
            case "w134_open":      // <body>
                if (f.Length == 1 && Wo134Rules.BodyName.IsMatch(f[0]) && Wo134JoinerRole)
                    _ = Wo134SendAsync(Protocol.LootAskUp, Protocol.JoinTargetHost, Protocol.LootAskBodyOpen, 0, f[0]);
                return;
            case "w134_take":      // <tok> <body> <cls> <amt> <hp> [screen]   (WO-195 3: "screen" = the loot screen's own item, e.g. the home key)
            case "w134_put":
                if ((f.Length == 5 || (f.Length == 6 && name == "w134_take" && Wo195Rules.IsScreenMark(f[5]))) && Wo134Rules.BodyName.IsMatch(f[1])
                    && Wo134Rules.TryClass(f[2], out _) && Wo134Rules.TryAmount(f[3], out _) && Wo134Rules.TryHealth(f[4], out _) && Wo134JoinerRole)
                {
                    bool screen = f.Length == 6 && _w195BodyLootHost;
                    if (screen) Wo195NoteScreenTake();
                    _ = Wo134SendAsync(Protocol.LootAskUp, Protocol.JoinTargetHost, name == "w134_take" ? Protocol.LootAskBodyTake : Protocol.LootAskBodyPut,
                                       U(f[0]), $"{f[1]} {f[2]} {f[3]} {f[4]}{(screen ? " screen" : "")}");
                }
                return;
            case "w134_item":      // <tok> <cls> <x> <y> <z> <fromBody> [<amount>]   (WO-177 B1: the amount rides as an optional 7th field)
                if (f.Length is 6 or 7 && Wo134Rules.TryClass(f[1], out _) && Wo134Rules.TryCoord(f[2], out _) && Wo134Rules.TryCoord(f[3], out _)
                    && Wo134Rules.TryCoord(f[4], out _) && f[5] is "0" or "1" && (f.Length == 6 || Wo134Rules.TryAmount(f[6], out _)) && Wo134JoinerRole)
                    _ = Wo134SendAsync(Protocol.LootAskUp, Protocol.JoinTargetHost, Protocol.LootAskItemTake, U(f[0]), $"{f[1]} {f[2]} {f[3]} {f[4]} {f[5]}{(f.Length == 7 ? " " + f[6] : "")}");
                return;
            // ---- host -> joiner(s)
            case "w134_bstate":    // <peer> <tok> <body> <reason> <flags> <part> <nparts> <items>
                if (f.Length == 8 && Wo134HostRole) _ = Wo134HostBodyStateAsync(f);
                return;
            case "w134_tres":      // <peer> <tok> <ok|gone|mine|none> <body> <cls> <amt> (WO-136: mine/none = no notice)
                if (f.Length == 6 && Wo134HostRole && byte.TryParse(f[0], out byte tp) && Wo136Rules.IsTakeVerdict(f[2]))
                    _ = Wo134SendAsync(Protocol.LootHostUp, tp, Protocol.LootHostTakeResult, U(f[1]), $"{f[2]} {f[3]} {f[4]} {f[5]}");
                return;
            case "w134_ires":      // <peer> <tok> <ok|gone|unknown|mine> <cls> <x> <y> <z>
                if (f.Length == 7 && Wo134HostRole && byte.TryParse(f[0], out byte ip) && Wo136Rules.IsItemVerdict(f[2]))
                {
                    _ = Wo134SendAsync(Protocol.LootHostUp, ip, Protocol.LootHostItemResult, U(f[1]), $"{f[2]} {f[3]} {f[4]} {f[5]} {f[6]}");
                    if (f[2] == "ok")   // gone for every other joiner too
                        foreach (byte g in Wo134Peers().Where(g => g != ip))
                            _ = Wo134SendAsync(Protocol.LootHostUp, g, Protocol.LootHostItemGone, 0, $"{f[3]} {f[4]} {f[5]} {f[6]}");
                }
                return;
            case "w134_igone":     // <cls> <x> <y> <z>: the host picked a world item up
                if (f.Length == 4 && Wo134HostRole)
                    foreach (byte g in Wo134Peers())
                        _ = Wo134SendAsync(Protocol.LootHostUp, g, Protocol.LootHostItemGone, 0, string.Join(' ', f));
                return;
            // ---- both: chests
            case "w134_chest":     // <container> <cls> <n> <hp> <worldT> <restockDays>
                Wo134OnChestEvent(arg ?? "");
                return;
            case "w134_applied":
                Console.WriteLine($"MP-WO134 chests: the join ledger applied (applied expired skipped) = {arg}");
                return;
        }
    }

    /// <summary>Host: a body's items for one joiner (peer) or every joiner (peer 0), worn ones marked.</summary>
    private readonly SemaphoreSlim _w134BodySend = new(1, 1);   // one body state at a time: its parts go out in order

    private async Task Wo134HostBodyStateAsync(string[] f)
    {
        await _w134BodySend.WaitAsync();
        try { await Wo134HostBodyStateInOrderAsync(f); }
        finally { _w134BodySend.Release(); }
    }

    private async Task Wo134HostBodyStateInOrderAsync(string[] f)
    {
        if (!byte.TryParse(f[0], out byte peer) || !Wo134Rules.BodyName.IsMatch(f[2]) || f[3] is not ("open" or "update")) return;
        var items = Wo134Rules.ParseItems(f[7]);
        if (items is null) { Console.WriteLine($"MP-WO134 host: body {f[2]}: malformed item list from the mod -- not sent"); return; }
        Guid[]? worn = null;
        if (items.Count > 0)
        {
            long now = Environment.TickCount64;
            if (_w134Worn.TryGetValue(f[2], out var c) && now - c.AtMs < 1500) worn = c.Worn;
            else
            {
                try { worn = await _transport.ReadGhostEquippedItemClassesAsync(f[2]); } catch { }
                _w134Worn[f[2]] = (now, worn);
            }
        }
        var marked = Wo134Rules.MarkWorn(items, worn);
        string text = $"{f[2]} {f[3]} {f[4]} {f[5]} {f[6]} {Wo134Rules.FormatItems(marked)}";
        uint tok = uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint t) ? t : 0;
        var targets = peer == 0 ? Wo134Peers() : [peer];
        foreach (byte g in targets) await Wo134SendAsync(Protocol.LootHostUp, g, Protocol.LootHostBodyState, tok, text);
        if (f[5] == "1")
            Console.WriteLine($"MP-WO134 host: body {f[2]} ({f[3]}) -> {(peer == 0 ? $"every joiner ({targets.Count})" : $"ghost {peer}")}: parts={f[6]} worn_known={B(worn is not null)}");
    }

    // ---------------------------------------------------------------- frames from the relay

    private async Task Wo134OnFrameAsync(int type, byte src, byte[] body)
    {
        if (!LootMsg.TryDecode(body, out var m)) { Interlocked.Increment(ref _w134Dropped); return; }
        if (await Wo135OnLootFrameAsync(type, src, m)) return;   // WO-135: takedowns, the host's build
        if (await Wo217OnLootFrameAsync(type, src, m)) return;   // WO-217 C: the live loot (requests, grants, the body's list by item ids)
        var f = m.Text.Split(' ');
        string Q(string s) => EscapeLua(s);
        if (type == Protocol.LootAskDown)
        {
            // The host: a joiner's request. Checked field by field, then to the mod.
            Interlocked.Increment(ref _w134AsksIn);
            if (!Wo134HostRole) { Interlocked.Increment(ref _w134Dropped); return; }
            switch (m.Kind)
            {
                case Protocol.LootAskBodyOpen when f.Length == 1 && Wo134Rules.BodyName.IsMatch(f[0]):
                    await ExecLuaAsync($"if KCD2MP_W134HostOpen then KCD2MP_W134HostOpen({src}, {m.Tok}, \"{f[0]}\") end");
                    return;
                case Protocol.LootAskBodyTake or Protocol.LootAskBodyPut when (f.Length == 4 || (f.Length == 5 && m.Kind == Protocol.LootAskBodyTake && Wo195Rules.IsScreenMark(f[4])))
                        && Wo134Rules.BodyName.IsMatch(f[0])
                        && Wo134Rules.TryClass(f[1], out var cls) && Wo134Rules.TryAmount(f[2], out int amt) && Wo134Rules.TryHealth(f[3], out float hp):
                    string fn = m.Kind == Protocol.LootAskBodyTake ? "KCD2MP_W134HostTake" : "KCD2MP_W134HostPut";
                    bool screenTake = f.Length == 5 && _w195BodyLootHost;   // WO-195 3: the joiner's loot screen made this item (the home key)
                    await ExecLuaAsync($"if {fn} then {fn}({src}, {m.Tok}, \"{f[0]}\", \"{cls:D}\", {amt}, {Wo134Rules.F(hp)}{(screenTake ? ", true" : "")}) end");
                    return;
                case Protocol.LootAskItemTake when f.Length is 5 or 6 && Wo134Rules.TryClass(f[0], out var icls) && Wo134Rules.TryCoord(f[1], out float x)
                        && Wo134Rules.TryCoord(f[2], out float y) && Wo134Rules.TryCoord(f[3], out float z) && f[4] is "0" or "1"
                        && (f.Length == 5 || Wo134Rules.TryAmount(f[5], out _)):
                    await ExecLuaAsync($"if KCD2MP_W134HostItem then KCD2MP_W134HostItem({src}, {m.Tok}, \"{icls:D}\", {Wo134Rules.F3(x)}, {Wo134Rules.F3(y)}, {Wo134Rules.F3(z)}, {f[4]}, {(f.Length == 6 ? f[5] : "nil")}) end");
                    return;
            }
            Interlocked.Increment(ref _w134Dropped);
            Console.WriteLine($"MP-WO134 host: malformed {Protocol.LootAskName(m.Kind)} from ghost {src} -- dropped");
            return;
        }
        // LootHostDown: the host's answer (the relay accepts these from the host only).
        Interlocked.Increment(ref _w134HostIn);
        if (!Wo134JoinerRole) { Interlocked.Increment(ref _w134Dropped); return; }
        switch (m.Kind)
        {
            case Protocol.LootHostBodyState when f.Length == 6 && Wo134Rules.BodyName.IsMatch(f[0]) && f[1] is "open" or "update"
                    && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags)
                    && int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int part)
                    && int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int np) && part >= 1 && np >= 1 && part <= np && np <= 20
                    && Wo134Rules.ParseItems(f[5]) is { } items:
                await ExecLuaAsync($"if KCD2MP_W134BodyState then KCD2MP_W134BodyState(\"{f[0]}\", \"{f[1]}\", {flags}, {part}, {np}, {Wo134Rules.LuaItems(items)}) end");
                return;
            case Protocol.LootHostTakeResult when f.Length == 4 && Wo136Rules.IsTakeVerdict(f[0]) && Wo134Rules.BodyName.IsMatch(f[1]):
                await ExecLuaAsync($"if KCD2MP_W134TakeResult then KCD2MP_W134TakeResult(\"{m.Tok}\", \"{f[0]}\", \"{f[1]}\") end");
                return;
            case Protocol.LootHostItemResult when f.Length == 5 && Wo136Rules.IsItemVerdict(f[0]):
                await ExecLuaAsync($"if KCD2MP_W134ItemResult then KCD2MP_W134ItemResult(\"{m.Tok}\", \"{f[0]}\") end");
                return;
            case Protocol.LootHostItemGone when f.Length == 4 && Wo134Rules.TryClass(f[0], out var gcls) && Wo134Rules.TryCoord(f[1], out float gx)
                    && Wo134Rules.TryCoord(f[2], out float gy) && Wo134Rules.TryCoord(f[3], out float gz):
                await ExecLuaAsync($"if KCD2MP_W134ItemGone then KCD2MP_W134ItemGone(\"{gcls:D}\", {Wo134Rules.F3(gx)}, {Wo134Rules.F3(gy)}, {Wo134Rules.F3(gz)}) end");
                return;
            case Protocol.LootHostLedger when Wo134Rules.ParseLedgerPart(m.Text) is not null:
                // M2: per-player chest delta replay is retired; world chests share the host's save via WO-217 live sync.
                return;
        }
        Interlocked.Increment(ref _w134Dropped);
        Console.WriteLine($"MP-WO134 joiner: malformed {Protocol.LootHostName(m.Kind)} from the host -- dropped ({Q(m.Text.Length > 60 ? m.Text[..60] : m.Text)})");
    }

    // ---------------------------------------------------------------- chests (retired per-player replay; M2 shared world chests)

    private void Wo134OnChestEvent(string arg)
    {
        // M2: non-personal world chests are shared live via WO-217 and persisted in the host's .whs save.
    }

    private void Wo134FlushHost()
    {
    }

    /// <summary>WO-125's host identify hook (M2: per-player chest ledger pairing retired; used for world-scoped loot sidecars).</summary>
    private void Wo134OnHostWorld(string tag, string md5, bool loaded)
    {
        lock (_w134Gate)
        {
            _w134HostTag = tag;
            _w134HostDirty = false;
        }
    }

    /// <summary>Host: the joiner is in the world (M2: chest ledger replay retired; loose-item tombstones replay here).</summary>
    private Task Wo134OnJoinerReadyAsync(byte joiner) => Task.CompletedTask;

    /// <summary>WO-125 restore hook (M2: per-player chest ledger sidecar retired).</summary>
    private void Wo134OnRestorePick(HenryStore.Snapshot? s) => _w134RestoreSnap = null;

    /// <summary>After the join's Ready (M2: per-player chest ledger apply retired; host's .whs save is the chest truth).</summary>
    private void Wo134AfterReady(string tag)
    {
        lock (_w134Gate)
        {
            _w134RestoreSnap = null;
            _w134ApplyTag = null;
            _w134RxLedger = null;
            _w134RxParts.Clear();
        }
    }

    /// <summary>WO-125 snapshot stored hook (M2: per-player chest ledger sidecar retired).</summary>
    private void Wo134OnSnapshotStored(HenryStore.Snapshot snap)
    {
    }

    private Task Wo134ApplyIfReadyAsync(bool timeout) => Task.CompletedTask;
}
