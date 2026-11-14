// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Reflection;

namespace KcdMp.Client.Tests;

public class M1PostLoadLedgerTests
{
    private const string Dice = "b8df2253-e5c8-4e6c-9303-b4bc84192e67";
    private const string Chest = "stash[Chest/chest3_c1d92081-d74b-4955-ade8-384e71325794]";
    private const string WorldTag = "0123456789";

    private sealed class RecordingTransport : IGameTransport
    {
        public readonly ConcurrentQueue<string> LuaCalls = new();
        public string Name => "recording";
        public int RoundTripsPerStateRead => 0;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) => Task.FromResult<PlayerState?>(null);
        public Task ExecuteAsync(string lua, CancellationToken ct = default) { LuaCalls.Enqueue(lua); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid[]?> ReadEquippedItemClassesAsync(CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);
        public Task<Guid[]?> ReadGhostEquippedItemClassesAsync(string ghostSoulName, CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);
        public Task EquipItemOnGhostAsync(string ghostSoulName, Guid itemClass, bool createIfMissing, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnequipItemOnGhostAsync(string ghostSoulName, Guid itemClass, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> ReadGhostSoulGuidAsync(string ghostSoulName, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<string?> ReadSoulNameByGuidAsync(Guid soulGuid, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(Guid? Guid, string? Name)> ReadPlayerSoulIdentityAsync(CancellationToken ct = default) => Task.FromResult<(Guid?, string?)>((null, null));
        public Task ExecuteNowAsync(string lua, CancellationToken ct = default) { LuaCalls.Enqueue(lua); return Task.CompletedTask; }
    }

    private static string RepoRoot()
    {
        string d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "dotnet", "KcdMp.Client"))) d = Path.GetDirectoryName(d)!;
        return d;
    }

    private static (GameBridge Bridge, RecordingTransport Transport) CreateBridge()
    {
        var bridge = new GameBridge(new ClientConfig());
        var transport = new RecordingTransport();
        typeof(GameBridge).GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, transport);
        return (bridge, transport);
    }

    private static void InvokeRestorePick(GameBridge b, HenryStore.Snapshot? s) =>
        typeof(GameBridge).GetMethod("Wo134OnRestorePick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, [s]);

    private static void InvokeAfterReady(GameBridge b, string tag) =>
        typeof(GameBridge).GetMethod("Wo134AfterReady", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, [tag]);

    private static void InvokeOnLedgerPart(GameBridge b, int part, int n, List<Wo134Rules.Entry> rows) =>
        typeof(GameBridge).GetMethod("Wo134OnLedgerPart", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, [part, n, rows]);

    private static Task InvokeApplyIfReadyAsync(GameBridge b, bool timeout) =>
        (Task)typeof(GameBridge).GetMethod("Wo134ApplyIfReadyAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, [timeout])!;

    [Fact]
    public void PostLoadAsync_arms_Wo134AfterReady_strictly_before_SendJoinerReadyAsync()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "dotnet", "KcdMp.Client", "GameBridge.Wo124.cs"));
        int postLoadIdx = src.IndexOf("private async Task PostLoadAsync(", StringComparison.Ordinal);
        Assert.True(postLoadIdx >= 0);
        int armIdx = src.IndexOf("Wo134AfterReady(wt0)", postLoadIdx, StringComparison.Ordinal);
        int sendReadyIdx = src.IndexOf("await SendJoinerReadyAsync(\"wo124\")", postLoadIdx, StringComparison.Ordinal);
        Assert.True(armIdx > postLoadIdx);
        Assert.True(sendReadyIdx > postLoadIdx);
        Assert.True(armIdx < sendReadyIdx, "Wo134AfterReady(wt0) must run BEFORE await SendJoinerReadyAsync(\"wo124\") so < 2 ms LAN replies cannot be wiped");
    }

    [Fact]
    public async Task Fast_lan_ledger_arrival_after_arm_or_concurrently_before_after_ready_applies_immediately_without_wipe()
    {
        var entry = new Wo134Rules.Entry { C = Chest, Cls = Dice, N = 1, Hp = 1f, T = 1000, R = 7 };

        // Case 1: Armed before SendJoinerReadyAsync, sub-millisecond LAN response arrives right after
        {
            var (bridge, transport) = CreateBridge();
            InvokeRestorePick(bridge, null);
            InvokeAfterReady(bridge, WorldTag);
            InvokeOnLedgerPart(bridge, 1, 1, [entry]);
            await InvokeApplyIfReadyAsync(bridge, false);

            Assert.Contains(transport.LuaCalls, c => c.Contains("KCD2MP_W134ChestApply", StringComparison.Ordinal) && c.Contains(Dice, StringComparison.Ordinal));
            // Consumed ledger is cleared so a subsequent join never reuses stale host entries
            Assert.Null(typeof(GameBridge).GetField("_w134RxLedger", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge));
        }

        // Case 2: Packet arrives concurrently BEFORE Wo134AfterReady runs -> preserved and applied immediately!
        {
            var (bridge, transport) = CreateBridge();
            InvokeRestorePick(bridge, null);
            InvokeOnLedgerPart(bridge, 1, 1, [entry]);
            InvokeAfterReady(bridge, WorldTag);
            // Wait briefly for the fire-and-forget Wo134ApplyIfReadyAsync kicked by Wo134AfterReady
            for (int i = 0; i < 20 && transport.LuaCalls.IsEmpty; i++) await Task.Delay(10);

            Assert.Contains(transport.LuaCalls, c => c.Contains("KCD2MP_W134ChestApply", StringComparison.Ordinal) && c.Contains(Dice, StringComparison.Ordinal));
        }

        // Case 3: Stale world-epoch packet arriving before Wo134AfterReady IS discarded when world epoch advanced
        {
            var (bridge, transport) = CreateBridge();
            var epochs = (Wo206Epochs)typeof(GameBridge).GetField("_w206", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
            var frameStampField = typeof(GameBridge).GetField("_w206FrameStamp", BindingFlags.NonPublic | BindingFlags.Instance)!;

            InvokeRestorePick(bridge, null);
            epochs.LearnWorld(1, 1);
            frameStampField.SetValue(bridge, 1u);
            InvokeOnLedgerPart(bridge, 1, 1, [entry]);

            // Host reloads into world epoch 2 before Wo134AfterReady runs
            epochs.LearnWorld(2, 2);
            InvokeAfterReady(bridge, WorldTag);
            await Task.Delay(25);

            Assert.Empty(transport.LuaCalls);
            Assert.Null(typeof(GameBridge).GetField("_w134RxLedger", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge));
        }
    }
}
