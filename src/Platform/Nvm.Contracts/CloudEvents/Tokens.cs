using System.Diagnostics.CodeAnalysis;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Shape rules for the individual segments that make up an event type, a source URN and a routing key.
/// </summary>
/// <remarks>
/// Kept in one place because the same two shapes appear in three different strings, and three copies
/// of "is this a valid segment" is how they start disagreeing with each other.
/// </remarks>
internal static class Tokens
{
    /// <summary>
    /// Fixed vocabulary: lower-case ASCII, words joined by single hyphens. For example
    /// <c>traceability</c>, <c>unit-serialized</c>, <c>app-execution</c>.
    /// </summary>
    /// <remarks>
    /// Lower case is not a style preference. These segments are written by us, read by machines, and
    /// compared byte for byte by an AMQP broker; allowing two spellings of the same word is allowing
    /// two routing keys that look identical to a human and never match each other.
    /// </remarks>
    internal static bool IsFixedVocabulary([NotNullWhen(true)] string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // Must open with a letter and close with a letter or digit, so '-lot', 'lot-' and '2fast'
        // are all rejected. A leading digit would also make the segment ambiguous with a version.
        if (!char.IsAsciiLetterLower(token[0]) || token[^1] == '-')
        {
            return false;
        }

        for (var index = 1; index < token.Length; index++)
        {
            var character = token[index];

            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            {
                continue;
            }

            // A single hyphen between words is allowed; a double hyphen is not, because it survives
            // a careless copy-paste and produces a second, silently different, spelling.
            if (character == '-' && token[index - 1] != '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// A site code in its canonical form: upper-case ASCII letters and digits, for example <c>NV1</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately looser than the three characters a serial number allows. The authoritative list of
    /// sites belongs to the factory model, not to a string parser; this only enforces what the wire
    /// format itself needs, which is that a site code carries no separator and no lower case.
    /// </remarks>
    internal static bool IsSiteCode([NotNullWhen(true)] string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
