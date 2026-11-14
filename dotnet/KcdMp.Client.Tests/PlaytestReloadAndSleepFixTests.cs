// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class PlaytestReloadAndSleepFixTests
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
    public async Task In_world_joiner_reload_drops_wo216_wo217_wo218_roles_to_zero_and_arms_w136_hold_before_load_command()
    {
        var joinerOut = new MemoryStream();
        var joinerTx = new RecordingTransport();
        var joiner = new GameBridge(new ClientConfig());
        joiner.Wo140ConfigureTestPeer(isHost: false, myId: 1, joinerOut, joinerTx);
        joiner.Wo139PeerConnectForTest(0, "HostHenry");

        // Before reload: joiner is in the host's world with active native roles (role == 2)
        Assert.Equal((byte)2, joiner.Wo216RoleForTest);
        Assert.Equal((byte)2, joiner.Wo217RoleForTest);
        Assert.Equal((byte)2, joiner.Wo218RoleForTest);

        // Pre-load barrier executes synchronously BEFORE KCD2MP_Wo124LoadGame
        await joiner.Wo136PrepareBeforeLoadAsync("join");

        Assert.Equal((byte)0, joiner.Wo216RoleForTest);
        Assert.Equal((byte)0, joiner.Wo217RoleForTest);
        Assert.Equal((byte)0, joiner.Wo218RoleForTest);
        Assert.Contains(joinerTx.Calls, c => c.Contains("KCD2MP_W136Hold(true, 240, \"agent-join\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sleep_prompt_quotes_vote_id_for_float32_lua_and_accepts_native_f11_f12_key_fallback_once()
    {
        Assert.True(Wo140Frame.TryParse(new byte[] { Wo140Frame.KindKey, 1 }, out var yesKey));
        Assert.Equal((Wo140Frame.KindKey, (byte)1), (yesKey.Kind, yesKey.Id));
        Assert.True(Wo140Frame.TryParse(new byte[] { Wo140Frame.KindKey, 0 }, out var noKey));
        Assert.Equal((Wo140Frame.KindKey, (byte)0), (noKey.Kind, noKey.Id));
        Assert.False(Wo140Frame.TryParse(new byte[] { Wo140Frame.KindKey, 2 }, out _));

        var joinerOut = new MemoryStream();
        var joinerTx = new RecordingTransport();
        var joiner = new GameBridge(new ClientConfig());
        joiner.Wo140ConfigureTestPeer(isHost: false, myId: 1, joinerOut, joinerTx);
        joiner.Wo139PeerConnectForTest(2, "Fahrice");

        // Vote id > 2^24 (0x02000001 == 33554433, which float32 Lua would round to 33554432 if unquoted)
        uint voteId = 0x02000001u;
        Assert.Equal(33554433u, voteId);
        byte[] askPayload = new LootMsg(Protocol.SleepAsk, voteId, Wo140Text.Ask("sleep", 2, save: true))
            .BuildUp(Protocol.SleepVoteUp, Protocol.JoinTargetHost)
            .AsSpan(3 + Protocol.JoinHeaderLen).ToArray();
        await joiner.Wo140InboundFrameForTestAsync(0, askPayload);

        Assert.Contains(joinerTx.Calls, c => c.Contains("KCD2MP_W140Prompt(\"33554433\"", StringComparison.Ordinal));

        // Native F11 fallback answers YES and hides the prompt; a duplicate key frame is a no-op
        await joiner.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindKey, 0, 1, 0, 0));
        await joiner.Wo140DllFrameForTestAsync(new Wo140Frame(Wo140Frame.KindKey, 0, 1, 0, 0));

        var sent = ReadSleepVoteMessages(joinerOut);
        Assert.Single(sent);
        Assert.Equal(Protocol.JoinTargetHost, sent[0].Target);
        Assert.Equal(Protocol.SleepAnswer, sent[0].Msg.Kind);
        Assert.Equal(voteId, sent[0].Msg.Tok);
        Assert.True(Wo140Text.TryAnswer(sent[0].Msg.Text, out string ans, out byte asker));
        Assert.Equal(("yes", (byte)2), (ans, asker));
        Assert.Contains(joinerTx.Calls, c => c.Contains("KCD2MP_W140PromptHide(\"33554433\")", StringComparison.Ordinal));
    }

    [Fact]
    public void KeybindPak_merges_overlays_prompt_actionmap_for_f11_and_f12_without_changing_action_counts()
    {
        const string vanillaProfile = "<profile version=\"0\">\r\n\t<actionmap name=\"interaction\" priority=\"pure_include\" exclusivity=\"0\">\r\n\t\t<action name=\"use\" onPress=\"1\" keyboard=\"_keybinds_ref_\" />\r\n\t</actionmap>\r\n</profile>\r\n";
        const string vanillaSupers = "<keybinds>\r\n\t<superaction name=\"use\">\r\n\t\t<action name=\"use\" map=\"interaction\" />\r\n\t\t<control input=\"e\" controller=\"keyboard\" />\r\n\t</superaction>\r\n</keybinds>\r\n";

        string? p1 = KeybindPak.MergeProfile(vanillaProfile, KeybindPak.PatchBody(KeybindPak.EmbeddedPatch("defaultProfile.interaction.xml")), out _);
        Assert.NotNull(p1);
        string? p2 = KeybindPak.MergePromptMap(p1!, KeybindPak.PatchBody(KeybindPak.EmbeddedPatch("defaultProfile.prompt.xml")), out string why);
        Assert.NotNull(p2);
        Assert.Equal("ok", why);
        Assert.Contains("<actionmap name=\"kcd2mp_prompt\" priority=\"overlays\" exclusivity=\"0\">", p2);
        Assert.Contains("name=\"kcd2mp_prompt_yes\"", p2);
        Assert.Contains("name=\"kcd2mp_prompt_no\"", p2);
        Assert.Null(KeybindPak.MergePromptMap(p2!, KeybindPak.PatchBody(KeybindPak.EmbeddedPatch("defaultProfile.prompt.xml")), out _));

        string? s = KeybindPak.MergeSuperactions(vanillaSupers, KeybindPak.PatchBody(KeybindPak.EmbeddedPatch("keybindSuperactions.append.xml")), out _);
        Assert.NotNull(s);
        foreach (var (sup, act) in KeybindPak.PromptSuperactions)
        {
            s = KeybindPak.MergeIntoSuperaction(s!, sup, KeybindPak.PromptLine(act), [act], out string whyS);
            Assert.NotNull(s);
            Assert.Equal("ok", whyS);
        }
        Assert.Equal("f11", KeybindPak.BoundKey(s!, "kcd2mp_dice_bank"));
        Assert.Equal("f12", KeybindPak.BoundKey(s!, "kcd2mp_dice_yield"));
        Assert.Contains("<action name=\"kcd2mp_prompt_yes\" map=\"kcd2mp_prompt\" />", s!);
        Assert.Contains("<action name=\"kcd2mp_prompt_no\" map=\"kcd2mp_prompt\" />", s!);
    }

    private sealed class RecordingTransport : IGameTransport
    {
        public List<string> Calls { get; } = [];
        public string Name => "recording";
        public int RoundTripsPerStateRead => 0;
        public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) => Task.FromResult<PlayerState?>(null);
        public Task ExecuteAsync(string lua, CancellationToken ct = default) { Calls.Add(lua); return Task.CompletedTask; }
        public Task ExecuteNowAsync(string lua, CancellationToken ct = default) { Calls.Add(lua); return Task.CompletedTask; }
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
