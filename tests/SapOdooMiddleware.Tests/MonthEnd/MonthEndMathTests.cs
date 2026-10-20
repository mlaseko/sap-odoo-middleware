using SapOdooMiddleware.Services.MonthEnd;

namespace SapOdooMiddleware.Tests.MonthEnd;

public class MonthEndMathTests
{
    private static readonly DateOnly Today = new(2026, 12, 15);

    [Fact]
    public void ParseAsOf_DefaultsToToday_AndReadsIsoDates()
    {
        Assert.Equal((Today, (string?)null), MonthEndMath.ParseAsOf(null, Today));
        Assert.Equal((Today, (string?)null), MonthEndMath.ParseAsOf("  ", Today));
        Assert.Equal((new DateOnly(2026, 11, 30), (string?)null), MonthEndMath.ParseAsOf("2026-11-30", Today));
    }

    [Theory]
    [InlineData("30/11/2026")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    public void ParseAsOf_RejectsOtherFormats(string text)
    {
        var (date, error) = MonthEndMath.ParseAsOf(text, Today);
        Assert.Null(date);
        Assert.Contains("YYYY-MM-DD", error);
    }

    [Fact]
    public void ParsePeriod_DefaultsToTheCalendarMonthOfTo()
    {
        var (from, to, error) = MonthEndMath.ParsePeriod(null, null, Today);
        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 12, 1), from);
        Assert.Equal(Today, to);

        (from, to, error) = MonthEndMath.ParsePeriod(null, "2026-11-30", Today);
        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 11, 1), from);
        Assert.Equal(new DateOnly(2026, 11, 30), to);

        (from, to, error) = MonthEndMath.ParsePeriod("2026-10-15", "2026-11-30", Today);
        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 10, 15), from);
    }

    [Fact]
    public void ParsePeriod_FromAfterTo_IsAnError()
    {
        var (_, _, error) = MonthEndMath.ParsePeriod("2026-12-01", "2026-11-30", Today);
        Assert.Contains("is after", error);
    }

    [Theory]
    [InlineData(10, 0, 0, 10)]
    [InlineData(10, 4, 0, 6)]
    [InlineData(10, 4, 6, 0)]
    [InlineData(10, 12, 0, 0)]
    [InlineData(10, -1, -1, 10)]
    public void OpenQuantity_NeverNegative(double received, double invoiced, double returned, double expected)
    {
        Assert.Equal(expected, MonthEndMath.OpenQuantity(received, invoiced, returned));
    }

    [Fact]
    public void OpenValue_IsProRataToTheOpenQuantity()
    {
        Assert.Equal(600.0, MonthEndMath.OpenValue(10, 1000, 4, 0));
        Assert.Equal(0.0, MonthEndMath.OpenValue(10, 1000, 10, 0));
        Assert.Equal(0.0, MonthEndMath.OpenValue(0, 1000, 0, 0));
        Assert.Equal(333.33, MonthEndMath.OpenValue(3, 1000, 2, 0));
    }
}
