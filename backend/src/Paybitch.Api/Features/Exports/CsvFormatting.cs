using System.Globalization;
using System.Text;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// The Czech / English CSV dialect (EXT-D4c). Czech Excel reads a <c>;</c>-delimited, decimal-comma,
/// UTF-8-BOM file correctly (comma is the cs decimal separator, so it cannot double as the field
/// delimiter); <c>en</c> emits the classic no-BOM, comma-delimited, decimal-point file. The dialect
/// governs <b>field parsing</b> only — it is orthogonal to formula neutralization (<see cref="CsvSafe"/>,
/// EXT-D4j), which governs <b>cell evaluation</b>.
/// </summary>
public readonly record struct CsvDialect(bool ByteOrderMark, char Delimiter, char DecimalSeparator)
{
    /// <summary>Czech: UTF-8 BOM, <c>;</c> delimiter, decimal comma (the headline dialect).</summary>
    public static readonly CsvDialect Czech = new(ByteOrderMark: true, Delimiter: ';', DecimalSeparator: ',');

    /// <summary>English: no BOM, <c>,</c> delimiter, decimal point.</summary>
    public static readonly CsvDialect English = new(ByteOrderMark: false, Delimiter: ',', DecimalSeparator: '.');

    /// <summary>
    /// Resolve the dialect from a <c>locale</c> query value. Anything starting with <c>cs</c> (or absent —
    /// the Czech-first product default) is <see cref="Czech"/>; <c>en…</c> is <see cref="English"/>.
    /// </summary>
    public static CsvDialect ForLocale(string? locale)
    {
        if (locale is not null && locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return English;
        return Czech;
    }
}

/// <summary>
/// CSV cell safety (EXT-D4j / CWE-1236) and RFC-4180 quoting. <see cref="NeutralizeFreeText"/> defuses
/// a formula-injection trigger in a <b>free-text</b> cell (<c>title</c>, <c>paid_by_name</c>,
/// <c>category</c>); <see cref="Quote"/> applies RFC-4180 field quoting. Machine-truth columns
/// (<c>amount_minor</c>, <c>date</c>, <c>currency</c>, member ids, <c>split_type</c>) pass through
/// unchanged — numeric/enum/id, injection-safe, byte-exact (D1 / EXT-D4c).
/// </summary>
public static class CsvSafe
{
    // A leading '=' '+' '-' '@', TAB (0x09) or CR (0x0D) is the formula/DDE trigger (CWE-1236).
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Prefix a single <c>'</c> when a free-text value begins with a formula trigger, so another member
    /// opening the file in Excel/LibreOffice/Sheets cannot have the cell evaluated as a formula. Null /
    /// empty is returned unchanged.
    /// </summary>
    public static string NeutralizeFreeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        return Array.IndexOf(FormulaTriggers, value[0]) >= 0 ? "'" + value : value;
    }

    /// <summary>
    /// RFC-4180 quote a single field: wrap in <c>"</c> and double embedded <c>"</c> iff the value contains
    /// the <paramref name="delimiter"/>, a quote, CR, or LF. A value needing no quoting is emitted verbatim
    /// (so machine columns stay byte-exact).
    /// </summary>
    public static string Quote(string value, char delimiter)
    {
        var mustQuote = value.IndexOf('"') >= 0
                        || value.IndexOf(delimiter) >= 0
                        || value.IndexOf('\n') >= 0
                        || value.IndexOf('\r') >= 0;
        if (!mustQuote)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Neutralize a free-text cell (EXT-D4j) then RFC-4180 quote it — the full free-text pipeline.</summary>
    public static string SafeFreeText(string? value, char delimiter) =>
        Quote(NeutralizeFreeText(value), delimiter);
}

/// <summary>
/// The no-<c>double</c> money formatter (EXT-D4c). Builds the localized human amount string by integer
/// surgery on minor units + the <c>currencies.minor_units</c> scale (D2) — never <c>amount / 100.0</c>,
/// which would reintroduce the float round-trip D1 exists to kill. Scale 0 (CZK) emits no separator;
/// scale <c>n</c> emits <c>major{sep}frac</c> with <c>frac</c> zero-padded to <c>n</c> digits.
/// </summary>
public static class MoneyText
{
    private static readonly long[] Pow10 = [1, 10, 100, 1_000, 10_000];

    /// <summary>Format <paramref name="minor"/> at <paramref name="scale"/> (0..4) with <paramref name="decimalSeparator"/>.</summary>
    public static string Human(long minor, int scale, char decimalSeparator)
    {
        if (scale <= 0)
            return minor.ToString(CultureInfo.InvariantCulture);

        if (scale >= Pow10.Length)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Currency scale must be 0..4 (D2).");

        var negative = minor < 0;
        // Use unsigned magnitude to avoid long.MinValue overflow and to place the sign once.
        var magnitude = negative ? (ulong)(-(minor + 1)) + 1UL : (ulong)minor;
        var divisor = (ulong)Pow10[scale];
        var major = magnitude / divisor;
        var frac = magnitude % divisor;

        var sb = new StringBuilder(24);
        if (negative)
            sb.Append('-');
        sb.Append(major.ToString(CultureInfo.InvariantCulture));
        sb.Append(decimalSeparator);
        sb.Append(frac.ToString(CultureInfo.InvariantCulture).PadLeft(scale, '0'));
        return sb.ToString();
    }
}
