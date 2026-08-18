using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// One bin-resolution component used identically by every inventory document type
/// (spec §7). The frontend renders whatever it returns: nothing (auto-resolved or
/// non-bin warehouse), a small confirm sheet (≤5 options), or a required scan.
/// </summary>
public interface IBinResolver
{
    Task<BinResolution> ResolveAsync(
        string itemCode, string whsCode, BinDirection direction, CancellationToken ct);
}

public sealed class BinResolver : IBinResolver
{
    private readonly IAutohubInventorySqlService _sql;
    private readonly ILogger<BinResolver> _logger;

    public BinResolver(IAutohubInventorySqlService sql, ILogger<BinResolver> logger)
    {
        _sql = sql;
        _logger = logger;
    }

    public async Task<BinResolution> ResolveAsync(
        string itemCode, string whsCode, BinDirection direction, CancellationToken ct)
    {
        // Warehouse 01 (no bins): no bin UI at all.
        if (!await _sql.IsBinManagedAsync(whsCode, ct))
            return new BinResolution { Resolution = "not_bin_managed" };

        return direction == BinDirection.Source
            ? await ResolveSourceAsync(itemCode, whsCode, ct)
            : await ResolveDestinationAsync(itemCode, whsCode, ct);
    }

    /// <summary>
    /// Source bin (transfers out, issues): where the stock actually sits.
    /// 1 bin → auto (89.4% of cases); 2-5 bins → options sorted qty desc
    /// (never auto-drain across bins — the picker confirms); 0 → required.
    /// </summary>
    private async Task<BinResolution> ResolveSourceAsync(
        string itemCode, string whsCode, CancellationToken ct)
    {
        var bins = await _sql.GetBinStockAsync(itemCode, whsCode, ct);
        return bins.Count switch
        {
            0 => new BinResolution { Resolution = "required" },
            1 => new BinResolution { Resolution = "auto", Auto = bins[0] },
            _ => new BinResolution { Resolution = "options", Options = bins },
        };
    }

    /// <summary>
    /// Destination bin (receipts, transfers in):
    /// 1. Item's default bin in that warehouse (OITW.DftBinAbs — seeded).
    /// 2. Else the bin where the item already has stock (consolidate, don't fragment);
    ///    multiple stocked bins → options.
    /// 3. Else require a scan (the app offers "save as default bin for this item").
    /// </summary>
    private async Task<BinResolution> ResolveDestinationAsync(
        string itemCode, string whsCode, CancellationToken ct)
    {
        var bins = await _sql.GetBinStockAsync(itemCode, whsCode, ct);

        // Rung 1: item-level default bin.
        var defaultBin = await _sql.GetDefaultBinAsync(itemCode, whsCode, ct);
        if (defaultBin.HasValue)
        {
            var stocked = bins.FirstOrDefault(b => b.BinAbs == defaultBin.Value);
            if (stocked is not null)
                return new BinResolution { Resolution = "auto", Auto = stocked };

            var binCode = await _sql.GetBinCodeAsync(defaultBin.Value, ct);
            if (binCode is not null)
                return new BinResolution
                {
                    Resolution = "auto",
                    Auto = new BinOption { BinAbs = defaultBin.Value, BinCode = binCode, OnHandQty = 0m },
                };

            // Default bin points at a deleted bin — fall through to stock placement.
            _logger.LogWarning(
                "Default bin {BinAbs} for item {ItemCode} in whs {WhsCode} no longer exists; falling back.",
                defaultBin.Value, itemCode, whsCode);
        }

        // Rung 2: consolidate into an existing stocked bin.
        return bins.Count switch
        {
            0 => new BinResolution { Resolution = "required" },
            1 => new BinResolution { Resolution = "auto", Auto = bins[0] },
            _ => new BinResolution { Resolution = "options", Options = bins },
        };
    }
}
