using Npgsql;

namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Connection settings for the Neon (PostgreSQL) database that backs the
/// NeonProducts / NeonPriceLists / NeonLiquiMolyProducts tables consumed by the
/// existing Neon → Odoo automation.
/// </summary>
public class NeonSettings
{
    public const string SectionName = "Neon";

    private string _connectionString = string.Empty;

    /// <summary>
    /// Npgsql connection string for the Neon Postgres database, with connection-health defaults added
    /// (<see cref="Harden"/>) unless the configured string sets them itself.
    /// </summary>
    public string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = Harden(value);
    }

    /// <summary>
    /// Neon closes idle connections on its side; a pooled connection it has dropped fails its next command
    /// with "Exception while reading from stream" after the full command timeout (seen in Bulk Create).
    /// TCP keepalives every 30 s keep pooled connections alive or reveal them as dead, and idle pooled
    /// connections are closed by Npgsql after 60 s instead of 300 s. Values already in the string win.
    /// </summary>
    internal static string Harden(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? string.Empty;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0) given.Add(part[..eq].Trim().Replace(" ", ""));
            }
            if (!given.Contains("KeepAlive")) b.KeepAlive = 30;
            if (!given.Contains("ConnectionIdleLifetime")) b.ConnectionIdleLifetime = 60;
            return b.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;   // not a key=value string Npgsql can read: leave it to fail where it is used
        }
    }
}
