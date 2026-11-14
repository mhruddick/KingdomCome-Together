// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace KcdMp.Client;

/// <summary>
/// Milestone 3: independent horse ownership and saddlebag/tack persistence via <see cref="WhsSave.ISaveExtension"/> (<c>Order = 10</c>).
/// Splices Henry's horse companion link in <c>01f4/01f8/7308/3529/1160</c> (preserving Mutt and NPC companions), splices the joiner's
/// horse soul <c>0x1301</c> inventory/tack and <c>0x12FB</c> stats in <c>01f4/01f8/7308/3529/1161</c> (preserving <c>0x12F9</c> entity
/// binding and <c>0x12FF</c> reputation for downstream extensions), and routes saddlebag key bindings in <c>01f4/01f8/7302/000B</c>.
/// </summary>
public static partial class WhsSave
{
    public const string HorseCompanionExtensionKey = "horse_companion";
    public const string MuttSharedSoul = "44a28861-719d-9fed-e3dd-b20c479e8781";   // tvez_vorech (soul_archetype_id="8")
    internal static readonly ushort[] CompanionBlock = [0x01F4, 0x01F8, 0x7308, 0x3529, 0x1160];

    private static readonly HashSet<string> KnownNonHorseSharedSouls = new(StringComparer.OrdinalIgnoreCase)
    {
        HenrySoul,
        BohutaSoul,
        MuttSharedSoul,
        "4ffab0bb-617a-a998-8c03-73d409edc8b2",   // animal_wild_dog
        "4397dba0-e987-4615-b8e8-5314f76b1f67",   // animal_wildDog_kutnohorsko
        "f5e69211-1f58-4f92-923a-14613647c9a0",   // animal_wildDog_trosecko
    };

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterM3HorseSaveExtension() => RegisterExtension(new M3HorseSaveExtension());
#pragma warning restore CA2255

    public readonly record struct CompanionEntry(string MasterGuid, string CompanionGuid, byte[] RawNodeBytes);

    /// <summary>
    /// Parse <c>01f4/01f8/7308/3529/1160</c>. Real empty saves have <c>Len == 0</c>; synthetic <c>WhsSaveTests</c>
    /// fixtures write 4 zero bytes (<c>isSyntheticZeroPlaceholder = true</c>); populated saves hold <c>0x0DD6</c> TLV nodes.
    /// </summary>
    internal static bool TryReadCompanionEntries(byte[] raw, out List<CompanionEntry> entries, out bool isSyntheticZeroPlaceholder)
    {
        entries = [];
        isSyntheticZeroPlaceholder = false;
        if (PathGet(raw, CompanionBlock) is not Node n1160) return false;
        if (n1160.Len == 0) return true;
        if (n1160.Len == 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(n1160.PayloadOff, 4)) == 0u)
        {
            isSyntheticZeroPlaceholder = true;
            return true;
        }
        var kids = Children(raw, n1160.PayloadOff, n1160.End)
            ?? throw new InvalidDataException("RPGModule 1160 companion list does not parse as TLV");
        foreach (var k in kids)
        {
            var nodeBytes = NodeBytes(raw, k);
            if (k.Tag == 0x0DD6 && k.Len == 36)
            {
                ushort mTag = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(k.PayloadOff));
                ushort cTag = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(k.PayloadOff + 18));
                if (mTag != 0x0501 || cTag != 0x0501)
                    throw new InvalidDataException($"unexpected 0x0DD6 companion subtags 0x{mTag:x4}/0x{cTag:x4}");
                string master = GuidStr(raw.AsSpan(k.PayloadOff + 2, 16));
                string comp = GuidStr(raw.AsSpan(k.PayloadOff + 20, 16));
                entries.Add(new CompanionEntry(master, comp, nodeBytes));
            }
            else
            {
                entries.Add(new CompanionEntry("", "", nodeBytes));
            }
        }
        return true;
    }

    public static List<(string MasterGuid, string CompanionGuid)> ReadCompanionLinks(byte[] raw) =>
        TryReadCompanionEntries(raw, out var entries, out _)
            ? entries.Where(e => e.MasterGuid.Length > 0).Select(e => (e.MasterGuid, e.CompanionGuid)).ToList()
            : [];

    /// <summary>True when <paramref name="soulGuid"/> resolves in <c>1161</c> to a horse soul (never Mutt, dogs, wolves, or player souls).</summary>
    public static bool IsHorseCompanionSoul(byte[] raw, string soulGuid, out Node soulNode)
    {
        soulNode = default;
        if (string.IsNullOrEmpty(soulGuid)) return false;
        if (KnownNonHorseSharedSouls.Contains(soulGuid)) return false;
        if (FindSoul(raw, soulGuid) is not Node rec || rec.Len < 32) return false;
        string instGuid = GuidStr(raw.AsSpan(rec.PayloadOff, 16));
        string sharedGuid = GuidStr(raw.AsSpan(rec.PayloadOff + 16, 16));
        if (KnownNonHorseSharedSouls.Contains(instGuid) || KnownNonHorseSharedSouls.Contains(sharedGuid)) return false;
        if (rec.Tag == 0x115E && SoulFields(raw, rec).TryGetValue(0x12F9, out var nf) && nf.Count > 0)
        {
            var b = Payload(raw, nf[0]);
            int nul = Array.IndexOf(b, (byte)0);
            if (nul < 0) nul = b.Length;
            string name = Encoding.Latin1.GetString(b, 0, nul);
            if (name.Equals("Dude", StringComparison.OrdinalIgnoreCase)
                || name.Equals("player_henry", StringComparison.OrdinalIgnoreCase)
                || name.Equals("player_bohuta", StringComparison.OrdinalIgnoreCase)
                || name.Equals("tvez_vorech", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!name.Contains("horse", StringComparison.OrdinalIgnoreCase)
                && (name.Contains("vorech", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("dog", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("wolf", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("hound", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("mutt", StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        soulNode = rec;
        return true;
    }

    /// <summary>Henry's owned horse soul GUID in <c>1160</c>, or null if Henry owns no horse.</summary>
    public static string? ReadHenryHorseGuid(byte[] raw)
    {
        if (!TryReadCompanionEntries(raw, out var entries, out _)) return null;
        string? found = null;
        foreach (var e in entries)
            if (e.MasterGuid == HenrySoul && IsHorseCompanionSoul(raw, e.CompanionGuid, out _))
                found = e.CompanionGuid;
        return found;
    }

    internal static byte[] BuildCompanionLinkNode(string masterGuid, string companionGuid)
    {
        var b = new byte[6 + 36];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 0x0DD6);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), 36u);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 0x0501);
        new Guid(masterGuid).ToByteArray().CopyTo(b, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), 0x0501);
        new Guid(companionGuid).ToByteArray().CopyTo(b, 26);
        return b;
    }

    internal static byte[] EncodeNoHorseCompanion() => [0];

    internal static byte[] EncodeHorseCompanion(string horseGuid, byte[] horseSoulNodeBytes)
    {
        var g = new Guid(horseGuid).ToByteArray();
        var o = new byte[1 + 16 + 4 + horseSoulNodeBytes.Length];
        o[0] = 1;
        g.CopyTo(o, 1);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(17), horseSoulNodeBytes.Length);
        horseSoulNodeBytes.CopyTo(o, 21);
        return o;
    }

    internal static void DecodeHorseCompanionExtension(byte[] ext, out bool hasHorse, out string horseGuid, out byte[] horseSoulNodeBytes)
    {
        if (ext is null || ext.Length < 1)
            throw new InvalidDataException("horse_companion extension is empty");
        byte mode = ext[0];
        if (mode == 0)
        {
            if (ext.Length != 1) throw new InvalidDataException("horse_companion no-horse payload has trailing bytes");
            hasHorse = false;
            horseGuid = "";
            horseSoulNodeBytes = [];
            return;
        }
        if (mode != 1 || ext.Length < 21)
            throw new InvalidDataException("horse_companion extension header malformed");
        horseGuid = GuidStr(ext.AsSpan(1, 16));
        int nodeLen = BinaryPrimitives.ReadInt32LittleEndian(ext.AsSpan(17));
        if (nodeLen < 38 || ext.Length != 21 + nodeLen)
            throw new InvalidDataException("horse_companion soul record length mismatch");
        horseSoulNodeBytes = ext.AsSpan(21, nodeLen).ToArray();
        var rn = RecordNode(horseSoulNodeBytes);
        if (rn.Tag != 0x115E || 6 + rn.Len != horseSoulNodeBytes.Length)
            throw new InvalidDataException("horse_companion soul record is not a valid 0x115E node");
        if (GuidStr(horseSoulNodeBytes.AsSpan(6, 16)) != horseGuid)
            throw new InvalidDataException("horse_companion soul GUID does not match header GUID");
        if (Children(horseSoulNodeBytes, 6 + 32, horseSoulNodeBytes.Length) is null)
            throw new InvalidDataException("horse_companion soul record fields do not parse");
        hasHorse = true;
    }

    /// <summary>Decode all <c>0x0001</c> inventory items inside a standalone or in-stream <c>0x115E</c> soul record.</summary>
    public static List<InvItem> DecodeSoulInventoryItems(byte[] raw, Node rec)
    {
        var items = new List<InvItem>();
        if (rec.Tag != 0x115E) return items;
        var f = SoulFields(raw, rec);
        if (!f.TryGetValue(0x1301, out var invf) || invf.Count == 0) return items;
        var inv = KidsOf(raw, invf[0]);
        if (!inv.TryGetValue(0x0007, out var lst) || lst.Count == 0) return items;
        foreach (var node in Children(raw, lst[0].PayloadOff, lst[0].End) ?? [])
        {
            if (node.Tag == 0x0000) continue;
            if (node.Tag != 0x0001 || node.Len < 36) continue;
            var b = Payload(raw, node);
            var ps = new List<string>();
            foreach (var pn in Children(b, 36, b.Length) ?? [])
            {
                var v = b.AsSpan(pn.PayloadOff, pn.Len);
                if (pn.Tag == 0 && pn.Len == 4) ps.Add($"amount={BinaryPrimitives.ReadUInt32LittleEndian(v) + 1}");
                else ps.Add($"p{pn.Tag:x}={Convert.ToHexString(v).ToLowerInvariant()}");
            }
            items.Add(new InvItem(GuidStr(b), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)), GuidStr(b.AsSpan(20)), string.Join(";", ps)));
        }
        return items;
    }

    /// <summary>
    /// Read all equipped item <c>instanceGuid</c>s on a standalone <c>0x115E</c> soul record, supporting both horse/NPC
    /// <c>0x1301/0x0001/0x0000</c> equipment blocks and player preset <c>0x1301/0x0006/*/0x0000</c> blocks.
    /// </summary>
    public static List<string> ReadSoulEquippedInstances(byte[] rec)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddGuid(ReadOnlySpan<byte> b16)
        {
            var g = new Guid(b16[..16]);
            if (g == Guid.Empty) return;
            string s = g.ToString("D");
            if (seen.Add(s)) list.Add(s);
        }

        if (!HasField(rec, 0x1301)) return list;
        var ch1301 = FieldChain(rec, 0x1301)[^1];
        foreach (var child in Children(rec, ch1301.PayloadOff, ch1301.End) ?? [])
        {
            if (child.Tag == 0x0001)
            {
                foreach (var sub in Children(rec, child.PayloadOff, child.End) ?? [])
                    if (sub.Tag == 0x0000)
                        for (int off = 0; off + 16 <= sub.Len; off += 16)
                            AddGuid(rec.AsSpan(sub.PayloadOff + off, 16));
            }
            else if (child.Tag == 0x0006)
            {
                foreach (var pr in Children(rec, child.PayloadOff, child.End) ?? [])
                {
                    if (pr.Tag == 0x0000) continue;
                    foreach (var sub in Children(rec, pr.PayloadOff, pr.End) ?? [])
                        if (sub.Tag == 0x0000)
                            for (int off = 0; off + 16 <= sub.Len; off += 16)
                                AddGuid(rec.AsSpan(sub.PayloadOff + off, 16));
                }
            }
        }
        return list;
    }

    /// <summary>Extract sorted distinct equipped tack item class GUIDs from a joiner's <c>horse_companion</c> extension.</summary>
    public static List<string> ExtractHorseEquippedTackClasses(HenryParts parts)
    {
        if (parts is null || !parts.Extensions.TryGetValue(HorseCompanionExtensionKey, out var ext) || ext.Length <= 1)
            return [];
        DecodeHorseCompanionExtension(ext, out bool hasHorse, out _, out var horseRec);
        if (!hasHorse || !HasItemList(horseRec)) return [];
        var (_, items) = InventoryItems(horseRec);
        var classByInst = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, inst, cls) in items) classByInst[inst] = cls;
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var eqInst in ReadSoulEquippedInstances(horseRec))
            if (classByInst.TryGetValue(eqInst, out var cls))
                classes.Add(cls);
        return classes.ToList();
    }

    private static byte[] StripRemovedFromHorseEquipment0001(byte[] rec, HashSet<string> removedInstances)
    {
        if (removedInstances.Count == 0 || !HasField(rec, 0x1301)) return rec;
        List<Node> ch0001;
        try { ch0001 = FieldChain(rec, 0x1301, 0x0001); }
        catch (InvalidDataException) { return rec; }
        var n0001 = ch0001[^1];
        if (n0001.Len == 0) return rec;
        var subs = Children(rec, n0001.PayloadOff, n0001.End);
        if (subs is null) return rec;

        Span<byte> hdr = stackalloc byte[6];
        using var ms = new MemoryStream();
        bool changed = false;
        foreach (var sub in subs)
        {
            if (sub.Tag != 0x0000)
            {
                ms.Write(rec, sub.Off, 6 + sub.Len);
                continue;
            }
            using var kept = new MemoryStream();
            for (int off = 0; off + 16 <= sub.Len; off += 16)
            {
                var slice = rec.AsSpan(sub.PayloadOff + off, 16);
                if (removedInstances.Contains(GuidStr(slice))) { changed = true; continue; }
                kept.Write(slice);
            }
            if (kept.Length > 0)
            {
                var kp = kept.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x0000);
                BinaryPrimitives.WriteUInt32LittleEndian(hdr[2..], (uint)kp.Length);
                ms.Write(hdr);
                ms.Write(kp);
            }
            else changed = true;
        }
        return changed ? Replace(rec, ch0001, ms.ToArray()) : rec;
    }

    private static byte[] CleanHorseSoulRecord(byte[] horseRec, Dictionary<string, string> qclasses, SpliceReport? rep)
    {
        if (!HasItemList(horseRec) || qclasses.Count == 0) return horseRec;
        var (ch, items) = InventoryItems(horseRec);
        var drop = items.Where(it => qclasses.ContainsKey(it.Cls)).ToList();
        if (drop.Count == 0) return horseRec;

        var lst = ch[^1];
        var body = new List<byte>(Payload(horseRec, lst));
        var removedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in drop.OrderByDescending(x => x.Node.Off))
        {
            body.RemoveRange(d.Node.Off - lst.PayloadOff, 6 + d.Node.Len);
            removedIds.Add(d.Inst);
        }
        foreach (var d in drop)
            rep?.QuestItemsRemoved.Add(qclasses[d.Cls]);

        var cleaned = Replace(horseRec, ch, body.ToArray());
        cleaned = StripRemovedFromHorseEquipment0001(cleaned, removedIds);
        cleaned = StripRemovedFromEquipment(cleaned, removedIds);
        return cleaned;
    }

    private static byte[] MergeHorseSoulIntoHost(byte[] hostSoulNodeBytes, byte[] cleanedJoinerHorseBytes)
    {
        var hostNode = RecordNode(hostSoulNodeBytes);
        if (hostNode.Tag != 0x115E)
        {
            // Promote minimal 0x115D soul using host's 32-byte GUID prefix + joiner's 0x115E fields
            var promoted = (byte[])cleanedJoinerHorseBytes.Clone();
            if (hostSoulNodeBytes.Length >= 6 + 32)
                hostSoulNodeBytes.AsSpan(6, 32).CopyTo(promoted.AsSpan(6, 32));
            return promoted;
        }

        var hostKids = Children(hostSoulNodeBytes, 6 + 32, hostSoulNodeBytes.Length)
            ?? throw new InvalidDataException("host horse 0x115E record does not parse");
        var joinKids = Children(cleanedJoinerHorseBytes, 6 + 32, cleanedJoinerHorseBytes.Length)
            ?? throw new InvalidDataException("joiner horse 0x115E record does not parse");

        Node? j12fb = joinKids.FirstOrDefault(k => k.Tag == 0x12FB);
        bool hasJ12fb = joinKids.Any(k => k.Tag == 0x12FB);
        Node? j1301 = joinKids.FirstOrDefault(k => k.Tag == 0x1301);
        bool hasJ1301 = joinKids.Any(k => k.Tag == 0x1301);

        using var ms = new MemoryStream();
        ms.Write(hostSoulNodeBytes, 6, 32);   // [16B instGuid][16B sharedGuid]
        bool wrote12fb = false, wrote1301 = false;
        foreach (var hk in hostKids)
        {
            if (hk.Tag == 0x12FB)
            {
                if (hasJ12fb) ms.Write(cleanedJoinerHorseBytes, j12fb!.Value.Off, 6 + j12fb.Value.Len);
                else ms.Write(hostSoulNodeBytes, hk.Off, 6 + hk.Len);
                wrote12fb = true;
            }
            else if (hk.Tag == 0x1301)
            {
                // Replace with joiner's 0x1301, or omit when joiner's horse carries no 0x1301
                if (hasJ1301) ms.Write(cleanedJoinerHorseBytes, j1301!.Value.Off, 6 + j1301.Value.Len);
                wrote1301 = true;
            }
            else
            {
                // Preserves 0x12F9 (entity binding), 0x12FF (reputation for M4 Order=20), 0x1303, etc.
                ms.Write(hostSoulNodeBytes, hk.Off, 6 + hk.Len);
            }
        }
        if (!wrote12fb && hasJ12fb)
            ms.Write(cleanedJoinerHorseBytes, j12fb!.Value.Off, 6 + j12fb.Value.Len);
        if (!wrote1301 && hasJ1301)
            ms.Write(cleanedJoinerHorseBytes, j1301!.Value.Off, 6 + j1301.Value.Len);

        var pay = ms.ToArray();
        var outNode = new byte[6 + pay.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(outNode, 0x115E);
        BinaryPrimitives.WriteUInt32LittleEndian(outNode.AsSpan(2), (uint)pay.Length);
        pay.CopyTo(outNode, 6);
        return outNode;
    }

    internal static byte[] BuildMergedCompanionPayload(byte[] hostRaw, HenryParts parts)
    {
        if (!parts.Extensions.TryGetValue(HorseCompanionExtensionKey, out var ext))
            return PathGet(hostRaw, CompanionBlock) is Node n0 ? Payload(hostRaw, n0) : [];
        DecodeHorseCompanionExtension(ext, out bool hasHorse, out string jHorseGuid, out _);
        if (!TryReadCompanionEntries(hostRaw, out var hostEntries, out _)) return [];

        using var ms = new MemoryStream();
        foreach (var e in hostEntries)
        {
            if (e.MasterGuid == HenrySoul && IsHorseCompanionSoul(hostRaw, e.CompanionGuid, out _))
                continue;   // drop Host Henry's horse link; keep Mutt and all NPC companions
            ms.Write(e.RawNodeBytes);
        }
        if (hasHorse && FindSoul(hostRaw, jHorseGuid) is not null)
            ms.Write(BuildCompanionLinkNode(HenrySoul, jHorseGuid));
        return ms.ToArray();
    }

    private sealed class M3HorseSaveExtension : ISaveExtension
    {
        public int Order => 10;

        public void Extract(byte[] raw, HenryParts parts, HashSet<string> henryOwnedItemIds)
        {
            if (!TryReadCompanionEntries(raw, out var entries, out bool isSyntheticZeroPlaceholder)) return;
            if (isSyntheticZeroPlaceholder) return;   // leave legacy synthetic 4-byte 00 00 00 00 fixtures untouched

            string? horseGuid = null;
            Node horseNode = default;
            foreach (var e in entries)
            {
                if (e.MasterGuid == HenrySoul && IsHorseCompanionSoul(raw, e.CompanionGuid, out var hn))
                {
                    horseGuid = e.CompanionGuid;
                    horseNode = hn;
                }
            }
            if (horseGuid is not null && horseNode.Tag == 0x115E)
            {
                var horseBytes = NodeBytes(raw, horseNode);
                parts.Extensions[HorseCompanionExtensionKey] = EncodeHorseCompanion(horseGuid, horseBytes);
                henryOwnedItemIds.UnionWith(RecordItemSet(horseBytes));
            }
            else
            {
                parts.Extensions[HorseCompanionExtensionKey] = EncodeNoHorseCompanion();
            }
        }

        public void CollectHostPrivateItemIds(byte[] hostRaw, HashSet<string> hostPrivateItemIds)
        {
            if (ReadHenryHorseGuid(hostRaw) is not string hHorseGuid) return;
            if (FindSoul(hostRaw, hHorseGuid) is Node hNode && hNode.Tag == 0x115E)
                hostPrivateItemIds.UnionWith(RecordItemSet(NodeBytes(hostRaw, hNode)));
        }

        public byte[] TransformSoulRecord(string soulGuid, byte[] hostRaw, byte[] currentSoulNodeBytes,
                                          HenryParts parts, Dictionary<string, string> qclasses, QuestItemMode mode, SpliceReport rep)
        {
            if (!parts.Extensions.TryGetValue(HorseCompanionExtensionKey, out var ext)) return currentSoulNodeBytes;
            DecodeHorseCompanionExtension(ext, out bool hasHorse, out string jHorseGuid, out var jHorseNodeBytes);
            if (!hasHorse || soulGuid != jHorseGuid) return currentSoulNodeBytes;
            var cleanedJoinerHorse = CleanHorseSoulRecord(jHorseNodeBytes, qclasses, rep);
            return MergeHorseSoulIntoHost(currentSoulNodeBytes, cleanedJoinerHorse);
        }

        public void AddBlockJobs(byte[] hostRaw, HenryParts parts, Dictionary<string, string> qclasses,
                                 QuestItemMode mode, SpliceReport rep, List<(ushort[] Tags, byte[] Payload)> jobs)
        {
            if (!parts.Extensions.ContainsKey(HorseCompanionExtensionKey)) return;
            if (PathGet(hostRaw, CompanionBlock) is null) return;
            jobs.Add((CompanionBlock, BuildMergedCompanionPayload(hostRaw, parts)));
        }

        public void AddSplicedPaths(byte[] hostRaw, HenryParts parts, HashSet<string> splicedPaths)
        {
            if (parts.Extensions.ContainsKey(HorseCompanionExtensionKey) && PathGet(hostRaw, CompanionBlock) is not null)
                splicedPaths.Add("01f4/01f8/7308/3529/1160");
        }

        public void CheckSpliced(byte[] hostRaw, byte[] outRaw, HenryParts parts, Dictionary<string, string> qclasses,
                                 QuestItemMode mode, List<string> fails)
        {
            if (!parts.Extensions.TryGetValue(HorseCompanionExtensionKey, out var ext)) return;
            DecodeHorseCompanionExtension(ext, out bool hasHorse, out string jHorseGuid, out var jHorseNodeBytes);

            if (PathGet(hostRaw, CompanionBlock) is not null)
            {
                if (PathGet(outRaw, CompanionBlock) is not Node outComp)
                {
                    fails.Add("RPGModule 1160 companion block missing in output");
                    return;
                }
                var expComp = BuildMergedCompanionPayload(hostRaw, parts);
                if (!Payload(outRaw, outComp).AsSpan().SequenceEqual(expComp))
                    fails.Add("RPGModule 1160 companion block does not match expected merge");
            }

            if (hasHorse && FindSoul(hostRaw, jHorseGuid) is not null)
            {
                if (FindSoul(outRaw, jHorseGuid) is not Node outHorse || outHorse.Tag != 0x115E)
                {
                    fails.Add($"joiner horse soul {jHorseGuid} missing or not 0x115E in output");
                    return;
                }
                var expItems = DecodeSoulInventoryItems(jHorseNodeBytes, RecordNode(jHorseNodeBytes))
                    .Where(i => !qclasses.ContainsKey(i.Class))
                    .ToList();
                var actItems = DecodeSoulInventoryItems(outRaw, outHorse);
                if (!expItems.SequenceEqual(actItems))
                    fails.Add($"joiner horse {jHorseGuid} saddlebag inventory mismatch ({actItems.Count} vs {expItems.Count} items)");

                var cleanedRec = CleanHorseSoulRecord(jHorseNodeBytes, qclasses, null);
                var expEq = ReadSoulEquippedInstances(cleanedRec);
                var actEq = ReadSoulEquippedInstances(NodeBytes(outRaw, outHorse));
                if (!expEq.SequenceEqual(actEq))
                    fails.Add($"joiner horse {jHorseGuid} equipped tack mismatch");
            }
        }
    }
}
