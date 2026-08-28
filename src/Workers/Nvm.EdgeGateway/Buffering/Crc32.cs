namespace Nvm.EdgeGateway.Buffering;

/// <summary>CRC-32/ISO-HDLC for detecting torn or changed queue payloads.</summary>
internal static class Crc32
{
    private const uint Polynomial = 0xedb88320;

    internal static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;

        foreach (var value in bytes)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : Polynomial);
            }
        }

        return ~crc;
    }
}
