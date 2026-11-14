// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Client.Tests;

public class M3HorseSaveTests
{
    private static byte[] Tlv(ushort tag, byte[] payload)
    {
        var b = new byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(b, 6);
        return b;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] G(string guid) => new Guid(guid).ToByteArray();

    private const string PebblesInst = "4e5abeff-f19e-0eab-0921-a24611c4ad8f";
    private const string PebblesShared = "4e5abeff-f19e-0eab-0921-a24611c4ad8f";
    private const string HorseBInst = "afba3370-5c65-4e44-b015-84112376c17e";
    private const string HorseBShared = "e39e9f8c-6604-4e34-90fd-4be29da50ad2";
    private const string MuttInst = "44a28861-719d-9fed-e3dd-b20c479e8781";
    private const string NpcGuid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string NpcDogInst = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string NpcDogShared = "4ffab0bb-617a-a998-8c03-73d409edc8b2";

    private const string SaddleCls = "bf06d242-4dcb-48e9-bbf1-b6a36f368a57";
    private const string BridleCls = "505343b8-b5ff-4f47-94d9-470a1977a421";
    private const string HoneyCls = "7beb4bdc-6478-455c-8746-afb92c604be8";
    private const string QuestLetterCls = "0b0b0b0b-0000-0000-0000-00000000b002";

    private static readonly Dictionary<string, string> Quest = new() { [QuestLetterCls] = "loveLetter" };

    private sealed record HorseSpec(
        string InstGuid,
        string SharedGuid,
        string EntityName,
        ulong EntityGuid,
        byte StatStamp,
        byte[] Renown,
        List<WhsSaveTests.Item>? Items = null,
        List<string>? Equipped0001 = null);

    private static byte[] BuildItemNode(WhsSaveTests.Item it)
    {
        var p = Cat(G(it.Inst), U32(it.Flags), G(it.Cls));
        if (it.Amount != 1) p = Cat(p, Tlv(0x0000, U32(it.Amount - 1)));
        return Tlv(0x0001, p);
    }

    private static byte[] BuildHorseSoul(HorseSpec h)
    {
        var nameField = Tlv(0x12F9, Cat(Encoding.Latin1.GetBytes(h.EntityName + "\0"), BitConverter.GetBytes(h.EntityGuid)));
        var mainField = Tlv(0x12FB, Tlv(0x0927, Tlv(0x138B, [h.StatStamp, 1, 2, 3])));
        var renownField = Tlv(0x12FF, h.Renown);
        var fields = new List<byte[]> { nameField, mainField, renownField };
        if (h.Items is not null)
        {
            var invList = Cat(new[] { Tlv(0x0000, G("dddddddd-1111-2222-3333-444444444444")) }.Concat(h.Items.Select(BuildItemNode)).ToArray());
            var eq0001 = Tlv(0x0001, (h.Equipped0001 is { Count: > 0 } eq)
                ? Cat(eq.Select(id => Tlv(0x0000, G(id))).ToArray())
                : []);
            fields.Add(Tlv(0x1301, Cat(Tlv(0x0007, invList), eq0001)));
        }
        return Tlv(0x115E, Cat(new[] { G(h.InstGuid), G(h.SharedGuid) }.Concat(fields).ToArray()));
    }

    private static byte[] BuildDogSoul(string instGuid, string sharedGuid, string name) =>
        Tlv(0x115E, Cat(G(instGuid), G(sharedGuid),
            Tlv(0x12F9, Cat(Encoding.Latin1.GetBytes(name + "\0"), new byte[8])),
            Tlv(0x12FF, [9, 9, 9, 9])));

    private static byte[] BuildCompLink(string master, string comp) =>
        Tlv(0x0DD6, Cat(U16(0x0501), G(master), U16(0x0501), G(comp)));

    /// <summary>Inject horse/dog souls into 1161 and replace 1160 with real companion links (empty [] when none).</summary>
    private static byte[] BuildSaveWithCompanions(
        WhsSaveTests.Spec baseSpec,
        IReadOnlyList<byte[]> extraSouls,
        IReadOnlyList<(string Master, string Companion)> companions)
    {
        var baseFile = WhsSaveTests.File(baseSpec);
        var c = WhsSave.Inflate(baseFile);
        var raw = c.Raw;

        var soulChain = WhsSave.PathNodes(raw, 0x01F4, 0x01F8, 0x7308, 0x3529, 0x1161);
        var newSoulPayload = Cat(new[] { WhsSave.Payload(raw, soulChain[^1]) }.Concat(extraSouls).ToArray());
        raw = WhsSave.Replace(raw, soulChain, newSoulPayload);

        var compChain = WhsSave.PathNodes(raw, 0x01F4, 0x01F8, 0x7308, 0x3529, 0x1160);
        var compPayload = companions.Count == 0
            ? []
            : Cat(companions.Select(x => BuildCompLink(x.Master, x.Companion)).ToArray());
        raw = WhsSave.Replace(raw, compChain, compPayload);

        return WhsSave.Deflate(c.DescBytes, raw, c.FooterTail);
    }

    [Fact]
    public void Host_owns_Pebbles_and_Joiner_owns_HorseB_splices_companions_saddlebags_tack_and_keys_independently()
    {
        var (hs, js) = WhsSaveTests.Pair();
        const string hostHorseKey = "11111111-aaaa-bbbb-cccc-000000000001";
        const string joinHorseKey = "22222222-aaaa-bbbb-cccc-000000000002";
        hs.Keys.Add((WhsSave.HenrySoul, hostHorseKey));
        js.Keys.Add((WhsSave.HenrySoul, joinHorseKey));

        var hostPebbles = new HorseSpec(
            PebblesInst, PebblesShared, "tsem_sedivka", 0x1001UL, 10, [0xAA, 0xBB],
            Items: [new(hostHorseKey, HoneyCls, 2)],
            Equipped0001: []);
        var hostHorseBUnbought = new HorseSpec(
            HorseBInst, HorseBShared, "tsem_horseForSale_2", 0x2002UL, 20, [0xCC, 0xDD, 0xEE],
            Items: null);

        var joinPebblesWorld = new HorseSpec(
            PebblesInst, PebblesShared, "tsem_sedivka", 0x1001UL, 10, [0x01, 0x02],
            Items: null);
        var joinHorseBBought = new HorseSpec(
            HorseBInst, HorseBShared, "tsem_horseForSale_2", 0x9999UL, 77, [0x11, 0x22, 0x33],
            Items:
            [
                new("ad7098a2-b7df-4011-85ff-706df42fa574", SaddleCls),
                new("8373ec90-0e3d-463b-88e2-9606d24b3be9", BridleCls),
                new(joinHorseKey, HoneyCls, 4),
                new("77777777-0000-0000-0000-000000000007", QuestLetterCls),
            ],
            Equipped0001: ["ad7098a2-b7df-4011-85ff-706df42fa574", "8373ec90-0e3d-463b-88e2-9606d24b3be9", "77777777-0000-0000-0000-000000000007"]);

        var hostFile = BuildSaveWithCompanions(
            hs,
            [BuildHorseSoul(hostPebbles), BuildHorseSoul(hostHorseBUnbought)],
            [(WhsSave.HenrySoul, PebblesInst)]);
        var joinFile = BuildSaveWithCompanions(
            js,
            [BuildHorseSoul(joinPebblesWorld), BuildHorseSoul(joinHorseBBought)],
            [(WhsSave.HenrySoul, HorseBInst)]);

        var parts = WhsSave.PartsFromFile(joinFile, WhsSave.HenryParts.OriginSave);
        Assert.Contains(WhsSave.HorseCompanionExtensionKey, parts.Extensions.Keys);

        // .hblk round-trip preserves horse_companion and tack extraction
        var block = WhsSave.SerializeBlock(parts);
        var parsed = WhsSave.ParseBlock(block);
        Assert.Empty(WhsSave.DiffBlocks(parts, parsed));

        var res = WhsSave.SpliceParts(hostFile, parsed, Quest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(res.File).Ok);
        Assert.Empty(WhsSave.CheckParts(hostFile, parsed, res.File, Quest, WhsSave.QuestItemMode.Strip));

        var outRaw = WhsSave.Inflate(res.File).Raw;
        // Joiner owns Horse B, NOT Host's Pebbles
        Assert.Equal(HorseBInst, WhsSave.ReadHenryHorseGuid(outRaw));
        Assert.Single(WhsSave.ReadCompanionLinks(outRaw));

        // Horse B carries Joiner's saddle, bridle, and honey, with quest item stripped and Host's 0x12F9 + 0x12FF preserved
        var outHorseB = WhsSave.FindSoul(outRaw, HorseBInst)!.Value;
        var hbItems = WhsSave.DecodeSoulInventoryItems(outRaw, outHorseB);
        Assert.Equal(3, hbItems.Count);
        Assert.DoesNotContain(hbItems, i => i.Class == QuestLetterCls);
        Assert.Contains(hbItems, i => i.Class == SaddleCls);
        Assert.Contains(hbItems, i => i.Class == BridleCls);
        Assert.Contains(hbItems, i => i.Instance == joinHorseKey && i.Params == "amount=4");

        var hbEq = WhsSave.ReadSoulEquippedInstances(WhsSave.NodeBytes(outRaw, outHorseB));
        Assert.Equal(["ad7098a2-b7df-4011-85ff-706df42fa574", "8373ec90-0e3d-463b-88e2-9606d24b3be9"], hbEq);

        var hbFields = WhsSave.SoulFields(outRaw, outHorseB);
        Assert.Equal(new byte[] { 0xCC, 0xDD, 0xEE }, WhsSave.Payload(outRaw, hbFields[0x12FF][0]));   // Host's 0x12FF preserved
        var namePay = WhsSave.Payload(outRaw, hbFields[0x12F9][0]);
        Assert.Equal(0x2002UL, BinaryPrimitives.ReadUInt64LittleEndian(namePay.AsSpan(namePay.Length - 8)));   // Host's 0x12F9 entity GUID preserved

        // Host's Pebbles in 1161 remains untouched in the world
        var hostRaw = WhsSave.Inflate(hostFile).Raw;
        Assert.Equal(
            WhsSave.NodeBytes(hostRaw, WhsSave.FindSoul(hostRaw, PebblesInst)!.Value),
            WhsSave.NodeBytes(outRaw, WhsSave.FindSoul(outRaw, PebblesInst)!.Value));

        // Snapshot taken from the spliced shared-world save round-trips byte-for-byte
        var snap1 = WhsSave.PartsFromFile(res.File, WhsSave.HenryParts.OriginSnapshot);
        var second = WhsSave.SpliceParts(hostFile, WhsSave.ParseBlock(WhsSave.SerializeBlock(snap1)), Quest, WhsSave.QuestItemMode.Strip).File;
        Assert.Equal(res.File, second);
        Assert.Equal([BridleCls, SaddleCls], WhsSave.ExtractHorseEquippedTackClasses(snap1));
    }

    [Fact]
    public void Joiner_with_no_horse_does_not_inherit_Hosts_Pebbles_while_Mutt_and_NPC_dogs_stay_intact()
    {
        var (hs, js) = WhsSaveTests.Pair();
        var hostPebbles = new HorseSpec(PebblesInst, PebblesShared, "tsem_sedivka", 0x1001UL, 10, [0xAA]);
        var joinPebbles = new HorseSpec(PebblesInst, PebblesShared, "tsem_sedivka", 0x1001UL, 10, [0xAA]);

        var hostFile = BuildSaveWithCompanions(
            hs,
            [
                BuildHorseSoul(hostPebbles),
                BuildDogSoul(MuttInst, WhsSave.MuttSharedSoul, "tvez_vorech"),
                BuildDogSoul(NpcDogInst, NpcDogShared, "kkut_dog_1"),
            ],
            [
                (WhsSave.HenrySoul, MuttInst),
                (WhsSave.HenrySoul, PebblesInst),
                (NpcGuid, NpcDogInst),
            ]);

        var joinFile = BuildSaveWithCompanions(
            js,
            [
                BuildHorseSoul(joinPebbles),
                BuildDogSoul(MuttInst, WhsSave.MuttSharedSoul, "tvez_vorech"),
                BuildDogSoul(NpcDogInst, NpcDogShared, "kkut_dog_1"),
            ],
            []);   // Joiner owns NO horse in a real save (Len == 0)

        var res = WhsSave.Splice(hostFile, joinFile, Quest, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(res.File).Ok);
        Assert.Empty(WhsSave.Check(hostFile, joinFile, res.File, Quest, WhsSave.QuestItemMode.Strip));

        var outRaw = WhsSave.Inflate(res.File).Raw;
        Assert.Null(WhsSave.ReadHenryHorseGuid(outRaw));
        var links = WhsSave.ReadCompanionLinks(outRaw);
        Assert.Equal(
            [(WhsSave.HenrySoul, MuttInst), (NpcGuid, NpcDogInst)],
            links);
    }

    [Fact]
    public void Same_horse_collision_strips_Hosts_0x1301_when_Joiners_Pebbles_has_no_saddlebags()
    {
        var (hs, js) = WhsSaveTests.Pair();
        var hostPebblesLoaded = new HorseSpec(
            PebblesInst, PebblesShared, "tsem_sedivka", 0x1001UL, 10, [0xAA],
            Items: [new("ad7098a2-b7df-4011-85ff-706df42fa574", SaddleCls)],
            Equipped0001: ["ad7098a2-b7df-4011-85ff-706df42fa574"]);
        var joinPebblesBare = new HorseSpec(
            PebblesInst, PebblesShared, "tsem_sedivka", 0x1001UL, 33, [0xBB],
            Items: null);

        var hostFile = BuildSaveWithCompanions(hs, [BuildHorseSoul(hostPebblesLoaded)], [(WhsSave.HenrySoul, PebblesInst)]);
        var joinFile = BuildSaveWithCompanions(js, [BuildHorseSoul(joinPebblesBare)], [(WhsSave.HenrySoul, PebblesInst)]);

        var res = WhsSave.Splice(hostFile, joinFile, Quest, WhsSave.QuestItemMode.Strip);
        Assert.Empty(WhsSave.Check(hostFile, joinFile, res.File, Quest, WhsSave.QuestItemMode.Strip));

        var outRaw = WhsSave.Inflate(res.File).Raw;
        Assert.Equal(PebblesInst, WhsSave.ReadHenryHorseGuid(outRaw));
        var outPebbles = WhsSave.FindSoul(outRaw, PebblesInst)!.Value;
        Assert.Empty(WhsSave.DecodeSoulInventoryItems(outRaw, outPebbles));
        Assert.False(WhsSave.SoulFields(outRaw, outPebbles).ContainsKey(0x1301));
    }

    [Fact]
    public void Malformed_horse_companion_extension_is_rejected()
    {
        var (hs, js) = WhsSaveTests.Pair();
        var hostFile = WhsSaveTests.File(hs);
        var parts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSave);

        parts.Extensions[WhsSave.HorseCompanionExtensionKey] = [];
        Assert.Throws<InvalidDataException>(() => WhsSave.SpliceParts(hostFile, parts, Quest, WhsSave.QuestItemMode.Strip));

        parts.Extensions[WhsSave.HorseCompanionExtensionKey] = [0, 99];
        Assert.Throws<InvalidDataException>(() => WhsSave.SpliceParts(hostFile, parts, Quest, WhsSave.QuestItemMode.Strip));

        parts.Extensions[WhsSave.HorseCompanionExtensionKey] = [1, 2, 3];
        Assert.Throws<InvalidDataException>(() => WhsSave.SpliceParts(hostFile, parts, Quest, WhsSave.QuestItemMode.Strip));
    }

    [Fact]
    public void Real_backup_saves_splice_horse_companions_and_saddlebags_cleanly_when_present()
    {
        const string backupDir = @"d:\Code\kcd2\kct-save-backups";
        const string tablesPak = @"C:\Program Files (x86)\Steam\steamapps\common\KingdomComeDeliverance2\Data\Tables.pak";
        string s206 = Path.Combine(backupDir, "save206.whs");
        string s290 = Path.Combine(backupDir, "save290.whs");
        string s318 = Path.Combine(backupDir, "save318.whs");
        if (!File.Exists(s206) || !File.Exists(s290) || !File.Exists(s318) || !File.Exists(tablesPak))
            return;

        var qclasses = WhsSave.QuestClasses(tablesPak);
        byte[] f206 = WhsSave.ReadShared(s206);   // no horse (1160 Len == 0)
        byte[] f290 = WhsSave.ReadShared(s290);   // owns tsem_sedivka (Pebbles)
        byte[] f318 = WhsSave.ReadShared(s318);   // owns tsem_horseForSale_2 with cheapSaddle + cheapBridle + honey

        // 1) Host = save206 (no horse), Joiner = save318 (tsem_horseForSale_2 with tack + saddlebag items)
        var p318 = WhsSave.ParseBlock(WhsSave.SerializeBlock(WhsSave.PartsFromFile(f318, WhsSave.HenryParts.OriginSnapshot)));
        Assert.Equal([BridleCls, SaddleCls], WhsSave.ExtractHorseEquippedTackClasses(p318));
        var r206_318 = WhsSave.SpliceParts(f206, p318, qclasses, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(r206_318.File).Ok);
        Assert.Empty(WhsSave.CheckParts(f206, p318, r206_318.File, qclasses, WhsSave.QuestItemMode.Strip));
        var raw206_318 = WhsSave.Inflate(r206_318.File).Raw;
        Assert.Equal(HorseBInst, WhsSave.ReadHenryHorseGuid(raw206_318));
        Assert.Equal(3, WhsSave.DecodeSoulInventoryItems(raw206_318, WhsSave.FindSoul(raw206_318, HorseBInst)!.Value).Count);

        // 2) Host = save290 (Pebbles), Joiner = save206 (no horse) -> Joiner does NOT steal Host's Pebbles
        var r290_206 = WhsSave.Splice(f290, f206, qclasses, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(r290_206.File).Ok);
        Assert.Empty(WhsSave.Check(f290, f206, r290_206.File, qclasses, WhsSave.QuestItemMode.Strip));
        Assert.Null(WhsSave.ReadHenryHorseGuid(WhsSave.Inflate(r290_206.File).Raw));

        // 3) Host = save290 (Pebbles), Joiner = save318 (tsem_horseForSale_2) -> Joiner owns Horse B, Host's Pebbles intact
        var r290_318 = WhsSave.SpliceParts(f290, p318, qclasses, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(r290_318.File).Ok);
        Assert.Empty(WhsSave.CheckParts(f290, p318, r290_318.File, qclasses, WhsSave.QuestItemMode.Strip));
        var raw290_318 = WhsSave.Inflate(r290_318.File).Raw;
        Assert.Equal(HorseBInst, WhsSave.ReadHenryHorseGuid(raw290_318));
    }
}
