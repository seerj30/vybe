namespace Vybe.GameRules;

/// <summary>
/// PCG32 (XSH-RR, 64-bit LCG state): the reference <c>pcg32_srandom_r</c> /
/// <c>pcg32_random_r</c> from pcg-random.org. Spec v1.3 §7.
/// </summary>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _inc;

    public Pcg32(ulong initState, ulong initSeq)
    {
        _inc = (initSeq << 1) | 1UL;
        NextUInt32();
        _state = unchecked(_state + initState);
        NextUInt32();
    }

    public uint NextUInt32()
    {
        var old = _state;
        _state = unchecked(old * Multiplier + _inc);
        var xorShifted = unchecked((uint)(((old >> 18) ^ old) >> 27));
        var rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << (-rot & 31));
    }

    /// <summary>Integer in [lo, hi] as <c>lo + (next() mod (hi - lo + 1))</c>; modulo bias accepted (§7).</summary>
    public int NextInRange(int lo, int hi)
    {
        if (hi < lo)
        {
            throw new ArgumentOutOfRangeException(nameof(hi), $"invalid range [{lo}, {hi}]");
        }

        var span = (ulong)((long)hi - lo + 1);
        return (int)(lo + (long)(NextUInt32() % span));
    }
}
