// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using Spec = KcdMp.Client.Tests.WhsSaveTests.Spec;
using Item = KcdMp.Client.Tests.WhsSaveTests.Item;

namespace KcdMp.Client.Tests;

public class M1PersonalStashTests
{
    private const string Apple = "0a0a0a0a-0000-0000-0000-00000000a001";
    private const string Letter = "0b0b0b0b-0000-0000-0000-00000000b002"; // quest item in WhsSaveTests.Quest
    private const string HomeKey = "0c0c0c0c-0000-0000-0000-00000000c099";
    private const string BackupsDir = @"d:\Code\kcd2\kct-save-backups";

    private static byte[] Tlv(ushort tag, byte[] payload)
    {
        var b = new byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(b, 6);
        return b;
    }

    [Fact]
    public void Populated_joiner_stash_replaces_host_stash_strips_quest_items_keeps_world_stashes_and_merges_keys()
    {
        var (hs, js) = WhsSaveTests.Pair();
        // Host has two world stashes (e.g. a grave and a chest diff) plus 2 items in host's personal stash (including a bound key)
        hs.WorldStashes =
        [
            (0UL, 0x4C0EA10863D853EDUL, Tlv(0x0005, Tlv(0x0000, new byte[26]))),
            (0UL, 0x4810D69F3B706696UL, Tlv(0x0004, [1, 2, 3, 4, 5])),
        ];
        hs.PersonalStashItems =
        [
            new("11110000-0000-0000-0000-0000000000aa", Apple, 12),
            new("11110000-0000-0000-0000-0000000000bb", HomeKey, 1),
        ];
        hs.Keys.Add((WhsSave.HenrySoul, "11110000-0000-0000-0000-0000000000bb")); // key sitting in host's personal stash -> must be dropped!

        // Joiner has a different world stash (must NOT replace host's world stashes) and 3 personal stash items (1 quest item, 1 key, 1 stack)
        js.WorldStashes =
        [
            (0UL, 0x9999999999999999UL, Tlv(0x0002, [0x42])),
        ];
        js.PersonalStashItems =
        [
            new("22220000-0000-0000-0000-0000000000aa", Apple, 7),
            new("22220000-0000-0000-0000-0000000000bb", Letter, 1), // quest item -> must be stripped!
            new("22220000-0000-0000-0000-0000000000cc", HomeKey, 1),
        ];
        js.Keys.Add((WhsSave.HenrySoul, "22220000-0000-0000-0000-0000000000cc")); // key sitting in joiner's personal stash -> must be kept!

        byte[] hostFile = WhsSaveTests.File(hs);
        byte[] joinFile = WhsSaveTests.File(js);

        var parts = WhsSave.PartsFromFile(joinFile, WhsSave.HenryParts.OriginSnapshot);
        Assert.True(parts.Extensions.ContainsKey(WhsSave.PersonalStashExtensionKey));
        Assert.NotEmpty(parts.Extensions[WhsSave.PersonalStashExtensionKey]);

        // Round-trip through .hblk v2
        var roundTripped = WhsSave.ParseBlock(WhsSave.SerializeBlock(parts));
        var res = WhsSave.SpliceParts(hostFile, roundTripped, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);

        Assert.True(WhsSave.Verify(res.File).Ok);
        Assert.Empty(WhsSave.CheckParts(hostFile, roundTripped, res.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.Check(hostFile, joinFile, res.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));

        Assert.Equal(2, res.Report.PersonalStashItemsJoiner);
        Assert.Equal(["loveLetter"], res.Report.PersonalStashQuestItemsRemoved);
        Assert.Equal(2, res.Report.KeysHostDropped); // 1 in host inv + 1 in host personal stash
        Assert.Equal(2, res.Report.KeysJoinerAdded); // 1 in joiner inv + 1 in joiner personal stash

        var outRaw = WhsSave.Inflate(res.File).Raw;
        var stashItems = WhsSave.DecodePersonalStashItems(outRaw);
        Assert.Equal(["22220000-0000-0000-0000-0000000000aa", "22220000-0000-0000-0000-0000000000cc"],
                     stashItems.Select(i => i.Instance).ToArray());
        Assert.Equal("amount=7", stashItems[0].Params);
    }

    [Fact]
    public void Empty_joiner_stash_v1_block_fresh_default_and_quest_only_stash_all_leave_chest_cleanly_empty_with_no_0000_node()
    {
        var (hs, js) = WhsSaveTests.Pair();
        hs.WorldStashes = [(0UL, 0x4C0EA10863D853EDUL, Tlv(0x0002, [0x01]))];
        hs.PersonalStashItems =
        [
            new("11110000-0000-0000-0000-000000000001", Apple, 50),
            new("11110000-0000-0000-0000-000000000002", HomeKey, 1),
        ];
        byte[] hostFile = WhsSaveTests.File(hs);

        // 1. Joiner with empty personal stash -> Extensions["personal_stash"] is explicitly Array.Empty<byte>()
        js.PersonalStashItems = [];
        byte[] emptyJoinFile = WhsSaveTests.File(js);
        var emptyParts = WhsSave.PartsFromFile(emptyJoinFile, WhsSave.HenryParts.OriginSave);
        Assert.True(emptyParts.Extensions.TryGetValue(WhsSave.PersonalStashExtensionKey, out var emptyExt));
        Assert.Empty(emptyExt!);

        var emptyRes = WhsSave.SpliceParts(hostFile, emptyParts, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.Empty(WhsSave.CheckParts(hostFile, emptyParts, emptyRes.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.ExtractPersonalStash(WhsSave.Inflate(emptyRes.File).Raw));
        Assert.Equal(0, emptyRes.Report.PersonalStashItemsJoiner);

        // 2. Legacy v1 .hblk (no "personal_stash" key in Extensions) -> MUST still drop host's personal stash!
        var v1Bytes = WhsSave.SerializeBlockV1(emptyParts);
        var v1Parts = WhsSave.ParseBlock(v1Bytes);
        Assert.False(v1Parts.Extensions.ContainsKey(WhsSave.PersonalStashExtensionKey));

        var v1Res = WhsSave.SpliceParts(hostFile, v1Parts, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.Empty(WhsSave.CheckParts(hostFile, v1Parts, v1Res.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.ExtractPersonalStash(WhsSave.Inflate(v1Res.File).Raw));

        // 3. FreshDefaultParts -> MUST also drop host's personal stash!
        var freshParts = WhsSave.FreshDefaultParts(WhsSave.Inflate(hostFile).Raw);
        var freshRes = WhsSave.SpliceParts(hostFile, freshParts, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.Empty(WhsSave.CheckParts(hostFile, freshParts, freshRes.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.ExtractPersonalStash(WhsSave.Inflate(freshRes.File).Raw));

        // 4. Joiner whose personal stash holds ONLY quest items -> 0 items remain in 0x0005, so 0x0000 node is omitted completely!
        js.PersonalStashItems = [new("22220000-0000-0000-0000-000000000099", Letter, 1)];
        var questOnlyParts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSave);
        Assert.NotEmpty(questOnlyParts.Extensions[WhsSave.PersonalStashExtensionKey]);

        var questOnlyRes = WhsSave.SpliceParts(hostFile, questOnlyParts, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.Equal(0, questOnlyRes.Report.PersonalStashItemsJoiner);
        Assert.Equal(["loveLetter"], questOnlyRes.Report.PersonalStashQuestItemsRemoved);
        Assert.Empty(WhsSave.CheckParts(hostFile, questOnlyParts, questOnlyRes.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.ExtractPersonalStash(WhsSave.Inflate(questOnlyRes.File).Raw));
    }

    [Fact]
    public void CheckParts_catches_leaked_host_stash_or_modified_world_stashes()
    {
        var (hs, js) = WhsSaveTests.Pair();
        hs.WorldStashes = [(0UL, 0x4C0EA10863D853EDUL, Tlv(0x0002, [0x01]))];
        hs.PersonalStashItems = [new("11110000-0000-0000-0000-000000000001", Apple, 10)];
        js.PersonalStashItems = [];

        byte[] hostFile = WhsSaveTests.File(hs);
        var joinParts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSave);
        var valid = WhsSave.SpliceParts(hostFile, joinParts, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip).File;
        Assert.Empty(WhsSave.CheckParts(hostFile, joinParts, valid, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));

        // Passing unspliced host stash into output must fail CheckParts on personal stash
        var raw = WhsSave.Inflate(valid);
        var hRaw = WhsSave.Inflate(hostFile).Raw;
        var hostStashPay = WhsSave.Payload(hRaw, WhsSave.PathGet(hRaw, 0x01F4, 0x01F8, 0x7302, 0x0006)!.Value);
        var leakedRaw = WhsSave.Replace(raw.Raw, WhsSave.PathNodes(raw.Raw, 0x01F4, 0x01F8, 0x7302, 0x0006), hostStashPay);
        var leakedFile = WhsSave.Deflate(raw.DescBytes, leakedRaw, raw.FooterTail);
        var fails1 = WhsSave.CheckParts(hostFile, joinParts, leakedFile, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.Contains(fails1, f => f.Contains("personal stash", StringComparison.Ordinal));

        // Tampering with a non-personal world stash record must fail CheckParts on non-personal stashes
        var emptyWorldRaw = WhsSave.Replace(raw.Raw, WhsSave.PathNodes(raw.Raw, 0x01F4, 0x01F8, 0x7302, 0x0006), []);
        var emptyWorldFile = WhsSave.Deflate(raw.DescBytes, emptyWorldRaw, raw.FooterTail);
        var fails2 = WhsSave.CheckParts(hostFile, joinParts, emptyWorldFile, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
        Assert.Contains(fails2, f => f.Contains("non-personal stashes", StringComparison.Ordinal));
    }

    [Fact]
    public void PersonalStashIds_set_supports_multiple_personal_stash_records()
    {
        var dlcId = (Lo: 0UL, Hi: 0x04BA999988887777UL);
        lock (WhsSave.PersonalStashIds)
        {
            Assert.Contains(WhsSave.PersonalStashId, WhsSave.PersonalStashIds);
            WhsSave.PersonalStashIds.Add(dlcId);
        }
        try
        {
            var (hs, js) = WhsSaveTests.Pair();
            var dlcItemPayload = new List<byte>();
            dlcItemPayload.AddRange(new Guid("33330000-0000-0000-0000-000000000001").ToByteArray());
            dlcItemPayload.AddRange(new byte[4]);
            dlcItemPayload.AddRange(new Guid(Apple).ToByteArray());
            hs.WorldStashes = [(dlcId.Lo, dlcId.Hi, Tlv(0x0005, Tlv(0x0001, dlcItemPayload.ToArray())))];

            var joinItemPayload = new List<byte>();
            joinItemPayload.AddRange(new Guid("44440000-0000-0000-0000-000000000002").ToByteArray());
            joinItemPayload.AddRange(new byte[4]);
            joinItemPayload.AddRange(new Guid(HomeKey).ToByteArray());
            js.WorldStashes = [(dlcId.Lo, dlcId.Hi, Tlv(0x0005, Tlv(0x0001, joinItemPayload.ToArray())))];
            js.PersonalStashItems = [new("44440000-0000-0000-0000-000000000001", Apple, 2)];

            byte[] hostFile = WhsSaveTests.File(hs);
            byte[] joinFile = WhsSaveTests.File(js);
            var res = WhsSave.Splice(hostFile, joinFile, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip);
            Assert.Empty(WhsSave.Check(hostFile, joinFile, res.File, WhsSaveTests.Quest, WhsSave.QuestItemMode.Strip));
            Assert.Equal(2, res.Report.PersonalStashItemsJoiner);
            var items = WhsSave.DecodePersonalStashItems(WhsSave.Inflate(res.File).Raw);
            Assert.Equal(["44440000-0000-0000-0000-000000000002", "44440000-0000-0000-0000-000000000001"], items.Select(i => i.Instance).ToArray());
        }
        finally
        {
            lock (WhsSave.PersonalStashIds) WhsSave.PersonalStashIds.Remove(dlcId);
        }
    }

    [Fact]
    public void Real_backup_saves_verify_personal_stash_extraction_and_splicing_when_present()
    {
        string q022Path = Path.Combine(BackupsDir, "quicksave022.whs");
        string s206Path = Path.Combine(BackupsDir, "save206.whs");
        string s318Path = Path.Combine(BackupsDir, "save318.whs");
        if (!File.Exists(q022Path) || !File.Exists(s206Path) || !File.Exists(s318Path))
            return; // Graceful skip on CI machines without local save backups

        byte[] q022 = WhsSave.ReadShared(q022Path);
        byte[] s206 = WhsSave.ReadShared(s206Path);
        byte[] s318 = WhsSave.ReadShared(s318Path);

        var p022 = WhsSave.PartsFromFile(q022, WhsSave.HenryParts.OriginSnapshot);
        var p206 = WhsSave.PartsFromFile(s206, WhsSave.HenryParts.OriginSnapshot);
        var p318 = WhsSave.PartsFromFile(s318, WhsSave.HenryParts.OriginSnapshot);

        Assert.Empty(p022.Extensions[WhsSave.PersonalStashExtensionKey]);
        Assert.Equal(341, WhsSave.DecodePersonalStashItems(WhsSave.Inflate(s206).Raw).Count);
        var s318Items = WhsSave.DecodePersonalStashItems(WhsSave.Inflate(s318).Raw);
        Assert.Equal(364, s318Items.Count);
        Assert.Equal(6 + 23392, p318.Extensions[WhsSave.PersonalStashExtensionKey].Length);

        var emptyQuest = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1. Host = save318 (364 stash items), Joiner = quicksave022 (empty stash) -> joiner gets 0 stash items, host's 40 world stashes stay
        var rHostFullJoinEmpty = WhsSave.SpliceParts(s318, WhsSave.ParseBlock(WhsSave.SerializeBlock(p022)), emptyQuest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(rHostFullJoinEmpty.File).Ok);
        Assert.Empty(WhsSave.CheckParts(s318, p022, rHostFullJoinEmpty.File, emptyQuest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.DecodePersonalStashItems(WhsSave.Inflate(rHostFullJoinEmpty.File).Raw));

        // 2. Host = quicksave022 (empty stash), Joiner = save318 (364 stash items) -> joiner keeps all 364 stash items
        var p318Round = WhsSave.ParseBlock(WhsSave.SerializeBlock(p318));
        var rHostEmptyJoinFull = WhsSave.SpliceParts(q022, p318Round, emptyQuest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(rHostEmptyJoinFull.File).Ok);
        Assert.Empty(WhsSave.CheckParts(q022, p318Round, rHostEmptyJoinFull.File, emptyQuest, WhsSave.QuestItemMode.Strip));
        Assert.Equal(364, rHostEmptyJoinFull.Report.PersonalStashItemsJoiner);
        Assert.Equal(364, WhsSave.DecodePersonalStashItems(WhsSave.Inflate(rHostEmptyJoinFull.File).Raw).Count);

        // 3. Host = save318 (364 items), Joiner = save206 (341 items) with a synthetic quest-class filter on one stash item class
        var s206Items = WhsSave.DecodePersonalStashItems(WhsSave.Inflate(s206).Raw);
        string questCls = s206Items[0].Class;
        int questCntIn206 = s206Items.Count(i => i.Class == questCls);
        // Pick a class that is NOT equipped on save206's Henry so BuildHenry doesn't refuse an equipped quest item
        var s206Soul = WhsSave.DecodePlayerSoul(WhsSave.Inflate(s206).Raw, WhsSave.FindSoul(WhsSave.Inflate(s206).Raw, WhsSave.HenrySoul)!.Value);
        var equippedSet = (s206Soul.Scalars.GetValueOrDefault("equipped_instances") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var equippedClasses = s206Soul.Inventory.Where(i => equippedSet.Contains(i.Instance)).Select(i => i.Class).ToHashSet();
        questCls = s206Items.First(i => !equippedClasses.Contains(i.Class)).Class;
        questCntIn206 = s206Items.Count(i => i.Class == questCls);
        var customQuest = new Dictionary<string, string>(StringComparer.Ordinal) { [questCls] = "testQuestStashItem" };

        var p206Round = WhsSave.ParseBlock(WhsSave.SerializeBlock(p206));
        var rBothFull = WhsSave.SpliceParts(s318, p206Round, customQuest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(rBothFull.File).Ok);
        Assert.Empty(WhsSave.CheckParts(s318, p206Round, rBothFull.File, customQuest, WhsSave.QuestItemMode.Strip));
        Assert.Equal(341 - questCntIn206, rBothFull.Report.PersonalStashItemsJoiner);
        Assert.Equal(questCntIn206, rBothFull.Report.PersonalStashQuestItemsRemoved.Count);

        // 4. Host = save318 (364 items), Joiner = legacy v1 .hblk from save206 -> host's 364 items stripped, joiner chest starts empty
        var p206V1 = WhsSave.ParseBlock(WhsSave.SerializeBlockV1(p206));
        var rLegacyV1 = WhsSave.SpliceParts(s318, p206V1, emptyQuest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(rLegacyV1.File).Ok);
        Assert.Empty(WhsSave.CheckParts(s318, p206V1, rLegacyV1.File, emptyQuest, WhsSave.QuestItemMode.Strip));
        Assert.Empty(WhsSave.DecodePersonalStashItems(WhsSave.Inflate(rLegacyV1.File).Raw));
    }
}
