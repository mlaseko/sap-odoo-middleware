namespace SapOdooMiddleware.Diagnostics;

/// <summary>
/// Holds startup schema-probe results so workers can refuse to run against a drifted schema
/// (loud failure at deploy beats a silent exception every poll). Singleton.
/// </summary>
public sealed class SchemaGuard
{
    /// <summary>
    /// True when the Autohub auto-match SQL shapes are confirmed against the live parts_catalog.
    /// Defaults true so a transient probe/connectivity failure doesn't permanently idle the worker;
    /// the probe only sets it false on a confirmed column/table mismatch (42703 / 42P01).
    /// </summary>
    public volatile bool AutohubMatchOk = true;
}
