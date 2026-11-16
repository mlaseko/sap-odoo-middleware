using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Price review for an Autohub invoice: the prices Bulk Create would write for each line still to be
/// created, the warnings on them, and the reviewer's saved decisions. Separate from
/// AutohubDocumentsController so that controller's constructor is untouched.
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public sealed class AutohubPriceReviewController : ControllerBase
{
    private readonly IPriceReviewService _review;

    public AutohubPriceReviewController(IPriceReviewService review) => _review = review;

    /// <summary>
    /// GET /api/autohub/documents/{documentId}/price-review
    /// Every line with ReviewStatus create_new / create_failed, priced exactly as Bulk Create prices
    /// it: invoice unit price less the line discount, times the document currency's forex rate,
    /// then cost (PL01), the calculated PL03 and PL05, margin, ratio/band/floor, and warning flags.
    /// Saved decisions are included and marked stale when the figures behind them changed.
    /// Read-only. 404 when the document does not exist.
    /// </summary>
    [HttpGet("{documentId:guid}/price-review")]
    [ProducesResponseType(typeof(PriceReviewDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken ct)
    {
        var review = await _review.GetReviewAsync(documentId, ct);
        return review is null ? NotFound() : Ok(review);
    }
}
