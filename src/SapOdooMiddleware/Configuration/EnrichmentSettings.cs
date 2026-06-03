namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Phase B enrichment knobs. The DGX base URL itself comes from the active tenant
/// (Companies:Autohub:Classifier:BaseUrl) via ICompanyContext; this section governs the
/// background worker and the enrichment call options. Bound from the optional "Enrichment" section.
/// </summary>
public sealed class EnrichmentSettings
{
    public const string SectionName = "Enrichment";

    /// <summary>Allow DGX to run a live Germax scrape on a cache miss (Slice 2 path; Q4 = yes).</summary>
    public bool AllowLiveScrape { get; set; } = true;

    /// <summary>Max OEM numbers to try as borrow bridges (Path C). Q10 = first hit wins.</summary>
    public int MaxOemBridgesToTry { get; set; } = 5;

    /// <summary>Skip the DGX image HEAD/vision verification (Q5 = off in Slice 1).</summary>
    public bool SkipImageVerify { get; set; } = true;

    /// <summary>Run the background enricher after extraction (Q1 = background). On-demand always works too.</summary>
    public bool BackgroundWorkerEnabled { get; set; } = true;

    /// <summary>Seconds between background-worker passes.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Lines enriched per background pass (be nice to DGX).</summary>
    public int BatchSize { get; set; } = 20;
}
