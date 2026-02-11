// File: Services/Security/BackupStatusService.cs
// Description: Read-only service for backup run statuses for Security Center (audit-ready).
// Version: 1.01
// Created: 2026-01-28
// Updated:
// - 2026-01-28 - FIX: CurrentUserContextService uses Can() instead of Has().

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ITManager.Services.Auth;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace ITManager.Services.Security
{
    public sealed class BackupStatusService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<BackupStatusService> _log;
        private readonly CurrentUserContextService _ctx;

        public BackupStatusService(IConfiguration config, ILogger<BackupStatusService> log, CurrentUserContextService ctx)
        {
            _config = config;
            _log = log;
            _ctx = ctx;
        }

        public sealed class BackupRunRow
        {
            public int BackupRunId { get; set; }
            public string Tool { get; set; } = "";
            public string JobName { get; set; } = "";
            public string? Target { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime? EndedUtc { get; set; }
            public string Status { get; set; } = "";
            public int? DurationSeconds { get; set; }
            public long? BytesProcessed { get; set; }
            public string? Message { get; set; }
        }

        public sealed class BackupRunsQuery
        {
            public DateTime FromUtc { get; set; }
            public DateTime ToUtc { get; set; }
            public string? Tool { get; set; }
            public string? Status { get; set; }
            public string? JobNameContains { get; set; }
        }

        public async Task<IReadOnlyList<BackupRunRow>> GetBackupRunsAsync(BackupRunsQuery query)
        {
            if (!_ctx.Can("Security.Backups.View"))
                throw new UnauthorizedAccessException("Brak uprawnień: Security.Backups.View");

            var result = new List<BackupRunRow>();

            try
            {
                var cs = _config.GetConnectionString("DefaultConnection");
                using var con = new SqlConnection(cs);
                using var cmd = con.CreateCommand();
                cmd.CommandType = CommandType.Text;

                cmd.CommandText = @"
SELECT TOP (500)
    backup_run_id,
    tool,
    job_name,
    target,
    started_utc,
    ended_utc,
    status,
    duration_seconds,
    bytes_processed,
    message
FROM dbo.vw_backup_runs
WHERE started_utc >= @FromUtc AND started_utc < @ToUtc
  AND (@Tool IS NULL OR tool = @Tool)
  AND (@Status IS NULL OR status = @Status)
  AND (@JobNameContains IS NULL OR job_name LIKE '%' + @JobNameContains + '%')
ORDER BY started_utc DESC;";

                cmd.Parameters.AddWithValue("@FromUtc", query.FromUtc);
                cmd.Parameters.AddWithValue("@ToUtc", query.ToUtc);
                cmd.Parameters.AddWithValue("@Tool", (object?)query.Tool ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Status", (object?)query.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@JobNameContains", (object?)query.JobNameContains ?? DBNull.Value);

                await con.OpenAsync();

                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    result.Add(new BackupRunRow
                    {
                        BackupRunId = r.GetInt32(0),
                        Tool = r.GetString(1),
                        JobName = r.GetString(2),
                        Target = r.IsDBNull(3) ? null : r.GetString(3),
                        StartedUtc = r.GetDateTime(4),
                        EndedUtc = r.IsDBNull(5) ? null : r.GetDateTime(5),
                        Status = r.GetString(6),
                        DurationSeconds = r.IsDBNull(7) ? null : r.GetInt32(7),
                        BytesProcessed = r.IsDBNull(8) ? null : r.GetInt64(8),
                        Message = r.IsDBNull(9) ? null : r.GetString(9)
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "GetBackupRunsAsync failed.");
                return Array.Empty<BackupRunRow>();
            }
        }
    }
}
