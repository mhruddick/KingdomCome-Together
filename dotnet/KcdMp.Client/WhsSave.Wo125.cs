// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace KcdMp.Client;

/// <summary>
/// WO-125 -- continuity: a Henry that belongs to a world (docs/WO-125-findings.md).
///
/// <see cref="HenryParts"/> is the joiner side of the S3 splice, lifted out of a
/// save: the player_henry soul record, the six Henry side blocks and the
/// joiner's key-binding entries. A stored snapshot is exactly these bytes
/// (a "Henry block", <see cref="SerializeBlock"/>): splicing a block into a
/// host world gives the same stream as splicing the save it came from, byte
/// for byte (tested). No save header is stored (it carries the account name).
///
/// Also here: the playthrough seed (body 0x01FB, WO-112 s1.1: the same across
/// one playthrough, different across playthroughs -- save-verified on every
/// 1.5.5 playline on this machine) and "is the player in this save Henry"
/// (player_henry's soul is bound to the player entity 0x7777; in the prologue
/// and the Godwin stretch player_bohuta is -- save-verified on 194 saves).
/// </summary>
public static partial class WhsSave
{
    public const string BohutaSoul = "4666cffb-dea1-6263-72d7-b39f4db2d666";   // soul__player.xml player_bohuta (Godwin)
    public const ulong PlayerEntityGuid = 0x7777;                               // the player entity ("Dude") every Henry save binds to

    /// <summary>The joiner side of a splice.</summary>
    public sealed class HenryParts
    {
        public const string OriginSave = "save", OriginSnapshot = "snapshot", OriginFreshSave = "fresh-save", OriginFreshDefault = "fresh-default";

        /// <summary>The full 0x115E player_henry record: tag + length + payload.</summary>
        public byte[] Record = [];
        /// <summary>One payload per <see cref="SideBlocks"/> entry; null = the host's block stays.</summary>
        public byte[]?[] Side = new byte[]?[6];
        /// <summary>0x05AD key-binding entries (whole TLV nodes) naming one of this Henry's own items.</summary>
        public List<byte[]> KeyEntries = [];
        /// <summary>Named extension blobs carried by v2+ Henry blocks (empty on v1 blocks).</summary>
        public SortedDictionary<string, byte[]> Extensions = new(StringComparer.Ordinal);
        public string Build = "";
        public uint? Seed;
        public string Origin = OriginSave;
    }

    internal static Node RecordNode(byte[] rec) =>
        new(BinaryPrimitives.ReadUInt16LittleEndian(rec), 0, (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(2)));

    internal static string PathText(ushort[] tags) => string.Join("/", tags.Select(t => t.ToString("x4")));

    /// <summary>Modular save/block splice hook so independent subsystems (horses, reputation, etc.) compose cleanly on HenryParts and SoulList.</summary>
    public interface ISaveExtension
    {
        int Order { get; }
        void Extract(byte[] raw, HenryParts parts, HashSet<string> henryOwnedItemIds) { }
        void CollectHostPrivateItemIds(byte[] hostRaw, HashSet<string> hostPrivateItemIds) { }
        byte[] TransformHenryRecord(byte[] hostRaw, byte[] hostHenryNodeBytes, byte[] builtHenryNodeBytes,
                                    HenryParts parts, Dictionary<string, string> qclasses, QuestItemMode mode, SpliceReport rep) => builtHenryNodeBytes;
        byte[] TransformSoulRecord(string soulGuid, byte[] hostRaw, byte[] currentSoulNodeBytes,
                                   HenryParts parts, Dictionary<string, string> qclasses, QuestItemMode mode, SpliceReport rep) => currentSoulNodeBytes;
        void AddBlockJobs(byte[] hostRaw, HenryParts parts, Dictionary<string, string> qclasses,
                          QuestItemMode mode, SpliceReport rep, List<(ushort[] Tags, byte[] Payload)> jobs) { }
        void AddSplicedPaths(byte[] hostRaw, HenryParts parts, HashSet<string> splicedPaths) { }
        void CheckSpliced(byte[] hostRaw, byte[] outRaw, HenryParts parts, Dictionary<string, string> qclasses,
                          QuestItemMode mode, List<string> fails) { }
    }

    private static readonly object ExtensionLock = new();
    private static ISaveExtension[] ExtensionsSnapshot = [];

    public static void RegisterExtension(ISaveExtension ext)
    {
        ArgumentNullException.ThrowIfNull(ext);
        lock (ExtensionLock)
        {
            if (ExtensionsSnapshot.Any(x => x.GetType() == ext.GetType())) return;
            ExtensionsSnapshot = ExtensionsSnapshot.Append(ext).OrderBy(x => x.Order).ToArray();
        }
    }

    internal static ReadOnlySpan<ISaveExtension> RegisteredExtensions => ExtensionsSnapshot;

    /// <summary>Lift the joiner side out of a save's stream.</summary>
    public static HenryParts PartsFromStream(byte[] raw, string build, string origin)
    {
        var rec = NodeBytes(raw, HenryChain(raw)[^1]);
        var items = RecordItemSet(rec);
        var ps = ExtractPersonalStash(raw);
        items.UnionWith(PersonalStashItemSet(ps));
        var p = new HenryParts { Record = rec, Build = build, Seed = ReadSeed(raw), Origin = origin };
        p.Extensions[PersonalStashExtensionKey] = ps;
        foreach (var ext in RegisteredExtensions) ext.Extract(raw, p, items);
        for (int i = 0; i < SideBlocks.Length; i++) p.Side[i] = Payload(raw, PathNodes(raw, SideBlocks[i])[^1]);
        foreach (var (entry, key) in KeyEntries(raw).Entries)
            if (items.Contains(key)) p.KeyEntries.Add(entry);
        return p;
    }

    /// <summary>The parts of a save file (verified first).</summary>
    public static HenryParts PartsFromFile(byte[] file, string origin)
    {
        if (!VerifyFooter(file).Ok) throw new InvalidDataException("save does not verify; refusing");
        var c = Inflate(file);
        return PartsFromStream(c.Raw, DescriptionSummary(c.Desc).GetValueOrDefault("BuildInfo") ?? "", origin);
    }

    // ------------------------------------------------------------------ the stored block

    private static readonly byte[] BlockMagic = "KCDMPHB1"u8.ToArray();
    public const int BlockVersion = 2;
    public const int MaxBlockExtensions = 256;
    public const int MaxBlockExtensionBytes = 64 * 1024 * 1024;

    /// <summary>
    /// <code>
    ///   "KCDMPHB1" [u32 version=2] [str build] [u8 hasSeed][u32 seed] [str origin]
    ///   [u32 len][player_henry record]
    ///   [u8 6] { [i32 len (-1 = the host's block stays)][payload] } x6
    ///   [u32 n] { [u16 len][0x05AD entry] } x n
    ///   [i32 extCount] { [u16 keyLen][utf8 key][i32 valLen][bytes] } x extCount   (v2+)
    ///   [32-byte SHA-256 of everything before]
    /// </code>
    /// No save header, no world: nothing but Henry.
    /// </summary>
    public static byte[] SerializeBlock(HenryParts p) => SerializeBlockCore(p, BlockVersion);

    public static byte[] SerializeBlockV1(HenryParts p) => SerializeBlockCore(p, 1);

    private static byte[] SerializeBlockCore(HenryParts p, int version)
    {
        if (version >= 2 && p.Extensions.Count > MaxBlockExtensions)
            throw new InvalidDataException($"Henry block extension count {p.Extensions.Count} exceeds {MaxBlockExtensions}");
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(BlockMagic);
            w.Write(version);
            void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
            Str(p.Build);
            w.Write((byte)(p.Seed is null ? 0 : 1));
            w.Write(p.Seed ?? 0u);
            Str(p.Origin);
            w.Write(p.Record.Length);
            w.Write(p.Record);
            w.Write((byte)p.Side.Length);
            foreach (var s in p.Side)
            {
                w.Write(s is null ? -1 : s.Length);
                if (s is not null) w.Write(s);
            }
            w.Write(p.KeyEntries.Count);
            foreach (var e in p.KeyEntries) { w.Write((ushort)e.Length); w.Write(e); }
            if (version >= 2)
            {
                w.Write(p.Extensions.Count);
                foreach (var (ek, ev) in p.Extensions)
                {
                    if (string.IsNullOrEmpty(ek)) throw new InvalidDataException("Henry block extension key must not be empty");
                    var kb = Encoding.UTF8.GetBytes(ek);
                    if (kb.Length > ushort.MaxValue) throw new InvalidDataException("Henry block extension key length out of range");
                    if (ev is null || ev.Length > MaxBlockExtensionBytes) throw new InvalidDataException("Henry block extension value length out of range");
                    w.Write((ushort)kb.Length);
                    w.Write(kb);
                    w.Write(ev.Length);
                    w.Write(ev);
                }
            }
        }
        var body = ms.ToArray();
        var o = new byte[body.Length + 32];
        body.CopyTo(o, 0);
        SHA256.HashData(body).CopyTo(o, body.Length);
        return o;
    }

    /// <summary>Parse and fully check a stored block (v1 or v2). Throws <see cref="InvalidDataException"/> on anything wrong.</summary>
    public static HenryParts ParseBlock(byte[] b)
    {
        if (b.Length < BlockMagic.Length + 4 + 32 || !b.AsSpan(0, BlockMagic.Length).SequenceEqual(BlockMagic))
            throw new InvalidDataException("not a Henry block");
        if (!SHA256.HashData(b.AsSpan(0, b.Length - 32)).AsSpan().SequenceEqual(b.AsSpan(b.Length - 32)))
            throw new InvalidDataException("Henry block hash mismatch");
        var p = new HenryParts();
        using var r = new BinaryReader(new MemoryStream(b, 0, b.Length - 32), Encoding.UTF8);
        byte[] ExactBytes(int len)
        {
            if (len == 0) return [];
            if (len < 0 || r.BaseStream.Length - r.BaseStream.Position < len) throw new InvalidDataException("Henry block truncated");
            var buf = r.ReadBytes(len);
            if (buf.Length != len) throw new InvalidDataException("Henry block truncated");
            return buf;
        }
        try
        {
            ExactBytes(BlockMagic.Length);
            int ver = r.ReadInt32();
            if (ver is not (1 or BlockVersion)) throw new InvalidDataException($"Henry block version {ver}");
            string Str() => Encoding.UTF8.GetString(ExactBytes(r.ReadUInt16()));
            p.Build = Str();
            bool hasSeed = r.ReadByte() == 1;
            uint seed = r.ReadUInt32();
            p.Seed = hasSeed ? seed : null;
            p.Origin = Str();
            int rl = r.ReadInt32();
            if (rl < 38 || rl > 16 * 1024 * 1024) throw new InvalidDataException("Henry block record length out of range");
            p.Record = ExactBytes(rl);
            int n = r.ReadByte();
            if (n != SideBlocks.Length) throw new InvalidDataException($"Henry block carries {n} side blocks, not {SideBlocks.Length}");
            p.Side = new byte[]?[n];
            for (int i = 0; i < n; i++)
            {
                int l = r.ReadInt32();
                if (l < -1 || l > MaxBlockExtensionBytes) throw new InvalidDataException("Henry block side length out of range");
                p.Side[i] = l < 0 ? null : ExactBytes(l);
            }
            int k = r.ReadInt32();
            if (k < 0 || k > 100000) throw new InvalidDataException("Henry block key count out of range");
            for (int i = 0; i < k; i++) p.KeyEntries.Add(ExactBytes(r.ReadUInt16()));
            if (ver >= 2)
            {
                int extCount = r.ReadInt32();
                if (extCount < 0 || extCount > MaxBlockExtensions) throw new InvalidDataException("Henry block extension count out of range");
                for (int i = 0; i < extCount; i++)
                {
                    int keyLen = r.ReadUInt16();
                    if (keyLen == 0) throw new InvalidDataException("Henry block extension key must not be empty");
                    string key = Encoding.UTF8.GetString(ExactBytes(keyLen));
                    int valLen = r.ReadInt32();
                    if (valLen < 0 || valLen > MaxBlockExtensionBytes) throw new InvalidDataException("Henry block extension value length out of range");
                    var val = ExactBytes(valLen);
                    if (!p.Extensions.TryAdd(key, val)) throw new InvalidDataException($"Henry block duplicate extension key '{key}'");
                }
            }
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Henry block has trailing bytes");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Henry block truncated"); }
        if (p.Record.Length != 6 + RecordNode(p.Record).Len || RecordNode(p.Record).Tag != 0x115E || GuidStr(p.Record.AsSpan(6)) != HenrySoul)
            throw new InvalidDataException("Henry block record is not a full player_henry record");
        if (Children(p.Record, 6 + 32, p.Record.Length) is null) throw new InvalidDataException("Henry block record does not parse");
        foreach (var e in p.KeyEntries)
            if (e.Length != 47 || BinaryPrimitives.ReadUInt16LittleEndian(e) != 0x05AD) throw new InvalidDataException("Henry block key entry malformed");
        return p;
    }

    // ------------------------------------------------------------------ story stat, fields

    internal static bool HasField(byte[] rec, ushort tag) =>
        (Children(rec, 6 + 32, 6 + RecordNode(rec).Len) ?? []).Any(k => k.Tag == tag);

    /// <summary>Append a soul field (tag, payload) to a standalone record, fixing its length.</summary>
    internal static byte[] AppendField(byte[] rec, ushort tag, byte[] payload)
    {
        var o = new byte[rec.Length + 6 + payload.Length];
        rec.CopyTo(o, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(rec.Length), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(rec.Length + 2), (uint)payload.Length);
        payload.CopyTo(o, rec.Length + 6);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(2), (uint)(o.Length - 6));
        return o;
    }

    /// <summary>The record's storyProgress (stat id 8) or null (no stat list / no slot).</summary>
    internal static uint? StoryOf(byte[] rec)
    {
        List<Node> ch;
        try { ch = FieldChain(rec, 0x12FB, 0x0927, 0x1385); }
        catch (InvalidDataException) { return null; }
        uint? v = null;
        foreach (var (k, val) in Pairs(Payload(rec, ch[^1]))) if (k == StatStory) v = val;   // the last pair with the key wins
        return v;
    }

    /// <summary>
    /// The record with storyProgress set to <paramref name="host"/>: written in
    /// place, inserted in key order, or removed when the host has none. A
    /// record without a stat list gets one (the first 0x0927 child, as the game
    /// writes it) only when there is a value to hold.
    /// </summary>
    internal static byte[] WithStory(byte[] rec, uint? host, out uint? old)
    {
        old = StoryOf(rec);
        List<Node>? ch = null;
        try { ch = FieldChain(rec, 0x12FB, 0x0927, 0x1385); } catch (InvalidDataException) { }
        if (ch is null)
        {
            if (host is not uint hv0) return rec;
            List<Node> core;
            try { core = FieldChain(rec, 0x12FB, 0x0927); }
            catch (InvalidDataException) { throw new InvalidDataException("joiner Henry has no 0x12FB/0x0927 core to hold the story stat"); }
            var list = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(list, StatStory);
            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(4), hv0);
            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(8), 0xFFFFFFFFu);
            var node = new byte[6 + list.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(node, 0x1385);
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(2), (uint)list.Length);
            list.CopyTo(node, 6);
            return Replace(rec, core, [.. node, .. Payload(rec, core[^1])]);
        }
        var p = Payload(rec, ch[^1]);
        var pairs = new List<(uint K, uint V)>();
        int i = 0;
        for (; i + 8 <= p.Length; i += 8)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(i));
            if (k == 0xFFFFFFFFu) break;
            pairs.Add((k, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(i + 4))));
        }
        var tail = p.AsSpan(Math.Min(i, p.Length)).ToArray();   // the terminator and anything after it, kept as is
        if (host is uint hv)
        {
            bool set = false;
            for (int n = 0; n < pairs.Count; n++) if (pairs[n].K == StatStory) { pairs[n] = (StatStory, hv); set = true; }
            if (!set)
            {
                int at = pairs.FindIndex(x => x.K > StatStory);
                pairs.Insert(at < 0 ? pairs.Count : at, (StatStory, hv));
            }
        }
        else pairs.RemoveAll(x => x.K == StatStory);
        var o = new byte[pairs.Count * 8 + tail.Length];
        for (int n = 0; n < pairs.Count; n++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(n * 8), pairs[n].K);
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(n * 8 + 4), pairs[n].V);
        }
        tail.CopyTo(o, pairs.Count * 8);
        return o.AsSpan().SequenceEqual(p) ? rec : Replace(rec, ch, o);
    }

    // ------------------------------------------------------------------ seed, player

    /// <summary>The playthrough seed (body 0x01FB, u32) or null.</summary>
    public static uint? ReadSeed(byte[] raw)
    {
        var n = PathGet(raw, 0x01F4, 0x01FB);
        return n is Node s && s.Len == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(s.PayloadOff)) : null;
    }

    /// <summary>
    /// The seed straight from a file, inflating only the first blocks (0x01FB is
    /// the body's first child, right after the header chunk). Null when it
    /// cannot be read: never an exception for an unreadable or foreign file.
    /// </summary>
    public static uint? ReadSeedFromFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[8];
            if (fs.Read(head, 0, 8) != 8 || BinaryPrimitives.ReadUInt32LittleEndian(head) != 0xFFFFFFFFu) return null;
            int descLen = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            if (descLen < 0 || descLen > 1 << 20) return null;
            fs.Seek(descLen, SeekOrigin.Current);
            var raw = new MemoryStream();
            var bh = new byte[8];
            for (int block = 0; block < 64; block++)
            {
                if (fs.Read(bh, 0, 8) != 8) return null;
                int clen = BinaryPrimitives.ReadInt32LittleEndian(bh), rlen = BinaryPrimitives.ReadInt32LittleEndian(bh.AsSpan(4));
                if (rlen < 0 || rlen > 1 << 20) return null;
                if (clen == -1)
                {
                    var b = new byte[rlen];
                    if (fs.Read(b, 0, rlen) != rlen) return null;
                    raw.Write(b);
                }
                else
                {
                    if (clen < 0 || clen > 1 << 21) return null;
                    var z = new byte[clen];
                    if (fs.Read(z, 0, clen) != clen) return null;
                    using var zin = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress);
                    zin.CopyTo(raw);
                }
                if (SeedInPrefix(raw.GetBuffer().AsSpan(0, (int)raw.Length)) is { } r) return r.Found ? r.Seed : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>(found, seed) once the prefix is long enough to decide; null = need more bytes.</summary>
    private static (bool Found, uint Seed)? SeedInPrefix(ReadOnlySpan<byte> raw)
    {
        int p = 4;
        while (true)
        {
            if (p + 6 > raw.Length) return null;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(raw[p..]);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(raw[(p + 2)..]);
            if (tag == 0x01F4)
            {
                int q = p + 6;
                if (q + 6 > raw.Length) return null;
                ushort t2 = BinaryPrimitives.ReadUInt16LittleEndian(raw[q..]);
                uint l2 = BinaryPrimitives.ReadUInt32LittleEndian(raw[(q + 2)..]);
                if (t2 != 0x01FB || l2 != 4) return (false, 0);
                if (q + 10 > raw.Length) return null;
                return (true, BinaryPrimitives.ReadUInt32LittleEndian(raw[(q + 6)..]));
            }
            if (tag == 0x01FA) return (false, 0);
            p += 6 + (int)len;
            if (len > 64u << 20) return (false, 0);
        }
    }

    /// <summary>A short, non-reversible label for a seed in logs and folder names (never the seed itself).</summary>
    public static string SeedTag(uint seed)
    {
        Span<byte> b = stackalloc byte[4 + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, seed);
        "kcdmp-world"u8.CopyTo(b[4..]);
        return Convert.ToHexString(SHA256.HashData(b[..15]))[..10].ToLowerInvariant();
    }

    /// <summary>
    /// WO-157: a host world's tag when another host's world of the same seed is already stored here (two hosts of one
    /// start save): the seed and that host's install key, never reversible to either.
    /// </summary>
    public static string HostWorldTag(uint seed, uint hostKey)
    {
        Span<byte> b = stackalloc byte[8 + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, seed);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], hostKey);
        "kcdmp-world2"u8.CopyTo(b[8..]);
        return Convert.ToHexString(SHA256.HashData(b))[..10].ToLowerInvariant();
    }

    public readonly record struct PlayerInfo(bool IsHenry, string Player, bool Pristine);

    /// <summary>
    /// Who the player is in this save: the soul whose 0x12F9 names the player
    /// entity 0x7777. IsHenry = player_henry and nobody else. Pristine = a new
    /// game's first Henry save: Henry holds no stat and no skill XP yet.
    /// </summary>
    public static PlayerInfo PlayerOf(byte[] raw)
    {
        var bound = new List<string>();
        bool pristine = false;
        foreach (var rec in SoulList(raw))
        {
            if (rec.Tag != 0x115E) continue;
            var f = SoulFields(raw, rec);
            if (!f.TryGetValue(0x12F9, out var nf)) continue;
            var b = Payload(raw, nf[0]);
            int nul = Array.IndexOf(b, (byte)0);
            if (nul < 0 || nul + 9 > b.Length || BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(nul + 1)) != PlayerEntityGuid) continue;
            string g = GuidStr(raw.AsSpan(rec.Off + 6, 16));
            bound.Add(g);
            if (g == HenrySoul)
            {
                var rb = NodeBytes(raw, rec);
                bool xp = false;
                try { xp |= FieldChain(rb, 0x12FB, 0x0927, 0x1385) is not null; } catch (InvalidDataException) { }
                try { xp |= FieldChain(rb, 0x12FB, 0x0927, 0x138D) is not null; } catch (InvalidDataException) { }
                pristine = !xp;
            }
        }
        string who = bound.Count == 0 ? "none" : bound.Count > 1 ? "several" : bound[0] == HenrySoul ? "player_henry" : bound[0] == BohutaSoul ? "player_bohuta" : bound[0][..8];
        return new PlayerInfo(bound.Count == 1 && bound[0] == HenrySoul, who, bound.Count == 1 && bound[0] == HenrySoul && pristine);
    }

    // ------------------------------------------------------------------ a fresh Henry

    /// <summary>
    /// WO-125 Phase 3, the fallback when the joiner's machine holds no new
    /// game's first Henry save: a player_henry record with nothing in it but
    /// what binds it to the player entity, so the engine's own soul loader
    /// builds the rest from the game's soul database (C_Soul::LoadGame copies
    /// the soul from the DB before reading the saved fields, WO-112 s2.4).
    /// Built in code: no byte of any real save except the soul's 32-byte GUID
    /// prefix, which is the soul's identity (the same in every save). The
    /// side blocks stay the host's (null) -- see the findings for what that
    /// carries. The splice then writes the host's story stat and renown.
    /// </summary>
    public static HenryParts FreshDefaultParts(byte[] hostRaw)
    {
        var hrec = NodeBytes(hostRaw, HenryChain(hostRaw)[^1]);
        var prefix = hrec.AsSpan(6, 32).ToArray();
        static byte[] T(ushort tag, byte[] payload)
        {
            var b = new byte[6 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)payload.Length);
            payload.CopyTo(b, 6);
            return b;
        }
        var name = new byte[5 + 8];
        "Dude"u8.CopyTo(name);
        BinaryPrimitives.WriteUInt64LittleEndian(name.AsSpan(5), PlayerEntityGuid);
        var fields = new List<byte>();
        fields.AddRange(T(0x1303, Convert.FromHexString("fd1203000000000001")));   // the same 9 bytes in every save seen (194)
        fields.AddRange(T(0x12F9, name));
        fields.AddRange(T(0x12FB, T(0x0927, T(0x1385, [0xFF, 0xFF, 0xFF, 0xFF]))));  // an empty stat list: the splice writes the host's story stat into it
        fields.AddRange(T(0x12FF, new byte[16]));                                   // replaced by the host's renown
        var rec = T(0x115E, [.. prefix, .. fields]);
        return new HenryParts { Record = rec, Side = new byte[]?[SideBlocks.Length], Build = "", Origin = HenryParts.OriginFreshDefault };
    }

    // ------------------------------------------------------------------ comparing two blocks

    /// <summary>
    /// What differs between two Henry blocks: the record field by field (and
    /// inside 0x12FB/0x0927 one level deeper), each side block, the key
    /// entries. Empty = byte-identical Henry.
    /// </summary>
    public static List<string> DiffBlocks(HenryParts a, HenryParts b)
    {
        var o = new List<string>();
        static Dictionary<string, byte[]> Flat(byte[] rec)
        {
            var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var seen = new Dictionary<ushort, int>();
            foreach (var f in Children(rec, 6 + 32, rec.Length) ?? [])
            {
                int i = seen[f.Tag] = seen.GetValueOrDefault(f.Tag) + 1;
                string k = $"{f.Tag:x4}#{i}";
                if (f.Tag == 0x12FB)
                {
                    foreach (var c in Children(rec, f.PayloadOff, f.End) ?? [])
                    {
                        if (c.Tag == 0x0927)
                            foreach (var cc in Children(rec, c.PayloadOff, c.End) ?? []) d[$"{k}/0927/{cc.Tag:x4}"] = Payload(rec, cc);
                        else d[$"{k}/{c.Tag:x4}"] = Payload(rec, c);
                    }
                }
                else d[k] = Payload(rec, f);
            }
            return d;
        }
        if (!a.Record.AsSpan().SequenceEqual(b.Record))
        {
            var fa = Flat(a.Record); var fb = Flat(b.Record);
            foreach (var k in fa.Keys.Union(fb.Keys).OrderBy(x => x, StringComparer.Ordinal))
                if (!fa.TryGetValue(k, out var x) || !fb.TryGetValue(k, out var y) || !x.AsSpan().SequenceEqual(y))
                    o.Add($"record {k} ({(fa.TryGetValue(k, out var x2) ? x2.Length : -1)} vs {(fb.TryGetValue(k, out var y2) ? y2.Length : -1)} B)");
            if (o.Count == 0) o.Add("record (framing)");
        }
        for (int i = 0; i < SideBlocks.Length; i++)
        {
            var x = a.Side[i]; var y = b.Side[i];
            if (x is null && y is null) continue;
            if (x is null || y is null || !x.AsSpan().SequenceEqual(y)) o.Add($"side {PathText(SideBlocks[i])} ({x?.Length ?? -1} vs {y?.Length ?? -1} B)");
        }
        if (a.KeyEntries.Count != b.KeyEntries.Count || a.KeyEntries.Zip(b.KeyEntries).Any(p => !p.First.AsSpan().SequenceEqual(p.Second)))
            o.Add($"keys ({a.KeyEntries.Count} vs {b.KeyEntries.Count})");
        foreach (var k in a.Extensions.Keys.Union(b.Extensions.Keys).OrderBy(x => x, StringComparer.Ordinal))
            if (!a.Extensions.TryGetValue(k, out var ex) || !b.Extensions.TryGetValue(k, out var ey) || !ex.AsSpan().SequenceEqual(ey))
                o.Add($"ext {k} ({(a.Extensions.TryGetValue(k, out var ex2) ? ex2.Length : -1)} vs {(b.Extensions.TryGetValue(k, out var ey2) ? ey2.Length : -1)} B)");
        return o;
    }

    // ------------------------------------------------------------------ transferred-out drop deduction

    /// <summary>
    /// Deduct items that this joiner dropped and a peer claimed after <paramref name="parts"/>'s snapshot was taken.
    /// Prefers unequipped stacks in Henry's inventory (0x1301/0x0007), cleans equipment presets (0x1301/0x0006) and
    /// 000B key bindings for any fully removed item instances, and falls back to personal_stash if needed.
    /// Returns total item pieces deducted.
    /// </summary>
    public static int ApplyTransferredOut(HenryParts parts, IReadOnlyList<(string Class, int Amount)> transfers)
    {
        if (transfers.Count == 0) return 0;
        var needByClass = new Dictionary<string, int>(StringComparer.Ordinal);
        var classOrder = new List<string>();
        foreach (var (cls, amt) in transfers)
        {
            if (amt <= 0 || !Guid.TryParse(cls, out var cg)) continue;
            string norm = cg.ToString("D");
            if (!needByClass.ContainsKey(norm)) classOrder.Add(norm);
            needByClass[norm] = needByClass.GetValueOrDefault(norm) + amt;
        }
        if (needByClass.Count == 0) return 0;

        int totalDeducted = 0;
        var removedInstances = new HashSet<string>(StringComparer.Ordinal);

        if (HasItemList(parts.Record))
        {
            var eqSet = CollectAllEquipmentInstances(parts.Record);
            var ch = FieldChain(parts.Record, 0x1301, 0x0007);
            var lst = ch[^1];
            if (DeductFromItemContainer(parts.Record, lst, classOrder, needByClass, eqSet, removedInstances, ref totalDeducted, out var newInvPay))
                parts.Record = Replace(parts.Record, ch, newInvPay);
            if (removedInstances.Count > 0)
                parts.Record = StripRemovedFromEquipment(parts.Record, removedInstances);
        }

        if (classOrder.Any(c => needByClass[c] > 0)
            && parts.Extensions.TryGetValue(PersonalStashExtensionKey, out var psBytes) && psBytes.Length > 0)
        {
            parts.Extensions[PersonalStashExtensionKey] = DeductFromPersonalStashBytes(psBytes, classOrder, needByClass, removedInstances, ref totalDeducted);
        }

        if (removedInstances.Count > 0 && parts.KeyEntries.Count > 0)
            parts.KeyEntries.RemoveAll(e => e.Length >= 47 && removedInstances.Contains(GuidStr(e.AsSpan(6 + 25, 16))));

        return totalDeducted;
    }

    private static uint ReadSavedItemAmount(ReadOnlySpan<byte> itemPay, List<Node>? paramNodes)
    {
        foreach (var pn in paramNodes ?? [])
            if (pn.Tag == 0x0000 && pn.Len == 4)
                return BinaryPrimitives.ReadUInt32LittleEndian(itemPay.Slice(pn.PayloadOff, 4)) + 1u;
        return 1u;
    }

    private static byte[] RewriteSavedItemAmount(byte[] itemPay, List<Node>? paramNodes, uint newAmt)
    {
        using var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[6];
        Span<byte> u4 = stackalloc byte[4];
        ms.Write(itemPay, 0, 36);
        bool wroteAmt = false;
        foreach (var pn in paramNodes ?? [])
        {
            if (pn.Tag == 0x0000 && pn.Len == 4)
            {
                wroteAmt = true;
                if (newAmt > 1)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x0000);
                    BinaryPrimitives.WriteUInt32LittleEndian(hdr[2..], 4u);
                    BinaryPrimitives.WriteUInt32LittleEndian(u4, newAmt - 1u);
                    ms.Write(hdr);
                    ms.Write(u4);
                }
            }
            else ms.Write(itemPay, pn.Off, 6 + pn.Len);
        }
        if (!wroteAmt && newAmt > 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x0000);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr[2..], 4u);
            BinaryPrimitives.WriteUInt32LittleEndian(u4, newAmt - 1u);
            ms.Write(hdr);
            ms.Write(u4);
        }
        var pay = ms.ToArray();
        var node = new byte[6 + pay.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(node, 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(2), (uint)pay.Length);
        pay.CopyTo(node, 6);
        return node;
    }

    private static bool DeductFromItemContainer(
        byte[] buf,
        Node container,
        List<string> classOrder,
        Dictionary<string, int> needByClass,
        HashSet<string>? eqSet,
        HashSet<string> removedInstances,
        ref int totalDeducted,
        out byte[] newContainerPayload)
    {
        var nodes = container.Len == 0 ? [] : (Children(buf, container.PayloadOff, container.End) ?? []);
        int count = nodes.Count;
        var insts = new string?[count];
        var classes = new string?[count];
        var amounts = new uint[count];
        var payloads = new byte[]?[count];
        var paramLists = new List<Node>?[count];
        var removed = new bool[count];
        var modified = new bool[count];

        for (int i = 0; i < count; i++)
        {
            if (nodes[i].Tag != 0x0001 || nodes[i].Len < 36) continue;
            var b = Payload(buf, nodes[i]);
            payloads[i] = b;
            insts[i] = GuidStr(b.AsSpan(0, 16));
            classes[i] = GuidStr(b.AsSpan(20, 16));
            paramLists[i] = b.Length > 36 ? Children(b, 36, b.Length) : [];
            amounts[i] = ReadSavedItemAmount(b, paramLists[i]);
        }

        bool anyChange = false;
        foreach (var cls in classOrder)
        {
            int want = needByClass[cls];
            if (want <= 0) continue;
            var candidates = Enumerable.Range(0, count)
                .Where(i => classes[i] == cls && !removed[i] && amounts[i] > 0)
                .OrderBy(i => eqSet is not null && eqSet.Contains(insts[i]!) ? 1 : 0)
                .ThenBy(i => i)
                .ToList();
            foreach (int idx in candidates)
            {
                if (want <= 0) break;
                uint take = (uint)Math.Min(want, (long)amounts[idx]);
                amounts[idx] -= take;
                want -= (int)take;
                totalDeducted += (int)take;
                anyChange = true;
                if (amounts[idx] == 0)
                {
                    removed[idx] = true;
                    removedInstances.Add(insts[idx]!);
                }
                else modified[idx] = true;
            }
            needByClass[cls] = want;
        }

        if (!anyChange)
        {
            newContainerPayload = [];
            return false;
        }

        using var ms = new MemoryStream();
        for (int i = 0; i < count; i++)
        {
            if (removed[i]) continue;
            if (modified[i]) ms.Write(RewriteSavedItemAmount(payloads[i]!, paramLists[i], amounts[i]));
            else ms.Write(buf, nodes[i].Off, 6 + nodes[i].Len);
        }
        newContainerPayload = ms.ToArray();
        return true;
    }

    private static HashSet<string> CollectAllEquipmentInstances(byte[] rec)
    {
        var set = Equipped(rec);
        List<Node> eqChain;
        try { eqChain = FieldChain(rec, 0x1301, 0x0006); }
        catch (InvalidDataException) { return set; }
        var eqPay = Payload(rec, eqChain[^1]);
        foreach (var pr in Children(eqPay, 0, eqPay.Length) ?? [])
        {
            if (pr.Tag == 0x0000) continue;
            foreach (var sub in Children(eqPay, pr.PayloadOff, pr.End) ?? [])
            {
                if (sub.Tag == 0x0000)
                {
                    for (int off = 0; off + 16 <= sub.Len; off += 16)
                        set.Add(GuidStr(eqPay.AsSpan(sub.PayloadOff + off, 16)));
                }
                else
                {
                    var subPay = Payload(eqPay, sub);
                    foreach (var bk in Children(subPay, 0, subPay.Length) ?? [])
                    {
                        if (bk.Tag < 0x0001 || bk.Len < 26) continue;
                        foreach (var hk in Children(subPay, bk.PayloadOff + 4, bk.End) ?? [])
                            if (hk.Len == 16) set.Add(GuidStr(subPay.AsSpan(hk.PayloadOff, 16)));
                    }
                }
            }
        }
        return set;
    }

    private static byte[] StripRemovedFromEquipment(byte[] rec, HashSet<string> removedInstances)
    {
        List<Node> eqChain;
        try { eqChain = FieldChain(rec, 0x1301, 0x0006); }
        catch (InvalidDataException) { return rec; }
        var eqPay = Payload(rec, eqChain[^1]);
        var presets = Children(eqPay, 0, eqPay.Length);
        if (presets is null) return rec;

        Span<byte> hdr = stackalloc byte[6];
        static void WriteNode(Stream s, Span<byte> h, ushort tag, ReadOnlySpan<byte> pay)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h, tag);
            BinaryPrimitives.WriteUInt32LittleEndian(h[2..], (uint)pay.Length);
            s.Write(h);
            s.Write(pay);
        }

        using var eqMs = new MemoryStream();
        bool changed = false;
        foreach (var pr in presets)
        {
            if (pr.Tag == 0x0000) { eqMs.Write(eqPay, pr.Off, 6 + pr.Len); continue; }
            var subs = Children(eqPay, pr.PayloadOff, pr.End);
            if (subs is null) { eqMs.Write(eqPay, pr.Off, 6 + pr.Len); continue; }
            bool hasOtherSubs = subs.Any(s => s.Tag != 0x0000);
            using var prMs = new MemoryStream();
            foreach (var sub in subs)
            {
                if (sub.Tag == 0x0000)
                {
                    using var keptGuids = new MemoryStream();
                    for (int off = 0; off + 16 <= sub.Len; off += 16)
                    {
                        var slice = eqPay.AsSpan(sub.PayloadOff + off, 16);
                        if (removedInstances.Contains(GuidStr(slice))) { changed = true; continue; }
                        keptGuids.Write(slice);
                    }
                    if (keptGuids.Length > 0)
                        WriteNode(prMs, hdr, 0x0000, keptGuids.ToArray());
                    else if (!hasOtherSubs)
                        WriteNode(prMs, hdr, 0x0000, new byte[16]);
                    else changed = true;
                }
                else
                {
                    var subPay = Payload(eqPay, sub);
                    foreach (var bk in Children(subPay, 0, subPay.Length) ?? [])
                    {
                        if (bk.Tag < 0x0001 || bk.Len < 26) continue;
                        foreach (var hk in Children(subPay, bk.PayloadOff + 4, bk.End) ?? [])
                        {
                            if (hk.Len == 16 && removedInstances.Contains(GuidStr(subPay.AsSpan(hk.PayloadOff, 16))))
                            {
                                subPay.AsSpan(hk.PayloadOff, 16).Clear();
                                changed = true;
                            }
                        }
                    }
                    WriteNode(prMs, hdr, sub.Tag, subPay);
                }
            }
            WriteNode(eqMs, hdr, pr.Tag, prMs.ToArray());
        }
        return changed ? Replace(rec, eqChain, eqMs.ToArray()) : rec;
    }

    private static byte[] DeductFromPersonalStashBytes(
        byte[] psBytes,
        List<string> classOrder,
        Dictionary<string, int> needByClass,
        HashSet<string> removedInstances,
        ref int totalDeducted)
    {
        if (psBytes.Length == 0) return [];
        using var outMs = new MemoryStream();
        Span<byte> hdr = stackalloc byte[6];
        bool anyChanged = false;
        foreach (var st in StashNodes(psBytes))
        {
            var stKids = st.Len == 16 ? [] : (Children(psBytes, st.PayloadOff + 16, st.End) ?? []);
            int invIdx = stKids.FindIndex(k => k.Tag == 0x0005);
            if (invIdx < 0) { outMs.Write(psBytes, st.Off, 6 + st.Len); continue; }
            var invNode = stKids[invIdx];
            if (!DeductFromItemContainer(psBytes, invNode, classOrder, needByClass, null, removedInstances, ref totalDeducted, out var newInvPay))
            {
                outMs.Write(psBytes, st.Off, 6 + st.Len);
                continue;
            }
            anyChanged = true;
            int remainingItems = newInvPay.Length == 0 ? 0 : (Children(newInvPay, 0, newInvPay.Length)?.Count(n => n.Tag == 0x0001) ?? 0);
            if (remainingItems == 0) continue; // omit empty 0x0000 personal stash node

            using var stPayMs = new MemoryStream();
            stPayMs.Write(psBytes, st.PayloadOff, 16);
            for (int i = 0; i < stKids.Count; i++)
            {
                if (i == invIdx)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x0005);
                    BinaryPrimitives.WriteUInt32LittleEndian(hdr[2..], (uint)newInvPay.Length);
                    stPayMs.Write(hdr);
                    stPayMs.Write(newInvPay);
                }
                else stPayMs.Write(psBytes, stKids[i].Off, 6 + stKids[i].Len);
            }
            var newStPay = stPayMs.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x0000);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr[2..], (uint)newStPay.Length);
            outMs.Write(hdr);
            outMs.Write(newStPay);
        }
        return anyChanged ? outMs.ToArray() : psBytes;
    }

    // ------------------------------------------------------------------ CLI

    /// <summary>
    /// <c>--save-tool extract &lt;save&gt; &lt;out.hblk&gt;</c>, <c>splice-block &lt;host&gt; &lt;block&gt; &lt;out&gt; --tables</c>,
    /// <c>splice-fresh &lt;host&gt; &lt;out&gt; --tables</c>, <c>blockdiff &lt;a&gt; &lt;b&gt;</c> (blocks or saves),
    /// <c>seed|who &lt;save&gt;...</c>. The seed is printed only as its tag.
    /// </summary>
    private static int? RunCliWo125(string cmd, List<string> pos, Func<string, string?> opt, TextWriter w)
    {
        HenryParts Load(string p)
        {
            var b = ReadShared(p);
            return b.AsSpan().StartsWith(BlockMagic) ? ParseBlock(b) : PartsFromFile(b, HenryParts.OriginSave);
        }
        void WriteNew(string p, byte[] data)
        {
            if (File.Exists(p)) throw new IOException("output exists; refusing to overwrite");
            using var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write);
            fs.Write(data);
        }
        switch (cmd)
        {
            case "seed":
            case "who":
                foreach (var p in pos)
                {
                    if (cmd == "seed") { var s = ReadSeedFromFile(p); w.WriteLine($"{Path.GetFileName(p)} seed_tag={(s is uint v ? SeedTag(v) : "-")}"); continue; }
                    var raw = Inflate(ReadShared(p)).Raw;
                    var pi = PlayerOf(raw);
                    w.WriteLine($"{Path.GetFileName(p)} player={pi.Player} henry={(pi.IsHenry ? "yes" : "no")} pristine={(pi.Pristine ? "yes" : "no")} seed_tag={(ReadSeed(raw) is uint v2 ? SeedTag(v2) : "-")}");
                }
                return 0;
            case "extract":
            {
                if (pos.Count != 2) { w.WriteLine("usage: --save-tool extract <save.whs> <out.hblk>"); return 2; }
                var parts = PartsFromFile(ReadShared(pos[0]), HenryParts.OriginSnapshot);
                var blk = SerializeBlock(parts);
                WriteNew(pos[1], blk);
                w.WriteLine($"block {blk.Length} B: record {parts.Record.Length} B, sides {string.Join(",", parts.Side.Select(x => x?.Length ?? -1))}, keys {parts.KeyEntries.Count}, seed_tag={(parts.Seed is uint s ? SeedTag(s) : "-")}");
                return 0;
            }
            case "blockdiff":
            {
                if (pos.Count != 2) { w.WriteLine("usage: --save-tool blockdiff <a> <b>"); return 2; }
                var d = DiffBlocks(Load(pos[0]), Load(pos[1]));
                foreach (var x in d) w.WriteLine("DIFF " + x);
                w.WriteLine(d.Count == 0 ? "blockdiff: IDENTICAL" : $"blockdiff: {d.Count} difference(s)");
                return d.Count == 0 ? 0 : 1;
            }
            case "splice-block":
            case "splice-fresh":
            {
                int need = cmd == "splice-block" ? 3 : 2;
                if (pos.Count != need) { w.WriteLine($"usage: --save-tool {cmd} <host> {(need == 3 ? "<block|save> " : "")}<out> --tables <Tables.pak>"); return 2; }
                string? tables = opt("--tables");
                if (tables is null) { w.WriteLine("--tables <Tables.pak> is required"); return 2; }
                var q = QuestClasses(tables);
                var host = ReadShared(pos[0]);
                var parts = cmd == "splice-block" ? Load(pos[1]) : FreshDefaultParts(Inflate(host).Raw);
                var res = SpliceParts(host, parts, q, QuestItemMode.Strip);
                var fails = CheckParts(host, parts, res.File, q, QuestItemMode.Strip);
                var ver = Verify(res.File);
                foreach (var f in fails) w.WriteLine("FAIL " + f);
                w.WriteLine($"origin={parts.Origin} story joiner={res.Report.StoryJoiner?.ToString() ?? "-"} host={res.Report.StoryHost?.ToString() ?? "-"}; quest items removed={res.Report.QuestItemsRemoved.Count}; keys joiner_added={res.Report.KeysJoinerAdded}; out_bytes={res.File.Length}; verify={(ver.Ok ? "ok" : ver.Reason)}");
                w.WriteLine("check: " + (fails.Count == 0 ? "PASS" : $"{fails.Count} failure(s)"));
                if (fails.Count > 0 || !ver.Ok) return 1;
                WriteNew(pos[^1], res.File);
                return 0;
            }
        }
        return null;
    }
}
