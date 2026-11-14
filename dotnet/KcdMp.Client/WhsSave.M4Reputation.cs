// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace KcdMp.Client;

public static partial class WhsSave
{
    public const string ReputationFactionsExtensionKey = "reputation_factions";
    public const string ReputationHenryRenownExtensionKey = "reputation_henry_renown";
    public const string ReputationNpcOpinionsExtensionKey = "reputation_npc_opinions";

    internal static readonly ushort[] FactionTreeBlock = [0x01F4, 0x01F8, 0x7308, 0x352C];

    private static readonly ConditionalWeakTable<byte[], Dictionary<string, byte[]>> DecodedNpcOpinionsCache = new();

    /// <summary>Real KCD2 0x352C FactionTree payloads parse as a non-empty TLV list (synthetic test strings do not).</summary>
    public static bool IsValidFactionTreePayload(byte[]? payload) =>
        payload is { Length: > 0 } && Children(payload, 0, payload.Length) is { Count: > 0 };

    /// <summary>Real KCD2 0x12FF soul reputation/renown payloads parse as a TLV list whose first node is 0x1D2C.</summary>
    public static bool IsValidReputationPayload(byte[]? payload) =>
        payload is { Length: > 0 } && Children(payload, 0, payload.Length) is { Count: > 0 } kids && kids[0].Tag == 0x1D2C;

    /// <summary>
    /// Cross-playthrough import guard: if both host and joiner carry playthrough seeds and they differ,
    /// the joiner is importing an external solo character into this campaign for the first time, so the
    /// host world's faction tree and reputation stay intact until the joiner snapshots in this world.
    /// </summary>
    public static bool ShouldSpliceReputation(byte[] hostRaw, HenryParts parts) =>
        !(parts.Seed is uint js && ReadSeed(hostRaw) is uint hs && js != hs);

    public static byte[] EncodeNpcOpinions(IReadOnlyList<(byte[] Guid16, byte[] Payload)> entries)
    {
        if (entries.Count == 0) return [];
        int total = 4;
        foreach (var (g, p) in entries)
        {
            if (g.Length != 16) throw new ArgumentException("soul GUID must be 16 bytes", nameof(entries));
            total += 16 + 4 + p.Length;
        }
        var buf = new byte[total];
        BinaryPrimitives.WriteInt32LittleEndian(buf, entries.Count);
        int pos = 4;
        foreach (var (g, p) in entries)
        {
            g.CopyTo(buf.AsSpan(pos, 16));
            pos += 16;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), p.Length);
            pos += 4;
            p.CopyTo(buf.AsSpan(pos, p.Length));
            pos += p.Length;
        }
        return buf;
    }

    public static Dictionary<string, byte[]> DecodeNpcOpinions(byte[] blob)
    {
        if (blob.Length == 0) return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (DecodedNpcOpinionsCache.TryGetValue(blob, out var cached)) return cached;

        if (blob.Length < 4) throw new InvalidDataException("reputation_npc_opinions header truncated");
        int count = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (count < 0 || count > 100_000) throw new InvalidDataException($"reputation_npc_opinions count {count} out of range");

        var map = new Dictionary<string, byte[]>(count, StringComparer.Ordinal);
        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            if (blob.Length - pos < 20) throw new InvalidDataException("reputation_npc_opinions entry header truncated");
            string guid = GuidStr(blob.AsSpan(pos, 16));
            pos += 16;
            int len = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(pos, 4));
            pos += 4;
            if (len <= 0 || blob.Length - pos < len) throw new InvalidDataException($"reputation_npc_opinions length {len} out of range");
            var pay = blob.AsSpan(pos, len).ToArray();
            pos += len;
            if (!IsValidReputationPayload(pay))
                throw new InvalidDataException($"reputation_npc_opinions payload for {guid} is not a valid 0x1D2C TLV");
            map[guid] = pay;
        }
        if (pos != blob.Length) throw new InvalidDataException("reputation_npc_opinions has trailing bytes");

        DecodedNpcOpinionsCache.AddOrUpdate(blob, map);
        return map;
    }

    private sealed class M4ReputationSaveExtension : ISaveExtension
    {
        public static readonly M4ReputationSaveExtension Instance = new();
        public int Order => 20;

        public void Extract(byte[] raw, HenryParts parts, HashSet<string> henryOwnedItemIds)
        {
            if (PathGet(raw, FactionTreeBlock) is Node n352c && n352c.Len > 0)
            {
                var pay352c = Payload(raw, n352c);
                if (IsValidFactionTreePayload(pay352c))
                    parts.Extensions[ReputationFactionsExtensionKey] = pay352c;
            }

            if (HasField(parts.Record, 0x12FF))
            {
                var jr = FieldChain(parts.Record, 0x12FF)[^1];
                var payHenry = Payload(parts.Record, jr);
                if (IsValidReputationPayload(payHenry))
                    parts.Extensions[ReputationHenryRenownExtensionKey] = payHenry;
            }

            var npcEntries = new List<(byte[] Guid16, byte[] Payload)>();
            var seenGuids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rec in SoulList(raw))
            {
                if (rec.Tag != 0x115E || rec.Len < 32) continue;
                var gSpan = raw.AsSpan(rec.Off + 6, 16);
                string guid = GuidStr(gSpan);
                if (guid == HenrySoul || !seenGuids.Add(guid)) continue;
                var fields = SoulFields(raw, rec);
                if (!fields.TryGetValue(0x12FF, out var rlist) || rlist.Count == 0) continue;
                var payNpc = Payload(raw, rlist[0]);
                if (IsValidReputationPayload(payNpc))
                    npcEntries.Add((gSpan.ToArray(), payNpc));
            }
            if (npcEntries.Count > 0)
                parts.Extensions[ReputationNpcOpinionsExtensionKey] = EncodeNpcOpinions(npcEntries);
        }

        public byte[] TransformHenryRecord(byte[] hostRaw, byte[] hostHenryNodeBytes, byte[] builtHenryNodeBytes,
                                           HenryParts parts, Dictionary<string, string> qclasses, QuestItemMode mode, SpliceReport rep)
        {
            if (!ShouldSpliceReputation(hostRaw, parts)) return builtHenryNodeBytes;
            if (!parts.Extensions.TryGetValue(ReputationHenryRenownExtensionKey, out var payHenry) || payHenry.Length == 0)
                return builtHenryNodeBytes;
            if (!IsValidReputationPayload(payHenry))
                throw new InvalidDataException("reputation_henry_renown extension is not a valid 0x1D2C TLV");

            if (HasField(builtHenryNodeBytes, 0x12FF))
            {
                var ch = FieldChain(builtHenryNodeBytes, 0x12FF);
                return Payload(builtHenryNodeBytes, ch[^1]).AsSpan().SequenceEqual(payHenry)
                    ? builtHenryNodeBytes
                    : Replace(builtHenryNodeBytes, ch, payHenry);
            }
            return AppendField(builtHenryNodeBytes, 0x12FF, payHenry);
        }

        public byte[] TransformSoulRecord(string soulGuid, byte[] hostRaw, byte[] currentSoulNodeBytes,
                                          HenryParts parts, Dictionary<string, string> qclasses, QuestItemMode mode, SpliceReport rep)
        {
            if (soulGuid == HenrySoul || !ShouldSpliceReputation(hostRaw, parts)) return currentSoulNodeBytes;
            if (!parts.Extensions.TryGetValue(ReputationNpcOpinionsExtensionKey, out var blob) || blob.Length == 0)
                return currentSoulNodeBytes;

            var opinions = DecodeNpcOpinions(blob);
            if (currentSoulNodeBytes.Length < 6 || RecordNode(currentSoulNodeBytes).Tag != 0x115E)
                return currentSoulNodeBytes;   // short 0x115D soul record in host world: leave untouched
            if (!opinions.TryGetValue(soulGuid, out var payNpc))
                return currentSoulNodeBytes;

            if (HasField(currentSoulNodeBytes, 0x12FF))
            {
                var ch = FieldChain(currentSoulNodeBytes, 0x12FF);
                return Payload(currentSoulNodeBytes, ch[^1]).AsSpan().SequenceEqual(payNpc)
                    ? currentSoulNodeBytes
                    : Replace(currentSoulNodeBytes, ch, payNpc);
            }
            return AppendField(currentSoulNodeBytes, 0x12FF, payNpc);
        }

        public void AddBlockJobs(byte[] hostRaw, HenryParts parts, Dictionary<string, string> qclasses,
                                 QuestItemMode mode, SpliceReport rep, List<(ushort[] Tags, byte[] Payload)> jobs)
        {
            if (!ShouldSpliceReputation(hostRaw, parts)) return;
            if (!parts.Extensions.TryGetValue(ReputationFactionsExtensionKey, out var pay352c) || pay352c.Length == 0) return;
            if (!IsValidFactionTreePayload(pay352c))
                throw new InvalidDataException("reputation_factions extension is not a valid TLV stream");
            if (PathGet(hostRaw, FactionTreeBlock) is not null)
                jobs.Add((FactionTreeBlock, pay352c));
        }

        public void AddSplicedPaths(byte[] hostRaw, HenryParts parts, HashSet<string> splicedPaths)
        {
            if (!ShouldSpliceReputation(hostRaw, parts)) return;
            if (parts.Extensions.TryGetValue(ReputationFactionsExtensionKey, out var pay352c)
                && IsValidFactionTreePayload(pay352c)
                && PathGet(hostRaw, FactionTreeBlock) is not null)
            {
                splicedPaths.Add(PathText(FactionTreeBlock));
            }
        }

        public void CheckSpliced(byte[] hostRaw, byte[] outRaw, HenryParts parts, Dictionary<string, string> qclasses,
                                 QuestItemMode mode, List<string> fails)
        {
            bool splice = ShouldSpliceReputation(hostRaw, parts);
            if (PathGet(hostRaw, FactionTreeBlock) is Node h352c)
            {
                if (PathGet(outRaw, FactionTreeBlock) is not Node o352c)
                {
                    fails.Add("FactionTree 01f4/01f8/7308/352c missing in output");
                }
                else if (splice && parts.Extensions.TryGetValue(ReputationFactionsExtensionKey, out var want352c) && want352c.Length > 0)
                {
                    if (!IsValidFactionTreePayload(want352c) || !Payload(outRaw, o352c).AsSpan().SequenceEqual(want352c))
                        fails.Add("spliced block is not the joiner's: " + PathText(FactionTreeBlock));
                }
                else if (!Payload(outRaw, o352c).AsSpan().SequenceEqual(Payload(hostRaw, h352c)))
                {
                    fails.Add("FactionTree 01f4/01f8/7308/352c differs from expected host block");
                }
            }

            if (splice && parts.Extensions.TryGetValue(ReputationHenryRenownExtensionKey, out var wantHenry) && wantHenry.Length > 0)
            {
                if (FindSoul(outRaw, HenrySoul) is not Node oh || !SoulFields(outRaw, oh).TryGetValue(0x12FF, out var hf)
                    || !Payload(outRaw, hf[0]).AsSpan().SequenceEqual(wantHenry))
                {
                    fails.Add("Henry 0x12FF renown is not the joiner's");
                }
            }

            if (splice && parts.Extensions.TryGetValue(ReputationNpcOpinionsExtensionKey, out var npcBlob) && npcBlob.Length > 0)
            {
                try
                {
                    var opinions = DecodeNpcOpinions(npcBlob);
                    foreach (var rec in SoulList(outRaw))
                    {
                        if (rec.Tag != 0x115E || rec.Len < 32) continue;
                        string guid = GuidStr(outRaw.AsSpan(rec.Off + 6, 16));
                        if (guid == HenrySoul || !opinions.TryGetValue(guid, out var wantNpc)) continue;
                        if (!SoulFields(outRaw, rec).TryGetValue(0x12FF, out var nf) || !Payload(outRaw, nf[0]).AsSpan().SequenceEqual(wantNpc))
                            fails.Add($"NPC {guid} 0x12FF opinion is not the joiner's");
                    }
                }
                catch (InvalidDataException ex)
                {
                    fails.Add("reputation_npc_opinions invalid: " + ex.Message);
                }
            }
        }
    }

    internal static class M4ReputationModuleInitializer
    {
        [ModuleInitializer]
        internal static void Init() => RegisterExtension(M4ReputationSaveExtension.Instance);
    }
}
