namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for the SAP → Odoo purchases sync worker (spec v4 §6.1 / §6.4): the worker that drains
/// the purchase rows (EventType PO_UPSERT, GRPO_LINK, AP_INVOICE_*, AP_CREDIT_*) of the shared
/// <c>ODOO_WEBHOOK_QUEUE</c> table. The queue connection is <c>WebhookQueue:ConnectionString</c>.
/// </summary>
public class PurchaseSyncSettings
{
    public const string SectionName = "PurchaseSync";

    /// <summary>Master switch for the purchase queue worker. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between polls of the queue. Default 60.</summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>Rows taken per poll. Default 10.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Automatic retries for TRANSIENT failures (SQL, HTTP, Odoo down). Mapping failures
    /// (PARTNER_NOT_MAPPED, ACCOUNT_NOT_MAPPED, TAX_NOT_MAPPED) are terminal at once and are
    /// retried by a person from the queue view after the mapping is added. Default 5.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>How long the control-centre mapping tables are cached. Default 5 minutes.</summary>
    public int MappingCacheMinutes { get; set; } = 5;

    /// <summary>
    /// Optional SQL Server connection string for the direct reads of OPOR/OPDN/OPCH/ORPC. When empty
    /// the reader builds one from <c>SapB1</c> (Server, CompanyDb, DbUserName/DbPassword), exactly
    /// like the Lubes inventory forecast report.
    /// </summary>
    public string? SapSqlConnectionString { get; set; }

    /// <summary>
    /// Direction of <c>account.move.invoice_currency_rate</c>. Odoo 17+ stores "document currency
    /// units per company currency unit", i.e. 1 / SAP <c>DocRate</c> when the company currency is
    /// TZS and DocRate is TZS per EUR. Discovery item 5.0/4 confirms this with one test bill; flip
    /// here if the instance behaves differently. Default true (write 1 / DocRate).
    /// </summary>
    public bool InvoiceRateIsInverseOfDocRate { get; set; } = true;

    /// <summary>
    /// Odoo model that holds the bill/credit-note journal, tolerance and cut-over date.
    /// Spec v4 §5.7. Overridable only for test instances.
    /// </summary>
    public string SettingsModel { get; set; } = "icc.ap.sync.settings";
    public string VatGroupMapModel { get; set; } = "icc.sap.vatgroup.map";
    public string ItemGroupMapModel { get; set; } = "icc.sap.itemgroup.map";
    public string GlAccountMapModel { get; set; } = "icc.sap.glaccount.map";

    /// <summary>Odoo model of the container order (one order = one container).</summary>
    public string ContainerOrderModel { get; set; } = "supplier.order";
}
