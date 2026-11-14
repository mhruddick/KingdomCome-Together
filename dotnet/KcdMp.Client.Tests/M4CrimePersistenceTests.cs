// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class M4CrimePersistenceTests
{
    private const string Zelejov = "trosecko_settlements_zelejov";

    [Fact]
    public void Relative_timers_calmed_violent_crimes_and_world_time_expiration_restore_accurately_across_restarts()
    {
        long oldProcessNow = 9_000_000L;
        var rec = new JoinerCrimeRecord();

        // 1. Civilian-witnessed theft 12 s into the 20 s report delay (8 s remaining)
        Assert.True(rec.Add(new JoinerCrimeRecord.Crime
        {
            Id = 1, Kind = "theft", Settlement = Zelejov, Witnesses = 2, Guards = 0,
            AtWorldS = 100_000, AtMs = oldProcessNow - 12_000L,
        }));

        // 2. Guard-witnessed assault 75 s into the 120 s fresh window (45 s remaining)
        Assert.True(rec.Add(new JoinerCrimeRecord.Crime
        {
            Id = 2, Kind = "assault", Settlement = Zelejov, Witnesses = 1, Guards = 1,
            AtWorldS = 100_100, AtMs = oldProcessNow - 75_000L,
        }));

        // 3. Guard-witnessed murder that was CALMED (player went down) -- must NEVER re-aggro after restart!
        var calmedMurder = new JoinerCrimeRecord.Crime
        {
            Id = 3, Kind = "murder", Settlement = "trosecko_settlements_troskovice", Witnesses = 2, Guards = 1,
            AtWorldS = 100_200, AtMs = oldProcessNow - 10_000L,
        };
        Assert.True(rec.Add(calmedMurder));
        calmedMurder.Calmed = true;

        // 4. Resisted lockpick in tachov 210 s into the 300 s escalation window (90 s remaining)
        var resistedLockpick = new JoinerCrimeRecord.Crime
        {
            Id = 4, Kind = "lockpick", Settlement = "trosecko_settlements_tachov", Witnesses = 1, Guards = 1,
            AtWorldS = 100_300, AtMs = oldProcessNow - 250_000L,
        };
        Assert.True(rec.Add(resistedLockpick));
        resistedLockpick.Resisted = true;
        resistedLockpick.ResistedAtMs = oldProcessNow - 210_000L;

        var ledger = new JoinerCrimeStore.WorldCrimeLedger();
        ledger.SetPlayerRecord("Moose", rec, oldProcessNow);
        var reloaded = JoinerCrimeStore.WorldCrimeLedger.FromJson(ledger.ToJson());

        // Restore in a fresh OS/process session where Environment.TickCount64 is only 5,000 ms!
        long newProcessNow = 5_000L;
        Assert.True(reloaded.TryBuildRecord("moose", newProcessNow, currentWorldS: 100_500, out var restored));
        Assert.Equal(4, restored.Count);

        // (1) Theft is still unreported at t=5,000 + 7,999, then becomes Known at t=5,000 + 8,000
        var theft = restored.Open.Single(c => c.Kind == "theft");
        Assert.False(theft.Known);
        Assert.Empty(restored.Tick(newProcessNow + 7_999L, 100_500));
        var newlyKnown = restored.Tick(newProcessNow + 8_000L, 100_500);
        Assert.Single(newlyKnown);
        Assert.True(theft.Known);

        // (2) Assault (Guards=1) stays hostile for 45 s, then becomes arrestable
        Assert.True(restored.AnyHostileKnownIn(Zelejov, newProcessNow + 44_999L));
        Assert.False(restored.AnyHostileKnownIn(Zelejov, newProcessNow + 45_000L));
        Assert.Equal(2, restored.ArrestableIn(Zelejov, newProcessNow + 45_000L).Count);

        // (3) Calmed murder is Known and immediately ARRESTABLE, NOT hostile!
        Assert.False(restored.AnyHostileKnownIn("trosecko_settlements_troskovice", newProcessNow));
        Assert.Single(restored.ArrestableIn("trosecko_settlements_troskovice", newProcessNow));

        // (4) Resisted lockpick stays hostile for 90 s, then becomes arrestable
        Assert.True(restored.AnyHostileKnownIn("trosecko_settlements_tachov", newProcessNow + 89_999L));
        Assert.False(restored.AnyHostileKnownIn("trosecko_settlements_tachov", newProcessNow + 90_000L));

        // (5) Multi-day world-time expiration: after 4.5 days (+388,800 s), 4-day crimes (theft, lockpick) expire while 5-day assault and 7-day murder remain
        restored.Tick(newProcessNow + 95_000L, 100_000 + 4.5 * 86_400.0);
        Assert.Equal(["assault", "murder"], restored.Open.Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void Store_caps_paired_checkpoints_at_32_preserves_latest_and_drops_corrupt_entries()
    {
        string root = Path.Combine(Path.GetTempPath(), "m4-crimes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JoinerCrimeStore(root);
            string tag = WhsSave.SeedTag(0x99887766u);
            var rec = new JoinerCrimeRecord();
            rec.Add(new JoinerCrimeRecord.Crime { Id = 1, Kind = "theft", Settlement = Zelejov, Witnesses = 1, Guards = 1, AtWorldS = 5000, AtMs = 1000 });
            var ledger = new JoinerCrimeStore.WorldCrimeLedger();
            ledger.SetPlayerRecord("PartnerA", rec, 1000);
            store.SaveLatest(tag, ledger);

            for (int i = 0; i < 38; i++)
            {
                string md5 = i.ToString("x32");
                store.Pair(tag, md5, ledger);
            }

            string dir = Path.Combine(root, tag);
            Assert.True(File.Exists(Path.Combine(dir, "latest.json")));
            var md5Files = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).Where(f => f != "latest.json").ToList();
            Assert.Equal(JoinerCrimeStore.KeepPairs, md5Files.Count);

            // Corrupt / hostile JSON fields are stripped safely
            var sanitized = JoinerCrimeStore.WorldCrimeLedger.FromJson(
                "{\"Players\":{\"good\":[{\"Id\":1,\"Kind\":\"theft\",\"Settlement\":\"trosecko_settlements_zelejov\",\"Witnesses\":1,\"Guards\":1,\"Known\":true,\"AtWorldS\":100},"
                + "{\"Id\":2,\"Kind\":\"bad\\\"crime\",\"Settlement\":\"x;y\",\"Witnesses\":1,\"Guards\":1}]}}");
            Assert.True(sanitized.TryBuildRecord("good", 10_000, 100, out var cleanRec));
            Assert.Single(cleanRec.Open);
            Assert.Equal("theft", cleanRec.Open[0].Kind);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static List<LootMsg> ReadCrimeHostMessages(MemoryStream ms)
    {
        var list = new List<LootMsg>();
        var buf = ms.ToArray();
        int p = 0;
        while (p + 3 <= buf.Length)
        {
            byte type = buf[p];
            int len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 1, 2));
            if (p + 3 + len > buf.Length) break;
            if (type == Protocol.CrimeHostUp && len >= Protocol.JoinHeaderLen
                && LootMsg.TryDecode(buf.AsSpan(p + 3 + Protocol.JoinHeaderLen, len - Protocol.JoinHeaderLen), out var m))
            {
                list.Add(m);
            }
            p += 3 + len;
        }
        return list;
    }

    [Fact]
    public async Task Host_pairs_crimes_with_saves_rolls_back_on_reload_and_restores_across_sessions()
    {
        string root = Path.Combine(Path.GetTempPath(), "m4-host-crimes-" + Guid.NewGuid().ToString("N"));
        try
        {
            const uint seed = 0x55AA1234u;
            string tag = WhsSave.SeedTag(seed);
            string md5Clean = new('a', 32);
            string md5AfterTheft = new('b', 32);

            var store = new JoinerCrimeStore(root);
            var outMs = new MemoryStream();
            var tx = new NoopTransport();
            var host1 = new GameBridge(new ClientConfig());
            host1.Wo139ConfigureTestHost(store, outMs, tx, seed, worldTime: 50_000);

            // 1. Save A: clean world (0 crimes)
            host1.Wo139PeerConnectForTest(1, "Moose");
            host1.Wo139HostWorldForTest(tag, md5Clean, loaded: false);
            Assert.Empty(store.Paired(tag, md5Clean)!.Players);

            // 2. Moose commits witnessed theft + self-reported trespass -> Save B pairs both
            await host1.Wo139JudgedForTestAsync($"1 10 theft 2 1 {Zelejov} g1");
            await host1.Wo139JudgedForTestAsync($"1 11 trespass 1 1 {Zelejov} g1");
            Assert.Equal(2, host1.Wo139RecordOfPeerForTest(1)!.Count);
            host1.Wo139HostWorldForTest(tag, md5AfterTheft, loaded: false);
            Assert.NotNull(store.Paired(tag, md5AfterTheft));

            // 3. Moose commits a post-save murder (now 3 crimes in RAM & latest.json)
            await host1.Wo139JudgedForTestAsync($"1 0 murder 3 1 {Zelejov} g1");
            Assert.Equal(3, host1.Wo139RecordOfPeerForTest(1)!.Count);

            // 4. Host reloads Save B -> rolls back the post-save murder (and drops self-reported trespass per WO-157)!
            outMs.SetLength(0);
            host1.Wo139HostWorldForTest(tag, md5AfterTheft, loaded: true);
            var afterReloadB = host1.Wo139RecordOfPeerForTest(1);
            Assert.NotNull(afterReloadB);
            Assert.Equal(["theft"], afterReloadB!.Open.Select(c => c.Kind).ToArray());
            var msgsB = ReadCrimeHostMessages(outMs);
            Assert.Contains(msgsB, m => m.Kind == Protocol.CrimeHostRecord && m.Text.StartsWith("1 theft:1 ", StringComparison.Ordinal));

            // 5. Host reloads Save A (clean) -> clears Moose's record and sends CrimeHostCleared + CrimeHostRecord(0)
            outMs.SetLength(0);
            host1.Wo139HostWorldForTest(tag, md5Clean, loaded: true);
            Assert.Null(host1.Wo139RecordOfPeerForTest(1));
            var msgsA = ReadCrimeHostMessages(outMs);
            Assert.Contains(msgsA, m => m.Kind == Protocol.CrimeHostCleared && m.Text.StartsWith("reload ", StringComparison.Ordinal));
            Assert.Contains(msgsA, m => m.Kind == Protocol.CrimeHostRecord && m.Text == "0 - -");

            // 6. New play session tomorrow: Host2 loads Save B, then Moose reconnects (different case/whitespace, new ghost id 3) -> gets persisted theft back!
            var outMs2 = new MemoryStream();
            var host2 = new GameBridge(new ClientConfig());
            host2.Wo139ConfigureTestHost(store, outMs2, tx, seed, worldTime: 51_000);
            host2.Wo139HostWorldForTest(tag, md5AfterTheft, loaded: true);
            host2.Wo139PeerConnectForTest(3, "  moose ");
            var restoredSession2 = host2.Wo139RecordOfPeerForTest(3);
            Assert.NotNull(restoredSession2);
            Assert.Equal(["theft"], restoredSession2!.Open.Select(c => c.Kind).ToArray());

            // 7. Mid-world attach race: Host3 attaches before seed is known (seed: null), Moose connects first, then identify save discovers tag -> hydrated!
            var outMs3 = new MemoryStream();
            var host3 = new GameBridge(new ClientConfig());
            host3.Wo139ConfigureTestHost(store, outMs3, tx, seed: null, worldTime: 52_000);
            host3.Wo139PeerConnectForTest(4, "Moose");
            Assert.Null(host3.Wo139RecordOfPeerForTest(4));
            host3.Wo139HostWorldForTest(tag, new string('c', 32), loaded: false);
            Assert.Equal(["theft"], host3.Wo139RecordOfPeerForTest(4)!.Open.Select(c => c.Kind).ToArray());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class NoopTransport : IGameTransport
    {
        public string Name => "noop";
        public int RoundTripsPerStateRead => 0;
        public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) => Task.FromResult<PlayerState?>(null);
        public Task ExecuteAsync(string lua, CancellationToken ct = default) => Task.CompletedTask;
        public Task ExecuteNowAsync(string lua, CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid[]?> ReadEquippedItemClassesAsync(CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);
        public Task<Guid[]?> ReadGhostEquippedItemClassesAsync(string ghostSoulName, CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);
        public Task EquipItemOnGhostAsync(string ghostSoulName, Guid itemClass, bool createIfMissing, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnequipItemOnGhostAsync(string ghostSoulName, Guid itemClass, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> ReadGhostSoulGuidAsync(string ghostSoulName, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<string?> ReadSoulNameByGuidAsync(Guid soulGuid, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(Guid? Guid, string? Name)> ReadPlayerSoulIdentityAsync(CancellationToken ct = default) => Task.FromResult<(Guid?, string?)>((null, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
