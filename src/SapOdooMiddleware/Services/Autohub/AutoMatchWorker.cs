using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Continuously auto-matches pending Autohub staging lines (Tier 1 OEM / Tier 2 article). Polls
/// every 10s, idle when there is nothing pending. Each pass runs in a DI scope pinned to the
/// Autohub tenant (Phase A worker pattern) so all parts_catalog access uses the Autohub connection.
/// Applies decisions via <see cref="IPartsLineMatchRepository"/>; unmatched lines stay 'pending'
/// for the operator.
/// </summary>
public sealed class AutoMatchWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private const int BatchSize = 200;

    private readonly IServiceProvider _sp;
    private readonly ILogger<AutoMatchWorker> _logger;

    public AutoMatchWorker(IServiceProvider sp, ILogger<AutoMatchWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoMatchWorker started (Autohub).");
        using var timer = new PeriodicTimer(PollInterval);

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
                // Neon may be briefly unreachable — log and retry next tick.
                _logger.LogError(ex, "AutoMatch pass failed; will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<CompanyContext>().SetCompany(CompanyContext.AutohubKey);

        var lines = scope.ServiceProvider.GetRequiredService<IPartsLineMatchRepository>();
        var matcher = scope.ServiceProvider.GetRequiredService<IAutoMatchService>();

        var candidates = await lines.ListPendingMatchCandidatesAsync(BatchSize, ct);
        if (candidates.Count == 0) return;

        int matched = 0, skipped = 0, pending = 0;
        foreach (var line in candidates)
        {
            var decision = await matcher.DecideAsync(line, ct);
            switch (decision.Status)
            {
                case "matched":
                    await lines.SetMatchedAsync(line.Id, decision.ItemCode!, ct);
                    matched++;
                    break;
                case "skip":
                    await lines.SetReviewStatusAsync(line.Id, "skip", ct);
                    skipped++;
                    break;
                default:
                    pending++;   // left for the operator
                    break;
            }
        }

        _logger.LogInformation(
            "AutoMatch pass: {Matched} matched, {Skipped} skipped, {Pending} left pending (of {Total}).",
            matched, skipped, pending, candidates.Count);
    }
}
