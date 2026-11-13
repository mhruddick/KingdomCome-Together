// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
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
}
