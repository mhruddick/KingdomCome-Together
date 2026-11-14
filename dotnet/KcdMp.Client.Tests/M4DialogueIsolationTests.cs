// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class M4DialogueIsolationTests
{
    [Theory]
    [InlineData("Barbora.open_world.shop.bribe_haggle_negotiation_vyjednavani_smlouvani_podplaceni.smlouvani_v_obchode_muz", "smlouvani_neuspesne", false, false, "haggle-local")]
    [InlineData("Barbora.open_world.shop.bribe_haggle_negotiation_vyjednavani_smlouvani_podplaceni.smlouvani_v_obchode_muz", "add_haggle_denial", true, false, "haggle-local")]
    [InlineData("Barbora.open_world.shop.bribeHaggleNegotiation.adapter", "out_fail", true, false, "haggle-local")]
    [InlineData("Barbora.open_world.lazne_a_lazebnice.bathhouse.abbess_dialog", "zaplaceni_sluzby", false, false, "bathhouse-local")]
    [InlineData("Barbora.open_world.lazne_a_lazebnice.bathhouse.abbess_dialog", "zaplaceni_sluzby", true, false, "bathhouse-local")]
    [InlineData("Barbora.trosecko.bathhouse.abbess_dialog.objednani_sluzeb", "zaplaceni_sluzby", false, false, "bathhouse-local")]
    [InlineData("Barbora.trosecko.bathhouse.abbess_dialog.objednani_sluzeb", "zaplaceni_sluzby", true, false, "bathhouse-local")]
    [InlineData("Barbora.kutnohorsko.bathhouse.abbess_dialog.objednani_sluzeb", "zaplaceni_sluzby", false, false, "bathhouse-local")]
    [InlineData("Barbora.kutnohorsko.bathhouse.abbess_dialog.objednani_sluzeb", "zaplaceni_sluzby", true, false, "bathhouse-local")]
    [InlineData("Barbora.open_world.nextnextgenpunishment.svepomoc.svepomoc__dialog_civilista_muz", "fine_paid", true, false, "punishment-local")]
    [InlineData("Barbora.utils.crime.punishment.arrestdialogue.straze__zatykaci_dialog", "punish", false, false, "punishment-local")]
    [InlineData("Barbora.open_world.frisk.frisk_guard_dialog", "frisk_clean", true, false, "punishment-local")]
    [InlineData("Barbora.kutnohorsko.zikmunduvTabor.zatykani_v_zikmundove_tabore.arrest_node", "surrender", true, false, "punishment-local")]
    [InlineData("Barbora.klaster.crime.klaster__zatykaci_dialog__straz", "confiscate", true, false, "punishment-local")]
    [InlineData("Barbora.open_world.shop.nakupovani_z_dialogu_muz", "reputation_up", false, false, "open-world-solo")]
    [InlineData("Barbora.open_world.shop.nakupovani_z_dialogu_muz", "reputation_up", true, true, "open-world-listened")]
    [InlineData("Barbora.open_world.reakce_na_stav_hrace.npc_zdravi_hrace_chatem__straz", "rep_down", false, false, "open-world-solo")]
    [InlineData("Barbora.open_world.reakce_na_stav_hrace.npc_zdravi_hrace_chatem__straz", "rep_down", true, true, "open-world-listened")]
    [InlineData("Barbora.trosecko.zbranePanaSemina.h.na_semine.odjezd_ze_semina", "jindra_ma_nasledovat", false, true, "quest")]
    [InlineData("Barbora.trosecko.lazne__zelejov.hlavni_dialog", "step_done", false, true, "quest")]
    [InlineData("Barbora.kutnohorsko.budovaniLazni.otevreni_lazni", "quest_advance", false, true, "quest")]
    [InlineData("Barbora.kutnohorsko.sabotazLazni.hlavni_dialog", "sabotaz_hotova", false, true, "quest")]
    [InlineData("Barbora.kutnohorsko.hledaniLichtenstejna.lazebnice_dialog", "stopa_nalezena", false, true, "quest")]
    [InlineData("Barbora.kutnohorsko.brushes_with_death_kutnohorsko.maliruvLek.objective_ziskej_ricni_perlu.s_vudcem_dezerteru__haggle", "smlouvani_uspesne", false, true, "quest")]
    [InlineData("Barbora.klaster.tajemstviKlastera.rozmluva", "opat_souhlasi", false, true, "quest")]
    public void ShouldReplayDialogueOutPort_classifies_haggle_bathhouse_punishment_open_world_and_story_dialogues(
        string path, string port, bool hasListeners, bool expectedReplay, string expectedReason)
    {
        bool actual = Wo200Rules.ShouldReplayDialogueOutPort(path, port, hasListeners, out string reason);
        Assert.Equal(expectedReplay, actual);
        Assert.Equal(expectedReason, reason);
    }

    private static List<(byte Type, LootMsg Msg)> ReadSentJoinMessages(MemoryStream ms)
    {
        var list = new List<(byte, LootMsg)>();
        var buf = ms.ToArray();
        int p = 0;
        while (p + 3 <= buf.Length)
        {
            byte type = buf[p];
            int len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 1, 2));
            if (p + 3 + len > buf.Length) break;
            if (len >= Protocol.JoinHeaderLen && LootMsg.TryDecode(buf.AsSpan(p + 3 + Protocol.JoinHeaderLen, len - Protocol.JoinHeaderLen), out var m))
                list.Add((type, m));
            p += 3 + len;
        }
        return list;
    }

    [Fact]
    public async Task Joiner_solo_haggle_bathhouse_and_open_world_stay_local_while_listened_and_quest_dialogues_replay()
    {
        var outMs = new MemoryStream();
        var tx = new NoopTransport();
        var gb = new GameBridge(new ClientConfig());
        gb.Wo200ConfigureTestJoiner(outMs, tx);

        // 1. Solo haggle -> NOT replayed to Host, and closure-marked State changes stay held locally
        const string hagglePath = "Barbora.open_world.shop.bribe_haggle_negotiation_vyjednavani_smlouvani_podplaceni.smlouvani_v_obchode_muz";
        gb.Wo200EngineLineForTest($"New dialogue '{hagglePath} (normal - Id: 101) (Haggle)' is starting. Params: souls = 'Ex: Dude; Ex: ttkc_man_11' forced = false");
        await gb.Wo200CaptureForTestAsync(0x80000101u, hagglePath, "add_haggle_denial");
        Assert.Empty(ReadSentJoinMessages(outMs));
        Assert.True(gb.Wo200HoldLocalForTest(new QuestChange(1, QuestChange.FClosure | QuestChange.FNewOk, 0, 1, "Set", "State", "Barbora.trosecko.q.s", 18, 0x80000101u)));
        gb.Wo200EngineLineForTest("[ID: 101] Dialog ending [Ex0: Dude Ex1: ttkc_man_11 state: CLEANUP flags: 1]");

        // 2. Bathhouse service dialogue (even with an active listener!) -> NOT replayed to Host
        const string bathPath = "Barbora.open_world.lazne_a_lazebnice.bathhouse.abbess_dialog";
        gb.Wo200EngineLineForTest($"New dialogue '{bathPath} (normal - Id: 102) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: tzel_bathmaid_1' forced = false");
        gb.Wo200SetActiveListenersForTest(102, 0);
        await gb.Wo200CaptureForTestAsync(0x80000102u, bathPath, "zaplaceni_sluzby");
        Assert.Empty(ReadSentJoinMessages(outMs));
        gb.Wo200EngineLineForTest("[ID: 102] Dialog ending [Ex0: Dude Ex1: tzel_bathmaid_1 state: CLEANUP flags: 1]");

        // 3. Solo open_world dialogue (no listener) -> NOT replayed to Host
        const string openWorldPath = "Barbora.open_world.shop.nakupovani_z_dialogu_muz";
        gb.Wo200EngineLineForTest($"New dialogue '{openWorldPath} (normal - Id: 103) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: ttac_man_4' forced = false");
        await gb.Wo200CaptureForTestAsync(0x80000103u, openWorldPath, "reconcile_rep");
        Assert.Empty(ReadSentJoinMessages(outMs));
        gb.Wo200EngineLineForTest("[ID: 103] Dialog ending [Ex0: Dude Ex1: ttac_man_4 state: CLEANUP flags: 1]");

        // 4. Open_world dialogue where listener joined and LEFT BEFORE Dialog ending -> NOT replayed to Host
        gb.Wo200EngineLineForTest($"New dialogue '{openWorldPath} (normal - Id: 104) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: ttac_man_4' forced = false");
        gb.Wo200SetActiveListenersForTest(104, 0);
        gb.Wo200SetActiveListenersForTest(104);   // listener left before ending
        gb.Wo200EngineLineForTest("[ID: 104] Dialog ending [Ex0: Dude Ex1: ttac_man_4 state: CLEANUP flags: 1]");
        await gb.Wo200CaptureForTestAsync(0x80000104u, openWorldPath, "reconcile_rep");
        Assert.Empty(ReadSentJoinMessages(outMs));

        // 5. Open_world dialogue WITH listener still attached at Dialog ending (capture polled right after Dialog ending!) -> REPLAYED to Host!
        gb.Wo200EngineLineForTest($"New dialogue '{openWorldPath} (normal - Id: 105) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: ttac_man_4' forced = false");
        gb.Wo200SetActiveListenersForTest(105, 0);
        gb.Wo200EngineLineForTest("[ID: 105] Dialog ending [Ex0: Dude Ex1: ttac_man_4 state: CLEANUP flags: 1]");
        await gb.Wo200CaptureForTestAsync(0x80000105u, openWorldPath, "reconcile_rep");
        var sentAfterListened = ReadSentJoinMessages(outMs).Where(x => x.Type == Protocol.QuestAskUp && x.Msg.Kind == Protocol.QuestAskOutPortReplay).ToList();
        Assert.Single(sentAfterListened);
        Assert.Contains(openWorldPath, sentAfterListened[0].Msg.Text);

        // 6. Solo story/quest dialogue (no listener) -> REPLAYED to Host as normal!
        const string questPath = "Barbora.trosecko.zbranePanaSemina.h.na_semine.odjezd_ze_semina";
        gb.Wo200EngineLineForTest($"New dialogue '{questPath} (normal - Id: 106) (ForceDialogNode)' is starting. Params: souls = 'Ex: Dude; Ex: tsem_seminsr' forced = false");
        await gb.Wo200CaptureForTestAsync(0x80000106u, questPath, "jindra_souhlasi");
        var allReplays = ReadSentJoinMessages(outMs).Where(x => x.Type == Protocol.QuestAskUp && x.Msg.Kind == Protocol.QuestAskOutPortReplay).ToList();
        Assert.Equal(2, allReplays.Count);
        Assert.Contains(questPath, allReplays[1].Msg.Text);
    }

    [Fact]
    public async Task Runtime_bathhouse_frisk_and_unrelated_subsequent_listened_talk_never_leak_solo_out_ports()
    {
        var outMs = new MemoryStream();
        var tx = new NoopTransport();
        var gb = new GameBridge(new ClientConfig());
        gb.Wo200ConfigureTestJoiner(outMs, tx);

        // 1. Runtime instantiated Kuttenberg bathhouse path with active listener -> stays local
        const string runtimeBath = "Barbora.kutnohorsko.bathhouse.abbess_dialog.objednani_sluzeb";
        gb.Wo200EngineLineForTest($"New dialogue '{runtimeBath} (normal - Id: 201) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: kh_bathmaid_2' forced = false");
        gb.Wo200SetActiveListenersForTest(201, 0);
        await gb.Wo200CaptureForTestAsync(0x80000201u, runtimeBath, "zaplaceni_sluzby");
        Assert.Empty(ReadSentJoinMessages(outMs));
        gb.Wo200EngineLineForTest("[ID: 201] Dialog ending [Ex0: Dude Ex1: kh_bathmaid_2 state: CLEANUP flags: 1]");

        // 2. Auto-joined guard frisk dialogue -> stays local
        const string friskPath = "Barbora.open_world.frisk.frisk_guard_dialog";
        gb.Wo200EngineLineForTest($"New dialogue '{friskPath} (normal - Id: 202) (ForceDialogNode)' is starting. Params: souls = 'Ex: Dude; Ex: kh_guard_3' forced = true");
        gb.Wo200SetActiveListenersForTest(202, 0);
        await gb.Wo200CaptureForTestAsync(0x80000202u, friskPath, "frisk_surrender");
        Assert.Empty(ReadSentJoinMessages(outMs));
        gb.Wo200EngineLineForTest("[ID: 202] Dialog ending [Ex0: Dude Ex1: kh_guard_3 state: CLEANUP flags: 1]");

        // 3. Solo open_world dialogue 203 ends WITHOUT listeners, then unrelated open_world dialogue 204 starts WITH a listener,
        //    and THEN delayed out-port capture from solo dialogue 203 arrives -> must NOT misclassify 203 as listened!
        const string soloShopPath = "Barbora.open_world.shop.nakupovani_z_dialogu_muz";
        const string otherListenedPath = "Barbora.open_world.reakce_na_stav_hrace.npc_zdravi_hrace_chatem__straz";
        gb.Wo200EngineLineForTest($"New dialogue '{soloShopPath} (normal - Id: 203) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: ttac_man_4' forced = false");
        gb.Wo200EngineLineForTest("[ID: 203] Dialog ending [Ex0: Dude Ex1: ttac_man_4 state: CLEANUP flags: 1]");

        gb.Wo200EngineLineForTest($"New dialogue '{otherListenedPath} (normal - Id: 204) (ForceDialogNode)' is starting. Params: souls = 'Ex: Dude; Ex: ttac_guard_1' forced = true");
        gb.Wo200SetActiveListenersForTest(204, 0);

        // Delayed out-port from solo talk 203 -> stays local!
        await gb.Wo200CaptureForTestAsync(0x80000203u, soloShopPath, "reputation_up");
        Assert.Empty(ReadSentJoinMessages(outMs));

        // Out-port from actually-listened talk 204 -> replays!
        await gb.Wo200CaptureForTestAsync(0x80000204u, otherListenedPath, "rep_down");
        var sent = ReadSentJoinMessages(outMs).Where(x => x.Type == Protocol.QuestAskUp && x.Msg.Kind == Protocol.QuestAskOutPortReplay).ToList();
        Assert.Single(sent);
        Assert.Contains(otherListenedPath, sent[0].Msg.Text);
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
