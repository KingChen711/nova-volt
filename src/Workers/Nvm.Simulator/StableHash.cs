namespace Nvm.Simulator;

/// <summary>A hash that gives the same answer in every process, which <c>GetHashCode</c> does not.</summary>
/// <remarks>
/// FNV-1a. <see cref="string.GetHashCode()"/> is randomised per process, so a channel's personality —
/// how far its readings sit from the line's average, whether its clock is the broken one — would be
/// reshuffled by every restart. Two runs of the same plant could then not be compared, and a fault
/// reproduced this morning would land on a different machine this afternoon.
/// </remarks>
internal static class StableHash
{
    private const uint OffsetBasis = 2166136261u;
    private const uint Prime = 16777619u;

    /// <summary>Hashes text the same way on every machine and in every run.</summary>
    /// <param name="text">What to hash.</param>
    /// <remarks>
    /// FNV-1a and then an avalanche step. The second half is not decoration: FNV-1a's low bits are
    /// weak, and every caller here takes the answer modulo something. Channel codes differ only in
    /// their last few digits, and on the thousand of them a formation line actually has, asking for a
    /// tenth of the devices got 12.8% without the mixing and 8.9% with it — a fault injector that
    /// overshoots by a quarter puts that bias into every drift number the milestone reports.
    /// </remarks>
    public static uint Of(string text)
    {
        var hash = OffsetBasis;

        foreach (var character in text)
        {
            hash = (hash ^ character) * Prime;
        }

        // Murmur3-style finalizer: spreads the entropy of the last characters over the whole word, so
        // taking the bottom bits is as good as taking any others.
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        hash *= 0x846ca68b;
        hash ^= hash >> 16;

        return hash;
    }
}
