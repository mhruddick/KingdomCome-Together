// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KcdMp.Client.Tests;

public class M1HblkV2Tests
{
    private static WhsSave.HenryParts SampleParts()
    {
        var (_, js) = WhsSaveTests.Pair();
        return WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot);
    }

    private static byte[] SignBody(ReadOnlySpan<byte> body)
    {
        var outBytes = new byte[body.Length + 32];
        body.CopyTo(outBytes);
        SHA256.HashData(body).CopyTo(outBytes.AsSpan(body.Length));
        return outBytes;
    }

    [Fact]
    public void Legacy_v1_block_parses_cleanly_with_empty_extensions_and_upgrades_to_v2()
    {
        var parts = SampleParts();
        parts.Extensions.Clear();
        var v1 = WhsSave.SerializeBlockV1(parts);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(v1.AsSpan(8)));

        var parsed = WhsSave.ParseBlock(v1);
        Assert.Empty(parsed.Extensions);
        Assert.Equal(parts.Build, parsed.Build);
        Assert.Equal(parts.Seed, parsed.Seed);
        Assert.Equal(parts.Origin, parsed.Origin);
        Assert.Equal(parts.Record, parsed.Record);
        Assert.Equal(parts.KeyEntries.Count, parsed.KeyEntries.Count);
        Assert.Empty(WhsSave.DiffBlocks(parts, parsed));

        var upgradedV2 = WhsSave.SerializeBlock(parsed);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(upgradedV2.AsSpan(8)));
        Assert.Equal(v1.Length + 4, upgradedV2.Length); // +4 bytes for extCount == 0
        var reParsed = WhsSave.ParseBlock(upgradedV2);
        Assert.Empty(reParsed.Extensions);
        Assert.Empty(WhsSave.DiffBlocks(parsed, reParsed));
    }

    [Fact]
    public void V2_block_round_trips_multiple_extensions_including_empty_and_binary_payloads()
    {
        var parts = SampleParts();
        parts.Extensions.Clear();
        parts.Extensions["personal_stash"] = [0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0xFE, 0xCA];
        parts.Extensions["empty_ext"] = [];
        parts.Extensions["z_meta_utf8"] = Encoding.UTF8.GetBytes("bedside-stash-v2");

        var v2 = WhsSave.SerializeBlock(parts);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(v2.AsSpan(8)));

        var parsed = WhsSave.ParseBlock(v2);
        Assert.Equal(3, parsed.Extensions.Count);
        Assert.Equal(["empty_ext", "personal_stash", "z_meta_utf8"], parsed.Extensions.Keys.ToArray());
        Assert.Empty(parsed.Extensions["empty_ext"]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0xFE, 0xCA }, parsed.Extensions["personal_stash"]);
        Assert.Equal("bedside-stash-v2", Encoding.UTF8.GetString(parsed.Extensions["z_meta_utf8"]));
        Assert.Empty(WhsSave.DiffBlocks(parts, parsed));

        parsed.Extensions["personal_stash"] = [0x99];
        var diff = WhsSave.DiffBlocks(parts, parsed);
        Assert.Contains(diff, d => d.StartsWith("ext personal_stash (", StringComparison.Ordinal));
    }

    [Fact]
    public void Sha256_tamper_in_extension_payload_or_trailer_is_rejected()
    {
        var parts = SampleParts();
        parts.Extensions["personal_stash"] = [1, 2, 3, 4, 5, 6, 7, 8];
        var v2 = WhsSave.SerializeBlock(parts);

        // Flip a byte inside the extension value (right before the 32-byte SHA-256 trailer)
        var tamperedPayload = (byte[])v2.Clone();
        tamperedPayload[tamperedPayload.Length - 33] ^= 0x55;
        var ex1 = Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(tamperedPayload));
        Assert.Contains("hash mismatch", ex1.Message);

        // Flip a byte inside the SHA-256 trailer itself
        var tamperedHash = (byte[])v2.Clone();
        tamperedHash[^1] ^= 0x01;
        var ex2 = Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(tamperedHash));
        Assert.Contains("hash mismatch", ex2.Message);
    }

    [Fact]
    public void Truncation_bounds_duplicate_keys_and_trailing_bytes_are_rejected()
    {
        var parts = SampleParts();
        parts.Extensions.Clear();
        var v1Body = WhsSave.SerializeBlockV1(parts).AsSpan(0, WhsSave.SerializeBlockV1(parts).Length - 32).ToArray();

        byte[] BuildV2BodyWithTail(byte[] tail)
        {
            var body = new byte[v1Body.Length + tail.Length];
            v1Body.CopyTo(body, 0);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 2);
            tail.CopyTo(body, v1Body.Length);
            return SignBody(body);
        }

        // Unsupported versions (0 and 3)
        var badVer0 = (byte[])v1Body.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badVer0.AsSpan(8), 0);
        Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(SignBody(badVer0)));

        var badVer3 = (byte[])v1Body.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badVer3.AsSpan(8), 3);
        Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(SignBody(badVer3)));

        // Truncated: v2 with missing extCount
        Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail([])));

        // Negative extCount and extCount > 256
        var negCount = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(negCount, -1);
        Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(negCount)));

        var hugeCount = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(hugeCount, 257);
        Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(hugeCount)));

        // Empty key (keyLen == 0)
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);
            w.Write((ushort)0);
            w.Write(0);
            Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
        }

        // Truncated key bytes and truncated val bytes
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);
            w.Write((ushort)10);
            w.Write("abc"u8);
            Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
        }
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);
            w.Write((ushort)3);
            w.Write("abc"u8);
            w.Write(100);
            w.Write(new byte[12]);
            Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
        }

        // Negative valLen and valLen > 64 MB
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);
            w.Write((ushort)3);
            w.Write("abc"u8);
            w.Write(-5);
            Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
        }
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);
            w.Write((ushort)3);
            w.Write("abc"u8);
            w.Write(WhsSave.MaxBlockExtensionBytes + 1);
            Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
        }

        // Duplicate extension keys
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(2);
            for (int i = 0; i < 2; i++)
            {
                w.Write((ushort)3);
                w.Write("dup"u8);
                w.Write(1);
                w.Write((byte)i);
            }
            var ex = Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
            Assert.Contains("duplicate extension key", ex.Message);
        }

        // Trailing byte after valid extension list
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);
            w.Write((ushort)2);
            w.Write("ok"u8);
            w.Write(1);
            w.Write((byte)0x42);
            w.Write((byte)0xFF); // trailing garbage byte
            var ex = Assert.Throws<InvalidDataException>(() => WhsSave.ParseBlock(BuildV2BodyWithTail(ms.ToArray())));
            Assert.Contains("trailing bytes", ex.Message);
        }

        // SerializeBlock rejects > 256 extensions
        var over = SampleParts();
        for (int i = 0; i <= WhsSave.MaxBlockExtensions; i++)
            over.Extensions[$"k{i:D3}"] = [1];
        Assert.Throws<InvalidDataException>(() => WhsSave.SerializeBlock(over));
    }
}
