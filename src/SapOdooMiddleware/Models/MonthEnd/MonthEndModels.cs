namespace SapOdooMiddleware.Models.MonthEnd;

/// <summary>
/// Read-only month-end figures from the SAP company database (spec v4 §7, Phase 5), consumed by the control
/// centre's month-end record. Local currency is the company currency (TZS). Snake_case on the wire.
/// </summary>
public sealed class SupplierBalancesResponse
{
    public DateOnly AsOf { get; set; }
    public string LocalCurrency { get; set; } = "TZS";
    public List<SupplierBalanceRow> Rows { get; set; } = new();
}

/// <summary>Balance of one supplier (OCRD.CardType = 'S') at the date, from the journal (JDT1). Credit-positive:
/// a positive balance is owed to the supplier.</summary>
public sealed class SupplierBalanceRow
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;

    /// <summary>Currency of the journal lines (JDT1.FCCurrency); empty for local-currency lines.</summary>
    public string Currency { get; set; } = string.Empty;

    public double BalanceLc { get; set; }
    public double BalanceFc { get; set; }
}

public sealed class GrniResponse
{
    public DateOnly AsOf { get; set; }
    public string LocalCurrency { get; set; } = "TZS";

    /// <summary>Sum of <see cref="GrniRow.OpenValueLc"/>.</summary>
    public double TotalLc { get; set; }

    public List<GrniRow> Rows { get; set; } = new();
}

/// <summary>A goods receipt with quantity received at the date but not yet invoiced or returned at that date.</summary>
public sealed class GrniRow
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public DateOnly DocDate { get; set; }
    public string? ShipRef { get; set; }

    /// <summary>Lines with an open quantity.</summary>
    public int OpenLines { get; set; }

    /// <summary>Received value not invoiced, local currency: line total × open quantity / received quantity.</summary>
    public double OpenValueLc { get; set; }
}

public sealed class InventoryValuationResponse
{
    public DateOnly AsOf { get; set; }
    public string LocalCurrency { get; set; } = "TZS";

    /// <summary>SAP's perpetual inventory book value at the date: the cumulative OINM transaction value, which includes
    /// the capitalised landed costs.</summary>
    public double TotalLc { get; set; }

    public List<WarehouseValueRow> ByWarehouse { get; set; } = new();
}

public sealed class WarehouseValueRow
{
    public string Warehouse { get; set; } = string.Empty;
    public double ValueLc { get; set; }
}

public sealed class LandedCostCreditsResponse
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public string LocalCurrency { get; set; } = "TZS";
    public List<LandedCostCodeRow> Codes { get; set; } = new();
    public List<LandedCostDocumentRow> Documents { get; set; } = new();
}

/// <summary>Credits SAP's landed cost documents (transaction type 69) posted to a code's allocation account in the period.</summary>
public sealed class LandedCostCodeRow
{
    public string LcCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public double CreditsLc { get; set; }
    public int Entries { get; set; }
}

/// <summary>A landed cost document (OIPF) of the period and what it credited to the allocation accounts.</summary>
public sealed class LandedCostDocumentRow
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public DateOnly DocDate { get; set; }
    public string? OdooShipmentId { get; set; }
    public double TotalLc { get; set; }
}
