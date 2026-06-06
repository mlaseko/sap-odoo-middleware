using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Background enrichment (Q1 = background): after extraction + auto-match, proactively enriches the
/// pending, non-promotional, not-yet-enriched lines so the review page loads ready. Each pass runs
/// in a DI scope pinned to the Autohub tenant (same pattern as AutoMatchWorker). On-demand
/// enrichment (the operator clicking "Create New") remains the fallback for cache misses.
///
/// Outcomes are persisted on the line: usable results stay 'pending' (the review UI distinguishes
/// ready vs needs-confirmation via EnrichmentSource + confirmation_required); failures and
/// partial/unmatched results move to 'needs_manual' so the operator decides — never silently dropped.
/// </summary>
public sealed class EnrichmentBackgroundWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly EnrichmentSettings _settings;
    private readonly ILogger<EnrichmentBackgroundWorker> _logger;

    public EnrichmentBackgroundWorker(IServiceProvider sp, IOptions<EnrichmentSettings> settings, ILogger<EnrichmentBackgroundWorker> logger)
    {
        _sp = sp;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.BackgroundWorkerEnabled)
        {
            _logger.LogInformation("EnrichmentBackgroundWorker disabled (Enrichment:BackgroundWorkerEnabled=false).");
            return;
        }

        _logger.LogInformation("EnrichmentBackgroundWorker started (Autohub).");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _settings.PollIntervalSeconds)));

        do
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enrichment pass failed; will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<CompanyContext>().SetCompany(CompanyContext.AutohubKey);

        var review = scope.ServiceProvider.GetRequiredService<IPartsReviewRepository>();
        var enrichment = scope.ServiceProvider.GetRequiredService<IEnrichmentService>();
        var router = scope.ServiceProvider.GetRequiredService<IEnrichmentResultRouter>();
        var filter = scope.ServiceProvider.GetRequiredService<IOemFilterService>();

        var candidates = await review.GetLinesNeedingEnrichmentAsync(_settings.BatchSize, ct);
        if (candidates.Count == 0) return;

        int ready = 0, autoMatched = 0, confirm = 0, manual = 0;
        foreach (var line in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var clean = filter.Filter(line.OemNumbers, line.SupplierArticleNumber, line.Brand).CleanOems;
            try
            {
                var enr = await enrichment.EnrichLineAsync(
                    new EnrichmentInput(line.SupplierArticleNumber, clean, line.Brand, line.Description, null), ct);

                // Persist + route (failed/partial → needs_manual; donor already a SAP item → auto-match
                // when same supplier, needs_confirmation for vehicle-group brands, create-new cross-supplier).
                var result = await router.ApplyAsync(line.Id, line.Brand, enr, ct);
                switch (result.Routing)
                {
                    case LineEnrichmentRouting.AutoMatched:      autoMatched++; break;
                    case LineEnrichmentRouting.NeedsConfirmation: confirm++; break;
                    case LineEnrichmentRouting.NeedsManual:      manual++; break;
                    default:                                     ready++; break;
                }
            }
            catch (Exception ex)
            {
                // Transport failure — flag for the operator and move on (don't re-pick next pass).
                _logger.LogWarning(ex, "Enrichment failed for line {LineId}; moving to needs_manual.", line.Id);
                await review.RecordEnrichmentResultAsync(line.Id, null, null, null, null, false, "failed", "dgx_unreachable", "unmatched", null, ct);
                await review.SetReviewStatusAsync(line.Id, "needs_manual", null, ct);
                manual++;
            }

            await Task.Delay(200, ct);   // be nice to DGX
        }

        _logger.LogInformation("Enrichment pass: {Ready} ready, {AutoMatched} auto-matched, {Confirm} needs-confirm, {Manual} → needs_manual (of {Total}).",
            ready, autoMatched, confirm, manual, candidates.Count);
    }
}
