namespace PokeBattle.Domain;

public interface IRandomSource
{
    ulong State { get; }
    int NextInt(int minInclusive, int maxInclusive);
    double NextDouble();
}

public sealed class XorShiftRandom : IRandomSource
{
    private ulong _state;

    public XorShiftRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong State => _state;

    private ulong NextUInt64()
    {
        var x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 2685821657736338717UL;
    }

    public int NextInt(int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive) throw new ArgumentOutOfRangeException(nameof(maxInclusive));
        var range = (ulong)(maxInclusive - minInclusive + 1);
        return minInclusive + (int)(NextUInt64() % range);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
