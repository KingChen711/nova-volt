using System.Buffers;
using System.Security.Cryptography;

namespace Nvm.Ingestion.RawCurves;

/// <summary>Một SHA-256 tính trên đúng các byte được đưa ra để archive.</summary>
/// <param name="Hex">Dạng hexadecimal chữ thường, lưu trong PostgreSQL và object metadata.</param>
/// <param name="Base64">Dạng wire gửi qua header checksum của S3.</param>
/// <param name="ByteSize">Số byte mà digest bao phủ.</param>
public sealed record RawCurveDigest(string Hex, string Base64, long ByteSize)
{
    /// <summary>Hash từ vị trí hiện tại của stream và khôi phục lại vị trí đó để upload.</summary>
    public static async Task<RawCurveDigest> CalculateAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ValidateSource(source);

        var start = source.Position;

        try
        {
            return await CalculateOneWayAsync(source, cancellationToken);
        }
        finally
        {
            source.Position = start;
        }
    }

    /// <summary>Tính lại hash trên một stream đã tải về và so sánh cả digest lẫn số byte.</summary>
    public static async Task<bool> MatchesAsync(
        Stream candidate,
        string expectedHex,
        long expectedByteSize,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHex);

        ArgumentNullException.ThrowIfNull(candidate);

        if (!candidate.CanRead)
        {
            throw new ArgumentException("Raw curve input must be readable.", nameof(candidate));
        }

        var actual = candidate.CanSeek
            ? await CalculateAsync(candidate, cancellationToken)
            : await CalculateOneWayAsync(candidate, cancellationToken);

        return actual.ByteSize == expectedByteSize
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual.Hex),
                Convert.FromHexString(expectedHex));
    }

    private static void ValidateSource(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "Raw curve input must be readable and seekable so the bytes hashed are the bytes uploaded.",
                nameof(source));
        }
    }

    private static async Task<RawCurveDigest> CalculateOneWayAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        long byteSize = 0;

        try
        {
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(
                       buffer.AsMemory(0, buffer.Length),
                       cancellationToken)) > 0)
            {
                digest.AppendData(buffer, 0, bytesRead);
                byteSize = checked(byteSize + bytesRead);
            }

            if (byteSize == 0)
            {
                throw new ArgumentException("A raw formation curve cannot be empty.", nameof(source));
            }

            var hash = digest.GetHashAndReset();

            return new RawCurveDigest(
                Convert.ToHexStringLower(hash),
                Convert.ToBase64String(hash),
                byteSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
