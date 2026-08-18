using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Endpoints for the Molas Autohub inventory app (MOLAS_Live_2021).
/// Reads go through direct SQL (<see cref="IAutohubInventorySqlService"/>);
/// document writes go through the DI API (<see cref="IAutohubSapB1Service"/>) —
/// serialized by its internal lock, idempotent via the U_AppRef UDF.
/// Requires the <c>X-Api-Key</c> header.
/// </summary>
[ApiController]
[Route("api/autohub/inv")]
public class AutohubInventoryController : ControllerBase
{
    private readonly IAutohubInventorySqlService _sql;
    private readonly IBinResolver _binResolver;
    private readonly IAutohubSapB1Service _sap;
    private readonly AutohubInventorySettings _settings;
    private readonly ILogger<AutohubInventoryController> _logger;

    public AutohubInventoryController(
        IAutohubInventorySqlService sql,
        IBinResolver binResolver,
        IAutohubSapB1Service sap,
        IOptions<AutohubInventorySettings> settings,
        ILogger<AutohubInventoryController> logger)
    {
        _sql = sql;
        _binResolver = binResolver;
        _sap = sap;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/autohub/inv/warehouses
    /// All warehouses with their bin-activation flag (cached ~5 min).
    /// </summary>
    [HttpGet("warehouses")]
    [ProducesResponseType(typeof(ApiResponse<List<WarehouseInfo>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWarehouses(CancellationToken ct)
    {
        var warehouses = await _sql.GetWarehousesAsync(ct);
        return Ok(ApiResponse<List<WarehouseInfo>>.Ok(warehouses));
    }

    /// <summary>
    /// GET /api/autohub/inv/stock?item_code=BM10001&amp;whs_code=001
    /// Normalized stock for one item: per-bin detail (OIBQ) for bin warehouses,
    /// warehouse on-hand (OITW) for non-bin warehouse 01. One row per warehouse,
    /// including item name, article number, and manufacturer.
    /// </summary>
    /// <param name="itemCode">SAP ItemCode (required).</param>
    /// <param name="whsCode">Optional warehouse filter; all stocked warehouses when omitted.</param>
    [HttpGet("stock")]
    [ProducesResponseType(typeof(ApiResponse<List<StockResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<StockResponse>>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetStock(
        [FromQuery(Name = "item_code")] string? itemCode,
        [FromQuery(Name = "whs_code")] string? whsCode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<List<StockResponse>>.Fail(
                "item_code query parameter is required."));

        var stock = await _sql.GetStockAsync(itemCode.Trim(), NormalizeWhs(whsCode), ct);
        return Ok(ApiResponse<List<StockResponse>>.Ok(
            stock,
            new Dictionary<string, object>
            {
                ["item_code"] = itemCode.Trim(),
                ["total_on_hand"] = stock.Sum(s => s.TotalOnHand),
            }));
    }

    /// <summary>
    /// GET /api/autohub/inv/bins?item_code=BM10001&amp;whs_code=001&amp;direction=source
    /// Bin resolver output for one item + warehouse (spec §7):
    /// <c>auto</c> (bin pre-selected), <c>options</c> (user confirms, ≤5),
    /// <c>required</c> (user must scan), or <c>not_bin_managed</c> (warehouse 01).
    /// </summary>
    /// <param name="itemCode">SAP ItemCode (required).</param>
    /// <param name="whsCode">Warehouse code (required).</param>
    /// <param name="direction"><c>source</c> (issue/transfer out) or <c>destination</c> (receipt/transfer in). Default <c>source</c>.</param>
    [HttpGet("bins")]
    [ProducesResponseType(typeof(ApiResponse<BinResolution>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<BinResolution>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResolveBins(
        [FromQuery(Name = "item_code")] string? itemCode,
        [FromQuery(Name = "whs_code")] string? whsCode,
        [FromQuery(Name = "direction")] string direction = "source",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<BinResolution>.Fail("item_code query parameter is required."));
        if (string.IsNullOrWhiteSpace(whsCode))
            return BadRequest(ApiResponse<BinResolution>.Fail("whs_code query parameter is required."));

        BinDirection dir;
        switch (direction.Trim().ToLowerInvariant())
        {
            case "source": dir = BinDirection.Source; break;
            case "destination": dir = BinDirection.Destination; break;
            default:
                return BadRequest(ApiResponse<BinResolution>.Fail(
                    "direction must be 'source' or 'destination'."));
        }

        try
        {
            var resolution = await _binResolver.ResolveAsync(itemCode.Trim(), whsCode.Trim(), dir, ct);
            return Ok(ApiResponse<BinResolution>.Ok(
                resolution,
                new Dictionary<string, object>
                {
                    ["item_code"] = itemCode.Trim(),
                    ["whs_code"] = whsCode.Trim(),
                    ["direction"] = dir.ToString().ToLowerInvariant(),
                }));
        }
        catch (InvalidOperationException ex)
        {
            // Unknown warehouse etc. — caller error, not a server fault.
            return BadRequest(ApiResponse<BinResolution>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/transfer-requests?from_whs=003&amp;to_whs=001
    /// Open transfer request lines (OWTQ/WTQ1, spec §8.1) for the fulfillment screen,
    /// oldest first, with item name, article number, and manufacturer per line.
    /// </summary>
    /// <param name="fromWhs">Optional from-warehouse filter.</param>
    /// <param name="toWhs">Optional to-warehouse filter.</param>
    [HttpGet("transfer-requests")]
    [ProducesResponseType(typeof(ApiResponse<List<OpenTransferRequestLine>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOpenTransferRequests(
        [FromQuery(Name = "from_whs")] string? fromWhs = null,
        [FromQuery(Name = "to_whs")] string? toWhs = null,
        CancellationToken ct = default)
    {
        var lines = await _sql.GetOpenTransferRequestsAsync(
            NormalizeWhs(fromWhs), NormalizeWhs(toWhs), ct);

        return Ok(ApiResponse<List<OpenTransferRequestLine>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["total_documents"] = lines.Select(l => l.DocEntry).Distinct().Count(),
            }));
    }

    // ── Writes (Phase 2: transfers) ──────────────────────────────────

    /// <summary>
    /// POST /api/autohub/inv/transfer-requests
    /// Creates an Inventory Transfer Request (OWTQ) — intent only, no stock movement,
    /// no bins. Idempotent on <c>app_ref</c>: if a request with the same GUID already
    /// exists (e.g. after a timeout + retry), the existing DocEntry/DocNum is returned
    /// with <c>already_existed = true</c> instead of double-posting.
    /// </summary>
    [HttpPost("transfer-requests")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] TransferRequestCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.FromWhs)) errors.Add("from_whs is required.");
        if (string.IsNullOrWhiteSpace(request.ToWhs)) errors.Add("to_whs is required.");
        if (!string.IsNullOrWhiteSpace(request.FromWhs) && !string.IsNullOrWhiteSpace(request.ToWhs) &&
            string.Equals(request.FromWhs.Trim(), request.ToWhs.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("from_whs and to_whs must differ for a transfer request.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(request.Lines[i].ItemCode))
                errors.Add($"lines[{i}]: item_code is required.");
            if (request.Lines[i].Quantity <= 0)
                errors.Add($"lines[{i}]: quantity must be greater than zero.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.FromWhs = request.FromWhs.Trim();
        request.ToWhs = request.ToWhs.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            await ValidateWarehousesExistAsync(new[] { request.FromWhs, request.ToWhs }, ct);

            // Idempotency: same app_ref already posted → return it, don't double-post.
            var existing = await _sql.FindDocEntryByAppRefAsync("OWTQ", request.AppRef, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "Transfer request app_ref={AppRef} already posted as DocEntry={DocEntry} — returning existing.",
                    request.AppRef, existing.Value.DocEntry);
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));
            }

            var result = await _sap.CreateInventoryTransferRequestAsync(
                request, _settings.TransferRequestSeries, ct);
            return Ok(ApiResponse<InventoryDocResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transfer request creation failed (app_ref={AppRef}, from={From}, to={To})",
                request.AppRef, request.FromWhs, request.ToWhs);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/autohub/inv/transfers
    /// Creates an Inventory Transfer (OWTR). Lines drawn from a transfer request carry
    /// <c>base_request_entry</c>/<c>base_request_line</c> so SAP closes the request's
    /// open quantities (partials allowed). Bin handling is decided server-side from
    /// OWHS.BinActivat: for a bin-managed side with no bin supplied, the resolver
    /// auto-selects when unambiguous, otherwise the request is rejected with a message
    /// telling the caller to resolve via <c>GET /bins</c>. Same-warehouse putaway
    /// (from_whs == to_whs, both bins set) is supported. Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("transfers")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateTransfer(
        [FromBody] TransferCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.FromWhs)) errors.Add("from_whs is required.");
        if (string.IsNullOrWhiteSpace(request.ToWhs)) errors.Add("to_whs is required.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var l = request.Lines[i];
            if (string.IsNullOrWhiteSpace(l.ItemCode))
                errors.Add($"lines[{i}]: item_code is required.");
            if (l.Quantity <= 0)
                errors.Add($"lines[{i}]: quantity must be greater than zero.");
            if (l.BaseRequestEntry.HasValue != l.BaseRequestLine.HasValue)
                errors.Add($"lines[{i}]: base_request_entry and base_request_line must be set together.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.FromWhs = request.FromWhs.Trim();
        request.ToWhs = request.ToWhs.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            await ValidateWarehousesExistAsync(new[] { request.FromWhs, request.ToWhs }, ct);

            // Idempotency check before any bin/stock work.
            var existing = await _sql.FindDocEntryByAppRefAsync("OWTR", request.AppRef, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "Transfer app_ref={AppRef} already posted as DocEntry={DocEntry} — returning existing.",
                    request.AppRef, existing.Value.DocEntry);
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));
            }

            // Bin handling: middleware decides from OWHS.BinActivat (spec §5.2 step 4);
            // the frontend never needs to know which warehouses are bin-managed.
            bool fromBinManaged = await _sql.IsBinManagedAsync(request.FromWhs, ct);
            bool toBinManaged = await _sql.IsBinManagedAsync(request.ToWhs, ct);
            var binErrors = await PrepareTransferBinsAsync(request, fromBinManaged, toBinManaged, ct);
            if (binErrors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(binErrors));

            // Stock pre-check — clearer errors than SAP's, before touching the DI API.
            var stockErrors = await ValidateSourceStockAsync(request, fromBinManaged, ct);
            if (stockErrors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(stockErrors));

            var result = await _sap.CreateInventoryTransferAsync(
                request, _settings.TransferSeries, ct);
            return Ok(ApiResponse<InventoryDocResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transfer creation failed (app_ref={AppRef}, from={From}, to={To})",
                request.AppRef, request.FromWhs, request.ToWhs);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    // ── Counting → Posting (Phase 3) ─────────────────────────────────

    /// <summary>Max generated lines per counting session — keeps sessions small
    /// (an aisle or shelf run, spec §5.3) and the DI API update fast.</summary>
    private const int MaxCountingLines = 500;

    /// <summary>
    /// POST /api/autohub/inv/countings
    /// Creates a counting session (OINC). Scope: warehouse + bin_from/bin_to range or
    /// explicit bin_abs_list for bin warehouses; just the warehouse for non-bin whs 01.
    /// One line is generated per item-per-bin with stock; SAP snapshots system
    /// quantities at creation. Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("countings")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateCounting(
        [FromBody] CountingCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.WhsCode)) errors.Add("whs_code is required.");
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.WhsCode = request.WhsCode.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            await ValidateWarehousesExistAsync(new[] { request.WhsCode }, ct);
            bool binManaged = await _sql.IsBinManagedAsync(request.WhsCode, ct);

            bool hasRange = !string.IsNullOrWhiteSpace(request.BinFrom) && !string.IsNullOrWhiteSpace(request.BinTo);
            bool hasList = request.BinAbsList is { Count: > 0 };
            if (binManaged && !hasRange && !hasList)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    $"Warehouse {request.WhsCode} is bin-managed — provide bin_from/bin_to or bin_abs_list to scope the session."));
            if (!binManaged && (hasRange || hasList))
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    $"Warehouse {request.WhsCode} has no bins — omit bin_from/bin_to/bin_abs_list."));

            // Idempotency: same app_ref already posted → return it.
            var existing = await _sql.FindDocEntryByAppRefAsync("OINC", request.AppRef, ct);
            if (existing is not null)
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));

            var seeds = binManaged
                ? await _sql.GetBinCountingSeedsAsync(
                    request.WhsCode, request.BinFrom?.Trim(), request.BinTo?.Trim(), request.BinAbsList, ct)
                : await _sql.GetNonBinCountingSeedsAsync(request.WhsCode, ct);

            if (seeds.Count == 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    "No stocked lines in the requested scope — nothing to count."));
            if (seeds.Count > MaxCountingLines)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    $"Scope generates {seeds.Count} lines (max {MaxCountingLines}). " +
                    "Narrow the bin range — design around small sessions (an aisle or shelf run)."));

            var result = await _sap.CreateInventoryCountingAsync(
                request.CountDate ?? DateTime.Today, request.AppRef, seeds,
                _settings.CountingSeries, ct);

            return Ok(ApiResponse<InventoryDocResult>.Ok(
                result,
                new Dictionary<string, object> { ["generated_lines"] = seeds.Count }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Counting session creation failed (app_ref={AppRef}, whs={Whs})",
                request.AppRef, request.WhsCode);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/countings?status=open
    /// Counting session headers with counted-line progress, newest first.
    /// <c>status</c>: <c>open</c> (default) or <c>all</c>.
    /// </summary>
    [HttpGet("countings")]
    [ProducesResponseType(typeof(ApiResponse<List<CountingSessionSummary>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCountings(
        [FromQuery(Name = "status")] string status = "open",
        CancellationToken ct = default)
    {
        bool openOnly = !string.Equals(status?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var sessions = await _sql.GetCountingSessionsAsync(openOnly, ct);
        return Ok(ApiResponse<List<CountingSessionSummary>>.Ok(sessions));
    }

    /// <summary>
    /// GET /api/autohub/inv/countings/{docEntry}
    /// All lines of one counting session for the count-capture screen (expected items
    /// per bin with system quantity, item name, article number, manufacturer).
    /// </summary>
    [HttpGet("countings/{docEntry:int}")]
    [ProducesResponseType(typeof(ApiResponse<List<CountingLineDetail>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<CountingLineDetail>>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCountingLines(int docEntry, CancellationToken ct)
    {
        var lines = await _sql.GetCountingLinesAsync(docEntry, ct);
        if (lines.Count == 0)
            return NotFound(ApiResponse<List<CountingLineDetail>>.Fail(
                $"Counting document {docEntry} not found (or has no lines)."));

        return Ok(ApiResponse<List<CountingLineDetail>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["counted_lines"] = lines.Count(l => l.Counted),
            }));
    }

    /// <summary>
    /// PATCH /api/autohub/inv/countings/{docEntry}/lines
    /// Captures counted quantities: <c>updates</c> set counted qty (0 is a valid count)
    /// on existing lines; <c>additions</c> append unexpected finds — items found in a
    /// bin but not on the list (how misplaced stock is caught). Returns the refreshed lines.
    /// </summary>
    [HttpPatch("countings/{docEntry:int}/lines")]
    [ProducesResponseType(typeof(ApiResponse<List<CountingLineDetail>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<CountingLineDetail>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<List<CountingLineDetail>>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<List<CountingLineDetail>>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateCountingLines(
        int docEntry, [FromBody] CountingUpdateRequest request, CancellationToken ct)
    {
        var errors = new List<string>();
        if (request.Updates.Count == 0 && request.Additions.Count == 0)
            errors.Add("Provide at least one entry in updates or additions.");
        for (int i = 0; i < request.Updates.Count; i++)
            if (request.Updates[i].CountedQty < 0)
                errors.Add($"updates[{i}]: counted_qty cannot be negative.");
        for (int i = 0; i < request.Additions.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(request.Additions[i].ItemCode))
                errors.Add($"additions[{i}]: item_code is required.");
            if (request.Additions[i].CountedQty <= 0)
                errors.Add($"additions[{i}]: counted_qty must be greater than zero.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<List<CountingLineDetail>>.Fail(errors));

        try
        {
            var lines = await _sql.GetCountingLinesAsync(docEntry, ct);
            if (lines.Count == 0)
                return NotFound(ApiResponse<List<CountingLineDetail>>.Fail(
                    $"Counting document {docEntry} not found (or has no lines)."));

            var byLineNum = lines.ToDictionary(l => l.LineNum);
            foreach (var upd in request.Updates)
            {
                if (!byLineNum.TryGetValue(upd.LineNum, out var line))
                    errors.Add($"line_num {upd.LineNum} does not exist on counting {docEntry}.");
                else if (line.LineStatus == "C")
                    errors.Add($"line_num {upd.LineNum} is already closed (posted) — recounts need a new session.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<List<CountingLineDetail>>.Fail(errors));

            string whsCode = lines[0].WhsCode;
            await _sap.UpdateInventoryCountingLinesAsync(
                docEntry, request.Updates, request.Additions, whsCode, ct);

            var refreshed = await _sql.GetCountingLinesAsync(docEntry, ct);
            return Ok(ApiResponse<List<CountingLineDetail>>.Ok(
                refreshed,
                new Dictionary<string, object>
                {
                    ["total_lines"] = refreshed.Count,
                    ["counted_lines"] = refreshed.Count(l => l.Counted),
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Counting line update failed (doc_entry={DocEntry})", docEntry);
            return StatusCode(500, ApiResponse<List<CountingLineDetail>>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/countings/{docEntry}/variance
    /// Variance review (spec §8.4): system vs counted vs variance value, sorted by
    /// absolute variance value so expensive discrepancies surface first. The approval
    /// gate before posting.
    /// </summary>
    [HttpGet("countings/{docEntry:int}/variance")]
    [ProducesResponseType(typeof(ApiResponse<List<CountingVarianceLine>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<CountingVarianceLine>>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCountingVariance(int docEntry, CancellationToken ct)
    {
        var lines = await _sql.GetCountingVarianceAsync(docEntry, ct);
        if (lines.Count == 0)
            return NotFound(ApiResponse<List<CountingVarianceLine>>.Fail(
                $"Counting document {docEntry} not found (or has no lines)."));

        return Ok(ApiResponse<List<CountingVarianceLine>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["counted_lines"] = lines.Count(l => l.Counted),
                ["total_variance_value"] = lines.Where(l => l.Counted).Sum(l => l.VarianceValue),
            }));
    }

    /// <summary>
    /// POST /api/autohub/inv/postings
    /// Creates the Inventory Posting (OIQR) from reviewer-approved counting lines.
    /// Base refs make SAP post the stock/GL adjustments and close those counting lines;
    /// lines left out of <c>line_nums</c> stay open for recount. Every requested line
    /// must be counted and still open. Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("postings")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreatePosting(
        [FromBody] PostingCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (request.CountingDocEntry <= 0) errors.Add("counting_doc_entry is required.");
        if (request.LineNums.Count == 0) errors.Add("line_nums must contain at least one line.");
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.AppRef = request.AppRef.Trim();
        var lineNums = request.LineNums.Distinct().ToList();

        try
        {
            // Idempotency: same app_ref already posted → return it.
            var existing = await _sql.FindDocEntryByAppRefAsync("OIQR", request.AppRef, ct);
            if (existing is not null)
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));

            var lines = await _sql.GetCountingLinesAsync(request.CountingDocEntry, ct);
            if (lines.Count == 0)
                return NotFound(ApiResponse<InventoryDocResult>.Fail(
                    $"Counting document {request.CountingDocEntry} not found (or has no lines)."));

            var byLineNum = lines.ToDictionary(l => l.LineNum);
            var postLines = new List<CountingPostLine>(lineNums.Count);
            foreach (var num in lineNums)
            {
                if (!byLineNum.TryGetValue(num, out var line))
                    errors.Add($"line_num {num} does not exist on counting {request.CountingDocEntry}.");
                else if (line.LineStatus == "C")
                    errors.Add($"line_num {num} is already closed (posted).");
                else if (!line.Counted)
                    errors.Add($"line_num {num} ({line.ItemCode}) has not been counted yet.");
                else
                    postLines.Add(new CountingPostLine
                    {
                        LineNum = num,
                        CountedQty = (double)(line.CountedQty ?? 0m),
                    });
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            var result = await _sap.CreateInventoryPostingAsync(
                request.CountingDocEntry, postLines, request.AppRef,
                _settings.PostingSeries, ct);

            return Ok(ApiResponse<InventoryDocResult>.Ok(
                result,
                new Dictionary<string, object> { ["posted_lines"] = postLines.Count }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Inventory posting failed (app_ref={AppRef}, counting={Counting})",
                request.AppRef, request.CountingDocEntry);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    // ── Write helpers ────────────────────────────────────────────────

    private static void ValidateAppRef(string? appRef, List<string> errors)
    {
        // U_AppRef UDF is EditSize 40 in SAP.
        if (string.IsNullOrWhiteSpace(appRef))
            errors.Add("app_ref is required (app-generated GUID for idempotency).");
        else if (appRef.Trim().Length > 40)
            errors.Add("app_ref must be at most 40 characters (U_AppRef UDF size).");
    }

    private async Task ValidateWarehousesExistAsync(string[] whsCodes, CancellationToken ct)
    {
        var warehouses = await _sql.GetWarehousesAsync(ct);
        foreach (var whs in whsCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!warehouses.Any(w => string.Equals(w.WhsCode, whs, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Warehouse '{whs}' does not exist in SAP.");
        }
    }

    /// <summary>
    /// Fills missing bins where the resolver can auto-select, strips bins on non-bin
    /// sides, and collects errors for lines that need an explicit bin choice.
    /// </summary>
    private async Task<List<string>> PrepareTransferBinsAsync(
        TransferCreate request, bool fromBinManaged, bool toBinManaged, CancellationToken ct)
    {
        var errors = new List<string>();

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            line.ItemCode = line.ItemCode.Trim();

            if (!fromBinManaged)
            {
                line.FromBinAbs = null;
            }
            else if (!line.FromBinAbs.HasValue)
            {
                var res = await _binResolver.ResolveAsync(
                    line.ItemCode, request.FromWhs, BinDirection.Source, ct);
                if (res.Resolution == "auto")
                    line.FromBinAbs = res.Auto!.BinAbs;
                else
                    errors.Add(
                        $"lines[{i}] ({line.ItemCode}): source bin required in warehouse {request.FromWhs} " +
                        $"(resolver: {res.Resolution}) — pick one via GET /api/autohub/inv/bins.");
            }

            if (!toBinManaged)
            {
                line.ToBinAbs = null;
            }
            else if (!line.ToBinAbs.HasValue)
            {
                var res = await _binResolver.ResolveAsync(
                    line.ItemCode, request.ToWhs, BinDirection.Destination, ct);
                if (res.Resolution == "auto")
                    line.ToBinAbs = res.Auto!.BinAbs;
                else
                    errors.Add(
                        $"lines[{i}] ({line.ItemCode}): destination bin required in warehouse {request.ToWhs} " +
                        $"(resolver: {res.Resolution}) — pick one via GET /api/autohub/inv/bins.");
            }

            // Same-warehouse putaway must actually move between two different bins.
            if (string.Equals(request.FromWhs, request.ToWhs, StringComparison.OrdinalIgnoreCase)
                && line.FromBinAbs.HasValue && line.ToBinAbs.HasValue
                && line.FromBinAbs.Value == line.ToBinAbs.Value)
                errors.Add($"lines[{i}] ({line.ItemCode}): source and destination bin are the same.");
        }

        return errors;
    }

    /// <summary>
    /// Verifies the source side holds enough stock, aggregated across lines that draw
    /// from the same item + bin (or item for the non-bin warehouse 01).
    /// </summary>
    private async Task<List<string>> ValidateSourceStockAsync(
        TransferCreate request, bool fromBinManaged, CancellationToken ct)
    {
        var errors = new List<string>();

        if (fromBinManaged)
        {
            var byItemBin = request.Lines
                .Where(l => l.FromBinAbs.HasValue)
                .GroupBy(l => (l.ItemCode, BinAbs: l.FromBinAbs!.Value));
            foreach (var grp in byItemBin)
            {
                var bins = await _sql.GetBinStockAsync(grp.Key.ItemCode, request.FromWhs, ct);
                var bin = bins.FirstOrDefault(b => b.BinAbs == grp.Key.BinAbs);
                decimal available = bin?.OnHandQty ?? 0m;
                decimal needed = (decimal)grp.Sum(l => l.Quantity);
                if (available < needed)
                    errors.Add(
                        $"{grp.Key.ItemCode}: insufficient stock in bin {bin?.BinCode ?? grp.Key.BinAbs.ToString()} " +
                        $"of warehouse {request.FromWhs} — available {available}, requested {needed}.");
            }
        }
        else
        {
            foreach (var grp in request.Lines.GroupBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase))
            {
                var stock = await _sql.GetStockAsync(grp.Key, request.FromWhs, ct);
                decimal available = stock.Sum(s => s.TotalOnHand);
                decimal needed = (decimal)grp.Sum(l => l.Quantity);
                if (available < needed)
                    errors.Add(
                        $"{grp.Key}: insufficient stock in warehouse {request.FromWhs} — " +
                        $"available {available}, requested {needed}.");
            }
        }

        return errors;
    }

    private static string? NormalizeWhs(string? whs) =>
        string.IsNullOrWhiteSpace(whs) ? null : whs.Trim();
}
