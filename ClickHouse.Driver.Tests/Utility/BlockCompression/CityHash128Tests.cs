using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Utility.BlockCompression;

/// <summary>
/// Known-good CityHash128 vectors, validated end-to-end against a real ClickHouse server.
/// If any of these flip, block-compression framing will no longer interoperate with the server.
///
/// The vectors intentionally cover every branch in <see cref="CityHash128.Hash"/>:
///   len == 0, 1, &lt;8, 8..15 (the "CityMurmur via 8-byte seed" branch that was originally
///   mis-ported from CH.Native and which only a real server round-trip catches), ==16,
///   16..127 (CityMurmur main path), ==128, &gt;128 (unrolled main loop), and multi-MiB
///   payloads to stress the loop's tail handling.
/// </summary>
public class CityHash128Tests
{
    private static IEnumerable<TestCaseData> KnownVectors()
    {
        yield return new TestCaseData("empty", Array.Empty<byte>(), 0x3DF09DFC64C09A2BUL, 0x3CB540C392E51E29UL);
        yield return new TestCaseData("oneByte41", new byte[] { 0x41 }, 0xAF63BD6670CFB269UL, 0x879AEF3966E6EED9UL);
        yield return new TestCaseData("ascending7", Ascending(7), 0xAA38DB290CCB2B16UL, 0x684A34C21A5257DAUL);
        yield return new TestCaseData("ascending8", Ascending(8), 0xD0BFE8984E1E8682UL, 0x565141A0CF7FC611UL);
        yield return new TestCaseData("ascending11", Ascending(11), 0x83F8D0E0B2401660UL, 0xE7937E5BF0D19303UL);
        yield return new TestCaseData("ascending15", Ascending(15), 0x048856BB9FDDCF10UL, 0x0C4D01DD6777BB5FUL);
        yield return new TestCaseData("ascending16", Ascending(16), 0x17CEADE677C2F945UL, 0x579ED60675C8FEDCUL);
        yield return new TestCaseData("ascending17", Ascending(17), 0x8112E830FB310FF1UL, 0xC972AD09C64FD737UL);
        yield return new TestCaseData("ascending64", Ascending(64), 0x83D9A0502FD851D0UL, 0x718073343EA63F22UL);
        yield return new TestCaseData("ascending127", Ascending(127), 0x6E9CEFA0ACCAEEC6UL, 0x56818BBA66E6A718UL);
        yield return new TestCaseData("ascending128", Ascending(128), 0x7DEF035B54925590UL, 0xDE34765C4ACA9038UL);
        yield return new TestCaseData("ascending129", Ascending(129), 0x946689818B344E34UL, 0x5BAF696C26FB2021UL);
        yield return new TestCaseData("fox", Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog."), 0x5FDF541A7B66EC85UL, 0x83C5051377ED3E12UL);
        yield return new TestCaseData("lcg1024", Lcg(1024, 1), 0xC2FB750C58A66722UL, 0x11ECD5C074458BD9UL);
        yield return new TestCaseData("lcg1MiB", Lcg(1024 * 1024, 2), 0x0EB54FE45CFF5B01UL, 0x9A7BF62B1F1C8FBAUL);
        yield return new TestCaseData("lcg1MiBPlus1", Lcg(1024 * 1024 + 1, 3), 0x238ABD566315B68FUL, 0x85DEBA31A423E986UL);
    }

    [TestCaseSource(nameof(KnownVectors))]
    public void Hash_MatchesReference_ForKnownVectors(string label, byte[] input, ulong expectedLow, ulong expectedHigh)
    {
        var (low, high) = CityHash128.Hash(input);
        Assert.Multiple(() =>
        {
            Assert.That(low, Is.EqualTo(expectedLow),
                $"[{label}] Low word diverged — check CityHash128 branch for len={input.Length}.");
            Assert.That(high, Is.EqualTo(expectedHigh),
                $"[{label}] High word diverged — check CityHash128 branch for len={input.Length}.");
        });
    }

    [Test]
    public void HashBytes_WritesLowThenHigh_LittleEndian()
    {
        // On the wire, ClickHouse expects the 16-byte checksum as Low(8 LE) then High(8 LE).
        // This pins that layout so the frame writer and reader agree with each other AND with the server.
        var (low, high) = CityHash128.Hash(Encoding.UTF8.GetBytes("abc"));

        Span<byte> dest = stackalloc byte[16];
        CityHash128.HashBytes(Encoding.UTF8.GetBytes("abc"), dest);

        var actualLow = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(dest);
        var actualHigh = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(dest.Slice(8));

        Assert.Multiple(() =>
        {
            Assert.That(actualLow, Is.EqualTo(low));
            Assert.That(actualHigh, Is.EqualTo(high));
        });
    }

    [Test]
    public void HashBytes_ArrayOverload_AgreesWithSpanOverload()
    {
        var data = Lcg(777, 42);
        var arrayResult = CityHash128.HashBytes(data);

        Span<byte> spanResult = stackalloc byte[16];
        CityHash128.HashBytes(data, spanResult);

        Assert.That(arrayResult, Is.EqualTo(spanResult.ToArray()));
    }

    private static byte[] Ascending(int n) => Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

    private static byte[] Lcg(int n, ulong seed)
    {
        var a = new byte[n];
        ulong s = seed;
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            a[i] = (byte)(s >> 56);
        }
        return a;
    }
}
