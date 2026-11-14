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
    public void Store_caps_paired_checkpoints_at_100_preserves_latest_and_drops_corrupt_entries()
    {
        string root = Path.Combine(Path.GetTempPath(), "m4-crimes-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(100, JoinerCrimeStore.KeepPairs);
            var store = new JoinerCrimeStore(root);
            string tag = WhsSave.SeedTag(0x99887766u);
            var rec = new JoinerCrimeRecord();
            rec.Add(new JoinerCrimeRecord.Crime { Id = 1, Kind = "theft", Settlement = Zelejov, Witnesses = 1, Guards = 1, AtWorldS = 5000, AtMs = 1000 });
            var ledger = new JoinerCrimeStore.WorldCrimeLedger();
            ledger.SetPlayerRecord("PartnerA", rec, 1000);
            store.SaveLatest(tag, ledger);

            for (int i = 0; i < 108; i++)
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

    [Fact]
    public async Task Reload_several_days_earlier_never_expires_crimes_against_stale_clock_and_boundary_violent_crime_keeps_fresh_window()
    {
        // 1. Low #13: civilian-witnessed violent crime snapshotted right at ReportDelayMs (before Tick flipped Known) keeps full 120s FreshMs!
        long t0 = 500_000L;
        var rec = new JoinerCrimeRecord();
        Assert.True(rec.Add(new JoinerCrimeRecord.Crime
        {
            Id = 1, Kind = "murder", Settlement = Zelejov, Witnesses = 1, Guards = 0,
            AtWorldS = 172_800, AtMs = t0,
        }));
        var ledger = new JoinerCrimeStore.WorldCrimeLedger();
        ledger.SetPlayerRecord("Moose", rec, t0 + JoinerCrimeRecord.ReportDelayMs);   // exact 20,000 ms boundary!
        Assert.True(ledger.TryBuildRecord("Moose", 10_000L, currentWorldS: 172_800, out var boundaryRestored));
        Assert.True(boundaryRestored.AnyHostileKnownIn(Zelejov, 10_000L + JoinerCrimeRecord.FreshMs - 1L));
        Assert.False(boundaryRestored.AnyHostileKnownIn(Zelejov, 10_000L + JoinerCrimeRecord.FreshMs));

        // 2. High #3: Host at Day 10 (864,000 s) reloads a Day 2 (172,800 s) save carrying a 4-day theft
        string root = Path.Combine(Path.GetTempPath(), "m4-stale-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            const uint seed = 0x77771234u;
            string tag = WhsSave.SeedTag(seed);
            string md5Day2 = new('d', 32);
            var store = new JoinerCrimeStore(root);
            var tx = new NoopTransport();
            var outMs = new MemoryStream();
            var host = new GameBridge(new ClientConfig());
            host.Wo139ConfigureTestHost(store, outMs, tx, seed, worldTime: 172_800);   // Day 2
            host.Wo139PeerConnectForTest(1, "Moose");
            await host.Wo139JudgedForTestAsync($"1 10 theft 1 1 {Zelejov} g1");
            host.Wo139HostWorldForTest(tag, md5Day2, loaded: false);

            // Advance Host clock to Day 10 (8 days later -> theft expired in Day 10 timeline)
            host.Wo139SetWorldTimeForTest(864_000);
            await host.Wo139HostTickForTestAsync(Environment.TickCount64 + 1_000);
            Assert.Null(host.Wo139RecordOfPeerForTest(1));

            // Host reloads Day 2 save while _lastPolledWorldTime is STILL 864,000 (Day 10)!
            host.Wo139HostWorldForTest(tag, md5Day2, loaded: true);
            Assert.NotNull(host.Wo139RecordOfPeerForTest(1));
            Assert.Equal(["theft"], host.Wo139RecordOfPeerForTest(1)!.Open.Select(c => c.Kind).ToArray());

            // Even if 1s host tick fires BEFORE the loaded world's first WORLDTIME reading arrives, theft is NOT expired!
            await host.Wo139HostTickForTestAsync(Environment.TickCount64 + 1_000);
            Assert.NotNull(host.Wo139RecordOfPeerForTest(1));
            Assert.Equal(["theft"], host.Wo139RecordOfPeerForTest(1)!.Open.Select(c => c.Kind).ToArray());

            // Post-load Day 2 WORLDTIME reading lands -> crime stays active on Day 2, and expires normally if clock later reaches Day 7
            host.Wo139SetWorldTimeForTest(173_000, postLoadSample: true);
            await host.Wo139HostTickForTestAsync(Environment.TickCount64 + 2_000);
            Assert.NotNull(host.Wo139RecordOfPeerForTest(1));
            host.Wo139SetWorldTimeForTest(172_800 + 5 * 86_400, postLoadSample: true);
            await host.Wo139HostTickForTestAsync(Environment.TickCount64 + 3_000);
            Assert.Null(host.Wo139RecordOfPeerForTest(1));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Pre_connect_continue_newest_save_preserves_crimes_while_pre_connect_older_save_rolls_back_and_parked_keys_ignore_case()
    {
        string root = Path.Combine(Path.GetTempPath(), "m4-preconnect-" + Guid.NewGuid().ToString("N"));
        string savesDir = Path.Combine(root, "saves");
        string playline0 = Path.Combine(savesDir, "playline0");
        Directory.CreateDirectory(playline0);
        try
        {
            const uint seed = 0x1234ABCDu;
            string tag = WhsSave.SeedTag(seed);
            var store = new JoinerCrimeStore(Path.Combine(root, "crimes"));
            var tx = new NoopTransport();

            // 1. Write an older pre-coop save001.whs (SaveTime=1000, unpaired)
            string save001Path = Path.Combine(playline0, "save001.whs");
            File.WriteAllBytes(save001Path, WhsSaveTests.File(new WhsSaveTests.Spec { Seed = seed, SaveTime = 1000, Story = 100 }));
            File.SetLastWriteTimeUtc(save001Path, new DateTime(2026, 11, 13, 18, 0, 0, DateTimeKind.Utc));

            // 2. Session 1: Moose commits lockpick -> Save 002 paired; then Moose commits theft, disconnects & reconnects with different casing ("  mOoSe "), then disconnects
            string save002Path = Path.Combine(playline0, "save002.whs");
            var save002Bytes = WhsSaveTests.File(new WhsSaveTests.Spec { Seed = seed, SaveTime = 2000, Story = 200 });
            File.WriteAllBytes(save002Path, save002Bytes);
            string save002Md5 = WhsSave.Verify(save002Bytes).Md5.ToLowerInvariant();

            var hostSession1 = new GameBridge(new ClientConfig());
            hostSession1.Wo139ConfigureTestHost(store, new MemoryStream(), tx, seed, worldTime: 20_000);
            hostSession1.Wo139PeerConnectForTest(1, "Moose");
            await hostSession1.Wo139JudgedForTestAsync($"1 1 lockpick 1 1 {Zelejov} g1");
            hostSession1.Wo139HostWorldForTest(tag, save002Md5, loaded: false, save002Path);

            await hostSession1.Wo139JudgedForTestAsync($"1 2 theft 1 1 {Zelejov} g1");
            hostSession1.Wo139PeerDisconnectForTest(1);
            // Reconnect with different case/whitespace -> hits _w144ParkedCrime case-insensitively!
            hostSession1.Wo139PeerConnectForTest(2, "  mOoSe ");
            Assert.Equal(["lockpick", "theft"], hostSession1.Wo139RecordOfPeerForTest(2)!.Open.Select(c => c.Kind).ToArray());
            hostSession1.Wo139PeerDisconnectForTest(2);

            // 3. Host saves & quits solo AFTER disconnect -> exit_save.whs (SaveTime=3000, newest in playline, written AFTER latest.json, unpaired!)
            string exitSavePath = Path.Combine(playline0, "exit_save.whs");
            File.WriteAllBytes(exitSavePath, WhsSaveTests.File(new WhsSaveTests.Spec { Seed = seed, SaveTime = 3000, Story = 300 }));
            File.SetLastWriteTimeUtc(exitSavePath, DateTime.UtcNow.AddSeconds(5));

            // 4. "Continue newest save" before connecting: Host loaded playline0/exit_save.whs at main menu, THEN connected!
            var hostContinue = new GameBridge(new ClientConfig());
            hostContinue.Wo139ConfigureTestHost(store, new MemoryStream(), tx, seed: null, worldTime: 25_000);
            hostContinue.Wo139SetPreConnectLoadForTest(savesDir, "playline0/exit_save.whs");
            hostContinue.Wo125HostTickForTest();
            hostContinue.Wo139PeerConnectForTest(1, "Moose");
            Assert.NotNull(hostContinue.Wo139RecordOfPeerForTest(1));
            Assert.Equal(["lockpick", "theft"], hostContinue.Wo139RecordOfPeerForTest(1)!.Open.Select(c => c.Kind).ToArray());

            // 5. "Load older paired save before connecting": Host loaded playline0/save002.whs at main menu, THEN connected -> rolls back theft, keeps lockpick!
            var hostLoadPaired = new GameBridge(new ClientConfig());
            hostLoadPaired.Wo139ConfigureTestHost(store, new MemoryStream(), tx, seed: null, worldTime: 20_000);
            hostLoadPaired.Wo139SetPreConnectLoadForTest(savesDir, "playline0/save002.whs");
            hostLoadPaired.Wo139PeerConnectForTest(1, "Moose");
            Assert.NotNull(hostLoadPaired.Wo139RecordOfPeerForTest(1));
            Assert.Equal(["lockpick"], hostLoadPaired.Wo139RecordOfPeerForTest(1)!.Open.Select(c => c.Kind).ToArray());

            // 6. "Load older unpaired save before connecting": Host loaded playline0/save001.whs at main menu, THEN connected -> rolls back ALL crimes to empty!
            var hostLoadOlder = new GameBridge(new ClientConfig());
            hostLoadOlder.Wo139ConfigureTestHost(store, new MemoryStream(), tx, seed: null, worldTime: 10_000);
            hostLoadOlder.Wo139SetPreConnectLoadForTest(savesDir, "playline0/save001.whs");
            hostLoadOlder.Wo139PeerConnectForTest(1, "Moose");
            Assert.Null(hostLoadOlder.Wo139RecordOfPeerForTest(1));
            Assert.Empty(store.Latest(tag).Players);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task JoinerCrimeRecord_is_thread_safe_under_concurrent_mutation_and_enumeration()
    {
        var rec = new JoinerCrimeRecord();
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                rec.Add(new JoinerCrimeRecord.Crime
                {
                    Id = (uint)(worker * 1000 + i),
                    Kind = (i % 3 == 0) ? "trespass" : (i % 2 == 0 ? "theft" : "assault"),
                    Settlement = Zelejov,
                    Witnesses = 1,
                    Guards = i % 2,
                    AtWorldS = 10_000 + i,
                    AtMs = 1_000 + i,
                });
                _ = rec.Open.Count;
                _ = rec.Settlements.Count;
                _ = rec.AnyHostileKnownIn(Zelejov, 2_000 + i);
                _ = rec.ArrestableIn(Zelejov, 2_000 + i);
                _ = rec.Tick(25_000 + i, 10_500);
                if (i % 25 == 0) rec.DropReportedTrespasses();
                if (i % 40 == 0) rec.MarkResisted(Zelejov, 25_000 + i);
                if (i % 60 == 0) rec.Calm(Zelejov);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.True(rec.Count >= 0);
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
