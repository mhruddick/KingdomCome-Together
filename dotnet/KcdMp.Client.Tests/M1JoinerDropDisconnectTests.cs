// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using KcdMp.Wire;
using Spec = KcdMp.Client.Tests.WhsSaveTests.Spec;
using Item = KcdMp.Client.Tests.WhsSaveTests.Item;

namespace KcdMp.Client.Tests;

public class M1JoinerDropDisconnectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kcdmp-m1-drops-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _log = [];

    private const string Apple = "0a0a0a0a-0000-0000-0000-00000000a001";
    private const string Sword = "0c0c0c0c-0000-0000-0000-00000000c001";
    private const string HomeKey = "0c0c0c0c-0000-0000-0000-00000000c099";
    private const string Money = GameBridge.MoneyClass;

    public M1JoinerDropDisconnectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private HenryStore CreateStore() => new(Path.Combine(_root, "henry"), () => _now, s => _log.Add(s));

    private static string M(int n) => n.ToString("x32");

    [Fact]
    public void Unpaired_disconnect_after_peer_claims_joiner_drops_deducts_stacks_and_whole_items_on_fallback_pick()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(101);
        var (hs, js) = WhsSaveTests.Pair();
        js.Items =
        [
            new("20000000-0000-0000-0000-000000000001", Apple, 5),
            new("20000000-0000-0000-0000-000000000002", Sword, 1),
            new("20000000-0000-0000-0000-000000000003", Money, 151),
            new("20000000-0000-0000-0000-000000000004", Apple, 1),
        ];
        js.Equipped = ["20000000-0000-0000-0000-000000000004"];

        // 1. Joiner joins at host save0 (M(0)) -> snap0 stored
        var snap0Parts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot);
        st.Store(tag, snap0Parts, M(0), HenryStore.SourceJoin, "join save0");

        // 2. Joiner drops 1 Sword, 2 Apples, and 20 Groschen; Host claims all three; Joiner crashes before Host saves save1 (M(1))!
        _now = _now.AddSeconds(10);
        st.RecordTransferOut(tag, 701, Sword, 1, 0.95f);
        st.RecordTransferOut(tag, 702, Apple, 2, 1.0f);
        st.RecordTransferOut(tag, 703, Money, 20, 1.0f);

        Assert.Empty(Directory.GetFiles(st.WorldDir(tag), "*.part"));

        // 3. Host saved M(1) (and M(2) offer on rejoin); Joiner has no snapshot for M(2) or M(1), falling back to M(0)
        var pick = st.PickFor(tag, [M(2), M(1), M(0)])!;
        Assert.Equal(M(0), pick.Snapshot.Md5);
        Assert.Contains("transferred-out", pick.How);

        // Verify the restored HenryParts splices, verifies, and passes CheckParts with exact deducted quantities
        byte[] hostFile = WhsSaveTests.File(hs);
        var spliced = WhsSave.SpliceParts(hostFile, pick.Parts, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(spliced.File).Ok);
        Assert.Empty(WhsSave.CheckParts(hostFile, pick.Parts, spliced.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));

        var outRaw = WhsSave.Inflate(spliced.File).Raw;
        var decoded = WhsSave.DecodePlayerSoul(outRaw, WhsSave.FindSoul(outRaw, WhsSave.HenrySoul)!.Value);

        // Sword is completely gone; unequipped Apple stack reduced 5 -> 3 while equipped Apple (1) untouched; Money reduced 151 -> 131
        Assert.DoesNotContain(decoded.Inventory, i => i.Class == Sword);
        Assert.Equal(2, decoded.Inventory.Count(i => i.Class == Apple));
        Assert.Equal("amount=3", decoded.Inventory.First(i => i.Instance == "20000000-0000-0000-0000-000000000001").Params);
        Assert.Equal("", decoded.Inventory.First(i => i.Instance == "20000000-0000-0000-0000-000000000004").Params);
        Assert.Equal("amount=131", decoded.Inventory.First(i => i.Class == Money).Params);

        // CompareHenry passes cleanly with the post-drop money (13.10 groschen = 131 tenths)
        string live = $"money=13.10 items={Apple}:3;{Apple}:1;{Money}:131 skills=";
        Assert.True(GameBridge.CompareHenry(decoded, live).Ok);
    }

    [Fact]
    public void Snapshot_stored_after_transfer_never_double_deducts_while_host_reload_to_older_solo_save_still_deducts()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(102);
        var (_, js) = WhsSaveTests.Pair();
        js.Items = [new("20000000-0000-0000-0000-000000000001", Sword, 2)];
        js.Equipped = [];

        // t=0: snap0 at M(0) has 2 Swords
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "join");

        // t=10s: Joiner drops 1 Sword, Host claims it
        _now = _now.AddSeconds(10);
        st.RecordTransferOut(tag, 801, Sword, 1, 1.0f);

        // Case A: Joiner did NOT crash; Host saved M(2) and Joiner paired snap2 (which already has 1 Sword left)
        _now = _now.AddSeconds(20);
        js.Items = [new("20000000-0000-0000-0000-000000000001", Sword, 1)]; // reduced to 1 (amount=1 -> no 0x0000 param)
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(2), HenryStore.SourceSnapshot, "paired");

        var pickLatest = st.PickFor(tag, [M(2), M(0)])!;
        Assert.Equal(M(2), pickLatest.Snapshot.Md5);
        var latestDecoded = WhsSave.DecodePlayerSoul(pickLatest.Parts.Record, new WhsSave.Node(0x115E, 0, pickLatest.Parts.Record.Length - 6));
        Assert.Single(latestDecoded.Inventory.Where(i => i.Class == Sword));
        Assert.Equal("", latestDecoded.Inventory.Single(i => i.Class == Sword).Params); // still 1 Sword, NOT double-deducted to 0!

        // Case B: Suppose M(1) was saved by Host solo BEFORE snap2 (so Joiner had no snapshot for M(1)), and Host later reloads M(1)!
        // PickFor([M(3), M(1), M(0)]) falls back to snap0 (M(0)) -> transfer IS applied so snap0 drops from 2 Swords to 1 Sword!
        var pickFallback = st.PickFor(tag, [M(3), M(1), M(0)])!;
        Assert.Equal(M(0), pickFallback.Snapshot.Md5);
        var fallbackDecoded = WhsSave.DecodePlayerSoul(pickFallback.Parts.Record, new WhsSave.Node(0x115E, 0, pickFallback.Parts.Record.Length - 6));
        Assert.Single(fallbackDecoded.Inventory.Where(i => i.Class == Sword));
        Assert.Equal("", fallbackDecoded.Inventory.Single(i => i.Class == Sword).Params); // 2 - 1 == 1 Sword!
    }

    [Fact]
    public void Equipped_items_bound_keys_and_withdrawn_personal_stash_items_clean_all_references_on_transfer_deduction()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(103);
        var (hs, js) = WhsSaveTests.Pair();
        js.Items =
        [
            new("20000000-0000-0000-0000-000000000010", Sword, 1),   // equipped at snap0, later unequipped & dropped
            new("20000000-0000-0000-0000-000000000020", HomeKey, 1), // bound in 000B at snap0, later dropped
        ];
        js.Equipped = ["20000000-0000-0000-0000-000000000010"];
        js.Keys = [(WhsSave.HenrySoul, "20000000-0000-0000-0000-000000000020")];
        js.PersonalStashItems =
        [
            new("20000000-0000-0000-0000-000000000030", Apple, 4),   // in bedside chest at snap0, later withdrawn & dropped
        ];

        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "join");

        // Same millisecond (no _now advance!) to prove monotonic sequence ordering works even without clock advance!
        st.RecordTransferOut(tag, 901, Sword, 1, 1.0f);
        st.RecordTransferOut(tag, 902, HomeKey, 1, 1.0f);
        st.RecordTransferOut(tag, 903, Apple, 4, 1.0f);

        var pick = st.PickFor(tag, [M(1), M(0)])!;
        Assert.Empty(pick.Parts.KeyEntries); // HomeKey 0x05AD binding removed
        Assert.Empty(pick.Parts.Extensions[WhsSave.PersonalStashExtensionKey]); // All 4 Apples deducted -> 0x0000 node omitted

        // Round-trip through .hblk v2 and splice onto host world
        var roundTripped = WhsSave.ParseBlock(WhsSave.SerializeBlock(pick.Parts));
        byte[] hostFile = WhsSaveTests.File(hs);
        var spliced = WhsSave.SpliceParts(hostFile, roundTripped, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(spliced.File).Ok);
        Assert.Empty(WhsSave.CheckParts(hostFile, roundTripped, spliced.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));

        var outRaw = WhsSave.Inflate(spliced.File).Raw;
        var decoded = WhsSave.DecodePlayerSoul(outRaw, WhsSave.FindSoul(outRaw, WhsSave.HenrySoul)!.Value);
        Assert.Empty(decoded.Inventory);
        Assert.Empty(WhsSave.DecodePersonalStashItems(outRaw));
    }

    [Fact]
    public void GameBridge_records_transfer_only_when_joined_and_claimed_by_another_peer()
    {
        var bridge = new GameBridge(new ClientConfig());
        var st = CreateStore();
        string tag = WhsSave.SeedTag(104);

        typeof(GameBridge).GetField("_henry", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, st);
        typeof(GameBridge).GetField("_myGhostId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, (byte)2);
        typeof(GameBridge).GetField("_joinedWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, true);
        typeof(GameBridge).GetField("_joinedTag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, tag);

        var buildPayload = typeof(GameBridge).GetMethod("BuildItemDropPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] dropPayload = (byte[])buildPayload.Invoke(null, [555u, Guid.Parse(Sword), (ushort)1, 0.85f, 100f, 200f, 10f])!;

        var hook = typeof(GameBridge).GetMethod("Wo125OnJoinerDropClaimedByPeer", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 1. Peer (ghost 1 != myGhostId 2) claims Joiner's drop while joined -> recorded!
        hook.Invoke(bridge, [555u, (byte)1, dropPayload]);
        var transfers = st.TransfersOut(tag);
        Assert.Single(transfers);
        Assert.Equal(555u, transfers[0].DropId);
        Assert.Equal(Sword, transfers[0].Class);
        Assert.Equal(1, transfers[0].Amount);

        // 2. Not in joined world (host or menu) -> ignored!
        typeof(GameBridge).GetField("_joinedWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, false);
        byte[] dropPayload2 = (byte[])buildPayload.Invoke(null, [556u, Guid.Parse(Apple), (ushort)3, 1.0f, 100f, 200f, 10f])!;
        hook.Invoke(bridge, [556u, (byte)1, dropPayload2]);
        Assert.Single(st.TransfersOut(tag));
    }

    [Fact]
    public void Drop_before_save_then_late_peer_claim_preserves_remaining_item_while_older_save_still_deducts()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(105);
        var (_, js) = WhsSaveTests.Pair();

        // 1. snap0 (M(0)) has 2 distinct Swords
        js.Items =
        [
            new("20000000-0000-0000-0000-000000000101", Sword, 1),
            new("20000000-0000-0000-0000-000000000102", Sword, 1),
        ];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "join");

        // 2. Joiner drops 1 of the 2 Swords onto the ground (leaves Henry's inventory now, before save)
        _now = _now.AddSeconds(5);
        st.NoteOutgoingDrop(tag, 810);

        // 3. Host saves M(1) while the dropped sword is still lying on the ground -> snap1 already has only 1 Sword
        _now = _now.AddSeconds(5);
        js.Items = [new("20000000-0000-0000-0000-000000000101", Sword, 1)];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(1), HenryStore.SourceSnapshot, "saved after drop");

        // 4. Peer picks up drop 810 AFTER snap1 was saved
        _now = _now.AddSeconds(5);
        st.RecordTransferOut(tag, 810, Sword, 1, 1.0f);

        // Restoring M(1) (snap1) MUST keep the remaining 1 Sword (not delete it to 0!)
        var pickM1 = st.PickFor(tag, [M(2), M(1), M(0)])!;
        Assert.Equal(M(1), pickM1.Snapshot.Md5);
        Assert.DoesNotContain("transferred-out", pickM1.How);
        var decodedM1 = WhsSave.DecodePlayerSoul(pickM1.Parts.Record, new WhsSave.Node(0x115E, 0, pickM1.Parts.Record.Length - 6));
        Assert.Single(decodedM1.Inventory.Where(i => i.Class == Sword));

        // Restoring older pre-drop M(0) (snap0) MUST still deduct the transferred Sword (2 - 1 = 1 Sword)
        var pickM0 = st.PickFor(tag, [M(0)])!;
        Assert.Equal(M(0), pickM0.Snapshot.Md5);
        Assert.Contains("transferred-out", pickM0.How);
        var decodedM0 = WhsSave.DecodePlayerSoul(pickM0.Parts.Record, new WhsSave.Node(0x115E, 0, pickM0.Parts.Record.Length - 6));
        Assert.Single(decodedM0.Inventory.Where(i => i.Class == Sword));
    }

    [Fact]
    public void Pre_drop_snapshot_stored_after_peer_claim_still_deducts_transferred_item()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(106);
        var (_, js) = WhsSaveTests.Pair();
        js.Items =
        [
            new("20000000-0000-0000-0000-000000000201", Sword, 1),
            new("20000000-0000-0000-0000-000000000202", Sword, 1),
        ];

        // 1. Game captures Henry's pre-drop state (2 Swords), e.g. QuickSave started before the drop
        var preDropParts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot);
        var preDropCapture = st.CaptureBoundary(tag);

        // 2. While snapshot file verification/late-file delivery is still in flight, Joiner drops 1 Sword and Peer claims it
        _now = _now.AddSeconds(2);
        st.NoteOutgoingDrop(tag, 820);
        _now = _now.AddSeconds(1);
        st.RecordTransferOut(tag, 820, Sword, 1, 1.0f);

        // 3. Pre-drop snapshot finishes storing AFTER the peer claim was recorded
        _now = _now.AddSeconds(3);
        st.Store(tag, preDropParts, M(1), HenryStore.SourceSnapshot, "delayed store", preDropCapture);

        // 4. Reconnect restores M(1): MUST deduct the transferred sword so 1 Sword remains (never 2!)
        var pick = st.PickFor(tag, [M(2), M(1)])!;
        Assert.Equal(M(1), pick.Snapshot.Md5);
        Assert.Contains("transferred-out", pick.How);
        var decoded = WhsSave.DecodePlayerSoul(pick.Parts.Record, new WhsSave.Node(0x115E, 0, pick.Parts.Record.Length - 6));
        Assert.Single(decoded.Inventory.Where(i => i.Class == Sword));
    }

    [Fact]
    public void Retained_snapshot_with_513_transfers_deducts_all_513_and_prunes_only_after_snapshot_rotates_out()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(107);
        var (_, js) = WhsSaveTests.Pair();
        js.Items = [new("20000000-0000-0000-0000-000000000301", Apple, 513)];

        // 1. Store snap0 (M(0)) holding 513 Apples
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "join");

        // 2. Record 513 outgoing 1-Apple transfers while snap0 remains retained
        for (uint i = 1; i <= 513; i++)
        {
            st.NoteOutgoingDrop(tag, 10_000 + i);
            st.RecordTransferOut(tag, 10_000 + i, Apple, 1, 1.0f);
        }

        Assert.Equal(513, st.TransfersOut(tag).Count);

        // 3. Even across a fresh HenryStore instance (agent restart), restoring snap0 MUST deduct all 513 Apples -> 0 Apples left (never 1!)
        var restartedStore = CreateStore();
        var pick = restartedStore.PickFor(tag, [M(999), M(0)])!;
        Assert.Equal(M(0), pick.Snapshot.Md5);
        var decoded = WhsSave.DecodePlayerSoul(pick.Parts.Record, new WhsSave.Node(0x115E, 0, pick.Parts.Record.Length - 6));
        Assert.DoesNotContain(decoded.Inventory, x => x.Class == Apple);

        // 4. Once KeepPerWorld (100) newer host saves rotate M(0) out, all 513 obsolete transfer entries are pruned cleanly
        js.Items = [];
        var postParts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot);
        for (int s = 1; s <= HenryStore.KeepPerWorld; s++)
        {
            _now = _now.AddSeconds(1);
            st.Store(tag, postParts, M(s), HenryStore.SourceSnapshot, $"save {s}");
        }

        Assert.DoesNotContain(st.Snapshots(tag), s => s.Md5 == M(0));
        Assert.Empty(st.TransfersOut(tag));
    }

    [Fact]
    public void GameBridge_hooks_preserve_remaining_item_on_drop_save_claim_and_deduct_after_self_claim_race_loss()
    {
        var bridge = new GameBridge(new ClientConfig());
        var st = CreateStore();
        string tag = WhsSave.SeedTag(108);

        typeof(GameBridge).GetField("_henry", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, st);
        typeof(GameBridge).GetField("_myGhostId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, (byte)2);
        typeof(GameBridge).GetField("_joinedWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, true);
        typeof(GameBridge).GetField("_joinedTag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, tag);

        var buildPayload = typeof(GameBridge).GetMethod("BuildItemDropPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dropStarted = typeof(GameBridge).GetMethod("Wo125OnJoinerDropStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var selfClaim = typeof(GameBridge).GetMethod("Wo125OnJoinerDropSelfClaim", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var peerClaim = typeof(GameBridge).GetMethod("Wo125OnJoinerDropClaimedByPeer", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var (_, js) = WhsSaveTests.Pair();
        js.Items =
        [
            new("20000000-0000-0000-0000-000000000401", Sword, 1),
            new("20000000-0000-0000-0000-000000000402", Sword, 1),
        ];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "join");

        // Flow A: Joiner drops sword 901 -> saves M(1) (1 Sword left) -> Peer claims 901 -> M(1) keeps 1 Sword!
        byte[] p901 = (byte[])buildPayload.Invoke(null, [901u, Guid.Parse(Sword), (ushort)1, 1.0f, 100f, 200f, 10f])!;
        dropStarted.Invoke(bridge, [901u]);
        js.Items = [new("20000000-0000-0000-0000-000000000401", Sword, 1)];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(1), HenryStore.SourceSnapshot, "post-drop save");
        peerClaim.Invoke(bridge, [901u, (byte)1, p901]);

        var pickA = st.PickFor(tag, [M(1)])!;
        var decA = WhsSave.DecodePlayerSoul(pickA.Parts.Record, new WhsSave.Node(0x115E, 0, pickA.Parts.Record.Length - 6));
        Assert.Single(decA.Inventory.Where(i => i.Class == Sword));

        // Flow B: Joiner drops remaining sword 902, tentatively picks 902 back up (self-claim in flight -> back to 1 Sword in inventory),
        // saves M(2) while holding that 1 Sword, then loses ItemClaimDown to Peer (Lua rolls back to 0 Swords) -> M(2) deducts to 0 Swords!
        byte[] p902 = (byte[])buildPayload.Invoke(null, [902u, Guid.Parse(Sword), (ushort)1, 1.0f, 100f, 200f, 10f])!;
        dropStarted.Invoke(bridge, [902u]);
        selfClaim.Invoke(bridge, [902u]);
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(2), HenryStore.SourceSnapshot, "mid-race save");
        peerClaim.Invoke(bridge, [902u, (byte)1, p902]);

        var pickB = st.PickFor(tag, [M(2)])!;
        var decB = WhsSave.DecodePlayerSoul(pickB.Parts.Record, new WhsSave.Node(0x115E, 0, pickB.Parts.Record.Length - 6));
        Assert.DoesNotContain(decB.Inventory, i => i.Class == Sword);
    }

    private sealed class SnapshotTestTransport(Func<string, Task>? onExecute = null) : IGameTransport
    {
        public readonly ConcurrentQueue<string> LuaCalls = new();
        public string Name => "snapshot-test";
        public int RoundTripsPerStateRead => 0;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) => Task.FromResult<PlayerState?>(null);
        public Task ExecuteAsync(string lua, CancellationToken ct = default)
        {
            LuaCalls.Enqueue(lua);
            return onExecute is not null ? onExecute(lua) : Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid[]?> ReadEquippedItemClassesAsync(CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);
        public Task<Guid[]?> ReadGhostEquippedItemClassesAsync(string ghostSoulName, CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);
        public Task EquipItemOnGhostAsync(string ghostSoulName, Guid itemClass, bool createIfMissing, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnequipItemOnGhostAsync(string ghostSoulName, Guid itemClass, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> ReadGhostSoulGuidAsync(string ghostSoulName, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<string?> ReadSoulNameByGuidAsync(Guid soulGuid, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(Guid? Guid, string? Name)> ReadPlayerSoulIdentityAsync(CancellationToken ct = default) => Task.FromResult<(Guid?, string?)>((null, null));
        public Task ExecuteNowAsync(string lua, CancellationToken ct = default) => ExecuteAsync(lua, ct);
    }

    private static void WireJoinedBridge(
        GameBridge bridge,
        HenryStore store,
        string tag,
        IGameTransport transport,
        List<byte[]> sentDrops,
        List<uint> sentClaims,
        string? savesDirOverride = null)
    {
        var bt = typeof(GameBridge);
        bt.GetField("_henry", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, store);
        bt.GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, transport);
        bt.GetField("_combat", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(
            bridge, new CombatPipe("kcdmp-test-" + Guid.NewGuid().ToString("N")));
        bt.GetField("_myGhostId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, (byte)2);
        bt.GetField("_joinedWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, true);
        bt.GetField("_joinedTag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, tag);
        var whereType = bt.GetNestedType("GameWhere", BindingFlags.NonPublic)!;
        bt.GetField("_where", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, Enum.Parse(whereType, "World"));
        bt.GetProperty("Interactions", BindingFlags.Public | BindingFlags.Instance)!.SetValue(
            bridge, new InteractionClient((_, _, _) => Task.CompletedTask));
        bt.GetField("_sendItemDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(
            bridge, (Func<byte[], Task>)(p => { sentDrops.Add(p); return Task.CompletedTask; }));
        bt.GetField("_sendItemClaim", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(
            bridge, (Func<uint, Task>)(id => { sentClaims.Add(id); return Task.CompletedTask; }));
        if (savesDirOverride is not null)
        {
            bt.GetField("_testSavesDirOverride", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, savesDirOverride);
            bt.GetField("_lastWorldPlayline", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, 0);
        }
    }

    private static void InvokeOnGameEvent(GameBridge bridge, string name, string arg) =>
        typeof(GameBridge).GetMethod("OnGameEvent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bridge, [name, arg]);

    private static async Task DeliverItemClaimDownAsync(GameBridge bridge, byte claimerGhostId, uint dropId)
    {
        byte[] payload = new byte[Protocol.ItemClaimDownPayloadLen];
        payload[0] = claimerGhostId;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), dropId);

        var inFrameType = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;
        object frame = Activator.CreateInstance(inFrameType, [(int)Protocol.ItemClaimDown, payload, Stopwatch.GetTimestamp(), 0u])!;
        var channelMethod = typeof(Channel)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateUnbounded" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            .MakeGenericMethod(inFrameType);
        object channel = channelMethod.Invoke(null, null)!;
        object writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
        object reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
        writer.GetType().GetMethod("TryWrite")!.Invoke(writer, [frame]);
        writer.GetType().GetMethod("Complete")!.Invoke(writer, [null]);
        var proc = typeof(GameBridge).GetMethod("ProcessFramesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)proc.Invoke(bridge, [reader, CancellationToken.None])!;
    }

    private static int CountClass(WhsSave.HenryParts parts, string itemClass) =>
        WhsSave.DecodePlayerSoul(parts.Record, new WhsSave.Node(0x115E, 0, parts.Record.Length - 6))
               .Inventory.Count(i => i.Class == itemClass);

    [Fact]
    public async Task Failed_self_pickup_preserves_remaining_item_on_ground_save_while_deducting_from_pre_drop_and_mid_race_saves()
    {
        var st = CreateStore();
        string tag = WhsSave.SeedTag(109);
        var sentDrops = new List<byte[]>();
        var sentClaims = new List<uint>();
        var bridge = new GameBridge(new ClientConfig());
        var transport = new SnapshotTestTransport();
        WireJoinedBridge(bridge, st, tag, transport, sentDrops, sentClaims);

        var (_, js) = WhsSaveTests.Pair();
        Item sword1 = new("20000000-0000-0000-0000-000000000501", Sword, 1);
        Item sword2 = new("20000000-0000-0000-0000-000000000502", Sword, 1);

        // 1. snap0 (M(0), pre-drop): 2 Swords in Henry's inventory
        js.Items = [sword1, sword2];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "pre-drop");

        // 2. Joiner drops sword2 onto the ground via real OnGameEvent("item_drop", ...)
        _now = _now.AddSeconds(2);
        InvokeOnGameEvent(bridge, "item_drop", $"{Sword} 1 1.0 100.0 200.0 10.0 dropped_sword_ent");
        Assert.Single(sentDrops);
        uint dropId = BinaryPrimitives.ReadUInt32LittleEndian(sentDrops[0]);
        Assert.NotEqual(0u, dropId);

        // 3. snap1 (M(1), while drop is lying on the ground): 1 Sword in Henry's inventory
        _now = _now.AddSeconds(2);
        js.Items = [sword1];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(1), HenryStore.SourceSnapshot, "on-ground");

        // 4. Joiner presses E to pick up own drop (item_claim in flight; Lua tentatively puts sword2 back into Henry's inventory)
        _now = _now.AddSeconds(2);
        InvokeOnGameEvent(bridge, "item_claim", $"{dropId}");

        // 5. snap2 (M(2), saved during pickup race before relay echo): 2 Swords temporarily in Henry's inventory
        _now = _now.AddSeconds(1);
        js.Items = [sword1, sword2];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(2), HenryStore.SourceSnapshot, "mid-pickup-race");

        // Duplicate item_claim tick before relay echo must NOT advance SelfPickupSeq past M(2)!
        InvokeOnGameEvent(bridge, "item_claim", $"{dropId}");

        // 6. Peer (ghost 1 != myGhostId 2) wins ItemClaimDown; Lua rolls Henry's live inventory back to 1 Sword
        _now = _now.AddSeconds(2);
        await DeliverItemClaimDownAsync(bridge, claimerGhostId: 1, dropId);

        // 7. snap3 (M(3), saved after rollback): 1 Sword in Henry's inventory
        _now = _now.AddSeconds(2);
        js.Items = [sword1];
        st.Store(tag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(3), HenryStore.SourceSnapshot, "post-rollback");

        var entries = st.TransfersOut(tag);
        Assert.Single(entries);
        Assert.True(entries[0].RemovedSeq > 0);
        Assert.True(entries[0].SelfPickupSeq > entries[0].RemovedSeq);
        Assert.True(entries[0].Seq > entries[0].SelfPickupSeq);

        // Assert ALL FOUR restores across both in-memory store and a fresh CreateStore() JSON reload:
        foreach (var store in new[] { st, CreateStore() })
        {
            // M(0) (pre-drop, had 2 Swords) -> deducts 1 -> 1 Sword
            var pick0 = store.PickFor(tag, [M(0)])!;
            Assert.Contains("transferred-out", pick0.How);
            Assert.Equal(1, CountClass(pick0.Parts, Sword));

            // M(1) (on-ground, ALREADY had 1 Sword) -> deducts 0 -> 1 Sword (NEVER 0!)
            var pick1 = store.PickFor(tag, [M(1)])!;
            Assert.DoesNotContain("transferred-out", pick1.How);
            Assert.Equal(1, CountClass(pick1.Parts, Sword));

            // M(2) (mid-pickup-race, temporarily had 2 Swords) -> deducts 1 -> 1 Sword (NEVER 2!)
            var pick2 = store.PickFor(tag, [M(2)])!;
            Assert.Contains("transferred-out", pick2.How);
            Assert.Equal(1, CountClass(pick2.Parts, Sword));

            // M(3) (post-rollback, had 1 Sword) -> deducts 0 -> 1 Sword
            var pick3 = store.PickFor(tag, [M(3)])!;
            Assert.DoesNotContain("transferred-out", pick3.How);
            Assert.Equal(1, CountClass(pick3.Parts, Sword));
        }

        // Winning self-pickup twin on a clean world: Joiner drops 1 of 2 Swords, saves on-ground M(10) (1 Sword),
        // presses E (item_claim), and WINS ItemClaimDown (claimer == myGhostId == 2) -> zero TransferOutEntry written!
        string winTag = WhsSave.SeedTag(110);
        sentDrops.Clear();
        sentClaims.Clear();
        typeof(GameBridge).GetField("_joinedTag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, winTag);

        js.Items = [sword1, sword2];
        st.Store(winTag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(0), HenryStore.SourceJoin, "win-pre-drop");
        InvokeOnGameEvent(bridge, "item_drop", $"{Sword} 1 1.0 100.0 200.0 10.0 win_sword_ent");
        uint winDropId = BinaryPrimitives.ReadUInt32LittleEndian(sentDrops.Single());
        js.Items = [sword1];
        st.Store(winTag, WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot), M(10), HenryStore.SourceSnapshot, "win-on-ground");
        InvokeOnGameEvent(bridge, "item_claim", $"{winDropId}");
        await DeliverItemClaimDownAsync(bridge, claimerGhostId: 2, winDropId);

        Assert.Empty(st.TransfersOut(winTag));
        Assert.Equal(2, CountClass(st.PickFor(winTag, [M(0)])!.Parts, Sword));
        Assert.Equal(1, CountClass(st.PickFor(winTag, [M(10)])!.Parts, Sword));

        // FilterRetainedTransfersUnlocked check on `tag`: rotate out M(0) and M(1) while M(2) (mid-race save, where
        // RemovedSeq < M(2).CaptureSeq < Seq) and M(3) remain retained (98 newer host saves + M(2) + M(3) = 100 = KeepPerWorld).
        js.Items = [sword1];
        var oneSwordParts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot);
        for (int s = 100; s < 100 + (HenryStore.KeepPerWorld - 2); s++)
        {
            _now = _now.AddSeconds(1);
            st.Store(tag, oneSwordParts, M(s), HenryStore.SourceSnapshot, $"save {s}");
        }
        Assert.DoesNotContain(st.Snapshots(tag), s => s.Md5 == M(0) || s.Md5 == M(1));
        Assert.Contains(st.Snapshots(tag), s => s.Md5 == M(2));
        Assert.Single(st.TransfersOut(tag)); // MUST still be retained for M(2)!
        Assert.Equal(1, CountClass(st.PickFor(tag, [M(2)])!.Parts, Sword));

        // Rotate M(2) and M(3) out with 2 more saves -> transfer entry is now obsolete and pruned cleanly!
        for (int s = 100 + (HenryStore.KeepPerWorld - 2); s < 100 + HenryStore.KeepPerWorld; s++)
        {
            _now = _now.AddSeconds(1);
            st.Store(tag, oneSwordParts, M(s), HenryStore.SourceSnapshot, $"save {s}");
        }
        Assert.DoesNotContain(st.Snapshots(tag), s => s.Md5 == M(2));
        Assert.Empty(st.TransfersOut(tag));
    }

    [Fact]
    public async Task Synchronous_reply_capture_prevents_duplication_when_drop_event_arrives_before_async_snapshot_task_resumes()
    {
        var st = CreateStore();
        var (hs, js) = WhsSaveTests.Pair();
        js.Seed = hs.Seed; // QuickSave written on the joiner while in the host's shared world carries the host world's seed
        string tag = WhsSave.SeedTag(hs.Seed);

        Item sword1 = new("20000000-0000-0000-0000-000000000601", Sword, 1);
        Item sword2 = new("20000000-0000-0000-0000-000000000602", Sword, 1);
        js.Items = [sword1, sword2];

        string savesDir = Path.Combine(_root, "saves");
        string playline0 = Path.Combine(savesDir, "playline0");
        Directory.CreateDirectory(playline0);

        var sentDrops = new List<byte[]>();
        var sentClaims = new List<uint>();
        var tokSeenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapCallRx = new Regex(@"KCD2MP_Wo125Snapshot\(""([0-9a-f]+)""\)", RegexOptions.CultureInvariant);

        var bridge = new GameBridge(new ClientConfig());
        var transport = new SnapshotTestTransport(lua =>
        {
            var m = snapCallRx.Match(lua);
            if (!m.Success) return Task.CompletedTask;
            tokSeenTcs.TrySetResult(m.Groups[1].Value);
            return releaseExecTcs.Task; // hold ExecLuaAsync open so Wo184TakeAsync cannot resume before the log batch finishes
        });
        WireJoinedBridge(bridge, st, tag, transport, sentDrops, sentClaims, savesDir);

        // Kick off real Wo184TakeAsync for host save M(20)
        var hostSave = new WorldSaved(20u, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Protocol.SaveKindAuto, 0, 20, Convert.FromHexString(M(20)));
        var takeMethod = typeof(GameBridge).GetMethod("Wo184TakeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var takeTask = (Task)takeMethod.Invoke(bridge, [hostSave, null, "host save", 60_000L])!;

        string tok = await tokSeenTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Game writes the pre-drop QuickSave (2 Swords) during KCD2MP_Wo125Snapshot(tok)
        byte[] preDropQuickSave = WhsSaveTests.File(js);
        string qsPath = Path.Combine(playline0, "quicksave001.whs");
        File.WriteAllBytes(qsPath, preDropQuickSave);
        string qsMd5 = WhsSave.Verify(preDropQuickSave).Md5!;

        // Log-tail reader processes two consecutive kcd.log lines synchronously on the same thread (NO BeforeGameSave "is ready" line,
        // and BEFORE AskModAsync / Wo184TakeAsync resumes on the ThreadPool!):
        //   Line 1: [KCD2-MP-EVT] ... wo124_reply <tok> ok=true
        //   Line 2: [KCD2-MP-EVT] ... item_drop <Sword> 1 ...
        InvokeOnGameEvent(bridge, "wo124_reply", $"{tok} ok=true");
        InvokeOnGameEvent(bridge, "item_drop", $"{Sword} 1 1.0 100.0 200.0 10.0 dropped_after_quicksave");

        Assert.False(takeTask.IsCompleted); // Wo184TakeAsync has NOT resumed yet when item_drop was already recorded!
        Assert.Single(sentDrops);
        uint dropId = BinaryPrimitives.ReadUInt32LittleEndian(sentDrops[0]);

        // Now let ExecLuaAsync / Wo184TakeAsync resume and observe quicksave001.whs landing
        releaseExecTcs.TrySetResult();
        var saveObserved = typeof(GameBridge).GetMethod("Wo125OnJoinerSaveObserved", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool claimedBySnapshot = false;
        for (int i = 0; i < 50 && !claimedBySnapshot; i++)
        {
            claimedBySnapshot = (bool)saveObserved.Invoke(bridge, [qsPath, qsMd5])!;
            if (!claimedBySnapshot) await Task.Delay(10);
        }
        Assert.True(claimedBySnapshot);

        await takeTask.WaitAsync(TimeSpan.FromSeconds(5));
        object pairOutcome = takeTask.GetType().GetProperty("Result")!.GetValue(takeTask)!;
        Assert.Equal("Paired", pairOutcome.ToString());

        // Peer (ghost 1) claims the dropped sword; Joiner disconnects before the next host save (M(21) unpaired)
        await DeliverItemClaimDownAsync(bridge, claimerGhostId: 1, dropId);

        // Active captures must not leak after pairing completes
        var activeCapturesField = typeof(HenryStore).GetField("_activeCaptures", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var activeCaptures = (System.Collections.IEnumerable)activeCapturesField.GetValue(st)!;
        Assert.Empty(activeCaptures);

        // Reconnect picks paired M(20) when M(21) is unpaired: MUST deduct the post-quicksave drop -> 1 Sword (NEVER 2 Swords!)
        foreach (var store in new[] { st, CreateStore() })
        {
            var pick = store.PickFor(tag, [M(21), M(20)])!;
            Assert.Equal(M(20), pick.Snapshot.Md5);
            Assert.Contains("transferred-out", pick.How);
            Assert.Equal(1, CountClass(pick.Parts, Sword));
        }

        // Refused snapshot reply (ok=false) must also return NotNow cleanly with zero leaked active captures
        tokSeenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseExecTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostSave2 = new WorldSaved(22u, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Protocol.SaveKindAuto, 0, 22, Convert.FromHexString(M(22)));
        var refusedTask = (Task)takeMethod.Invoke(bridge, [hostSave2, null, "host save", 60_000L])!;
        string refusedTok = await tokSeenTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        InvokeOnGameEvent(bridge, "wo124_reply", $"{refusedTok} ok=false");
        releaseExecTcs.TrySetResult();
        await refusedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("NotNow", refusedTask.GetType().GetProperty("Result")!.GetValue(refusedTask)!.ToString());
        Assert.Empty((System.Collections.IEnumerable)activeCapturesField.GetValue(st)!);
    }
}
