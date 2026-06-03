using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BadmintonDraw.Core;

internal sealed class StableRandom
{
    private ulong _state;

    public StableRandom(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            throw new DrawValidationException("随机数种子不能为空。");
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed.Trim()));
        _state = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
        if (_state == 0)
        {
            _state = 0x9E3779B97F4A7C15UL;
        }
    }

    public void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = NextInt(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }

        var bound = (ulong)maxExclusive;
        var threshold = (ulong.MaxValue - bound + 1) % bound;

        while (true)
        {
            var value = NextUInt64();
            if (value >= threshold)
            {
                return (int)(value % bound);
            }
        }
    }

    private ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
