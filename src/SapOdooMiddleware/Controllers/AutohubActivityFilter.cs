using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Writes the invoice history (invoice_activity) after each successful change on the Autohub invoice
/// controllers, so every action is recorded in plain words without each endpoint doing it. Reads the
/// route (documentId, lineId), the request body and the result; GETs and failed requests are not
/// recorded. Recording is best effort (see <see cref="InvoiceActivityLog"/>).
/// </summary>
public sealed class AutohubActivityFilter : IAsyncActionFilter
{
    private readonly IInvoiceActivityLog _log;

    public AutohubActivityFilter(IInvoiceActivityLog log) => _log = log;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || executed.Exception is not null && !executed.ExceptionHandled) return;
        if (!Succeeded(executed.Result)) return;
        if (!TryGuid(context, "documentId", out var documentId)) return;

        var actor = context.HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";
        TryGuid(context, "lineId", out var lineId);
        var args = context.ActionArguments;
        var value = (executed.Result as ObjectResult)?.Value;
        var ct = context.HttpContext.RequestAborted;

        Task Line(string action, string detail) =>
            _log.RecordLinesAsync(documentId, action, detail, actor, new[] { new ActivityLine(lineId) }, ct);
        Task Doc(string action, string detail) => _log.RecordAsync(documentId, action, detail, actor, ct);

        switch (context.ActionDescriptor.RouteValues["action"])
        {
            // ── per line ──
            case "MatchLine":
                await Line("matched", $"Matched to SAP item {(Arg<PartsMatchRequest>(args)?.ItemCode ?? "?").Trim()}."); break;
            case "AutoMatchLine":
                await Line("auto_match", $"Auto-match run again: now {StatusText((value as PartsReviewLineRow)?.ReviewStatus)}."); break;
            case "SkipLine":
                await Line("skipped", "Skipped."); break;
            case "EnrichLine":
                await Line("looked_up", "Part looked up again (DGX)."); break;
            case "RejectLine":
                await Line("rejected", "Rejected the matched part: needs manual handling."); break;
            case "ReopenLine":
                await Line("reopened", "Reopened: needs manual handling."); break;
            case "MarkNeedsManual":
                await Line("needs_manual", "Marked needs manual handling."); break;
            case "ResolveManufacturer":
            {
                var b = Arg<AutohubDocumentsController.ResolveManufacturerBody>(args);
                await Line("marque", $"Marque set to {b?.ManufacturerCode?.Trim().ToUpperInvariant() ?? "?"}" +
                    (b?.ItemsGroupCode is > 0 ? $", SAP group {b.ItemsGroupCode}." : ".")); break;
            }
            case "SwapDonor":
            {
                var b = Arg<SwapDonorRequest>(args);
                await Line("part_changed", $"Part changed to {b?.ArticleNumber}{(string.IsNullOrWhiteSpace(b?.Supplier) ? "" : $" ({b!.Supplier})")}."); break;
            }
            case "SwapDonorApi":
            {
                var b = Arg<SwapDonorApiRequest>(args);
                await Line("part_changed", $"Part changed to {b?.ArticleNumber ?? b?.TecdocArticleId?.ToString()}" +
                    $"{(string.IsNullOrWhiteSpace(b?.Supplier) ? "" : $" ({b!.Supplier})")} from a TecDoc search."); break;
            }
            case "UpdateLine":
            {
                var b = Arg<UpdateLineRequest>(args);
                var parts = new List<string>();
                if (b?.Quantity is { } q) parts.Add($"quantity {q:0.###}");
                if (b?.UnitPriceForeign is { } p) parts.Add($"unit price {p:0.####}");
                if (b?.Description is { } d) parts.Add($"description \"{d}\"");
                await Line("edited", "Edited the line: " + (parts.Count > 0 ? string.Join(", ", parts) : "no change") + "."); break;
            }
            case "CreateNewLine":
                await Line("queued", Arg<CreateNewRequest>(args)?.Confirmed == true
                    ? "Confirmed the matched part and marked to create." : "Marked to create."); break;

            // ── whole invoice ──
            case "AutoMatch":
                await Doc("auto_match", "Auto-match run again for pending lines."); break;
            case "ConfirmResolvedMarques":
                await Doc("marques_accepted", $"Accepted the DGX marque on {Count(value, "confirmed")} line(s)."); break;
            case "BulkMarkPendingAsCreateNew":
                await Doc("queued_all", "Marked all pending lines to create."); break;
            case "BulkSkipPending":
                await Doc("skipped_all", $"Skipped {Count(value, "skipped")} undecided line(s)."); break;
            case "BulkReopenSkipped":
                await Doc("reopened_all", args.TryGetValue("reEnrich", out var re) && re is true
                    ? "Reopened the skipped lines to be looked up again." : "Reopened the skipped lines: needs manual handling."); break;
            case "BulkConfirmCreateNew":
                await Doc("confirmed_all", $"Confirmed {Count(value, "confirmed")} queued line(s)."); break;
            case "BulkCreate":
                await Doc("bulk_create", $"Bulk Create: {Count(value, "created")} of {Count(value, "attempted")} created, {Count(value, "failed")} failed."); break;
            case "CompleteReview":
                await Doc("completed", "Completed the review."); break;

            // ── Bulk Create job (AutohubBulkCreateController) ──
            case "Start":
            {
                var b = Arg<AutohubBulkCreateController.BulkCreateStartRequest>(args);
                await Doc("bulk_create_started", b?.Lines is { Count: > 0 } l
                    ? $"Started Bulk Create for {l.Count} chosen line(s)." : "Started Bulk Create for all queued lines."); break;
            }

            // ── price review (AutohubPriceReviewController) ──
            case "Decide" when value is PriceReviewDecideResult { Saved: > 0 }:
            {
                var b = Arg<AutohubPriceReviewController.DecisionsRequest>(args);
                var reviewer = string.IsNullOrWhiteSpace(b?.ReviewedBy) ? actor : b!.ReviewedBy!.Trim();
                var lines = (b?.Lines ?? new()).Select(l => new ActivityLine(l.LineId,
                    (l.Status ?? "").Trim().ToLowerInvariant() == "held"
                        ? "Price held" + (l.Pl03 is { } h ? $" at PL03 {h:N0}" : "") + Reason(l.Reason) + "."
                        : (l.Pl03 is { } p ? $"Price approved at PL03 {p:N0}" : "Price approved at the calculated PL03") + Reason(l.Reason) + "."))
                    .ToList();
                await _log.RecordLinesAsync(documentId, "price", "Price decision.", reviewer, lines, ct); break;
            }
            case "Clear":
                await Line("price_cleared", "Price decision cleared."); break;

            // ── Issues bulk (AutohubIssuesController) ──
            case "Bulk" when value is IssueBulkResult r && r.Succeeded > 0:
            {
                var b = Arg<AutohubIssuesController.BulkRequest>(args);
                var text = r.Action switch
                {
                    "skip" => "Skipped (from Issues).",
                    "queue" => "Marked to create (from Issues).",
                    "confirm_and_queue" => "Confirmed the matched part and marked to create (from Issues).",
                    "use_suggested_match" => "Matched to the suggested SAP item (from Issues).",
                    "accept_marque" => "Accepted the DGX marque (from Issues).",
                    "assign_marque" => $"Marque set to {b?.ManufacturerCode?.Trim().ToUpperInvariant()}" +
                                       (b?.ItemsGroupCode is > 0 ? $", SAP group {b.ItemsGroupCode}" : "") + " (from Issues).",
                    "retry_create" => "Creation retried (from Issues).",
                    _ => r.Action,
                };
                await _log.RecordLinesAsync(documentId, r.Action, text, actor,
                    r.Results.Where(x => x.Ok).Select(x => new ActivityLine(x.LineId)).ToList(), ct);
                break;
            }
        }
    }

    private static string Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : $" (reason: {reason.Trim()})";

    private static bool Succeeded(IActionResult? result) => result switch
    {
        null => false,
        IStatusCodeActionResult s => s.StatusCode is null or (>= 200 and < 300),
        _ => true,
    };

    private static bool TryGuid(ActionExecutingContext ctx, string name, out Guid id)
    {
        id = Guid.Empty;
        return ctx.ActionArguments.TryGetValue(name, out var v) && v is Guid g && (id = g) != Guid.Empty;
    }

    private static T? Arg<T>(IDictionary<string, object?> args) where T : class =>
        args.Values.OfType<T>().FirstOrDefault();

    /// <summary>A number from an anonymous result object (e.g. new { skipped = 12 }); "?" when absent.</summary>
    private static string Count(object? value, string property) =>
        value?.GetType().GetProperty(property)?.GetValue(value)?.ToString() ?? "?";

    private static string StatusText(string? status) => status switch
    {
        "matched" => "matched", "skip" => "skipped", "needs_confirmation" => "confirm match",
        "pending" => "pending", "needs_manual" => "needs manual", null => "unchanged", _ => status,
    };
}
