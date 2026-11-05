using System.Text;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class Crc32CTests
{
    [Test]
    public void Compute_KnownVector_123456789()
    {
        // Canonical CRC-32C check value (iSCSI / RFC 3720 appendix).
        var data = Encoding.ASCII.GetBytes("123456789");
        Assert.That(Crc32C.Compute(data), Is.EqualTo(0xE3069283u));
        Assert.That(Crc32C.ComputeSoftware(data), Is.EqualTo(0xE3069283u));
    }

    [Test]
    public void Compute_Empty_IsZero()
    {
        Assert.That(Crc32C.Compute(ReadOnlySpan<byte>.Empty), Is.EqualTo(0u));
        Assert.That(Crc32C.ComputeSoftware(ReadOnlySpan<byte>.Empty), Is.EqualTo(0u));
    }

    [Test]
    public void Compute_32ZeroBytes_MatchesReference()
    {
        // From the RFC 3720 test vectors: 32 bytes of 0x00 -> 0x8A9136AA.
        Assert.That(Crc32C.Compute(new byte[32]), Is.EqualTo(0x8A9136AAu));
    }

    [Test]
    public void Compute_32FFBytes_MatchesReference()
    {
        // From the RFC 3720 test vectors: 32 bytes of 0xFF -> 0x62A8AB43.
        var data = new byte[32];
        Array.Fill(data, (byte)0xFF);
        Assert.That(Crc32C.Compute(data), Is.EqualTo(0x62A8AB43u));
    }

    [Test]
    public void HardwareAndSoftwarePaths_Agree([Values(0, 1, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, 1023, 1024, 65_537, 1_048_576 + 5)] int size)
    {
        var data = TestTransport.RandomBytes(size, seed: size);
        Assert.That(Crc32C.Compute(data), Is.EqualTo(Crc32C.ComputeSoftware(data)));
    }

    [Test]
    public void Append_IsEquivalentToSingleShot()
    {
        var data = TestTransport.RandomBytes(100_003, seed: 5);
        uint whole = Crc32C.Compute(data);

        uint running = 0;
        int offset = 0;
        int[] pieces = { 1, 2, 3, 5, 8, 13, 100, 1000, 10_000 };
        int i = 0;
        while (offset < data.Length)
        {
            int take = Math.Min(pieces[i++ % pieces.Length], data.Length - offset);
            running = Crc32C.Append(running, data.AsSpan(offset, take));
            offset += take;
        }

        Assert.That(running, Is.EqualTo(whole));
    }

    [Test]
    public void Compute_DetectsSingleBitFlip()
    {
        var data = TestTransport.RandomBytes(4096, seed: 9);
        uint original = Crc32C.Compute(data);
        data[2048] ^= 0x01;
        Assert.That(Crc32C.Compute(data), Is.Not.EqualTo(original));
    }
}
