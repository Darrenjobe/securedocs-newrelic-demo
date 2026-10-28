using System.Data.Common;
using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Shared;
using Microsoft.EntityFrameworkCore;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Services;

/// <summary>
/// Hand-written SQL used when the "slow queries" / "database errors" faults are on.
/// Azure SQL: WAITFOR DELAY plus non-sargable predicates (UPPER/LTRIM on columns, leading-wildcard LIKE)
/// so the query shows up in New Relic's slow query list with a table scan in the plan.
/// SQLite (local dev): a recursive CTE burns CPU for roughly the same time.
/// </summary>
public class SqlFaultQueries(SecureDocsDb db, FaultState faults)
{
    bool IsSqlServer => db.Database.IsSqlServer();

    [Trace]
    public async Task<List<DocumentDto>> SlowDocumentSearchAsync(int clientId, string q, CancellationToken ct)
    {
        var ms = faults.Current.SlowQueryMs;
        await using var cmd = await CreateCommandAsync(ct);
        if (IsSqlServer)
        {
            cmd.CommandText = """
                WAITFOR DELAY @delay;
                SELECT TOP (50) d.Id, d.BatchId, d.DocumentType, d.Channel, d.RecipientName, d.MemberId,
                       d.City, d.State, d.PostalCode, d.Status, d.CreatedUtc
                FROM Documents d
                WHERE d.ClientId = @clientId
                  AND (UPPER(LTRIM(RTRIM(d.RecipientName))) LIKE '%' + UPPER(@q) + '%'
                       OR CONVERT(nvarchar(64), d.MemberId) LIKE '%' + @q + '%'
                       OR d.AddressLine1 LIKE '%' + @q + '%')
                ORDER BY d.CreatedUtc DESC;
                """;
            AddParam(cmd, "@delay", ToWaitFor(ms));
        }
        else
        {
            cmd.CommandText = $"""
                WITH RECURSIVE burn(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM burn WHERE x < {ms * 8000})
                SELECT d.Id, d.BatchId, d.DocumentType, d.Channel, d.RecipientName, d.MemberId,
                       d.City, d.State, d.PostalCode, d.Status, d.CreatedUtc
                FROM Documents d
                WHERE d.ClientId = @clientId
                  AND (UPPER(TRIM(d.RecipientName)) LIKE '%' || UPPER(@q) || '%'
                       OR d.MemberId LIKE '%' || @q || '%'
                       OR d.AddressLine1 LIKE '%' || @q || '%')
                  AND (SELECT COUNT(*) FROM burn) > 0
                ORDER BY d.CreatedUtc DESC
                LIMIT 50;
                """;
        }
        AddParam(cmd, "@clientId", clientId);
        AddParam(cmd, "@q", q);

        var results = new List<DocumentDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DocumentDto(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                Phi.Mask(reader.GetString(5)), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), null, reader.GetDateTime(10)));
        }
        return results;
    }

    /// <summary>A heavy "aging" report the dashboard runs when slow queries are on.</summary>
    [Trace]
    public async Task<int> SlowComplianceAgingReportAsync(int clientId, CancellationToken ct)
    {
        var ms = faults.Current.SlowQueryMs;
        await using var cmd = await CreateCommandAsync(ct);
        cmd.CommandText = IsSqlServer
            ? """
              WAITFOR DELAY @delay;
              SELECT COUNT(*) FROM (
                  SELECT d.DocumentType, d.Status, COUNT(*) AS Cnt, MIN(d.CreatedUtc) AS Oldest,
                         SUM(CASE WHEN q.Passed = 0 THEN 1 ELSE 0 END) AS QaFails
                  FROM Documents d
                  LEFT JOIN QaInspections q ON CONVERT(nvarchar(128), q.ExpectedName) = d.RecipientName
                  WHERE d.ClientId = @clientId AND DATEDIFF(day, d.CreatedUtc, SYSUTCDATETIME()) >= 0
                  GROUP BY d.DocumentType, d.Status) aging;
              """
            : $"""
              WITH RECURSIVE burn(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM burn WHERE x < {ms * 8000})
              SELECT COUNT(*) FROM (
                  SELECT d.DocumentType, d.Status, COUNT(*) AS Cnt
                  FROM Documents d
                  WHERE d.ClientId = @clientId AND julianday('now') - julianday(d.CreatedUtc) >= 0
                    AND (SELECT COUNT(*) FROM burn) > 0
                  GROUP BY d.DocumentType, d.Status) aging;
              """;
        if (IsSqlServer) AddParam(cmd, "@delay", ToWaitFor(ms));
        AddParam(cmd, "@clientId", clientId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Queries a table that doesn't exist, producing a real SqlException / SqliteException.</summary>
    [Trace]
    public async Task QueryArchivedPrintJobsAsync(int clientId, CancellationToken ct)
    {
        await using var cmd = await CreateCommandAsync(ct);
        cmd.CommandText = IsSqlServer
            ? "SELECT TOP (1) JobId, PrintedUtc FROM dbo.PrintJobArchive_2019 WHERE ClientId = @clientId;"
            : "SELECT JobId, PrintedUtc FROM PrintJobArchive_2019 WHERE ClientId = @clientId LIMIT 1;";
        AddParam(cmd, "@clientId", clientId);
        await cmd.ExecuteScalarAsync(ct);
    }

    async Task<DbCommand> CreateCommandAsync(CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        return cmd;
    }

    static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    static string ToWaitFor(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\.fff");
}
