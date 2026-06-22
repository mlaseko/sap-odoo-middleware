namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Controls SapSkuCounterRefreshService — the job that bumps the Neon sku_counters up to the live
/// SAP MAX (per prefix) so counters never need manual seeding. Bound from the optional
/// "AutohubSkuRefresh" section.
/// </summary>
public sealed class AutohubSkuRefreshSettings
{
    public const string SectionName = "AutohubSkuRefresh";

    /// <summary>Master switch for the background refresh (the admin endpoint always works).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Run a refresh once at service startup.</summary>
    public bool RefreshOnStartup { get; set; } = true;

    /// <summary>Interval between background refreshes. Default 24h.</summary>
    public int IntervalHours { get; set; } = 24;
}
