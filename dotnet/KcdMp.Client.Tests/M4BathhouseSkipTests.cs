// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class M4BathhouseSkipTests
{
    private static List<(byte Target, LootMsg Msg)> ReadSleepVoteMessages(MemoryStream ms)
    {
        var list = new List<(byte, LootMsg)>();
        var buf = ms.ToArray();
        int p = 0;
        while (p + 3 <= buf.Length)
        {
            byte type = buf[p];
            int len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 1, 2));
            if (p + 3 + len > buf.Length) break;
            if (type == Protocol.SleepVoteUp && len >= Protocol.JoinHeaderLen)
            {
                byte target = buf[p + 3];
                if (LootMsg.TryDecode(buf.AsSpan(p + 3 + Protocol.JoinHeaderLen, len - Protocol.JoinHeaderLen), out var m))
                    list.Add((target, m));
            }
            p += 3 + len;
        }
        return list;
    }

    [Fact]
    public void Bath_kind_round_trips_on_wire_and_maps_to_bystander_wait_skip()
    {
        string text = Wo140Text.Begin("bath", 2, 3f, true);
        Assert.Equal("bath 2 3 1", text);
        Assert.True(Wo140Text.TryBegin(text, out string kind, out byte asker, out float hours, out bool save));
        Assert.Equal(("bath", (byte)2, 3f, true), (kind, asker, hours, save));
        Assert.Equal("bath", Wo140Rules.KindOfSkipId(7));
        Assert.Equal((byte)1, Wo140Rules.AccepterSkipId("bath"));
        Assert.Equal(Protocol.TimeSkipKindWait, Wo140Rules.SharedTimeSkipKind("bath"));
        Assert.Equal(Protocol.TimeSkipKindWait, Wo140Rules.SharedTimeSkipKind("wait"));
        Assert.Equal(Protocol.TimeSkipKindSleep, Wo140Rules.SharedTimeSkipKind("sleep"));
    }

    [Fact]
    public async Task Joiner_bathhouse_skip_broadcasts_begin_protects_clock_relays_through_host_and_runs_rest_save()
    {
        var joinerOut = new MemoryStream();
        var joinerTx = new RecordingTransport();
        var joiner = new GameBridge(new ClientConfig());
        joiner.Wo140ConfigureTestPeer(isHost: false, myId: 1, joinerOut, joinerTx);
        joiner.Wo139PeerConnectForTest(0, "HostHenry");
        joiner.Wo140NoteHostClockForTest(0, Protocol.TimeSkipPhaseDone, 50_000);

        // Joiner starts a 2h CureClean bathhouse service (C_SkipTime id == 7, no prior SleepAsk vote!)
        await joiner.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeBegan, 7, 2f, 2));
        Assert.True(joiner.Wo140SharedSkipForTest);
        Assert.False(joiner.Wo140SaveAfterForTest);

        var joinerSent = ReadSleepVoteMessages(joinerOut);
        Assert.Single(joinerSent);
        Assert.Equal(Protocol.JoinTargetHost, joinerSent[0].Target);
        Assert.Equal(Protocol.SleepBegin, joinerSent[0].Msg.Kind);
        Assert.True(Wo140Text.TryBegin(joinerSent[0].Msg.Text, out string kind, out byte asker, out float hours, out bool save));
        Assert.Equal(("bath", (byte)1, 2f, true), (kind, asker, hours, save));

        // Even after Joiner's local 2h skip ends before Host's clock report arrives, W140SharedSkip prevents pulling Joiner's clock backward!
        await joiner.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeEnded, 0, 0f, 0));
        Assert.False(joiner.Wo140CheckClockForTest(50_000 + 7_200));

        // Host receives Joiner 1's SleepBegin("bath", save=true) with Joiner 1 ("Moose") and Joiner 2 ("Partner2") connected
        var hostOut = new MemoryStream();
        var hostTx = new RecordingTransport();
        var host = new GameBridge(new ClientConfig());
        host.Wo140ConfigureTestPeer(isHost: true, myId: 0, hostOut, hostTx);
        host.Wo139PeerConnectForTest(1, "Moose");
        host.Wo139PeerConnectForTest(2, "Partner2");

        var inboundBody = new LootMsg(Protocol.SleepBegin, joinerSent[0].Msg.Tok, joinerSent[0].Msg.Text)
            .BuildUp(Protocol.SleepVoteUp, Protocol.JoinTargetHost)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await host.Wo140InboundFrameForTestAsync(1, inboundBody);

        Assert.True(host.Wo140SharedSkipForTest);
        Assert.True(host.Wo140SaveAfterForTest);
        Assert.Contains(hostTx.LuaCalls, c => c.Contains("Moose is using the bathhouse (2h)...", StringComparison.Ordinal));

        // Host relayed SleepBegin("bath") to Joiner 2 (and not back to Joiner 1)
        var hostRelayed = ReadSleepVoteMessages(hostOut);
        Assert.Single(hostRelayed);
        Assert.Equal((byte)2, hostRelayed[0].Target);
        Assert.Equal(Protocol.SleepBegin, hostRelayed[0].Msg.Kind);

        // When Host's skip finishes (Began -> Ended), Host triggers KCD2MP_W140RestSave() so world clock + Joiner's .hblk v2 are saved!
        await host.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeBegan, 1, 2f, 2));
        await host.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeEnded, 0, 0f, 0));
        Assert.False(host.Wo140SaveAfterForTest);
        Assert.Contains(hostTx.LuaCalls, c => c.Contains("KCD2MP_W140RestSave()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Host_bathhouse_skip_sends_save_false_to_joiner_while_unaccepted_sleep_still_refuses()
    {
        var hostOut = new MemoryStream();
        var hostTx = new RecordingTransport();
        var host = new GameBridge(new ClientConfig());
        host.Wo140ConfigureTestPeer(isHost: true, myId: 0, hostOut, hostTx);
        host.Wo139PeerConnectForTest(1, "Moose");

        // Host buys a 3h bathhouse service -> save=false (Host's own bathhouse graph runs save_hry.xml)
        await host.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeBegan, 7, 3f, 2));
        var sent = ReadSleepVoteMessages(hostOut);
        Assert.Single(sent);
        Assert.Equal((byte)1, sent[0].Target);
        Assert.True(Wo140Text.TryBegin(sent[0].Msg.Text, out string kind, out byte asker, out float hours, out bool save));
        Assert.Equal(("bath", (byte)0, 3f, false), (kind, asker, hours, save));

        // Joiner receives Host's bath SleepBegin without prior vote -> shows toast and marks shared skip!
        var joinerOut = new MemoryStream();
        var joinerTx = new RecordingTransport();
        var joiner = new GameBridge(new ClientConfig());
        joiner.Wo140ConfigureTestPeer(isHost: false, myId: 1, joinerOut, joinerTx);
        joiner.Wo139PeerConnectForTest(0, "HostHenry");

        // First verify an unaccepted normal "sleep" Begin is STILL refused
        var unacceptedSleep = new LootMsg(Protocol.SleepBegin, 0x00000009u, Wo140Text.Begin("sleep", 0, 6f, false))
            .BuildUp(Protocol.SleepVoteUp, 1)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await joiner.Wo140InboundFrameForTestAsync(0, unacceptedSleep);
        Assert.False(joiner.Wo140SharedSkipForTest);

        // Now deliver Host's bath SleepBegin -> accepted immediately!
        var bathBody = new LootMsg(Protocol.SleepBegin, sent[0].Msg.Tok, sent[0].Msg.Text)
            .BuildUp(Protocol.SleepVoteUp, 1)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await joiner.Wo140InboundFrameForTestAsync(0, bathBody);
        Assert.True(joiner.Wo140SharedSkipForTest);
        Assert.False(joiner.Wo140SaveAfterForTest);
        Assert.Contains(joinerTx.LuaCalls, c => c.Contains("HostHenry is using the bathhouse (3h)...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Host_unable_to_start_bath_skip_cancels_wake_hold_clears_save_after_and_validates_asker_and_hours()
    {
        var joinerOut = new MemoryStream();
        var joinerTx = new RecordingTransport();
        var joiner = new GameBridge(new ClientConfig());
        joiner.Wo140ConfigureTestPeer(isHost: false, myId: 1, joinerOut, joinerTx);
        joiner.Wo139PeerConnectForTest(0, "HostHenry");

        await joiner.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeBegan, 7, 2f, 2));
        Assert.False(joiner.Wo140HostWokeNotedForTest);
        var joinerSent = ReadSleepVoteMessages(joinerOut);
        Assert.Single(joinerSent);

        var hostOut = new MemoryStream();
        var hostTx = new RecordingTransport();
        var host = new GameBridge(new ClientConfig());
        host.Wo140ConfigureTestPeer(isHost: true, myId: 0, hostOut, hostTx);
        host.Wo139PeerConnectForTest(1, "Moose");
        host.Wo139PeerConnectForTest(2, "Partner2");

        // 1. Spoofed asker != src (src=1 claiming asker=2) is rejected immediately
        var spoofedBody = new LootMsg(Protocol.SleepBegin, 0x00020001u, "bath 2 2 1")
            .BuildUp(Protocol.SleepVoteUp, Protocol.JoinTargetHost)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await host.Wo140InboundFrameForTestAsync(1, spoofedBody);
        Assert.False(host.Wo140SharedSkipForTest);
        Assert.False(host.Wo140SaveAfterForTest);
        Assert.Empty(ReadSleepVoteMessages(hostOut));

        // 2. Host is in a cutscene / fader dialogue / downed so both id=1 and fallback id=14 return 0 (cannot start C_SkipTime)
        host.Wo140SetStartForTest(static (_, _) => (byte)0);
        var validBathBody = new LootMsg(Protocol.SleepBegin, joinerSent[0].Msg.Tok, joinerSent[0].Msg.Text)
            .BuildUp(Protocol.SleepVoteUp, Protocol.JoinTargetHost)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await host.Wo140InboundFrameForTestAsync(1, validBathBody);

        // Host did NOT arm shared skip, did NOT leak _w140SaveAfter, did NOT relay to Partner2, and sent SleepCancel("woke") to Moose!
        Assert.False(host.Wo140SharedSkipForTest);
        Assert.False(host.Wo140SaveAfterForTest);
        var hostReplies = ReadSleepVoteMessages(hostOut);
        Assert.Single(hostReplies);
        Assert.Equal((byte)1, hostReplies[0].Target);
        Assert.Equal(Protocol.SleepCancel, hostReplies[0].Msg.Kind);
        Assert.True(Wo140Text.TryCancel(hostReplies[0].Msg.Text, out string why, out byte cAsker) && why == "woke" && cAsker == 1);

        // Delivering SleepCancel("woke") to Joiner marks host woke so Joiner NEVER sits behind WO-192's 15s black fader screen!
        var cancelBody = new LootMsg(Protocol.SleepCancel, hostReplies[0].Msg.Tok, hostReplies[0].Msg.Text)
            .BuildUp(Protocol.SleepVoteUp, 1)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await joiner.Wo140InboundFrameForTestAsync(0, cancelBody);
        Assert.True(joiner.Wo140HostWokeNotedForTest);

        // 3. Host loading (Wo136Holding) also immediately sends SleepCancel("woke") without arming _w140SaveAfter
        hostOut.SetLength(0);
        host.Wo140SetStartForTest(static (_, _) => (byte)1);
        host.Wo140SetLoadingForTest(true);
        await host.Wo140InboundFrameForTestAsync(1, validBathBody);
        Assert.False(host.Wo140SharedSkipForTest);
        Assert.False(host.Wo140SaveAfterForTest);
        var loadingReplies = ReadSleepVoteMessages(hostOut);
        Assert.Single(loadingReplies);
        Assert.Equal(Protocol.SleepCancel, loadingReplies[0].Msg.Kind);

        // 4. Host in trespass/combat where id=1 returns 0 but fallback id=14 returns 1 DOES start and arm shared skip + saveAfter
        hostOut.SetLength(0);
        host.Wo140SetLoadingForTest(false);
        host.Wo140SetStartForTest(static (id, _) => id == 14 ? (byte)1 : (byte)0);
        await host.Wo140InboundFrameForTestAsync(1, validBathBody);
        Assert.True(host.Wo140SharedSkipForTest);
        Assert.True(host.Wo140SaveAfterForTest);

        // 5. Even while W140SharedSkip is in its post-wake tail, a new local bathhouse skip (id=7) is NOT swallowed!
        await host.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeEnded, 0, 0f, 0));
        Assert.True(host.Wo140SharedSkipForTest);   // 15s post-wake window
        hostOut.SetLength(0);
        await host.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindState, Wo140Frame.EdgeBegan, 7, 1f, 2));
        var postWakeSent = ReadSleepVoteMessages(hostOut);
        Assert.Equal(2, postWakeSent.Count);   // sent to both Moose (1) and Partner2 (2)
        Assert.All(postWakeSent, m => Assert.Equal(Protocol.SleepBegin, m.Msg.Kind));
    }

    private sealed class RecordingTransport : IGameTransport
    {
        public List<string> LuaCalls { get; } = [];
        public string Name => "recording";
        public int RoundTripsPerStateRead => 0;
        public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) => Task.FromResult<PlayerState?>(null);
        public Task ExecuteAsync(string lua, CancellationToken ct = default) { LuaCalls.Add(lua); return Task.CompletedTask; }
        public Task ExecuteNowAsync(string lua, CancellationToken ct = default) { LuaCalls.Add(lua); return Task.CompletedTask; }
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
