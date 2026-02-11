// Path: Services/Scheduling/TicketSchedulingRepository.cs
// File: TicketSchedulingRepository.cs
// Description: Repo dla ScheduledTicketDefinitions + logów uruchomień.
// Notes:
// - Runner lock (sp_getapplock) musi działać na tym samym SqlConnection co cała praca RunOnce.
// - Dlatego metody schedulerowe mają overloady z SqlConnection.
// Created: 2026-01-03
// Updated: 2026-01-04 - Overloady z SqlConnection dla runnera (applock session).
// Updated: 2026-01-04 - FIX: SELECTy w definicjach zabezpieczone przed NULL w polach audytowych.
// Updated: 2026-01-04 - FIX: Runs z DurationMs (kolumna istnieje w dbo.TicketSchedulerRuns).
// Updated: 2026-01-04 - FIX: INSERT ticketów bez ticket_priority_value (kolumna obliczana).
// Updated: 2026-01-04 - FIX: UpsertDefinition wylicza i zapisuje ScheduleNextRunAtUtc (UI ma "Następne uruchomienie").
// Version: 1.15

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ITManager.Services.Scheduling;

public sealed class TicketSchedulingRepository
{
    private readonly string _cs;

    // Jeśli masz inną strefę dla zakładu, podmień tutaj albo przenieś do konfiguracji.
    private const string DefaultPlantTimeZoneId = "Central European Standard Time";

    public TicketSchedulingRepository(IConfiguration config)
    {
        _cs = config.GetConnectionString("ITManagerConnection")
              ?? throw new InvalidOperationException("Missing connection string: ConnectionStrings:ITManagerConnection");
    }

    private SqlConnection CreateConn() => new SqlConnection(_cs);

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = CreateConn();
        await conn.OpenAsync(ct);
        return conn;
    }

    public DateTime ToPlantLocalTime(DateTime utc)
    {
        var tz = GetTimeZoneOrLocal(DefaultPlantTimeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
    }

    private static TimeZoneInfo GetTimeZoneOrLocal(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Local; }
    }

    private static DateTime ComputeNextRunUtcFromDto(ScheduledTicketDefinitionDto dto, DateTime nowUtc)
    {
        var tzId = string.IsNullOrWhiteSpace(dto.ScheduleTimeZoneId)
            ? DefaultPlantTimeZoneId
            : dto.ScheduleTimeZoneId!.Trim();

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Local; }

        var nowUtcKind = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtcKind, tz);

        var startUtc = DateTime.SpecifyKind(dto.ScheduleStartAtUtc, DateTimeKind.Utc);
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);

        DateTime? endLocal = null;
        if (dto.ScheduleEndAtUtc.HasValue)
        {
            var endUtc = DateTime.SpecifyKind(dto.ScheduleEndAtUtc.Value, DateTimeKind.Utc);
            endLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, tz);
        }

        var baselineLocal = nowLocal > startLocal ? nowLocal : startLocal;

        var at = dto.ScheduleAtTime;
        var candidateLocal = new DateTime(
            baselineLocal.Year, baselineLocal.Month, baselineLocal.Day,
            at.Hours, at.Minutes, at.Seconds);

        if (candidateLocal <= baselineLocal)
            candidateLocal = candidateLocal.AddDays(1);

        bool IsMatch(DateTime d)
        {
            // 1 Daily, 2 Weekly, 3 Monthly
            if (dto.ScheduleFrequencyType == 1)
                return true;

            if (dto.ScheduleFrequencyType == 2)
            {
                if (!dto.ScheduleDaysOfWeekMask.HasValue || dto.ScheduleDaysOfWeekMask.Value <= 0)
                    return false;

                var mask = d.DayOfWeek switch
                {
                    DayOfWeek.Monday => 1,
                    DayOfWeek.Tuesday => 2,
                    DayOfWeek.Wednesday => 4,
                    DayOfWeek.Thursday => 8,
                    DayOfWeek.Friday => 16,
                    DayOfWeek.Saturday => 32,
                    DayOfWeek.Sunday => 64,
                    _ => 0
                };

                return (dto.ScheduleDaysOfWeekMask.Value & mask) != 0;
            }

            if (dto.ScheduleFrequencyType == 3)
            {
                var dom = Math.Clamp(dto.ScheduleDayOfMonth.GetValueOrDefault(1), 1, 31);
                return d.Day == dom;
            }

            return false;
        }

        DateTime ApplyBusinessRules(DateTime d)
        {
            if (!dto.ScheduleSkipWeekends && !dto.ScheduleMoveToNextBusinessDay)
                return d;

            var x = d;

            if (dto.ScheduleSkipWeekends)
            {
                if (x.DayOfWeek == DayOfWeek.Saturday) x = x.AddDays(2);
                if (x.DayOfWeek == DayOfWeek.Sunday) x = x.AddDays(1);
            }

            if (dto.ScheduleMoveToNextBusinessDay)
            {
                while (x.DayOfWeek == DayOfWeek.Saturday || x.DayOfWeek == DayOfWeek.Sunday)
                    x = x.AddDays(1);
            }

            return x;
        }

        for (var i = 0; i < 370; i++)
        {
            if (endLocal.HasValue && candidateLocal > endLocal.Value)
            {
                var safe = endLocal.Value.AddSeconds(1);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(safe, DateTimeKind.Unspecified), tz);
            }

            if (IsMatch(candidateLocal))
            {
                candidateLocal = ApplyBusinessRules(candidateLocal);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidateLocal, DateTimeKind.Unspecified), tz);
            }

            candidateLocal = candidateLocal.AddDays(1);
        }

        return nowUtcKind.AddDays(1);
    }

    /* =========================
       Runner lock (single runner)
       ========================= */

    public async Task<bool> TryAcquireRunnerLockAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp_getapplock";
        cmd.Parameters.AddWithValue("@Resource", "TicketSchedulerRunnerLock");
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        cmd.Parameters.AddWithValue("@LockTimeout", 0);

        var ret = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        cmd.Parameters.Add(ret);

        await cmd.ExecuteNonQueryAsync(ct);
        return (int)ret.Value >= 0;
    }

    public async Task ReleaseRunnerLockAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp_releaseapplock";
        cmd.Parameters.AddWithValue("@Resource", "TicketSchedulerRunnerLock");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /* =========================
       Definitions (CRUD for UI)
       ========================= */

    public async Task<List<ScheduledTicketDefinitionDto>> GetDefinitionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT
    d.DefinitionId,
    d.Name,
    d.IsEnabled,
    d.TitleTemplate,
    d.DescriptionTemplate,
    d.CategoryId,
    d.Impact,
    d.Urgency,
    d.QueueId,
    d.RequesterUserId,

    d.ScheduleIsEnabled,
    d.ScheduleStartAtUtc,
    d.ScheduleEndAtUtc,
    d.ScheduleAtTime,
    d.ScheduleTimeZoneId,
    d.ScheduleFrequencyType,
    d.ScheduleIntervalValue,
    d.ScheduleDaysOfWeekMask,
    d.ScheduleDayOfMonth,
    d.ScheduleMonthOfYear,
    d.ScheduleSkipWeekends,
    d.ScheduleMoveToNextBusinessDay,
    d.ScheduleLastRunAtUtc,
    d.ScheduleNextRunAtUtc,

    COALESCE(d.CreatedAtUtc, SYSUTCDATETIME()) AS CreatedAtUtc,
    ISNULL(d.CreatedByUserId, 0)              AS CreatedByUserId,
    COALESCE(d.UpdatedAtUtc, d.CreatedAtUtc, SYSUTCDATETIME()) AS UpdatedAtUtc,
    ISNULL(d.UpdatedByUserId, 0)              AS UpdatedByUserId
FROM dbo.ScheduledTicketDefinitions d
ORDER BY d.Name;";

        var list = new List<ScheduledTicketDefinitionDto>();

        await using var conn = CreateConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(ScheduledTicketDefinitionDto.FromReader(rd));

        return list;
    }

    public async Task<ScheduledTicketDefinitionDto?> GetDefinitionAsync(int definitionId, CancellationToken ct)
    {
        const string sql = @"
SELECT
    d.DefinitionId,
    d.Name,
    d.IsEnabled,
    d.TitleTemplate,
    d.DescriptionTemplate,
    d.CategoryId,
    d.Impact,
    d.Urgency,
    d.QueueId,
    d.RequesterUserId,

    d.ScheduleIsEnabled,
    d.ScheduleStartAtUtc,
    d.ScheduleEndAtUtc,
    d.ScheduleAtTime,
    d.ScheduleTimeZoneId,
    d.ScheduleFrequencyType,
    d.ScheduleIntervalValue,
    d.ScheduleDaysOfWeekMask,
    d.ScheduleDayOfMonth,
    d.ScheduleMonthOfYear,
    d.ScheduleSkipWeekends,
    d.ScheduleMoveToNextBusinessDay,
    d.ScheduleLastRunAtUtc,
    d.ScheduleNextRunAtUtc,

    COALESCE(d.CreatedAtUtc, SYSUTCDATETIME()) AS CreatedAtUtc,
    ISNULL(d.CreatedByUserId, 0)              AS CreatedByUserId,
    COALESCE(d.UpdatedAtUtc, d.CreatedAtUtc, SYSUTCDATETIME()) AS UpdatedAtUtc,
    ISNULL(d.UpdatedByUserId, 0)              AS UpdatedByUserId
FROM dbo.ScheduledTicketDefinitions d
WHERE d.DefinitionId = @Id;";

        await using var conn = CreateConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", definitionId);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        return ScheduledTicketDefinitionDto.FromReader(rd);
    }

    public async Task<int> UpsertDefinitionAsync(ScheduledTicketDefinitionDto dto, int byUserId, CancellationToken ct)
    {
        const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.ScheduledTicketDefinitions WHERE DefinitionId = @DefinitionId)
BEGIN
    UPDATE d
    SET
        Name = @Name,
        IsEnabled = @IsEnabled,
        TitleTemplate = @TitleTemplate,
        DescriptionTemplate = @DescriptionTemplate,
        CategoryId = @CategoryId,
        Impact = @Impact,
        Urgency = @Urgency,
        QueueId = @QueueId,
        RequesterUserId = @RequesterUserId,

        ScheduleIsEnabled = @ScheduleIsEnabled,
        ScheduleStartAtUtc = @ScheduleStartAtUtc,
        ScheduleEndAtUtc = @ScheduleEndAtUtc,
        ScheduleAtTime = @ScheduleAtTime,
        ScheduleTimeZoneId = @ScheduleTimeZoneId,
        ScheduleFrequencyType = @ScheduleFrequencyType,
        ScheduleIntervalValue = @ScheduleIntervalValue,
        ScheduleDaysOfWeekMask = @ScheduleDaysOfWeekMask,
        ScheduleDayOfMonth = @ScheduleDayOfMonth,
        ScheduleMonthOfYear = @ScheduleMonthOfYear,
        ScheduleSkipWeekends = @ScheduleSkipWeekends,
        ScheduleMoveToNextBusinessDay = @ScheduleMoveToNextBusinessDay,

        ScheduleNextRunAtUtc = @ScheduleNextRunAtUtc,

        UpdatedAtUtc = SYSUTCDATETIME(),
        UpdatedByUserId = @ByUserId
    FROM dbo.ScheduledTicketDefinitions d
    WHERE d.DefinitionId = @DefinitionId;

    SELECT @DefinitionId;
END
ELSE
BEGIN
    INSERT INTO dbo.ScheduledTicketDefinitions
    (
        Name, IsEnabled,
        TitleTemplate, DescriptionTemplate,
        CategoryId, Impact, Urgency, QueueId, RequesterUserId,

        ScheduleIsEnabled,
        ScheduleStartAtUtc, ScheduleEndAtUtc,
        ScheduleAtTime, ScheduleTimeZoneId,
        ScheduleFrequencyType, ScheduleIntervalValue,
        ScheduleDaysOfWeekMask, ScheduleDayOfMonth, ScheduleMonthOfYear,
        ScheduleSkipWeekends, ScheduleMoveToNextBusinessDay,

        ScheduleLastRunAtUtc, ScheduleNextRunAtUtc,

        CreatedAtUtc, CreatedByUserId,
        UpdatedAtUtc, UpdatedByUserId
    )
    VALUES
    (
        @Name, @IsEnabled,
        @TitleTemplate, @DescriptionTemplate,
        @CategoryId, @Impact, @Urgency, @QueueId, @RequesterUserId,

        @ScheduleIsEnabled,
        @ScheduleStartAtUtc, @ScheduleEndAtUtc,
        @ScheduleAtTime, @ScheduleTimeZoneId,
        @ScheduleFrequencyType, @ScheduleIntervalValue,
        @ScheduleDaysOfWeekMask, @ScheduleDayOfMonth, @ScheduleMonthOfYear,
        @ScheduleSkipWeekends, @ScheduleMoveToNextBusinessDay,

        NULL, @ScheduleNextRunAtUtc,

        SYSUTCDATETIME(), @ByUserId,
        SYSUTCDATETIME(), @ByUserId
    );

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END";

        await using var conn = CreateConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("@DefinitionId", dto.DefinitionId);
        cmd.Parameters.AddWithValue("@Name", dto.Name?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("@IsEnabled", dto.IsEnabled);

        cmd.Parameters.AddWithValue("@TitleTemplate", dto.TitleTemplate?.Trim() ?? string.Empty);

        var desc = dto.DescriptionTemplate?.Trim();
        cmd.Parameters.AddWithValue("@DescriptionTemplate", string.IsNullOrWhiteSpace(desc) ? (object)DBNull.Value : desc!);

        cmd.Parameters.AddWithValue("@CategoryId", (object?)dto.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Impact", (object?)dto.Impact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Urgency", (object?)dto.Urgency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@QueueId", (object?)dto.QueueId ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@RequesterUserId", dto.RequesterUserId);

        cmd.Parameters.AddWithValue("@ScheduleIsEnabled", dto.ScheduleIsEnabled);
        cmd.Parameters.AddWithValue("@ScheduleStartAtUtc", DateTime.SpecifyKind(dto.ScheduleStartAtUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("@ScheduleEndAtUtc", dto.ScheduleEndAtUtc.HasValue
            ? DateTime.SpecifyKind(dto.ScheduleEndAtUtc.Value, DateTimeKind.Utc)
            : (object)DBNull.Value);

        cmd.Parameters.AddWithValue("@ScheduleAtTime", dto.ScheduleAtTime);

        var tz = dto.ScheduleTimeZoneId?.Trim();
        cmd.Parameters.AddWithValue("@ScheduleTimeZoneId", string.IsNullOrWhiteSpace(tz) ? (object)DBNull.Value : tz!);

        cmd.Parameters.AddWithValue("@ScheduleFrequencyType", dto.ScheduleFrequencyType);
        cmd.Parameters.AddWithValue("@ScheduleIntervalValue", dto.ScheduleIntervalValue);

        cmd.Parameters.AddWithValue("@ScheduleDaysOfWeekMask", (object?)dto.ScheduleDaysOfWeekMask ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ScheduleDayOfMonth", (object?)dto.ScheduleDayOfMonth ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ScheduleMonthOfYear", (object?)dto.ScheduleMonthOfYear ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@ScheduleSkipWeekends", dto.ScheduleSkipWeekends);
        cmd.Parameters.AddWithValue("@ScheduleMoveToNextBusinessDay", dto.ScheduleMoveToNextBusinessDay);

        // Kluczowa poprawka: backend wylicza NextRun i zapisuje do DB.
        // UI potem pokazuje ScheduleNextRunAtUtc, a scheduler ma co przetwarzać.
        var nextRunUtc = ComputeNextRunUtcFromDto(dto, DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@ScheduleNextRunAtUtc", DateTime.SpecifyKind(nextRunUtc, DateTimeKind.Utc));

        cmd.Parameters.AddWithValue("@ByUserId", byUserId);

        var obj = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(obj);
    }

    public async Task SetDefinitionEnabledAsync(int definitionId, bool enabled, int byUserId, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.ScheduledTicketDefinitions
SET IsEnabled = @Enabled,
    UpdatedAtUtc = SYSUTCDATETIME(),
    UpdatedByUserId = @ByUserId
WHERE DefinitionId = @Id;";

        await using var conn = CreateConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", definitionId);
        cmd.Parameters.AddWithValue("@Enabled", enabled);
        cmd.Parameters.AddWithValue("@ByUserId", byUserId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /* =========================
       Runs for UI
       ========================= */

    public async Task<List<TicketSchedulerRunDto>> GetRecentSchedulerRunsAsync(int top, CancellationToken ct = default)
    {
        const string sql = @"
SELECT TOP (@Top)
    r.Id,
    r.RunAtUtc,
    r.DefinitionId,
    r.OccurrenceUtc,
    r.ResultCode,
    r.TicketId,
    r.Message,
    r.DurationMs,
    d.Name AS DefinitionName
FROM dbo.TicketSchedulerRuns r
LEFT JOIN dbo.ScheduledTicketDefinitions d ON d.DefinitionId = r.DefinitionId
ORDER BY r.RunAtUtc DESC;";

        var list = new List<TicketSchedulerRunDto>();

        await using var conn = CreateConn();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Top", top);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(TicketSchedulerRunDto.FromReader(rd));

        return list;
    }

    /* =========================
       Scheduler methods
       UWAGA: overloady na SqlConnection, żeby utrzymać applock (LockOwner=Session)
       ========================= */

    public async Task<List<ScheduledTicketDefinitionRow>> GetDueDefinitionsAsync(SqlConnection conn, DateTime nowUtc, int maxBatch, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (@MaxBatch)
    d.DefinitionId,
    d.Name,
    d.IsEnabled,

    d.TitleTemplate,
    d.DescriptionTemplate,

    d.CategoryId,
    d.Impact,
    d.Urgency,
    d.QueueId,
    d.RequesterUserId,

    d.ScheduleIsEnabled,
    d.ScheduleStartAtUtc,
    d.ScheduleEndAtUtc,
    d.ScheduleAtTime,
    d.ScheduleTimeZoneId,

    d.ScheduleFrequencyType,
    d.ScheduleIntervalValue,
    d.ScheduleDaysOfWeekMask,
    d.ScheduleDayOfMonth,
    d.ScheduleMonthOfYear,

    d.ScheduleSkipWeekends,
    d.ScheduleMoveToNextBusinessDay,

    d.ScheduleLastRunAtUtc,
    d.ScheduleNextRunAtUtc
FROM dbo.ScheduledTicketDefinitions d
WHERE
    d.IsEnabled = 1
    AND d.ScheduleIsEnabled = 1
    AND d.ScheduleNextRunAtUtc IS NOT NULL
    AND d.ScheduleNextRunAtUtc <= @NowUtc
    AND d.ScheduleStartAtUtc <= @NowUtc
    AND (d.ScheduleEndAtUtc IS NULL OR d.ScheduleEndAtUtc >= @NowUtc)
ORDER BY d.ScheduleNextRunAtUtc ASC;";

        var list = new List<ScheduledTicketDefinitionRow>();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@NowUtc", DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("@MaxBatch", maxBatch);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(ScheduledTicketDefinitionRow.FromReader(rd));

        return list;
    }

    public sealed record CreateTicketResult(bool Created, long? TicketId);

    public async Task<CreateTicketResult> TryCreateTicketFromDefinitionAsync(
        SqlConnection conn,
        ScheduledTicketDefinitionRow d,
        string title,
        string? description,
        DateTime occurrenceUtc,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var nowLocal = ToPlantLocalTime(nowUtc);

        // FIX: bez ticket_priority_value (kolumna obliczana)
        const string sql = @"
INSERT INTO dbo.tickets
(
    ticket_title,
    ticket_request_date,
    ticket_closing_date,
    ticket_requester_id,
    ticket_keeper_id,
    problem_description,
    problem_solution,
    ticket_status_id,
    ticket_category_id,
    ticket_impact_id,
    ticket_urgency_id,
    Source,
    CreatedFromScheduledTicketDefinitionId,
    ScheduledOccurrenceUtc
)
VALUES
(
    @Title,
    @RequestDateLocal,
    NULL,
    @RequesterId,
    NULL,
    @Description,
    NULL,
    @StatusId,
    @CategoryId,
    @ImpactId,
    @UrgencyId,
    1,
    @DefinitionId,
    @OccurrenceUtc
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("@Title", title);
        cmd.Parameters.AddWithValue("@RequestDateLocal", nowLocal);
        cmd.Parameters.AddWithValue("@RequesterId", d.RequesterUserId);
        cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@StatusId", 1);

        cmd.Parameters.AddWithValue("@CategoryId", (object?)d.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ImpactId", (object?)d.Impact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UrgencyId", (object?)d.Urgency ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@DefinitionId", d.DefinitionId);
        cmd.Parameters.AddWithValue("@OccurrenceUtc", DateTime.SpecifyKind(occurrenceUtc, DateTimeKind.Utc));

        try
        {
            var idObj = await cmd.ExecuteScalarAsync(ct);
            return new CreateTicketResult(true, Convert.ToInt64(idObj));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UNIQUE konflikt: to samo occurrence już wstawione (idempotencja)
            return new CreateTicketResult(false, null);
        }
    }

    public async Task InsertRunLogAsync(
        SqlConnection conn,
        int definitionId,
        DateTime occurrenceUtc,
        byte resultCode,
        long? createdTicketId,
        string? message,
        int? durationMs,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.TicketSchedulerRuns
(
    RunAtUtc,
    DefinitionId,
    OccurrenceUtc,
    ResultCode,
    TicketId,
    Message,
    DurationMs
)
VALUES
(
    SYSUTCDATETIME(),
    @DefinitionId,
    @OccurrenceUtc,
    @ResultCode,
    @TicketId,
    @Message,
    @DurationMs
);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DefinitionId", definitionId);
        cmd.Parameters.AddWithValue("@OccurrenceUtc", DateTime.SpecifyKind(occurrenceUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("@ResultCode", resultCode);
        cmd.Parameters.AddWithValue("@TicketId", (object?)createdTicketId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Message", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DurationMs", (object?)durationMs ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateDefinitionAfterRunAsync(
        SqlConnection conn,
        int definitionId,
        DateTime lastRunAtUtc,
        DateTime nextRunAtUtc,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.ScheduledTicketDefinitions
SET
    ScheduleLastRunAtUtc = @LastRunAtUtc,
    ScheduleNextRunAtUtc = @NextRunAtUtc
WHERE DefinitionId = @DefinitionId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@LastRunAtUtc", DateTime.SpecifyKind(lastRunAtUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("@NextRunAtUtc", DateTime.SpecifyKind(nextRunAtUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("@DefinitionId", definitionId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/* =========================
   Row model for scheduler
   ========================= */

public sealed record ScheduledTicketDefinitionRow(
    int DefinitionId,
    string Name,
    bool IsEnabled,

    string TitleTemplate,
    string? DescriptionTemplate,

    int? CategoryId,
    byte? Impact,
    byte? Urgency,
    int? QueueId,
    int RequesterUserId,

    bool ScheduleIsEnabled,
    DateTime ScheduleStartAtUtc,
    DateTime? ScheduleEndAtUtc,
    TimeSpan ScheduleAtTime,
    string? ScheduleTimeZoneId,

    byte ScheduleFrequencyType,
    int ScheduleIntervalValue,
    int? ScheduleDaysOfWeekMask,
    int? ScheduleDayOfMonth,
    int? ScheduleMonthOfYear,

    bool ScheduleSkipWeekends,
    bool ScheduleMoveToNextBusinessDay,

    DateTime? ScheduleLastRunAtUtc,
    DateTime ScheduleNextRunAtUtc
)
{
    public static ScheduledTicketDefinitionRow FromReader(SqlDataReader rd)
    {
        int O(string n) => rd.GetOrdinal(n);

        return new ScheduledTicketDefinitionRow(
            DefinitionId: rd.GetInt32(O("DefinitionId")),
            Name: rd.GetString(O("Name")),
            IsEnabled: rd.GetBoolean(O("IsEnabled")),

            TitleTemplate: rd.GetString(O("TitleTemplate")),
            DescriptionTemplate: rd.IsDBNull(O("DescriptionTemplate")) ? null : rd.GetString(O("DescriptionTemplate")),

            CategoryId: rd.IsDBNull(O("CategoryId")) ? null : rd.GetInt32(O("CategoryId")),
            Impact: rd.IsDBNull(O("Impact")) ? null : rd.GetByte(O("Impact")),
            Urgency: rd.IsDBNull(O("Urgency")) ? null : rd.GetByte(O("Urgency")),
            QueueId: rd.IsDBNull(O("QueueId")) ? null : rd.GetInt32(O("QueueId")),
            RequesterUserId: rd.GetInt32(O("RequesterUserId")),

            ScheduleIsEnabled: rd.GetBoolean(O("ScheduleIsEnabled")),
            ScheduleStartAtUtc: rd.GetDateTime(O("ScheduleStartAtUtc")),
            ScheduleEndAtUtc: rd.IsDBNull(O("ScheduleEndAtUtc")) ? null : rd.GetDateTime(O("ScheduleEndAtUtc")),
            ScheduleAtTime: rd.GetTimeSpan(O("ScheduleAtTime")),
            ScheduleTimeZoneId: rd.IsDBNull(O("ScheduleTimeZoneId")) ? null : rd.GetString(O("ScheduleTimeZoneId")),

            ScheduleFrequencyType: rd.GetByte(O("ScheduleFrequencyType")),
            ScheduleIntervalValue: rd.GetInt32(O("ScheduleIntervalValue")),
            ScheduleDaysOfWeekMask: rd.IsDBNull(O("ScheduleDaysOfWeekMask")) ? null : rd.GetInt32(O("ScheduleDaysOfWeekMask")),
            ScheduleDayOfMonth: rd.IsDBNull(O("ScheduleDayOfMonth")) ? null : rd.GetInt32(O("ScheduleDayOfMonth")),
            ScheduleMonthOfYear: rd.IsDBNull(O("ScheduleMonthOfYear")) ? null : rd.GetInt32(O("ScheduleMonthOfYear")),

            ScheduleSkipWeekends: rd.GetBoolean(O("ScheduleSkipWeekends")),
            ScheduleMoveToNextBusinessDay: rd.GetBoolean(O("ScheduleMoveToNextBusinessDay")),

            ScheduleLastRunAtUtc: rd.IsDBNull(O("ScheduleLastRunAtUtc")) ? null : rd.GetDateTime(O("ScheduleLastRunAtUtc")),
            ScheduleNextRunAtUtc: rd.GetDateTime(O("ScheduleNextRunAtUtc"))
        );
    }

    public DateTime ComputeNextOccurrenceUtc(DateTime fromUtc)
    {
        var tzId = string.IsNullOrWhiteSpace(ScheduleTimeZoneId)
            ? "Central European Standard Time"
            : ScheduleTimeZoneId!;

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Local; }

        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), tz);

        var candidateLocal = new DateTime(
            fromLocal.Year, fromLocal.Month, fromLocal.Day,
            ScheduleAtTime.Hours, ScheduleAtTime.Minutes, ScheduleAtTime.Seconds);

        if (candidateLocal <= fromLocal)
            candidateLocal = candidateLocal.AddDays(1);

        for (var i = 0; i < 370; i++)
        {
            if (ScheduleEndAtUtc.HasValue)
            {
                var endLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ScheduleEndAtUtc.Value, DateTimeKind.Utc), tz);
                if (candidateLocal > endLocal)
                    return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidateLocal, DateTimeKind.Unspecified), tz);
            }

            if (IsMatch(candidateLocal))
            {
                candidateLocal = ApplyBusinessDayRules(candidateLocal);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidateLocal, DateTimeKind.Unspecified), tz);
            }

            candidateLocal = candidateLocal.AddDays(1);
        }

        return DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).AddDays(1);
    }

    private bool IsMatch(DateTime candidateLocal)
    {
        // 1 Daily, 2 Weekly, 3 Monthly
        if (ScheduleFrequencyType == 1)
            return true;

        if (ScheduleFrequencyType == 2)
        {
            if (!ScheduleDaysOfWeekMask.HasValue || ScheduleDaysOfWeekMask.Value <= 0)
                return false;

            var mask = MapDayOfWeekToMask(candidateLocal.DayOfWeek);
            return (ScheduleDaysOfWeekMask.Value & mask) != 0;
        }

        if (ScheduleFrequencyType == 3)
        {
            var dom = Math.Clamp(ScheduleDayOfMonth.GetValueOrDefault(1), 1, 31);
            return candidateLocal.Day == dom;
        }

        return false;
    }

    private static int MapDayOfWeekToMask(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 4,
            DayOfWeek.Thursday => 8,
            DayOfWeek.Friday => 16,
            DayOfWeek.Saturday => 32,
            DayOfWeek.Sunday => 64,
            _ => 0
        };
    }

    private DateTime ApplyBusinessDayRules(DateTime dt)
    {
        if (!ScheduleSkipWeekends && !ScheduleMoveToNextBusinessDay)
            return dt;

        var x = dt;

        if (ScheduleSkipWeekends)
        {
            if (x.DayOfWeek == DayOfWeek.Saturday) x = x.AddDays(2);
            if (x.DayOfWeek == DayOfWeek.Sunday) x = x.AddDays(1);
        }

        if (ScheduleMoveToNextBusinessDay)
        {
            while (x.DayOfWeek == DayOfWeek.Saturday || x.DayOfWeek == DayOfWeek.Sunday)
                x = x.AddDays(1);
        }

        return x;
    }
}

/* =========================
   Runs DTO for UI
   ========================= */

public sealed class TicketSchedulerRunDto
{
    public long Id { get; set; }
    public DateTime RunAtUtc { get; set; }

    public int DefinitionId { get; set; }
    public string DefinitionName { get; set; } = "";

    public DateTime OccurrenceUtc { get; set; }
    public byte ResultCode { get; set; }

    public long? TicketId { get; set; }
    public string? Message { get; set; }

    public int? DurationMs { get; set; }

    public static TicketSchedulerRunDto FromReader(SqlDataReader rd)
    {
        int O(string n) => rd.GetOrdinal(n);

        return new TicketSchedulerRunDto
        {
            Id = rd.GetInt64(O("Id")),
            RunAtUtc = rd.GetDateTime(O("RunAtUtc")),
            DefinitionId = rd.IsDBNull(O("DefinitionId")) ? 0 : rd.GetInt32(O("DefinitionId")),

            DefinitionName = rd.IsDBNull(O("DefinitionName")) ? "" : rd.GetString(O("DefinitionName")),

            OccurrenceUtc = rd.GetDateTime(O("OccurrenceUtc")),
            ResultCode = rd.IsDBNull(O("ResultCode")) ? (byte)0 : rd.GetByte(O("ResultCode")),

            TicketId = rd.IsDBNull(O("TicketId")) ? null : rd.GetInt64(O("TicketId")),
            Message = rd.IsDBNull(O("Message")) ? null : rd.GetString(O("Message")),
            DurationMs = rd.IsDBNull(O("DurationMs")) ? null : rd.GetInt32(O("DurationMs"))
        };
    }
}
