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
    private readonly DefaultBinSeedJobService _binSeed;
    private readonly ILogger<AutohubInventoryController> _logger;

    public AutohubInventoryController(
        IAutohubInventorySqlService sql,
        IBinResolver binResolver,
        IAutohubSapB1Service sap,
        IOptions<AutohubInventorySettings> settings,
        DefaultBinSeedJobService binSeed,
        ILogger<AutohubInventoryController> logger)
    {
        _sql = sql;
        _binResolver = binResolver;
        _sap = sap;
        _settings = settings.Value;
        _binSeed = binSeed;
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
    /// GET /api/autohub/inv/warehouse-bins?whs_code=001
    /// ALL active bins of a warehouse, by BinCode — the free bin-picker list for when
    /// the user wants a bin the resolver didn't suggest (e.g. an empty destination bin).
    /// System bins are included but flagged (<c>sys_bin</c>) so the UI can de-emphasize
    /// them. For stock-holding bins of a specific item, use <c>GET /bins</c> instead.
    /// </summary>
    [HttpGet("warehouse-bins")]
    [ProducesResponseType(typeof(ApiResponse<List<WarehouseBin>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<WarehouseBin>>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetWarehouseBins(
        [FromQuery(Name = "whs_code")] string? whsCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(whsCode))
            return BadRequest(ApiResponse<List<WarehouseBin>>.Fail("whs_code query parameter is required."));

        try
        {
            await ValidateWarehousesExistAsync(new[] { whsCode.Trim() }, ct);
            if (!await _sql.IsBinManagedAsync(whsCode.Trim(), ct))
                return BadRequest(ApiResponse<List<WarehouseBin>>.Fail(
                    $"Warehouse {whsCode.Trim()} has no bins."));

            var bins = await _sql.GetWarehouseBinsAsync(whsCode.Trim(), ct);
            return Ok(ApiResponse<List<WarehouseBin>>.Ok(
                bins,
                new Dictionary<string, object> { ["total_bins"] = bins.Count }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<List<WarehouseBin>>.Fail(ex.Message));
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
    /// PATCH /api/autohub/inv/transfer-requests/{docEntry}
    /// Updates a still-open transfer request: absolute quantity changes on open
    /// lines (never below the already-fulfilled amount — SAP enforces it too),
    /// appended lines on the same route, and replaced comments. Quantities are
    /// absolute, so retrying the same update is a no-op (<c>already_applied</c>).
    /// Returns the refreshed document snapshot.
    /// </summary>
    [HttpPatch("transfer-requests/{docEntry:int}")]
    [ProducesResponseType(typeof(ApiResponse<TransferRequestSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TransferRequestSnapshot>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<TransferRequestSnapshot>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTransferRequest(
        int docEntry, [FromBody] TransferRequestUpdate request, CancellationToken ct)
    {
        try
        {
            var snapshot = await _sql.GetTransferRequestSnapshotAsync(docEntry, ct);
            if (snapshot is null)
                return NotFound(ApiResponse<TransferRequestSnapshot>.Fail(
                    $"Transfer request {docEntry} was not found."));

            var plan = TransferRequestUpdatePlanner.PlanUpdate(snapshot, request);
            if (plan.Errors.Count > 0)
                return BadRequest(ApiResponse<TransferRequestSnapshot>.Fail(plan.Errors));

            if (plan.AlreadyApplied)
                return Ok(ApiResponse<TransferRequestSnapshot>.Ok(
                    snapshot, new Dictionary<string, object> { ["already_applied"] = true }));

            await _sap.UpdateInventoryTransferRequestAsync(docEntry, plan, ct);
            var refreshed = await _sql.GetTransferRequestSnapshotAsync(docEntry, ct) ?? snapshot;
            return Ok(ApiResponse<TransferRequestSnapshot>.Ok(
                refreshed, new Dictionary<string, object> { ["already_applied"] = false }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer request update failed (DocEntry={DocEntry})", docEntry);
            return StatusCode(500, ApiResponse<TransferRequestSnapshot>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/autohub/inv/transfer-requests/{docEntry}/close
    /// Closes an open transfer request, cancelling every remaining open quantity.
    /// Idempotent: a request that is already closed or cancelled returns OK with
    /// <c>already_closed = true</c>. Returns the refreshed document snapshot.
    /// </summary>
    [HttpPost("transfer-requests/{docEntry:int}/close")]
    [ProducesResponseType(typeof(ApiResponse<TransferRequestSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TransferRequestSnapshot>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CloseTransferRequest(int docEntry, CancellationToken ct)
    {
        try
        {
            var snapshot = await _sql.GetTransferRequestSnapshotAsync(docEntry, ct);
            if (snapshot is null)
                return NotFound(ApiResponse<TransferRequestSnapshot>.Fail(
                    $"Transfer request {docEntry} was not found."));

            var errors = TransferRequestUpdatePlanner.ValidateClose(snapshot, out bool alreadyClosed);
            if (errors.Count > 0)
                return BadRequest(ApiResponse<TransferRequestSnapshot>.Fail(errors));
            if (alreadyClosed)
                return Ok(ApiResponse<TransferRequestSnapshot>.Ok(
                    snapshot, new Dictionary<string, object> { ["already_closed"] = true }));

            await _sap.CloseInventoryTransferRequestAsync(docEntry, ct);
            var refreshed = await _sql.GetTransferRequestSnapshotAsync(docEntry, ct) ?? snapshot;
            return Ok(ApiResponse<TransferRequestSnapshot>.Ok(
                refreshed, new Dictionary<string, object> { ["already_closed"] = false }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer request close failed (DocEntry={DocEntry})", docEntry);
            return StatusCode(500, ApiResponse<TransferRequestSnapshot>.Fail(ex.Message));
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
            var itemCodes = request.ItemCodes?
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => i.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool hasItems = itemCodes is { Count: > 0 };
            if (binManaged && !hasRange && !hasList && !hasItems)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    $"Warehouse {request.WhsCode} is bin-managed — scope the session with " +
                    "bin_from/bin_to, bin_abs_list, and/or item_codes."));
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
                    request.WhsCode, request.BinFrom?.Trim(), request.BinTo?.Trim(),
                    request.BinAbsList, itemCodes, ct)
                : await _sql.GetNonBinCountingSeedsAsync(request.WhsCode, itemCodes, ct);

            if (seeds.Count == 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    "No stocked lines in the requested scope — nothing to count."));
            if (seeds.Count > MaxCountingLines)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(
                    $"Scope generates {seeds.Count} lines (max {MaxCountingLines}). " +
                    "Narrow the bin range — design around small sessions (an aisle or shelf run)."));

            // Branch (OINC.BPLId) is mandatory on service-created documents when
            // SAP's multi-branch feature is enabled — resolve it from the warehouse,
            // and fail with a clear message instead of SAP's -5002.
            var (bplId, branchError) = await ResolveActiveBranchAsync(request.WhsCode, ct);
            if (branchError is not null)
                return UnprocessableEntity(ApiResponse<InventoryDocResult>.Fail(branchError));

            // Optional cross-check: a caller-supplied branch_id must agree with the
            // warehouse's resolved branch — the resolved value is the source of truth.
            if (request.BranchId.HasValue && request.BranchId.Value != bplId)
                return UnprocessableEntity(ApiResponse<InventoryDocResult>.Fail(
                    $"branch_id {request.BranchId.Value} does not match warehouse " +
                    $"{request.WhsCode}'s assigned branch ({bplId})."));

            var result = await _sap.CreateInventoryCountingAsync(
                request.CountDate ?? DateTime.Today, request.AppRef, seeds,
                _settings.CountingSeries, bplId, ct);

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

            // Same branch requirement as counting creation (OIQR.BPLId) — derive it
            // from the counting's warehouse.
            var (bplId, branchError) = await ResolveActiveBranchAsync(lines[0].WhsCode, ct);
            if (branchError is not null)
                return UnprocessableEntity(ApiResponse<InventoryDocResult>.Fail(branchError));

            var result = await _sap.CreateInventoryPostingAsync(
                request.CountingDocEntry, postLines, request.AppRef,
                _settings.PostingSeries, bplId, ct);

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

    /// <summary>
    /// POST /api/autohub/inv/postings/direct
    /// STANDALONE stock correction: an Inventory Posting (OIQR) with no counting
    /// session. <c>counted_qty</c> per line is the ABSOLUTE new quantity (0 zeroes the
    /// stock out) — SAP computes the variance against current stock at post time and
    /// writes the stock/GL adjustment. In bin-managed warehouses each line must name
    /// its bin explicitly — a correction targets a specific bin deliberately, never a
    /// guessed one. Idempotent on <c>app_ref</c>.
    /// <para>For counted-session posting (review + subset approval), use
    /// <c>POST /postings</c> instead.</para>
    /// </summary>
    [HttpPost("postings/direct")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateDirectPosting(
        [FromBody] DirectPostingCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.WhsCode)) errors.Add("whs_code is required.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var l = request.Lines[i];
            if (string.IsNullOrWhiteSpace(l.ItemCode))
                errors.Add($"lines[{i}]: item_code is required.");
            if (l.CountedQty < 0)
                errors.Add($"lines[{i}]: counted_qty cannot be negative (0 zeroes the stock out).");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.WhsCode = request.WhsCode.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            await ValidateWarehousesExistAsync(new[] { request.WhsCode }, ct);

            // Idempotency: same app_ref already posted → return it, don't double-post.
            var existing = await _sql.FindDocEntryByAppRefAsync("OIQR", request.AppRef, ct);
            if (existing is not null)
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));

            // Bin discipline: corrections in bin warehouses must name their bin; the
            // bin must exist and belong to this warehouse. Non-bin warehouse strips bins.
            bool binManaged = await _sql.IsBinManagedAsync(request.WhsCode, ct);
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                line.ItemCode = line.ItemCode.Trim();

                if (!binManaged)
                {
                    line.BinAbs = null;
                    continue;
                }
                if (!line.BinAbs.HasValue)
                {
                    errors.Add(
                        $"lines[{i}] ({line.ItemCode}): bin_abs is required in bin-managed warehouse " +
                        $"{request.WhsCode} — pick via GET /bins (stocked) or GET /warehouse-bins (all).");
                    continue;
                }

                var bin = await _sql.GetBinInfoAsync(line.BinAbs.Value, ct);
                if (bin is null)
                    errors.Add($"lines[{i}] ({line.ItemCode}): bin AbsEntry {line.BinAbs} does not exist.");
                else if (!string.Equals(bin.Value.WhsCode, request.WhsCode, StringComparison.OrdinalIgnoreCase))
                    errors.Add(
                        $"lines[{i}] ({line.ItemCode}): bin {bin.Value.BinCode} belongs to warehouse " +
                        $"{bin.Value.WhsCode}, not {request.WhsCode}.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            // Branch (OIQR.BPLId) — same requirement as all service-created documents.
            var (bplId, branchError) = await ResolveActiveBranchAsync(request.WhsCode, ct);
            if (branchError is not null)
                return UnprocessableEntity(ApiResponse<InventoryDocResult>.Fail(branchError));

            var result = await _sap.CreateDirectInventoryPostingAsync(
                request, _settings.PostingSeries, bplId, ct);

            return Ok(ApiResponse<InventoryDocResult>.Ok(
                result,
                new Dictionary<string, object> { ["posted_lines"] = request.Lines.Count }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Direct inventory posting failed (app_ref={AppRef}, whs={Whs})",
                request.AppRef, request.WhsCode);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    // ── Goods Receipt + default bin (Phase 4) ────────────────────────

    /// <summary>
    /// POST /api/autohub/inv/goods-receipts
    /// Creates a Goods Receipt (OIGN) — standalone, non-PO stock-in (spec §5.1).
    /// Destination bins are decided server-side: for a bin-managed warehouse with no
    /// bin supplied, the resolver auto-selects (default bin → consolidate into stocked
    /// bin), otherwise the request is rejected with guidance to <c>GET /bins</c>.
    /// <c>unit_cost</c> is optional per line — omitted, SAP uses the item cost.
    /// Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("goods-receipts")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateGoodsReceipt(
        [FromBody] GoodsReceiptCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.WhsCode)) errors.Add("whs_code is required.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var l = request.Lines[i];
            if (string.IsNullOrWhiteSpace(l.ItemCode))
                errors.Add($"lines[{i}]: item_code is required.");
            if (l.Quantity <= 0)
                errors.Add($"lines[{i}]: quantity must be greater than zero.");
            if (l.UnitCost is < 0)
                errors.Add($"lines[{i}]: unit_cost cannot be negative.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.WhsCode = request.WhsCode.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            await ValidateWarehousesExistAsync(new[] { request.WhsCode }, ct);

            // Idempotency: same app_ref already posted → return it, don't double-post.
            var existing = await _sql.FindDocEntryByAppRefAsync("OIGN", request.AppRef, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "Goods receipt app_ref={AppRef} already posted as DocEntry={DocEntry} — returning existing.",
                    request.AppRef, existing.Value.DocEntry);
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));
            }

            // Destination bin handling — same server-side rule as transfers.
            bool binManaged = await _sql.IsBinManagedAsync(request.WhsCode, ct);
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                line.ItemCode = line.ItemCode.Trim();

                if (!binManaged)
                {
                    line.BinAbs = null;
                    continue;
                }
                if (line.BinAbs.HasValue) continue;

                var res = await _binResolver.ResolveAsync(
                    line.ItemCode, request.WhsCode, BinDirection.Destination, ct);
                if (res.Resolution == "auto")
                    line.BinAbs = res.Auto!.BinAbs;
                else
                    errors.Add(
                        $"lines[{i}] ({line.ItemCode}): destination bin required in warehouse {request.WhsCode} " +
                        $"(resolver: {res.Resolution}) — pick one via GET /api/autohub/inv/bins.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            var result = await _sap.CreateGoodsReceiptAsync(
                request, _settings.GoodsReceiptSeries, ct);
            return Ok(ApiResponse<InventoryDocResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Goods receipt creation failed (app_ref={AppRef}, whs={Whs})",
                request.AppRef, request.WhsCode);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/autohub/inv/default-bin
    /// Sets an item's default bin in one warehouse (OITW.DftBinAbs) — the app's
    /// "save as default bin for this item" action after a scan (spec §7 rung 3).
    /// The destination resolver auto-selects this bin from then on. Defaults suggest,
    /// never block.
    /// </summary>
    [HttpPut("default-bin")]
    [ProducesResponseType(typeof(ApiResponse<DefaultBinUpdate>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DefaultBinUpdate>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<DefaultBinUpdate>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SetDefaultBin(
        [FromBody] DefaultBinUpdate request, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ItemCode)) errors.Add("item_code is required.");
        if (string.IsNullOrWhiteSpace(request.WhsCode)) errors.Add("whs_code is required.");
        if (request.BinAbs <= 0) errors.Add("bin_abs is required.");
        if (errors.Count > 0)
            return BadRequest(ApiResponse<DefaultBinUpdate>.Fail(errors));

        request.ItemCode = request.ItemCode.Trim();
        request.WhsCode = request.WhsCode.Trim();

        try
        {
            await ValidateWarehousesExistAsync(new[] { request.WhsCode }, ct);
            if (!await _sql.IsBinManagedAsync(request.WhsCode, ct))
                return BadRequest(ApiResponse<DefaultBinUpdate>.Fail(
                    $"Warehouse {request.WhsCode} has no bins — a default bin cannot be set."));

            var bin = await _sql.GetBinInfoAsync(request.BinAbs, ct);
            if (bin is null)
                return BadRequest(ApiResponse<DefaultBinUpdate>.Fail(
                    $"Bin AbsEntry {request.BinAbs} does not exist."));
            if (!string.Equals(bin.Value.WhsCode, request.WhsCode, StringComparison.OrdinalIgnoreCase))
                return BadRequest(ApiResponse<DefaultBinUpdate>.Fail(
                    $"Bin {bin.Value.BinCode} belongs to warehouse {bin.Value.WhsCode}, not {request.WhsCode}."));

            await _sap.SetItemDefaultBinAsync(request.ItemCode, request.WhsCode, request.BinAbs, ct);

            return Ok(ApiResponse<DefaultBinUpdate>.Ok(
                request,
                new Dictionary<string, object> { ["bin_code"] = bin.Value.BinCode }));
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("does not exist") || ex.Message.Contains("not found") ||
            ex.Message.Contains("no warehouse-info row"))
        {
            return BadRequest(ApiResponse<DefaultBinUpdate>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Default bin update failed (item={Item}, whs={Whs}, bin={Bin})",
                request.ItemCode, request.WhsCode, request.BinAbs);
            return StatusCode(500, ApiResponse<DefaultBinUpdate>.Fail(ex.Message));
        }
    }

    // ── GRPO: receive against purchase orders (Phase 4) ──────────────

    /// <summary>
    /// GET /api/autohub/inv/purchase-orders?card_code=V00001&amp;item_code=BM10001
    /// Open purchase order lines awaiting receipt (the GRPO receiving screen), oldest
    /// first, with item name, article number, manufacturer, open quantity, and the
    /// PO line's warehouse (the default receiving destination).
    /// </summary>
    [HttpGet("purchase-orders")]
    [ProducesResponseType(typeof(ApiResponse<List<OpenPurchaseOrderLine>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOpenPurchaseOrders(
        [FromQuery(Name = "card_code")] string? cardCode = null,
        [FromQuery(Name = "item_code")] string? itemCode = null,
        CancellationToken ct = default)
    {
        var lines = await _sql.GetOpenPurchaseOrderLinesAsync(
            NormalizeWhs(cardCode), NormalizeWhs(itemCode), ct);

        return Ok(ApiResponse<List<OpenPurchaseOrderLine>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["total_documents"] = lines.Select(l => l.DocEntry).Distinct().Count(),
            }));
    }

    /// <summary>
    /// POST /api/autohub/inv/grpo
    /// Creates a Goods Receipt PO (OPDN) by copying from open purchase order lines.
    /// Base refs update the PO's open quantities and vendor liability; SAP closes
    /// fully received lines (partials allowed). Every line must reference an open PO
    /// line of the given vendor and receive at most its open quantity. Destination
    /// bins resolve server-side like all other documents. Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("grpo")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateGrpo(
        [FromBody] GrpoCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.CardCode)) errors.Add("card_code is required.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var l = request.Lines[i];
            if (l.PoDocEntry <= 0) errors.Add($"lines[{i}]: po_doc_entry is required.");
            if (l.PoLineNum < 0) errors.Add($"lines[{i}]: po_line_num cannot be negative.");
            if (l.Quantity <= 0) errors.Add($"lines[{i}]: quantity must be greater than zero.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.CardCode = request.CardCode.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            // Idempotency: same app_ref already posted → return it, don't double-post.
            var existing = await _sql.FindDocEntryByAppRefAsync("OPDN", request.AppRef, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "GRPO app_ref={AppRef} already posted as DocEntry={DocEntry} — returning existing.",
                    request.AppRef, existing.Value.DocEntry);
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));
            }

            // Validate every line against the vendor's open PO lines.
            var openLines = await _sql.GetOpenPurchaseOrderLinesAsync(request.CardCode, null, ct);
            var openByRef = openLines.ToDictionary(l => (l.DocEntry, l.LineNum));

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (!openByRef.TryGetValue((line.PoDocEntry, line.PoLineNum), out var poLine))
                {
                    errors.Add(
                        $"lines[{i}]: PO {line.PoDocEntry} line {line.PoLineNum} is not an open " +
                        $"purchase order line of vendor {request.CardCode}.");
                    continue;
                }
                if (line.Quantity > poLine.OpenQty)
                    errors.Add(
                        $"lines[{i}] ({poLine.ItemCode}): quantity {line.Quantity} exceeds the PO line's " +
                        $"open quantity {poLine.OpenQty}.");

                // Receiving warehouse: explicit override or the PO line's warehouse.
                line.WhsCode = string.IsNullOrWhiteSpace(line.WhsCode)
                    ? poLine.WhsCode
                    : line.WhsCode.Trim();
                if (string.IsNullOrWhiteSpace(line.WhsCode))
                    errors.Add($"lines[{i}] ({poLine.ItemCode}): no warehouse on the PO line — supply whs_code.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            // Destination bin handling per line, using each line's receiving warehouse.
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                var poLine = openByRef[(line.PoDocEntry, line.PoLineNum)];

                if (!await _sql.IsBinManagedAsync(line.WhsCode!, ct))
                {
                    line.BinAbs = null;
                    continue;
                }
                if (line.BinAbs.HasValue) continue;

                var res = await _binResolver.ResolveAsync(
                    poLine.ItemCode, line.WhsCode!, BinDirection.Destination, ct);
                if (res.Resolution == "auto")
                    line.BinAbs = res.Auto!.BinAbs;
                else
                    errors.Add(
                        $"lines[{i}] ({poLine.ItemCode}): destination bin required in warehouse {line.WhsCode} " +
                        $"(resolver: {res.Resolution}) — pick one via GET /api/autohub/inv/bins.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            var result = await _sap.CreateGrpoAsync(request, _settings.GrpoSeries, ct);
            return Ok(ApiResponse<InventoryDocResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GRPO creation failed (app_ref={AppRef}, card_code={CardCode})",
                request.AppRef, request.CardCode);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    // ── Customer returns: Return Request ← invoice, Goods Return ← request ──

    /// <summary>
    /// GET /api/autohub/inv/invoices?card_code=C00001&amp;status=open
    /// The customer's AR invoice item lines for the return-request copy screen,
    /// newest first. <c>status</c>: <c>open</c> (default) or <c>all</c> — note an AR
    /// invoice closes when PAID, so returns for already-paid sales need <c>all</c>.
    /// </summary>
    [HttpGet("invoices")]
    [ProducesResponseType(typeof(ApiResponse<List<OpenInvoiceLine>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<OpenInvoiceLine>>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInvoicesForReturn(
        [FromQuery(Name = "card_code")] string? cardCode,
        [FromQuery(Name = "status")] string status = "open",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cardCode))
            return BadRequest(ApiResponse<List<OpenInvoiceLine>>.Fail(
                "card_code query parameter is required."));

        bool openOnly = !string.Equals(status?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var lines = await _sql.GetInvoiceLinesForReturnAsync(cardCode.Trim(), openOnly, ct);

        return Ok(ApiResponse<List<OpenInvoiceLine>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["total_documents"] = lines.Select(l => l.DocEntry).Distinct().Count(),
            }));
    }

    /// <summary>
    /// POST /api/autohub/inv/return-requests
    /// Creates a Return Request (ORRR) with every line copied from one of the
    /// customer's AR invoice lines — prices and the document chain flow from the base.
    /// No stock moves yet; the physical return posts later via <c>POST /returns</c>.
    /// Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("return-requests")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateReturnRequest(
        [FromBody] ReturnRequestCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.CardCode)) errors.Add("card_code is required.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var l = request.Lines[i];
            if (l.InvoiceDocEntry <= 0) errors.Add($"lines[{i}]: invoice_doc_entry is required.");
            if (l.InvoiceLineNum < 0) errors.Add($"lines[{i}]: invoice_line_num cannot be negative.");
            if (l.Quantity <= 0) errors.Add($"lines[{i}]: quantity must be greater than zero.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.CardCode = request.CardCode.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            // Idempotency: same app_ref already posted → return it.
            var existing = await _sql.FindDocEntryByAppRefAsync("ORRR", request.AppRef, ct);
            if (existing is not null)
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));

            // Validate each line against the customer's invoice lines (open AND paid).
            var invoiceLines = await _sql.GetInvoiceLinesForReturnAsync(request.CardCode, openOnly: false, ct);
            var byRef = invoiceLines.ToDictionary(l => (l.DocEntry, l.LineNum));

            string? branchWhs = null;
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (!byRef.TryGetValue((line.InvoiceDocEntry, line.InvoiceLineNum), out var inv))
                {
                    errors.Add(
                        $"lines[{i}]: invoice {line.InvoiceDocEntry} line {line.InvoiceLineNum} is not an " +
                        $"AR invoice item line of customer {request.CardCode}.");
                    continue;
                }
                if (line.Quantity > inv.Quantity)
                    errors.Add(
                        $"lines[{i}] ({inv.ItemCode}): quantity {line.Quantity} exceeds the invoiced " +
                        $"quantity {inv.Quantity}.");

                line.WhsCode = string.IsNullOrWhiteSpace(line.WhsCode) ? inv.WhsCode : line.WhsCode.Trim();
                branchWhs ??= line.WhsCode;
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            int? bplId = null;
            if (!string.IsNullOrWhiteSpace(branchWhs))
            {
                var (resolved, branchError) = await ResolveActiveBranchAsync(branchWhs, ct);
                if (branchError is not null)
                    return UnprocessableEntity(ApiResponse<InventoryDocResult>.Fail(branchError));
                bplId = resolved;
            }

            var result = await _sap.CreateAutohubReturnRequestAsync(
                request, _settings.SalesReturnRequestSeries, bplId, ct);
            return Ok(ApiResponse<InventoryDocResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Return request creation failed (app_ref={AppRef}, card_code={CardCode})",
                request.AppRef, request.CardCode);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/return-requests?card_code=C00001
    /// Open Return Request lines awaiting the physical goods return, oldest first —
    /// the copy source for <c>POST /returns</c>.
    /// </summary>
    [HttpGet("return-requests")]
    [ProducesResponseType(typeof(ApiResponse<List<OpenReturnRequestLine>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOpenReturnRequests(
        [FromQuery(Name = "card_code")] string? cardCode = null,
        CancellationToken ct = default)
    {
        var lines = await _sql.GetOpenReturnRequestLinesAsync(NormalizeWhs(cardCode), ct);
        return Ok(ApiResponse<List<OpenReturnRequestLine>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["total_documents"] = lines.Select(l => l.DocEntry).Distinct().Count(),
            }));
    }

    /// <summary>
    /// POST /api/autohub/inv/returns
    /// Posts the Goods Return (ORDN) by copying from open Return Request lines —
    /// SAP closes the request's open quantities (partials allowed) and the stock
    /// comes back into the warehouse. Destination bins resolve server-side like
    /// receipts. Idempotent on <c>app_ref</c>.
    /// </summary>
    [HttpPost("returns")]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<InventoryDocResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateGoodsReturn(
        [FromBody] GoodsReturnCreate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (string.IsNullOrWhiteSpace(request.CardCode)) errors.Add("card_code is required.");
        if (request.Lines.Count == 0) errors.Add("At least one line is required.");
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var l = request.Lines[i];
            if (l.ReturnRequestDocEntry <= 0) errors.Add($"lines[{i}]: return_request_doc_entry is required.");
            if (l.ReturnRequestLineNum < 0) errors.Add($"lines[{i}]: return_request_line_num cannot be negative.");
            if (l.Quantity <= 0) errors.Add($"lines[{i}]: quantity must be greater than zero.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

        request.CardCode = request.CardCode.Trim();
        request.AppRef = request.AppRef.Trim();

        try
        {
            // Idempotency: same app_ref already posted → return it.
            var existing = await _sql.FindDocEntryByAppRefAsync("ORDN", request.AppRef, ct);
            if (existing is not null)
                return Ok(ApiResponse<InventoryDocResult>.Ok(new InventoryDocResult
                {
                    DocEntry = existing.Value.DocEntry,
                    DocNum = existing.Value.DocNum,
                    AlreadyExisted = true,
                }));

            // Validate against the customer's open return request lines.
            var openLines = await _sql.GetOpenReturnRequestLinesAsync(request.CardCode, ct);
            var byRef = openLines.ToDictionary(l => (l.DocEntry, l.LineNum));

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (!byRef.TryGetValue((line.ReturnRequestDocEntry, line.ReturnRequestLineNum), out var rrLine))
                {
                    errors.Add(
                        $"lines[{i}]: return request {line.ReturnRequestDocEntry} line " +
                        $"{line.ReturnRequestLineNum} is not an open return request line of customer " +
                        $"{request.CardCode}.");
                    continue;
                }
                if (line.Quantity > rrLine.OpenQty)
                    errors.Add(
                        $"lines[{i}] ({rrLine.ItemCode}): quantity {line.Quantity} exceeds the request " +
                        $"line's open quantity {rrLine.OpenQty}.");

                line.WhsCode = string.IsNullOrWhiteSpace(line.WhsCode) ? rrLine.WhsCode : line.WhsCode.Trim();
                if (string.IsNullOrWhiteSpace(line.WhsCode))
                    errors.Add($"lines[{i}] ({rrLine.ItemCode}): no warehouse on the request line — supply whs_code.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            // Destination bins — goods come back IN, same rules as receipts.
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                var rrLine = byRef[(line.ReturnRequestDocEntry, line.ReturnRequestLineNum)];

                if (!await _sql.IsBinManagedAsync(line.WhsCode!, ct))
                {
                    line.BinAbs = null;
                    continue;
                }
                if (line.BinAbs.HasValue) continue;

                var res = await _binResolver.ResolveAsync(
                    rrLine.ItemCode, line.WhsCode!, BinDirection.Destination, ct);
                if (res.Resolution == "auto")
                    line.BinAbs = res.Auto!.BinAbs;
                else
                    errors.Add(
                        $"lines[{i}] ({rrLine.ItemCode}): destination bin required in warehouse {line.WhsCode} " +
                        $"(resolver: {res.Resolution}) — pick one via GET /api/autohub/inv/bins.");
            }
            if (errors.Count > 0)
                return BadRequest(ApiResponse<InventoryDocResult>.Fail(errors));

            var (bplId, branchError) = await ResolveActiveBranchAsync(request.Lines[0].WhsCode!, ct);
            if (branchError is not null)
                return UnprocessableEntity(ApiResponse<InventoryDocResult>.Fail(branchError));

            var result = await _sap.CreateAutohubGoodsReturnAsync(
                request, _settings.GoodsReturnSeries, bplId, ct);
            return Ok(ApiResponse<InventoryDocResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
        {
            return BadRequest(ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Goods return creation failed (app_ref={AppRef}, card_code={CardCode})",
                request.AppRef, request.CardCode);
            return StatusCode(500, ApiResponse<InventoryDocResult>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/customers?search=juma&amp;limit=50
    /// Customer picker list (active customers only), filtered by code or name,
    /// alphabetical. Use for the dropdown instead of typing exact CardCodes.
    /// </summary>
    [HttpGet("customers")]
    [ProducesResponseType(typeof(ApiResponse<List<CustomerSummary>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCustomers(
        [FromQuery(Name = "search")] string? search = null,
        [FromQuery(Name = "limit")] int limit = 50,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var customers = await _sql.GetCustomersAsync(
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(), limit, ct);
        return Ok(ApiResponse<List<CustomerSummary>>.Ok(
            customers,
            new Dictionary<string, object> { ["count"] = customers.Count, ["limit"] = limit }));
    }

    /// <summary>
    /// GET /api/autohub/inv/return-requests/list?card_code=&amp;status=all
    /// Return Request documents with status (<c>open</c> | <c>closed</c> |
    /// <c>canceled</c> | <c>all</c>, default all), newest first, with line/quantity
    /// totals. For the line-level fulfillment view use <c>GET /return-requests</c>.
    /// </summary>
    [HttpGet("return-requests/list")]
    [ProducesResponseType(typeof(ApiResponse<List<ReturnDocumentSummary>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListReturnRequests(
        [FromQuery(Name = "card_code")] string? cardCode = null,
        [FromQuery(Name = "status")] string status = "all",
        CancellationToken ct = default)
    {
        var docs = await _sql.GetReturnDocumentsAsync("ORRR", NormalizeWhs(cardCode), status.Trim(), ct);
        return Ok(ApiResponse<List<ReturnDocumentSummary>>.Ok(
            docs, new Dictionary<string, object> { ["count"] = docs.Count, ["status"] = status }));
    }

    /// <summary>
    /// GET /api/autohub/inv/returns/list?card_code=&amp;status=all
    /// Posted Goods Return documents (ORDN) with status, newest first, with
    /// line/quantity totals.
    /// </summary>
    [HttpGet("returns/list")]
    [ProducesResponseType(typeof(ApiResponse<List<ReturnDocumentSummary>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListGoodsReturns(
        [FromQuery(Name = "card_code")] string? cardCode = null,
        [FromQuery(Name = "status")] string status = "all",
        CancellationToken ct = default)
    {
        var docs = await _sql.GetReturnDocumentsAsync("ORDN", NormalizeWhs(cardCode), status.Trim(), ct);
        return Ok(ApiResponse<List<ReturnDocumentSummary>>.Ok(
            docs, new Dictionary<string, object> { ["count"] = docs.Count, ["status"] = status }));
    }

    /// <summary>
    /// POST /api/autohub/inv/return-requests/{docEntry}/cancel
    /// Cancels an open Return Request. Idempotent: an already-cancelled request
    /// returns 200 with <c>already_cancelled = true</c>. A request already fully
    /// drawn to a Goods Return is rejected by SAP with its own message.
    /// </summary>
    [HttpPost("return-requests/{docEntry:int}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<DocCancelResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DocCancelResult>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<DocCancelResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelReturnRequest(int docEntry, CancellationToken ct)
    {
        try
        {
            var result = await _sap.CancelAutohubReturnRequestAsync(docEntry, ct);
            return Ok(ApiResponse<DocCancelResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(ApiResponse<DocCancelResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Return request cancellation failed (doc_entry={DocEntry})", docEntry);
            return StatusCode(500, ApiResponse<DocCancelResult>.Fail(ex.Message));
        }
    }

    // ── Default-bin seeding job (Phase 5, spec §10) ──────────────────

    /// <summary>
    /// POST /api/autohub/inv/default-bin-seed?dry_run=true&amp;overwrite=false&amp;limit=0
    /// One-time seeding of item default bins (OITW.DftBinAbs) from where stock sits
    /// today — the resolver's rung 1 then auto-selects from day one.
    /// <para>
    /// <c>dry_run=true</c> returns the analysis immediately (no writes): how many
    /// pairs would be set, already correct, or differently defaulted, plus a sample.
    /// A live run starts a BACKGROUND job (poll GET for progress). Spike first with
    /// <c>limit=5</c>, verify in the B1 client, then run without limit after hours.
    /// Re-running with <c>overwrite=false</c> (default) only fills empty defaults,
    /// so an interrupted run just continues.
    /// </para>
    /// </summary>
    [HttpPost("default-bin-seed")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> StartDefaultBinSeed(
        [FromQuery(Name = "dry_run")] bool dryRun = true,
        [FromQuery(Name = "overwrite")] bool overwrite = false,
        [FromQuery(Name = "limit")] int limit = 0,
        CancellationToken ct = default)
    {
        try
        {
            if (dryRun)
            {
                var analysis = await _binSeed.AnalyzeAsync(overwrite, ct);
                return Ok(ApiResponse<object>.Ok(analysis));
            }

            var job = _binSeed.Start(overwrite, Math.Max(0, limit));
            return Ok(ApiResponse<object>.Ok(SeedJobSnapshot(job)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Default-bin seed start failed (dry_run={DryRun})", dryRun);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/default-bin-seed
    /// Progress/result of the latest seeding run (in-memory; empty after a service restart).
    /// </summary>
    [HttpGet("default-bin-seed")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public IActionResult GetDefaultBinSeedStatus()
    {
        var job = _binSeed.Current;
        if (job is null)
            return NotFound(ApiResponse<object>.Fail(
                "No seeding run since service start. POST /default-bin-seed to begin (dry_run=true first)."));
        return Ok(ApiResponse<object>.Ok(SeedJobSnapshot(job)));
    }

    /// <summary>
    /// POST /api/autohub/inv/default-bin-seed/stop
    /// Gracefully stops the running seeding job after the current item. Already-written
    /// defaults stay (they're the correct end state); a later run continues the rest.
    /// </summary>
    [HttpPost("default-bin-seed/stop")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public IActionResult StopDefaultBinSeed()
    {
        if (!_binSeed.Stop())
            return BadRequest(ApiResponse<object>.Fail("No seeding job is currently running."));
        return Ok(ApiResponse<object>.Ok(SeedJobSnapshot(_binSeed.Current!)));
    }

    /// <summary>Job DTO — Status is a volatile field, so map it explicitly for JSON.</summary>
    private static object SeedJobSnapshot(DefaultBinSeedJob job) => new
    {
        job.JobId,
        Status = job.Status,
        job.StartedAt,
        job.FinishedAt,
        job.Overwrite,
        job.Limit,
        job.TotalItems,
        job.ProcessedItems,
        job.UpdatedItems,
        job.UnchangedItems,
        job.FailedItems,
        job.Error,
        job.Failures,
    };

    // ── Pick list picking operations ─────────────────────────────────

    /// <summary>
    /// PATCH /api/autohub/inv/pick-lists/{absEntry}/allocations
    /// Re-bins RELEASED pick-list lines before picking. Allocations are the full
    /// replacement bin breakdown per line (total ≤ released qty; bins must hold the
    /// item's stock). Every bin change appends an audit note to OPKL.Remarks naming
    /// <c>changed_by</c>. Requests matching SAP's current state return already_applied.
    /// </summary>
    [HttpPatch("pick-lists/{absEntry:int}/allocations")]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdatePickListAllocations(
        int absEntry, [FromBody] PickListAllocationUpdate request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (errors.Count > 0)
            return BadRequest(ApiResponse<PickListActionResult>.Fail(errors));

        try
        {
            var snapshot = await _sql.GetPickListSnapshotAsync(absEntry, ct);
            if (snapshot is null)
                return NotFound(ApiResponse<PickListActionResult>.Fail($"Pick list {absEntry} was not found."));

            var binStock = await LoadPickListBinStockAsync(snapshot, request.Lines.Select(l => l.PickEntry), ct);
            var plan = PickListUpdatePlanner.PlanAllocationUpdate(snapshot, request, binStock, DateTime.UtcNow);
            if (plan.Errors.Count > 0)
                return BadRequest(ApiResponse<PickListActionResult>.Fail(plan.Errors));
            if (plan.AlreadyApplied)
                return Ok(ApiResponse<PickListActionResult>.Ok(
                    await BuildPickListResultAsync(absEntry, alreadyApplied: true, noteWritten: true, ct)));

            var remarks = plan.Note is null
                ? null
                : PickListUpdatePlanner.AppendNote(snapshot.Remarks, plan.Note);
            var noteWritten = await _sap.UpdatePickListAllocationsAsync(absEntry, plan.Lines, remarks, ct);

            _logger.LogInformation(
                "Pick list {AbsEntry}: allocations updated ({Lines} line(s)) by {ChangedBy} (app_ref={AppRef}).",
                absEntry, plan.Lines.Count, request.ChangedBy, request.AppRef);
            return Ok(ApiResponse<PickListActionResult>.Ok(
                await BuildPickListResultAsync(absEntry, alreadyApplied: false, noteWritten, ct)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<PickListActionResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Pick list allocation update failed (abs_entry={AbsEntry}, app_ref={AppRef})",
                absEntry, request.AppRef);
            return StatusCode(500, ApiResponse<PickListActionResult>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/autohub/inv/pick-lists/{absEntry}/pick
    /// Picks lines by setting the ABSOLUTE picked quantity (with the full bin
    /// breakdown for bin-managed warehouses). Below the releasable total leaves the
    /// line Partially Picked; equal completes it. Replaying an identical request is a
    /// no-op (already_applied) — quantities are absolute, so retries are safe.
    /// </summary>
    [HttpPost("pick-lists/{absEntry:int}/pick")]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<PickListActionResult>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PickPickListLines(
        int absEntry, [FromBody] PickListPickRequest request, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateAppRef(request.AppRef, errors);
        if (errors.Count > 0)
            return BadRequest(ApiResponse<PickListActionResult>.Fail(errors));

        try
        {
            var snapshot = await _sql.GetPickListSnapshotAsync(absEntry, ct);
            if (snapshot is null)
                return NotFound(ApiResponse<PickListActionResult>.Fail($"Pick list {absEntry} was not found."));

            var binStock = await LoadPickListBinStockAsync(snapshot, request.Lines.Select(l => l.PickEntry), ct);
            var binManaged = new Dictionary<string, bool>();
            foreach (var whs in snapshot.Lines.Select(line => line.WhsCode).Where(w => w != "").Distinct())
                binManaged[whs] = await _sql.IsBinManagedAsync(whs, ct);

            var plan = PickListUpdatePlanner.PlanPick(snapshot, request, binStock, binManaged, DateTime.UtcNow);
            if (plan.Errors.Count > 0)
                return BadRequest(ApiResponse<PickListActionResult>.Fail(plan.Errors));
            if (plan.AlreadyApplied)
                return Ok(ApiResponse<PickListActionResult>.Ok(
                    await BuildPickListResultAsync(absEntry, alreadyApplied: true, noteWritten: true, ct)));

            var remarks = plan.Note is null
                ? null
                : PickListUpdatePlanner.AppendNote(snapshot.Remarks, plan.Note);
            var noteWritten = await _sap.PickPickListLinesAsync(absEntry, plan.Lines, remarks, ct);

            _logger.LogInformation(
                "Pick list {AbsEntry}: {Lines} line(s) picked by {ChangedBy} (app_ref={AppRef}).",
                absEntry, plan.Lines.Count, request.ChangedBy, request.AppRef);
            return Ok(ApiResponse<PickListActionResult>.Ok(
                await BuildPickListResultAsync(absEntry, alreadyApplied: false, noteWritten, ct)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<PickListActionResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Pick list pick failed (abs_entry={AbsEntry}, app_ref={AppRef})",
                absEntry, request.AppRef);
            return StatusCode(500, ApiResponse<PickListActionResult>.Fail(ex.Message));
        }
    }

    /// <summary>Per-requested-line item bin stock (OIBQ), keyed by PickEntry, for allocation validation.</summary>
    private async Task<Dictionary<int, IReadOnlyList<BinOption>>> LoadPickListBinStockAsync(
        PickListSnapshot snapshot, IEnumerable<int> pickEntries, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyList<BinOption>>();
        var cache = new Dictionary<(string ItemCode, string WhsCode), IReadOnlyList<BinOption>>();
        foreach (var pickEntry in pickEntries.Distinct())
        {
            var line = snapshot.Lines.FirstOrDefault(candidate => candidate.PickEntry == pickEntry);
            if (line is null || line.ItemCode == "" || line.WhsCode == "") continue;
            var key = (line.ItemCode, line.WhsCode);
            if (!cache.TryGetValue(key, out var stock))
            {
                stock = await _sql.GetBinStockAsync(line.ItemCode, line.WhsCode, ct);
                cache[key] = stock;
            }
            result[pickEntry] = stock;
        }
        return result;
    }

    /// <summary>Fresh post-write document state for the response (SQL reads committed DI writes).</summary>
    private async Task<PickListActionResult> BuildPickListResultAsync(
        int absEntry, bool alreadyApplied, bool noteWritten, CancellationToken ct)
    {
        var snapshot = await _sql.GetPickListSnapshotAsync(absEntry, ct);
        return new PickListActionResult
        {
            AbsEntry = absEntry,
            Status = snapshot?.Status ?? "",
            AlreadyApplied = alreadyApplied,
            NoteWritten = noteWritten,
            Remarks = snapshot?.Remarks ?? "",
            Lines = snapshot?.Lines ?? new List<PickListLineSnapshot>(),
        };
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

    /// <summary>
    /// Resolves the warehouse's active SAP branch (OWHS.BPLid + OBPL) for documents
    /// that require a header branch. Returns (bplId, null) on success, (null, message)
    /// when the warehouse has no branch or the branch is inactive — callers return
    /// 422 with the message instead of letting SAP fail with -5002.
    /// </summary>
    private async Task<(int? BplId, string? Error)> ResolveActiveBranchAsync(
        string whsCode, CancellationToken ct)
    {
        var branch = await _sql.GetWarehouseBranchAsync(whsCode, ct);
        if (branch is null)
            return (null,
                $"Warehouse {whsCode} is not assigned to a SAP branch. " +
                "Configure OWHS.BPLid before creating this document.");
        if (!branch.Active)
            return (null,
                $"Warehouse {whsCode} is assigned to branch {branch.BplId} " +
                $"('{branch.BranchName}') which is not active in SAP (OBPL). " +
                "Activate the branch or reassign the warehouse.");
        return (branch.BplId, null);
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
