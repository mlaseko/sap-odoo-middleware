using System.Globalization;

namespace SapOdooMiddleware.Services.MonthEnd;

/// <summary>The pure parts of the month-end endpoints (spec v4 §7), kept out of the SQL so they are unit-tested.</summary>
public static class MonthEndMath
{
    /// <summary><c>as_of</c> as a date; today when absent. Null with a message when the text is not a date.</summary>
    public static (DateOnly? Date, string? Error) ParseAsOf(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text)) return (today, null);
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? (date, null)
            : (null, $"'{text}' is not a date (YYYY-MM-DD).");
    }

    /// <summary>
    /// The period for the landed cost credits: <c>from</c>/<c>to</c> as given, or the calendar month of <c>to</c>
    /// (the month of today when both are absent). <c>from</c> after <c>to</c> is an error.
    /// </summary>
    public static (DateOnly From, DateOnly To, string? Error) ParsePeriod(string? fromText, string? toText, DateOnly today)
    {
        var (to, toError) = ParseAsOf(toText, today);
        if (toError != null) return (default, default, toError);
        var (from, fromError) = ParseAsOf(fromText, new DateOnly(to!.Value.Year, to.Value.Month, 1));
        if (fromError != null) return (default, default, fromError);
        if (from!.Value > to.Value) return (default, default, $"from ({from:yyyy-MM-dd}) is after to ({to:yyyy-MM-dd}).");
        return (from.Value, to.Value, null);
    }

    /// <summary>Quantity of a goods receipt line still open at the date: received minus invoiced minus returned, never negative.</summary>
    public static double OpenQuantity(double received, double invoiced, double returned) =>
        Math.Max(0, received - Math.Max(0, invoiced) - Math.Max(0, returned));

    /// <summary>The line's value not yet invoiced: the line total pro rata to the open quantity, rounded to cents.</summary>
    public static double OpenValue(double received, double lineTotal, double invoiced, double returned)
    {
        if (received <= 0) return 0;
        double open = OpenQuantity(received, invoiced, returned);
        if (open <= 0) return 0;
        return Math.Round(lineTotal * open / received, 2, MidpointRounding.AwayFromZero);
    }
}
