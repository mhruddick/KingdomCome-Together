// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Client.Tests;

public class M4ReputationSaveTests
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
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] F32(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); return b; }
    private static byte[] G(string guid) => new Guid(guid).ToByteArray();

    private const string Henry = WhsSave.HenrySoul;
    private const string Shared = "11111111-2222-3333-4444-555555555555";
    private const string MerchantSoul = "aaaaaaaa-0000-0000-0000-000000000010";
    private const string HorseSoul = "aaaaaaaa-0000-0000-0000-000000000020";
    private const string UnmetNpcSoul = "aaaaaaaa-0000-0000-0000-000000000030";
    private const string SaddleItem = "0c0c0c0c-0000-0000-0000-00000000c001";
    private const string Groschen = "5ef63059-322e-4e1b-abe8-926e100c770e";

    private static byte[] Reputation12FF(float value, bool withExtra1D2B = false)
    {
        var d2c = Tlv(0x1D2C, Cat(U32(1), F32(value), U32(0x12345678)));
        return withExtra1D2B ? Cat(d2c, Tlv(0x1D2B, F32(value * 0.5f))) : d2c;
    }

    private static byte[] FactionTree352C(float troskyRep, float kuttenbergRep) =>
        Cat(
            Tlv(0x1F34, Cat(Encoding.ASCII.GetBytes("trosecko\0"), F32(troskyRep))),
            Tlv(0x1F34, Cat(Encoding.ASCII.GetBytes("kutnohorsko\0"), F32(kuttenbergRep))));

    private static byte[] BuildRealisticSave(
        uint seed,
        uint story,
        uint strength,
        float henryRenown,
        float troskyFactionRep,
        float kuttenbergFactionRep,
        float merchantOpinion,
        float? horseOpinion,
        uint horseSaddleAmount,
        bool unmetNpcFullInThisSave,
        float unmetNpcOpinion,
        byte sideStamp)
    {
        var stats = Cat(U32(0), U32(strength), U32(8), U32(story), U32(0xFFFFFFFF), U32(0));
        var skills = Cat(U32(3), U32(120u + sideStamp), U32(0xFFFFFFFF), U32(0));
        var states = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(states, 80f + sideStamp);
        var core = Cat(Tlv(0x1385, stats), Tlv(0x138D, skills), Tlv(0x138B, states), Tlv(0x137E, Tlv(0x0001, [9])));
        var buffs = Cat(U32(0));
        var main = Cat(Tlv(0x0927, core), Tlv(0x0928, buffs));
        var moneyItem = Tlv(0x0001, Cat(G($"20000000-0000-0000-0000-00000000000{sideStamp}"), U32(0), G(Groschen), Tlv(0x0000, U32(99u + sideStamp))));
        var invList = Cat(Tlv(0x0000, G("dddddddd-0000-0000-0000-00000000d004")), moneyItem);
        var eq = Tlv(0x0001, Tlv(0x0000, new byte[16]));
        var inv = Cat(Tlv(0x0007, invList), Tlv(0x0006, eq));
        var henryName = Cat(Encoding.Latin1.GetBytes("Dude\0"), BitConverter.GetBytes(0x7777UL));
        var henryRec = Tlv(0x115E, Cat(
            G(Henry), G(Shared),
            Tlv(0x12F9, henryName),
            Tlv(0x12FB, main),
            Tlv(0x12FF, Reputation12FF(henryRenown)),
            Tlv(0x1301, inv)));

        var merchantRec = Tlv(0x115E, Cat(
            G(MerchantSoul), G(Shared),
            Tlv(0x12F9, Cat(Encoding.Latin1.GetBytes("ttkc_trader_1\0"), new byte[8])),
            Tlv(0x12FB, Tlv(0x0927, Tlv(0x138B, F32(100f)))),
            Tlv(0x12FF, Reputation12FF(merchantOpinion, withExtra1D2B: true))));

        var horseItem = Tlv(0x0001, Cat(G("77777777-0000-0000-0000-000000000077"), U32(0), G(SaddleItem), Tlv(0x0000, U32(horseSaddleAmount - 1))));
        var horseInv = Tlv(0x1301, Tlv(0x0007, Cat(Tlv(0x0000, G("eeeeeeee-0000-0000-0000-00000000e001")), horseItem)));
        var horseFields = new List<byte[]>
        {
            Tlv(0x12F9, Cat(Encoding.Latin1.GetBytes("pebbles\0"), new byte[8])),
            horseInv,
        };
        if (horseOpinion is float ho)
            horseFields.Insert(1, Tlv(0x12FF, Reputation12FF(ho)));
        var horseRec = Tlv(0x115E, Cat(new[] { G(HorseSoul), G(Shared) }.Concat(horseFields).ToArray()));

        var unmetRec = unmetNpcFullInThisSave
            ? Tlv(0x115E, Cat(
                G(UnmetNpcSoul), G(Shared),
                Tlv(0x12F9, Cat(Encoding.Latin1.GetBytes("kh_guard_9\0"), new byte[8])),
                Tlv(0x12FF, Reputation12FF(unmetNpcOpinion))))
            : Tlv(0x115D, Cat(G(UnmetNpcSoul), U32(0)));

        var souls = Tlv(0x1161, Cat(merchantRec, horseRec, unmetRec, henryRec));
        var rpg = Tlv(0x7308, Cat(
            Tlv(0x3529, Cat(Tlv(0x1160, new byte[4]), souls)),
            Tlv(0x352C, FactionTree352C(troskyFactionRep, kuttenbergFactionRep)),
            Tlv(0x352D, [0x2D, sideStamp, 1, 1]),
            Tlv(0x352E, [0x2E, sideStamp, 2, 2])));

        var ent = Tlv(0x7302, Cat(
            Tlv(0x0006, []),
            Tlv(0x000A, Encoding.ASCII.GetBytes("shared-qim")),
            Tlv(0x000B, new byte[8] { 1, 0, 0, 0, sideStamp, 0, 0, 0 })));
        var pm = Tlv(0x7309, Cat(Tlv(0x0000, [0x90, sideStamp]), Tlv(0x0001, [0x91, sideStamp]), Tlv(0x0002, [0x92])));
        var gui = Tlv(0x7301, [0x71, sideStamp]);
        var cry = Tlv(0x01F7, Tlv(0x20A7, Cat(Encoding.ASCII.GetBytes("level\0"), Encoding.ASCII.GetBytes("trosecko"))));
        var phaseB = Tlv(0x01F8, Cat(ent, rpg, pm, gui));
        var phaseC = Tlv(0x01F9, Cat(Tlv(0x7302, Tlv(0x0002, [0x72, sideStamp])), Tlv(0x7305, [0x75])));
        var body = Tlv(0x01F4, Cat(Tlv(0x01FB, U32(seed)), Tlv(0x01F6, Tlv(0x730A, [0x7A])), cry, phaseB, phaseC));
        var raw = Cat(U32(23), body, Tlv(0x01FA, []));

        var desc = Encoding.UTF8.GetBytes("<SaveGame SaveType=\"QuickSave\" SaveId=\"1\" LevelName=\"trosecko\" BuildInfo=\"1.5.5-release_1_5\" GameMode=\"normal\"/>");
        var tail = new byte[44];
        tail[0] = 0x5A;
        return WhsSave.Deflate(desc, raw, tail);
    }

    [Fact]
    public void Same_playthrough_splices_factions_henry_renown_and_npc_opinions_preserving_non_12ff_fields_and_115d_records()
    {
        var hostFile = BuildRealisticSave(
            seed: 0xCAFE1234u, story: 5000, strength: 100,
            henryRenown: 85f, troskyFactionRep: 90f, kuttenbergFactionRep: 75f,
            merchantOpinion: 95f, horseOpinion: null, horseSaddleAmount: 7,
            unmetNpcFullInThisSave: false, unmetNpcOpinion: 0f, sideStamp: 1);

        var joinFile = BuildRealisticSave(
            seed: 0xCAFE1234u, story: 4800, strength: 350,
            henryRenown: 20f, troskyFactionRep: 15f, kuttenbergFactionRep: 40f,
            merchantOpinion: 12f, horseOpinion: 60f, horseSaddleAmount: 1,
            unmetNpcFullInThisSave: true, unmetNpcOpinion: 33f, sideStamp: 2);

        var parts = WhsSave.PartsFromFile(joinFile, WhsSave.HenryParts.OriginSnapshot);
        Assert.Contains(WhsSave.ReputationFactionsExtensionKey, parts.Extensions.Keys);
        Assert.Contains(WhsSave.ReputationHenryRenownExtensionKey, parts.Extensions.Keys);
        Assert.Contains(WhsSave.ReputationNpcOpinionsExtensionKey, parts.Extensions.Keys);

        // Round-trip through .hblk v2
        var block = WhsSave.SerializeBlock(parts);
        var restored = WhsSave.ParseBlock(block);
        Assert.Empty(WhsSave.DiffBlocks(parts, restored));

        var emptyQ = new Dictionary<string, string>(StringComparer.Ordinal);
        var spliced = WhsSave.SpliceParts(hostFile, restored, emptyQ, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(spliced.File).Ok);
        Assert.Empty(WhsSave.CheckParts(hostFile, restored, spliced.File, emptyQ, WhsSave.QuestItemMode.Strip));

        var outRaw = WhsSave.Inflate(spliced.File).Raw;
        var joinRaw = WhsSave.Inflate(joinFile).Raw;
        var hostRaw = WhsSave.Inflate(hostFile).Raw;

        // 1. 352C FactionTree is Joiner's
        var out352c = WhsSave.Payload(outRaw, WhsSave.PathGet(outRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value);
        var join352c = WhsSave.Payload(joinRaw, WhsSave.PathGet(joinRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value);
        Assert.Equal(join352c, out352c);

        // 2. Henry's 0x12FF renown is Joiner's, while storyProgress is Host's
        var outHenry = WhsSave.FindSoul(outRaw, Henry)!.Value;
        Assert.Equal(5000u, WhsSave.DecodePlayerSoul(outRaw, outHenry).StatXp["storyProgress"]);
        Assert.Equal(Reputation12FF(20f), WhsSave.Payload(outRaw, WhsSave.SoulFields(outRaw, outHenry)[0x12FF][0]));

        // 3. Merchant's 0x12FF is Joiner's (12f), while Merchant's 0x12F9/0x12FB stay Host's
        var outMerchant = WhsSave.FindSoul(outRaw, MerchantSoul)!.Value;
        var outMerchantFields = WhsSave.SoulFields(outRaw, outMerchant);
        Assert.Equal(Reputation12FF(12f, withExtra1D2B: true), WhsSave.Payload(outRaw, outMerchantFields[0x12FF][0]));

        // 4. Horse lacked 0x12FF on Host -> AppendField added Joiner's 0x12FF, while Host's 0x1301 saddlebag (amount 7) stayed 100% untouched!
        var outHorse = WhsSave.FindSoul(outRaw, HorseSoul)!.Value;
        var hostHorse = WhsSave.FindSoul(hostRaw, HorseSoul)!.Value;
        var outHorseFields = WhsSave.SoulFields(outRaw, outHorse);
        var hostHorseFields = WhsSave.SoulFields(hostRaw, hostHorse);
        Assert.Equal(Reputation12FF(60f), WhsSave.Payload(outRaw, outHorseFields[0x12FF][0]));
        Assert.Equal(
            WhsSave.Payload(hostRaw, hostHorseFields[0x1301][0]),
            WhsSave.Payload(outRaw, outHorseFields[0x1301][0]));

        // 5. Short 0x115D soul in Host stays 0x115D untouched without throwing
        var outUnmet = WhsSave.FindSoul(outRaw, UnmetNpcSoul)!.Value;
        Assert.Equal(0x115D, outUnmet.Tag);
    }

    [Fact]
    public void Cross_playthrough_character_import_keeps_host_factions_until_joiner_snapshots_in_host_world()
    {
        var hostFile = BuildRealisticSave(
            seed: 0xAAAA0001u, story: 8000, strength: 200,
            henryRenown: 75f, troskyFactionRep: 80f, kuttenbergFactionRep: 92f,
            merchantOpinion: 88f, horseOpinion: 50f, horseSaddleAmount: 3,
            unmetNpcFullInThisSave: true, unmetNpcOpinion: 65f, sideStamp: 1);

        var foreignJoinFile = BuildRealisticSave(
            seed: 0xBBBB0002u, story: 100, strength: 310,
            henryRenown: 5f, troskyFactionRep: 50f, kuttenbergFactionRep: 0f,
            merchantOpinion: 50f, horseOpinion: 50f, horseSaddleAmount: 1,
            unmetNpcFullInThisSave: false, unmetNpcOpinion: 0f, sideStamp: 2);

        var emptyQ = new Dictionary<string, string>(StringComparer.Ordinal);
        var importedParts = WhsSave.PartsFromFile(foreignJoinFile, WhsSave.HenryParts.OriginSave);
        Assert.False(WhsSave.ShouldSpliceReputation(WhsSave.Inflate(hostFile).Raw, importedParts));

        // First join ("Bring my character" from another playthrough): Host's Kuttenberg factions/opinions stay intact!
        var firstJoin = WhsSave.SpliceParts(hostFile, importedParts, emptyQ, WhsSave.QuestItemMode.Strip);
        Assert.Empty(WhsSave.CheckParts(hostFile, importedParts, firstJoin.File, emptyQ, WhsSave.QuestItemMode.Strip));

        var firstOutRaw = WhsSave.Inflate(firstJoin.File).Raw;
        var hostRaw = WhsSave.Inflate(hostFile).Raw;
        Assert.Equal(
            WhsSave.Payload(hostRaw, WhsSave.PathGet(hostRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value),
            WhsSave.Payload(firstOutRaw, WhsSave.PathGet(firstOutRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value));
        Assert.Equal(
            Reputation12FF(88f, withExtra1D2B: true),
            WhsSave.Payload(firstOutRaw, WhsSave.SoulFields(firstOutRaw, WhsSave.FindSoul(firstOutRaw, MerchantSoul)!.Value)[0x12FF][0]));

        // Once Joiner plays in Host's world and takes her first snapshot, Seed == Host's seed (0xAAAA0001u)!
        var snapInHostWorld = WhsSave.PartsFromFile(firstJoin.File, WhsSave.HenryParts.OriginSnapshot);
        Assert.Equal(0xAAAA0001u, snapInHostWorld.Seed);

        // Simulate Host losing reputation later in Host's own game while Joiner kept her clean Kuttenberg reputation:
        var laterHostFile = BuildRealisticSave(
            seed: 0xAAAA0001u, story: 8100, strength: 210,
            henryRenown: 10f, troskyFactionRep: 5f, kuttenbergFactionRep: 10f,
            merchantOpinion: 5f, horseOpinion: 50f, horseSaddleAmount: 3,
            unmetNpcFullInThisSave: true, unmetNpcOpinion: 10f, sideStamp: 1);

        var rejoin = WhsSave.SpliceParts(laterHostFile, WhsSave.ParseBlock(WhsSave.SerializeBlock(snapInHostWorld)), emptyQ, WhsSave.QuestItemMode.Strip);
        Assert.Empty(WhsSave.CheckParts(laterHostFile, snapInHostWorld, rejoin.File, emptyQ, WhsSave.QuestItemMode.Strip));

        var rejoinRaw = WhsSave.Inflate(rejoin.File).Raw;
        Assert.Equal(
            FactionTree352C(80f, 92f),
            WhsSave.Payload(rejoinRaw, WhsSave.PathGet(rejoinRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value));
        Assert.Equal(
            Reputation12FF(88f, withExtra1D2B: true),
            WhsSave.Payload(rejoinRaw, WhsSave.SoulFields(rejoinRaw, WhsSave.FindSoul(rejoinRaw, MerchantSoul)!.Value)[0x12FF][0]));
        Assert.Equal(
            Reputation12FF(75f),
            WhsSave.Payload(rejoinRaw, WhsSave.SoulFields(rejoinRaw, WhsSave.FindSoul(rejoinRaw, Henry)!.Value)[0x12FF][0]));
    }

    [Fact]
    public void Malformed_reputation_extension_blobs_and_seedless_or_truncated_records_are_handled_safely()
    {
        Assert.Throws<InvalidDataException>(() => WhsSave.DecodeNpcOpinions([1, 2, 3]));
        var badCount = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(badCount, -5);
        Assert.Throws<InvalidDataException>(() => WhsSave.DecodeNpcOpinions(badCount));

        // Oversized count on tiny blob is rejected before dictionary allocation
        var amplifiedCount = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(amplifiedCount, 99_999);
        Assert.Throws<InvalidDataException>(() => WhsSave.DecodeNpcOpinions(amplifiedCount));

        var non1D2C = new byte[4 + 16 + 4 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(non1D2C, 1);
        G(MerchantSoul).CopyTo(non1D2C.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(non1D2C.AsSpan(20), 8);
        Tlv(0x9999, [1, 2]).CopyTo(non1D2C.AsSpan(24));
        Assert.Throws<InvalidDataException>(() => WhsSave.DecodeNpcOpinions(non1D2C));

        // Duplicate soul GUIDs in reputation_npc_opinions are rejected
        var validPay = Reputation12FF(55f);
        var dupBlob = WhsSave.EncodeNpcOpinions([(G(MerchantSoul), validPay), (G(MerchantSoul), validPay)]);
        Assert.Throws<InvalidDataException>(() => WhsSave.DecodeNpcOpinions(dupBlob));

        // ShouldSpliceReputation fails closed when either seed is null
        var hostFile = BuildRealisticSave(
            seed: 0xCAFE1234u, story: 5000, strength: 100,
            henryRenown: 85f, troskyFactionRep: 90f, kuttenbergFactionRep: 75f,
            merchantOpinion: 95f, horseOpinion: null, horseSaddleAmount: 7,
            unmetNpcFullInThisSave: false, unmetNpcOpinion: 0f, sideStamp: 1);
        var hostRaw = WhsSave.Inflate(hostFile).Raw;
        var seedlessParts = WhsSave.PartsFromFile(hostFile, WhsSave.HenryParts.OriginFreshDefault);
        seedlessParts.Seed = null;
        Assert.False(WhsSave.ShouldSpliceReputation(hostRaw, seedlessParts));
        Assert.False(WhsSave.ShouldSpliceReputation(new byte[16], WhsSave.PartsFromFile(hostFile, WhsSave.HenryParts.OriginSnapshot)));

        // Truncated (< 38 byte) 0x115E soul node in host SoulList is left untouched without throwing
        var sameSeedParts = WhsSave.PartsFromFile(hostFile, WhsSave.HenryParts.OriginSnapshot);
        sameSeedParts.Extensions[WhsSave.ReputationNpcOpinionsExtensionKey] =
            WhsSave.EncodeNpcOpinions([(G(UnmetNpcSoul), Reputation12FF(44f))]);
        var truncatedNode = Tlv(0x115E, G(UnmetNpcSoul));   // 22 bytes (< 38)
        var ext = typeof(WhsSave).GetNestedType("M4ReputationSaveExtension", System.Reflection.BindingFlags.NonPublic)!;
        var inst = (WhsSave.ISaveExtension)ext.GetField("Instance")!.GetValue(null)!;
        var unchanged = inst.TransformSoulRecord(UnmetNpcSoul, hostRaw, truncatedNode, sameSeedParts, new(), WhsSave.QuestItemMode.Strip, new());
        Assert.Same(truncatedNode, unchanged);
    }

    [Fact]
    public void Real_backup_saves_round_trip_extract_hblk_v2_splice_and_pass_check_when_present()
    {
        const string backupDir = @"d:\Code\kcd2\kct-save-backups";
        string save206Path = Path.Combine(backupDir, "save206.whs");
        string save318Path = Path.Combine(backupDir, "save318.whs");
        if (!File.Exists(save206Path) || !File.Exists(save318Path))
            return;   // skip gracefully when local backup directory is absent

        var hostBytes = WhsSave.ReadShared(save206Path);
        var joinBytes = WhsSave.ReadShared(save318Path);
        Assert.True(WhsSave.Verify(hostBytes).Ok);
        Assert.True(WhsSave.Verify(joinBytes).Ok);

        var parts = WhsSave.PartsFromFile(joinBytes, WhsSave.HenryParts.OriginSnapshot);
        Assert.True(parts.Extensions.TryGetValue(WhsSave.ReputationFactionsExtensionKey, out var fac) && fac.Length > 90_000);
        Assert.True(parts.Extensions.TryGetValue(WhsSave.ReputationHenryRenownExtensionKey, out var ren) && ren.Length > 0);
        Assert.True(parts.Extensions.TryGetValue(WhsSave.ReputationNpcOpinionsExtensionKey, out var ops) && ops.Length > 20_000);
        Assert.True(WhsSave.DecodeNpcOpinions(ops).Count >= 1400);

        var hblk = WhsSave.SerializeBlock(parts);
        var parsed = WhsSave.ParseBlock(hblk);
        Assert.Empty(WhsSave.DiffBlocks(parts, parsed));

        var emptyQ = new Dictionary<string, string>(StringComparer.Ordinal);
        var spliced = WhsSave.SpliceParts(hostBytes, parsed, emptyQ, WhsSave.QuestItemMode.Strip);
        Assert.True(WhsSave.Verify(spliced.File).Ok);
        var fails = WhsSave.CheckParts(hostBytes, parsed, spliced.File, emptyQ, WhsSave.QuestItemMode.Strip);
        Assert.Empty(fails);

        var outRaw = WhsSave.Inflate(spliced.File).Raw;
        var joinRaw = WhsSave.Inflate(joinBytes).Raw;
        Assert.Equal(
            WhsSave.Payload(joinRaw, WhsSave.PathGet(joinRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value),
            WhsSave.Payload(outRaw, WhsSave.PathGet(outRaw, 0x01F4, 0x01F8, 0x7308, 0x352C)!.Value));
        Assert.Equal(
            WhsSave.Payload(joinRaw, WhsSave.SoulFields(joinRaw, WhsSave.FindSoul(joinRaw, Henry)!.Value)[0x12FF][0]),
            WhsSave.Payload(outRaw, WhsSave.SoulFields(outRaw, WhsSave.FindSoul(outRaw, Henry)!.Value)[0x12FF][0]));
    }
}
