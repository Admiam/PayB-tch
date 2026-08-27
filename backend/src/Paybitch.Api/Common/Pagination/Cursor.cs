using System.Globalization;
using System.Text;

namespace Paybitch.Api.Common.Pagination;

/// <summary>
/// Opaque cursor codec — a base64url wrapper over the underlying keyset/seq payload. Clients never
/// parse a cursor: <c>nextCursor</c> in, opaque out (§3.4, §3.5.3). Encoding is deliberately
/// reversible-but-opaque (not encrypted): it only hides that the payload is a sortable key so no
/// client couples to its shape. A malformed cursor decodes to <c>false</c> so callers can reject it.
/// </summary>
public static class Cursor
{
    /// <summary>Wrap an arbitrary keyset payload (e.g. <c>"&lt;createdAt&gt;|&lt;id&gt;"</c>) as an opaque cursor.</summary>
    public static string Encode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Unwrap an opaque cursor to its payload; <c>false</c> if it is malformed.</summary>
    public static bool TryDecode(string? cursor, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrEmpty(cursor))
            return false;

        if (!TryBase64UrlDecode(cursor, out var bytes))
            return false;

        try
        {
            payload = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Wrap a <c>change_log.seq</c> (the <c>/sync</c> cursor, §3.5.3) as an opaque cursor.</summary>
    public static string EncodeSeq(long seq) => Encode(seq.ToString(CultureInfo.InvariantCulture));

    /// <summary>Unwrap a <c>/sync</c> cursor to its <c>seq</c>; <c>false</c> if malformed or non-numeric.</summary>
    public static bool TryDecodeSeq(string? cursor, out long seq)
    {
        seq = 0;
        return TryDecode(cursor, out var payload)
            && long.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        // base64url without padding.
        var encoded = Convert.ToBase64String(bytes);
        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        var sb = new StringBuilder(value.Length + 3);
        foreach (var c in value)
        {
            sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }
        switch (sb.Length % 4)
        {
            case 2: sb.Append("=="); break;
            case 3: sb.Append('='); break;
            case 1: return false; // never a valid base64 length
        }

        var candidate = sb.ToString();
        var buffer = new byte[candidate.Length]; // upper bound on decoded length
        if (Convert.TryFromBase64String(candidate, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }
        return false;
    }
}
