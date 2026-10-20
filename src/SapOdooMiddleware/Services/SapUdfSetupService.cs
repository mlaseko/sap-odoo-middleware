using System.Data;
using Microsoft.Data.SqlClient;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Services;

/// <summary>A SQL Server session connected to the company database other than this check's own.</summary>
public sealed record SapSession(
    int SessionId,
    string LoginName,
    string HostName,
    string ProgramName,
    DateTime LoginTime,
    bool LikelyThisMiddleware);

public sealed record SapUdfSetupResult(
    bool Ran,
    bool Refused,
    bool DryRun,
    string CompanyDb,
    List<SapSession> OtherSessions,
    List<string> Results,
    string Message);

/// <summary>
/// The UDF setup routine of spec v4 §4.1: refuses to run while other users are connected to the
/// company database (UDF creation needs exclusive access and would otherwise fail or lock SAP
/// clients), then calls the idempotent <see cref="ISapB1Service.EnsureUdfsAsync"/>.
/// </summary>
public sealed class SapUdfSetupService
{
    private readonly ISapB1Service _sap;
    private readonly SapSqlConnectionFactory _connections;
    private readonly ILogger<SapUdfSetupService> _logger;

    public SapUdfSetupService(ISapB1Service sap, SapSqlConnectionFactory connections, ILogger<SapUdfSetupService> logger)
    {
        _sap = sap;
        _connections = connections;
        _logger = logger;
    }

    /// <param name="dryRun">Only report the connected sessions; create nothing.</param>
    /// <param name="force">Run even when other sessions are connected (after a person has checked them).</param>
    public async Task<SapUdfSetupResult> RunAsync(bool dryRun, bool force, CancellationToken ct)
    {
        string companyDb = _connections.CompanyDb;
        var sessions = await ListOtherSessionsAsync(ct);

        if (dryRun)
        {
            return new SapUdfSetupResult(false, false, true, companyDb, sessions, [],
                sessions.Count == 0
                    ? "No other sessions are connected; the routine can run."
                    : $"{sessions.Count} other session(s) connected; the routine would refuse unless force=true.");
        }

        if (sessions.Count > 0 && !force)
        {
            var who = string.Join("; ", sessions.Select(s => $"{s.LoginName}@{s.HostName} ({s.ProgramName})"));
            _logger.LogWarning("UDF setup refused: {Count} other session(s) on {Db}: {Who}", sessions.Count, companyDb, who);
            return new SapUdfSetupResult(false, true, false, companyDb, sessions, [],
                $"Refused: {sessions.Count} other session(s) are connected to {companyDb}: {who}. " +
                "Run out of hours, or re-run with force=true once you have confirmed the listed sessions " +
                "(a session marked likely_this_middleware is this service's own DI-API connection).");
        }

        var results = await _sap.EnsureUdfsAsync();
        _logger.LogInformation("UDF setup ran on {Db} (force={Force}): {Results}", companyDb, force, string.Join("; ", results));

        int created = results.Count(r => r.StartsWith("CREATED", StringComparison.Ordinal));
        int existed = results.Count(r => r.StartsWith("EXISTS", StringComparison.Ordinal));
        int errors = results.Count(r => r.StartsWith("ERROR", StringComparison.Ordinal));

        return new SapUdfSetupResult(true, false, false, companyDb, sessions, results,
            $"Done: {created} created, {existed} already existed, {errors} error(s).");
    }

    /// <summary>
    /// Sessions on the company database other than this query's own connection and other
    /// connections of this middleware (Application Name = SapOdooMiddleware). Needs VIEW SERVER STATE.
    /// </summary>
    public async Task<List<SapSession>> ListOtherSessionsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT s.session_id, ISNULL(s.login_name, ''), ISNULL(s.host_name, ''), ISNULL(s.program_name, ''), s.login_time
            FROM sys.dm_exec_sessions s
            WHERE s.is_user_process = 1
              AND s.session_id <> @@SPID
              AND s.database_id = DB_ID()
              AND ISNULL(s.program_name, '') NOT LIKE @App + '%'
            ORDER BY s.login_time
            """;

        var sessions = new List<SapSession>();
        string machine = Environment.MachineName;

        await using var conn = await _connections.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@App", SqlDbType.NVarChar, 128).Value = SapSqlConnectionFactory.ApplicationName;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string host = r.GetString(2);
            string program = r.GetString(3);
            bool likelyOurs = string.Equals(host, machine, StringComparison.OrdinalIgnoreCase)
                && program.Contains("DI API", StringComparison.OrdinalIgnoreCase);

            sessions.Add(new SapSession(
                SessionId: Convert.ToInt32(r.GetValue(0)),
                LoginName: r.GetString(1),
                HostName: host,
                ProgramName: program,
                LoginTime: r.GetDateTime(4),
                LikelyThisMiddleware: likelyOurs));
        }
        return sessions;
    }
}
