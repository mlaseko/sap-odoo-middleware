using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Carries a counted line forward to the posting moment (pure; unit-tested).
/// <para>updated count = counted + (in − out of the counted bin entered at or after the
/// minute the line was counted); adjustment = updated count − stock now.</para>
/// <para>A movement entered in the same minute as the count is treated as after it.
/// SAP keeps entry times to the minute (OILM.CreateTime, HHMM).</para>
/// </summary>
public static class CountingRecalculator
{
    public const string StatusPost = "post";
    public const string StatusZero = "zero";
    public const string StatusConflict = "conflict";

    /// <summary>OILM.CreateDate + CreateTime (HHMM, SAP local time) as one moment.</summary>
    public static DateTime EntryMoment(DateTime createDate, int? createTime)
    {
        int hhmm = createTime ?? 0;
        int hours = Math.Clamp(hhmm / 100, 0, 23);
        int minutes = Math.Clamp(hhmm % 100, 0, 59);
        return createDate.Date.AddHours(hours).AddMinutes(minutes);
    }

    /// <summary>Truncates to the minute, the precision SAP stores entry times at.</summary>
    public static DateTime ToMinute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);

    public static CountingRecalcLine Recalculate(
        CountingLineDetail line,
        decimal countedQty,
        DateTime measuredFrom,
        bool countTimeKnown,
        decimal stockNow,
        IEnumerable<CountingLineMovement> movements,
        DateTime? countDate = null)
    {
        var from = ToMinute(measuredFrom);
        var mine = movements
            .Where(m => m.LineNum == line.LineNum)
            .OrderBy(m => m.CreatedAt)
            .ToList();
        var after = mine.Where(m => m.CreatedAt >= from).ToList();
        // For the reviewer: everything since the count date (start of day), whether the
        // counter saw it (before the count) or it was carried forward (after).
        var sinceCountDate = countDate is DateTime date
            ? mine.Where(m => m.CreatedAt >= date.Date).ToList()
            : after;
        decimal movedIn = after.Sum(m => m.QtyIn);
        decimal movedOut = after.Sum(m => m.QtyOut);
        decimal net = movedIn - movedOut;
        decimal updated = countedQty + net;

        decimal? adjustment = null;
        string status;
        if (updated < 0)
        {
            status = StatusConflict;
        }
        else
        {
            adjustment = updated - stockNow;
            status = adjustment == 0 ? StatusZero : StatusPost;
        }

        return new CountingRecalcLine
        {
            LineNum = line.LineNum,
            ItemCode = line.ItemCode,
            WhsCode = line.WhsCode,
            BinEntry = line.BinEntry,
            BinCode = line.BinCode,
            SystemQty = line.SystemQty,
            CountedQty = countedQty,
            MeasuredFrom = from,
            CountTimeKnown = countTimeKnown,
            MovedIn = movedIn,
            MovedOut = movedOut,
            NetMovement = net,
            UpdatedQty = updated,
            StockNow = stockNow,
            Adjustment = adjustment,
            Status = status,
            Movements = after,
            MovedInSinceCountDate = sinceCountDate.Sum(m => m.QtyIn),
            MovedOutSinceCountDate = sinceCountDate.Sum(m => m.QtyOut),
            MovementsSinceCountDate = sinceCountDate,
        };
    }

    /// <summary>
    /// Which count and which moment a line is measured from. The app's capture time is
    /// used only when the app's count equals the counting's own count, so a count changed
    /// in SAP after capture never borrows the app's older time. Otherwise the counting's
    /// count date (start of day) is used, which is what SAP measures against today.
    /// </summary>
    public static (decimal Counted, DateTime From, bool Known) MeasurePoint(
        CountingLineDetail line, CountingLineCount? appCount, DateTime countDate, TimeZoneInfo sapZone)
    {
        decimal counted = line.CountedQty ?? 0m;
        if (appCount?.CountedAt is DateTimeOffset at &&
            appCount.CountedQty is decimal appCounted &&
            appCounted == counted)
        {
            var local = TimeZoneInfo.ConvertTime(at, sapZone).DateTime;
            return (counted, DateTime.SpecifyKind(local, DateTimeKind.Unspecified), true);
        }
        return (counted, countDate.Date, false);
    }
}
