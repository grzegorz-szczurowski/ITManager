// File: Services/Scheduling/TicketSchedulerService.cs
// Description: CRUD szablonów i harmonogramów ticketa + lookupy do list rozwijanych.
// Notes:
// - System.Data.SqlClient (bez Microsoft.Data.SqlClient)
// - Connection string: ConnectionStrings:ITManagerConnection
//
// WAŻNE: konfiguracja harmonogramu przeniesiona do dbo.TicketTemplates.
// Dodatkowo: serwis robi UPSERT do dbo.TicketSchedules na podstawie template.
//
// FIXES w tej wersji:
// 1) Konflikt nazwy: property TicketTemplateDto.ScheduleFrequencyType vs enum ScheduleFrequencyType
//    Poprawione przez pełną kwalifikację enuma (global::ITManager.Services.Scheduling.ScheduleFrequencyType).
// 2) Identycznie dla TicketScheduleDto.FrequencyType default value i fallbacków.
// 3) NextRunAt: wyliczane na bazie StartAt+AtTime; jeżeli wypada w przeszłości, przesuwane o 1 dzień do przodu.
// 4) Zamiast GETDATE użyte SYSUTCDATETIME w miejscach, gdzie zapisujesz czasy systemowe.
//
// Version: 1.02
// Updated: 2026-01-04

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ITManager.Services.Scheduling;

public enum ScheduleFrequencyType : byte
{
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

public sealed class TicketSchedulerService
{
    private readonly string _cs;

    public TicketSchedulerService(IConfiguration config)
    {
        _cs = config.GetConnectionString("ITManagerConnection")
              ?? throw new InvalidOperationException("Missing connection string: ConnectionStrings:ITManagerConnection");
    }

    private SqlConnection CreateConn() => new SqlConnection(_cs);

    // ----------------------------
    // Priorytet wyliczany
    // ----------------------------

    public static int ComputePriorityLevel(int impactWeight, int urgencyWeight)
    {
        var score = impactWeight * urgencyWeight;

        if (score >= 80) return 1;
        if (score >= 50) return 2;
        if (score >= 30) return 3;
        if (score >= 15) return 4;
        return 5;
    }

    public Task<List<LookupItem<int>>> GetTicketPrioritiesAsync()
    {
        var list = new List<LookupItem<int>>
        {
            new() { Id = 1, Name = "Emergency (P1)  ≥ 80" },
            new() { Id = 2, Name = "Very High (P2)  50–79" },
            new() { Id = 3, Name = "High (P3)  30–49" },
            new() { Id = 4, Name = "Medium (P4)  15–29" },
            new() { Id = 5, Name = "Low (P5)  < 15" }
        };

        return Task.FromResult(list);
    }

    // ----------------------------
    // Lookups
    // ----------------------------

    public async Task<List<LookupItem<int>>> GetTicketCategoriesAsync()
    {
        const string sql = @"
SELECT CAST(c.id AS int) AS Id, c.name AS Name
FROM dbo.ticket_categories c
ORDER BY ISNULL(c.sort_order, 999999), c.name;";

        return await QueryLookupIntAsync(sql);
    }

    public async Task<List<WeightedLookupItem<byte>>> GetTicketImpactsWeightedAsync()
    {
        const string sql = @"
SELECT
    CAST(i.id AS tinyint) AS Id,
    i.name AS Name,
    ISNULL(i.weight, 0) AS Weight
FROM dbo.ticket_impacts i
ORDER BY ISNULL(i.sort_order, 999999), i.name;";

        return await QueryWeightedLookupByteAsync(sql);
    }

    public async Task<List<WeightedLookupItem<byte>>> GetTicketUrgenciesWeightedAsync()
    {
        const string sql = @"
SELECT
    CAST(u.id AS tinyint) AS Id,
    u.name AS Name,
    ISNULL(u.weight, 0) AS Weight
FROM dbo.ticket_urgencies u
ORDER BY ISNULL(u.sort_order, 999999), u.name;";

        return await QueryWeightedLookupByteAsync(sql);
    }

    public async Task<List<LookupItem<byte>>> GetTicketImpactsAsync()
    {
        var src = await GetTicketImpactsWeightedAsync();
        var list = new List<LookupItem<byte>>();
        foreach (var x in src)
            list.Add(new LookupItem<byte> { Id = x.Id, Name = x.Name });
        return list;
    }

    public async Task<List<LookupItem<byte>>> GetTicketUrgenciesAsync()
    {
        var src = await GetTicketUrgenciesWeightedAsync();
        var list = new List<LookupItem<byte>>();
        foreach (var x in src)
            list.Add(new LookupItem<byte> { Id = x.Id, Name = x.Name });
        return list;
    }

    public async Task<List<LookupItem<int>>> GetTicketQueuesAsync()
    {
        const string sql = @"
SELECT CAST(q.id AS int) AS Id, q.name AS Name
FROM dbo.ticket_queues q
ORDER BY ISNULL(q.sort_order, 999999), q.name;";

        return await QueryLookupIntAsync(sql);
    }

    public async Task<List<LookupItem<int>>> GetAssignableUsersAsync()
    {
        const string sql = @"
SELECT
    CAST(u.id AS int) AS Id,
    COALESCE(
        NULLIF(LTRIM(RTRIM(u.DisplayName)), ''),
        NULLIF(LTRIM(RTRIM(
            CONCAT(
                NULLIF(LTRIM(RTRIM(u.GivenName)), ''),
                CASE WHEN NULLIF(LTRIM(RTRIM(u.Surname)), '') IS NULL THEN '' ELSE ' ' END,
                NULLIF(LTRIM(RTRIM(u.Surname)), '')
            )
        )), ''),
        NULLIF(LTRIM(RTRIM(u.EmailAddress)), ''),
        CAST(u.id AS nvarchar(20))
    ) AS Name
FROM dbo.users u
WHERE (u.IsActive IS NULL OR u.IsActive = 1)
ORDER BY Name;";

        return await QueryLookupIntAsync(sql);
    }

    public Task<List<LookupItem<int>>> GetUsersAsync() => GetAssignableUsersAsync();

    public async Task<List<LookupItem<int>>> GetGroupsAsync()
    {
        const string sql = @"
SELECT CAST(r.RoleId AS int) AS Id, r.Name AS Name
FROM dbo.Roles r
WHERE r.IsActive = 1
ORDER BY r.Name;";

        return await QueryLookupIntAsync(sql);
    }

    private async Task<List<LookupItem<int>>> QueryLookupIntAsync(string sql)
    {
        var list = new List<LookupItem<int>>();

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        var ordId = rd.GetOrdinal("Id");
        var ordName = rd.GetOrdinal("Name");

        while (await rd.ReadAsync())
        {
            list.Add(new LookupItem<int>
            {
                Id = rd.GetInt32(ordId),
                Name = rd.IsDBNull(ordName) ? "" : rd.GetString(ordName)
            });
        }

        return list;
    }

    private async Task<List<WeightedLookupItem<byte>>> QueryWeightedLookupByteAsync(string sql)
    {
        var list = new List<WeightedLookupItem<byte>>();

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        var ordId = rd.GetOrdinal("Id");
        var ordName = rd.GetOrdinal("Name");
        var ordWeight = rd.GetOrdinal("Weight");

        while (await rd.ReadAsync())
        {
            list.Add(new WeightedLookupItem<byte>
            {
                Id = rd.GetByte(ordId),
                Name = rd.IsDBNull(ordName) ? "" : rd.GetString(ordName),
                Weight = rd.IsDBNull(ordWeight) ? 0 : rd.GetInt32(ordWeight)
            });
        }

        return list;
    }

    // ----------------------------
    // Templates
    // ----------------------------

    public async Task<List<TicketTemplateDto>> GetTemplatesAsync()
    {
        const string sql = @"
SELECT
    t.TemplateId,
    t.Name,
    t.IsEnabled,
    t.TitleTemplate,
    t.DescriptionTemplate,
    t.CategoryId,
    t.PriorityId,
    t.Impact,
    t.Urgency,
    t.QueueId,
    t.AssignedToUserId,
    t.AssignedToGroupId,
    t.DefaultDueInHours,

    t.ScheduleName,
    t.ScheduleIsEnabled,
    t.ScheduleOwnerUserId,
    t.ScheduleOwnerSamAccountName,
    t.ScheduleStartAt,
    t.ScheduleEndAt,
    t.ScheduleAtTime,
    t.ScheduleTimeZoneId,
    t.ScheduleFrequencyType,
    t.ScheduleIntervalValue,
    t.ScheduleDaysOfWeekMask,
    t.ScheduleDayOfMonth,
    t.ScheduleSkipWeekends,
    t.ScheduleMoveToNextBusinessDay,
    t.SchedulePreventDuplicatesWindowHours,

    t.CreatedAt,
    t.CreatedByUserId,
    t.UpdatedAt,
    t.UpdatedByUserId
FROM dbo.TicketTemplates t
ORDER BY t.Name ASC;";

        var list = new List<TicketTemplateDto>();

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();

        while (await rd.ReadAsync())
            list.Add(TicketTemplateDto.FromReader(rd));

        return list;
    }

    public async Task<TicketTemplateDto?> GetTemplateAsync(int templateId)
    {
        const string sql = @"
SELECT
    t.TemplateId,
    t.Name,
    t.IsEnabled,
    t.TitleTemplate,
    t.DescriptionTemplate,
    t.CategoryId,
    t.PriorityId,
    t.Impact,
    t.Urgency,
    t.QueueId,
    t.AssignedToUserId,
    t.AssignedToGroupId,
    t.DefaultDueInHours,

    t.ScheduleName,
    t.ScheduleIsEnabled,
    t.ScheduleOwnerUserId,
    t.ScheduleOwnerSamAccountName,
    t.ScheduleStartAt,
    t.ScheduleEndAt,
    t.ScheduleAtTime,
    t.ScheduleTimeZoneId,
    t.ScheduleFrequencyType,
    t.ScheduleIntervalValue,
    t.ScheduleDaysOfWeekMask,
    t.ScheduleDayOfMonth,
    t.ScheduleSkipWeekends,
    t.ScheduleMoveToNextBusinessDay,
    t.SchedulePreventDuplicatesWindowHours,

    t.CreatedAt,
    t.CreatedByUserId,
    t.UpdatedAt,
    t.UpdatedByUserId
FROM dbo.TicketTemplates t
WHERE t.TemplateId = @Id;";

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = templateId;

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;

        return TicketTemplateDto.FromReader(rd);
    }

    public async Task<int> UpsertTemplateAsync(TicketTemplateDto model, int actorUserId)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new InvalidOperationException("Name is required.");

        if (string.IsNullOrWhiteSpace(model.TitleTemplate))
            throw new InvalidOperationException("TitleTemplate is required.");

        if (model.ScheduleIntervalValue <= 0)
            model.ScheduleIntervalValue = 1;

        if (model.ScheduleOwnerUserId is null || model.ScheduleOwnerUserId <= 0)
            model.ScheduleOwnerUserId = actorUserId;

        if (model.ScheduleStartAt == default)
            model.ScheduleStartAt = DateTime.Today;

        if (model.ScheduleAtTime == default)
            model.ScheduleAtTime = new TimeSpan(8, 0, 0);

        const string sql = @"
DECLARE @OutId int;

IF EXISTS (SELECT 1 FROM dbo.TicketTemplates WHERE TemplateId = @TemplateId)
BEGIN
    UPDATE dbo.TicketTemplates
    SET
        Name = @Name,
        IsEnabled = @IsEnabled,
        TitleTemplate = @TitleTemplate,
        DescriptionTemplate = @DescriptionTemplate,
        CategoryId = @CategoryId,
        PriorityId = @PriorityId,
        Impact = @Impact,
        Urgency = @Urgency,
        QueueId = @QueueId,
        AssignedToUserId = @AssignedToUserId,
        AssignedToGroupId = @AssignedToGroupId,
        DefaultDueInHours = @DefaultDueInHours,

        ScheduleName = @ScheduleName,
        ScheduleIsEnabled = @ScheduleIsEnabled,
        ScheduleOwnerUserId = @ScheduleOwnerUserId,
        ScheduleOwnerSamAccountName = @ScheduleOwnerSamAccountName,
        ScheduleStartAt = @ScheduleStartAt,
        ScheduleEndAt = @ScheduleEndAt,
        ScheduleAtTime = @ScheduleAtTime,
        ScheduleTimeZoneId = @ScheduleTimeZoneId,
        ScheduleFrequencyType = @ScheduleFrequencyType,
        ScheduleIntervalValue = @ScheduleIntervalValue,
        ScheduleDaysOfWeekMask = @ScheduleDaysOfWeekMask,
        ScheduleDayOfMonth = @ScheduleDayOfMonth,
        ScheduleSkipWeekends = @ScheduleSkipWeekends,
        ScheduleMoveToNextBusinessDay = @ScheduleMoveToNextBusinessDay,
        SchedulePreventDuplicatesWindowHours = @SchedulePreventDuplicatesWindowHours,

        UpdatedAt = SYSUTCDATETIME(),
        UpdatedByUserId = @ActorUserId
    WHERE TemplateId = @TemplateId;

    SET @OutId = @TemplateId;
END
ELSE
BEGIN
    INSERT INTO dbo.TicketTemplates
    (
        Name, IsEnabled, TitleTemplate, DescriptionTemplate,
        CategoryId, PriorityId, Impact, Urgency,
        QueueId, AssignedToUserId, AssignedToGroupId, DefaultDueInHours,

        ScheduleName, ScheduleIsEnabled, ScheduleOwnerUserId, ScheduleOwnerSamAccountName,
        ScheduleStartAt, ScheduleEndAt, ScheduleAtTime, ScheduleTimeZoneId,
        ScheduleFrequencyType, ScheduleIntervalValue, ScheduleDaysOfWeekMask, ScheduleDayOfMonth,
        ScheduleSkipWeekends, ScheduleMoveToNextBusinessDay, SchedulePreventDuplicatesWindowHours,

        CreatedAt, CreatedByUserId, UpdatedAt, UpdatedByUserId
    )
    VALUES
    (
        @Name, @IsEnabled, @TitleTemplate, @DescriptionTemplate,
        @CategoryId, @PriorityId, @Impact, @Urgency,
        @QueueId, @AssignedToUserId, @AssignedToGroupId, @DefaultDueInHours,

        @ScheduleName, @ScheduleIsEnabled, @ScheduleOwnerUserId, @ScheduleOwnerSamAccountName,
        @ScheduleStartAt, @ScheduleEndAt, @ScheduleAtTime, @ScheduleTimeZoneId,
        @ScheduleFrequencyType, @ScheduleIntervalValue, @ScheduleDaysOfWeekMask, @ScheduleDayOfMonth,
        @ScheduleSkipWeekends, @ScheduleMoveToNextBusinessDay, @SchedulePreventDuplicatesWindowHours,

        SYSUTCDATETIME(), @ActorUserId, SYSUTCDATETIME(), @ActorUserId
    );

    SET @OutId = CAST(SCOPE_IDENTITY() AS INT);
END

SELECT @OutId;";

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.Add("@TemplateId", SqlDbType.Int).Value = model.TemplateId;
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = (model.Name ?? "").Trim();
        cmd.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = model.IsEnabled;

        cmd.Parameters.Add("@TitleTemplate", SqlDbType.NVarChar, 300).Value = (model.TitleTemplate ?? "").Trim();

        var desc = (model.DescriptionTemplate ?? "").Trim();
        cmd.Parameters.Add("@DescriptionTemplate", SqlDbType.NVarChar, -1).Value =
            string.IsNullOrWhiteSpace(desc) ? DBNull.Value : desc;

        cmd.Parameters.Add("@CategoryId", SqlDbType.Int).Value = (object?)model.CategoryId ?? DBNull.Value;
        cmd.Parameters.Add("@PriorityId", SqlDbType.Int).Value = (object?)model.PriorityId ?? DBNull.Value;

        cmd.Parameters.Add("@Impact", SqlDbType.TinyInt).Value = (object?)model.Impact ?? DBNull.Value;
        cmd.Parameters.Add("@Urgency", SqlDbType.TinyInt).Value = (object?)model.Urgency ?? DBNull.Value;

        cmd.Parameters.Add("@QueueId", SqlDbType.Int).Value = (object?)model.QueueId ?? DBNull.Value;
        cmd.Parameters.Add("@AssignedToUserId", SqlDbType.Int).Value = (object?)model.AssignedToUserId ?? DBNull.Value;
        cmd.Parameters.Add("@AssignedToGroupId", SqlDbType.Int).Value = (object?)model.AssignedToGroupId ?? DBNull.Value;
        cmd.Parameters.Add("@DefaultDueInHours", SqlDbType.Int).Value = (object?)model.DefaultDueInHours ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleName", SqlDbType.NVarChar, 150).Value =
            (object?)NullIfEmpty(model.ScheduleName) ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleIsEnabled", SqlDbType.Bit).Value = model.ScheduleIsEnabled;
        cmd.Parameters.Add("@ScheduleOwnerUserId", SqlDbType.Int).Value = (object?)model.ScheduleOwnerUserId ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleOwnerSamAccountName", SqlDbType.NVarChar, 80).Value =
            (object?)NullIfEmpty(model.ScheduleOwnerSamAccountName) ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleStartAt", SqlDbType.DateTime2).Value = model.ScheduleStartAt;
        cmd.Parameters.Add("@ScheduleEndAt", SqlDbType.DateTime2).Value = (object?)model.ScheduleEndAt ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleAtTime", SqlDbType.Time).Value = model.ScheduleAtTime;
        cmd.Parameters.Add("@ScheduleTimeZoneId", SqlDbType.NVarChar, 64).Value =
            (object?)NullIfEmpty(model.ScheduleTimeZoneId) ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleFrequencyType", SqlDbType.TinyInt).Value = model.ScheduleFrequencyType;
        cmd.Parameters.Add("@ScheduleIntervalValue", SqlDbType.Int).Value = model.ScheduleIntervalValue;

        cmd.Parameters.Add("@ScheduleDaysOfWeekMask", SqlDbType.Int).Value = (object?)model.ScheduleDaysOfWeekMask ?? DBNull.Value;
        cmd.Parameters.Add("@ScheduleDayOfMonth", SqlDbType.Int).Value = (object?)model.ScheduleDayOfMonth ?? DBNull.Value;

        cmd.Parameters.Add("@ScheduleSkipWeekends", SqlDbType.Bit).Value = model.ScheduleSkipWeekends;
        cmd.Parameters.Add("@ScheduleMoveToNextBusinessDay", SqlDbType.Bit).Value = model.ScheduleMoveToNextBusinessDay;

        cmd.Parameters.Add("@SchedulePreventDuplicatesWindowHours", SqlDbType.Int).Value = model.SchedulePreventDuplicatesWindowHours;

        cmd.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;

        var obj = await cmd.ExecuteScalarAsync();
        var templateId = Convert.ToInt32(obj);

        await UpsertScheduleForTemplateAsync(templateId, model, actorUserId);

        return templateId;
    }

    public async Task SetTemplateEnabledAsync(int templateId, bool enabled, int actorUserId)
    {
        await using var conn = CreateConn();
        await conn.OpenAsync();

        const string sqlT = @"
UPDATE dbo.TicketTemplates
SET IsEnabled=@Enabled,
    UpdatedByUserId=@ActorUserId,
    UpdatedAt=SYSUTCDATETIME()
WHERE TemplateId=@Id;";

        await using (var cmd = new SqlCommand(sqlT, conn))
        {
            cmd.Parameters.Add("@Enabled", SqlDbType.Bit).Value = enabled;
            cmd.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;
            cmd.Parameters.Add("@Id", SqlDbType.Int).Value = templateId;
            await cmd.ExecuteNonQueryAsync();
        }

        const string sqlS = @"
UPDATE dbo.TicketSchedules
SET IsEnabled=@Enabled,
    UpdatedByUserId=@ActorUserId,
    UpdatedAt=SYSUTCDATETIME()
WHERE TemplateId=@TemplateId;";

        await using (var cmd = new SqlCommand(sqlS, conn))
        {
            cmd.Parameters.Add("@Enabled", SqlDbType.Bit).Value = enabled;
            cmd.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;
            cmd.Parameters.Add("@TemplateId", SqlDbType.Int).Value = templateId;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task UpsertScheduleForTemplateAsync(int templateId, TicketTemplateDto t, int actorUserId)
    {
        const string sql = @"
DECLARE @BaseDate date;
DECLARE @NewNextRunAt datetime2(0);

SET @BaseDate = CAST(COALESCE(@StartAt, CAST(SYSUTCDATETIME() AS date)) AS date);
SET @NewNextRunAt = DATEADD(SECOND, DATEDIFF(SECOND, '00:00:00', @AtTime), CAST(@BaseDate AS datetime2(0)));

IF (@NewNextRunAt < SYSUTCDATETIME())
    SET @NewNextRunAt = DATEADD(DAY, 1, @NewNextRunAt);

IF EXISTS (SELECT 1 FROM dbo.TicketSchedules WHERE TemplateId = @TemplateId)
BEGIN
    UPDATE dbo.TicketSchedules
    SET
        Name=@Name,
        IsEnabled=@IsEnabled,
        OwnerUserId=@OwnerUserId,
        OwnerSamAccountName=@OwnerSamAccountName,
        StartAt=@StartAt,
        EndAt=@EndAt,
        AtTime=@AtTime,
        TimeZoneId=@TimeZoneId,
        FrequencyType=@FrequencyType,
        IntervalValue=@IntervalValue,
        DaysOfWeekMask=@DaysOfWeekMask,
        DayOfMonth=@DayOfMonth,
        SkipWeekends=@SkipWeekends,
        MoveToNextBusinessDay=@MoveToNextBusinessDay,
        PreventDuplicatesWindowHours=@PreventDuplicatesWindowHours,
        NextRunAt = CASE WHEN NextRunAt IS NULL THEN @NewNextRunAt ELSE NextRunAt END,
        UpdatedByUserId=@ActorUserId,
        UpdatedAt=SYSUTCDATETIME()
    WHERE TemplateId=@TemplateId;
END
ELSE
BEGIN
    INSERT INTO dbo.TicketSchedules
    (
        Name, IsEnabled, TemplateId,
        OwnerUserId, OwnerSamAccountName,
        StartAt, EndAt, AtTime, TimeZoneId,
        FrequencyType, IntervalValue, DaysOfWeekMask, DayOfMonth,
        SkipWeekends, MoveToNextBusinessDay, PreventDuplicatesWindowHours,
        LastRunAt, NextRunAt,
        CreatedByUserId, CreatedAt, UpdatedByUserId, UpdatedAt
    )
    VALUES
    (
        @Name, @IsEnabled, @TemplateId,
        @OwnerUserId, @OwnerSamAccountName,
        @StartAt, @EndAt, @AtTime, @TimeZoneId,
        @FrequencyType, @IntervalValue, @DaysOfWeekMask, @DayOfMonth,
        @SkipWeekends, @MoveToNextBusinessDay, @PreventDuplicatesWindowHours,
        NULL, @NewNextRunAt,
        @ActorUserId, SYSUTCDATETIME(), @ActorUserId, SYSUTCDATETIME()
    );
END";

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.Add("@TemplateId", SqlDbType.Int).Value = templateId;

        var scheduleName = !string.IsNullOrWhiteSpace(t.ScheduleName) ? t.ScheduleName!.Trim() : (t.Name ?? "").Trim();
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = scheduleName;

        cmd.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = t.ScheduleIsEnabled;
        cmd.Parameters.Add("@OwnerUserId", SqlDbType.Int).Value = (object?)t.ScheduleOwnerUserId ?? actorUserId;
        cmd.Parameters.Add("@OwnerSamAccountName", SqlDbType.NVarChar, 80).Value = (object?)NullIfEmpty(t.ScheduleOwnerSamAccountName) ?? DBNull.Value;

        cmd.Parameters.Add("@StartAt", SqlDbType.DateTime2).Value = t.ScheduleStartAt;
        cmd.Parameters.Add("@EndAt", SqlDbType.DateTime2).Value = (object?)t.ScheduleEndAt ?? DBNull.Value;

        cmd.Parameters.Add("@AtTime", SqlDbType.Time).Value = t.ScheduleAtTime;
        cmd.Parameters.Add("@TimeZoneId", SqlDbType.NVarChar, 64).Value = (object?)NullIfEmpty(t.ScheduleTimeZoneId) ?? DBNull.Value;

        cmd.Parameters.Add("@FrequencyType", SqlDbType.TinyInt).Value = t.ScheduleFrequencyType;
        cmd.Parameters.Add("@IntervalValue", SqlDbType.Int).Value = t.ScheduleIntervalValue;

        cmd.Parameters.Add("@DaysOfWeekMask", SqlDbType.Int).Value = (object?)t.ScheduleDaysOfWeekMask ?? DBNull.Value;
        cmd.Parameters.Add("@DayOfMonth", SqlDbType.Int).Value = (object?)t.ScheduleDayOfMonth ?? DBNull.Value;

        cmd.Parameters.Add("@SkipWeekends", SqlDbType.Bit).Value = t.ScheduleSkipWeekends;
        cmd.Parameters.Add("@MoveToNextBusinessDay", SqlDbType.Bit).Value = t.ScheduleMoveToNextBusinessDay;

        cmd.Parameters.Add("@PreventDuplicatesWindowHours", SqlDbType.Int).Value = t.SchedulePreventDuplicatesWindowHours;

        cmd.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;

        await cmd.ExecuteNonQueryAsync();
    }

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ----------------------------
    // Schedules (pozostają, ale UI ich nie pokazuje)
    // ----------------------------

    public async Task<List<TicketScheduleDto>> GetSchedulesAsync()
    {
        const string sql = @"
SELECT
    s.ScheduleId,
    s.Name,
    s.IsEnabled,
    s.TemplateId,
    s.OwnerUserId,
    s.OwnerSamAccountName,
    s.StartAt,
    s.EndAt,
    s.AtTime,
    s.TimeZoneId,
    s.FrequencyType,
    s.IntervalValue,
    s.DaysOfWeekMask,
    s.DayOfMonth,
    s.SkipWeekends,
    s.MoveToNextBusinessDay,
    s.PreventDuplicatesWindowHours,
    s.LastRunAt,
    s.NextRunAt,
    s.CreatedAt,
    s.CreatedByUserId,
    s.UpdatedAt,
    s.UpdatedByUserId
FROM dbo.TicketSchedules s
ORDER BY s.Name ASC;";

        var list = new List<TicketScheduleDto>();

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();

        while (await rd.ReadAsync())
            list.Add(TicketScheduleDto.FromReader(rd));

        return list;
    }

    public async Task<TicketScheduleDto?> GetScheduleAsync(int scheduleId)
    {
        const string sql = @"
SELECT
    s.ScheduleId,
    s.Name,
    s.IsEnabled,
    s.TemplateId,
    s.OwnerUserId,
    s.OwnerSamAccountName,
    s.StartAt,
    s.EndAt,
    s.AtTime,
    s.TimeZoneId,
    s.FrequencyType,
    s.IntervalValue,
    s.DaysOfWeekMask,
    s.DayOfMonth,
    s.SkipWeekends,
    s.MoveToNextBusinessDay,
    s.PreventDuplicatesWindowHours,
    s.LastRunAt,
    s.NextRunAt,
    s.CreatedAt,
    s.CreatedByUserId,
    s.UpdatedAt,
    s.UpdatedByUserId
FROM dbo.TicketSchedules s
WHERE s.ScheduleId=@Id;";

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = scheduleId;

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;

        return TicketScheduleDto.FromReader(rd);
    }

    public async Task<int> UpsertScheduleAsync(TicketScheduleDto model, int actorUserId)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new InvalidOperationException("Name is required.");
        if (model.TemplateId <= 0)
            throw new InvalidOperationException("TemplateId is required.");
        if (model.OwnerUserId <= 0)
            throw new InvalidOperationException("OwnerUserId is required.");
        if (model.IntervalValue <= 0)
            model.IntervalValue = 1;

        const string sql = @"
DECLARE @BaseDate date;
DECLARE @NewNextRunAt datetime2(0);

SET @BaseDate = CAST(COALESCE(@StartAt, CAST(SYSUTCDATETIME() AS date)) AS date);
SET @NewNextRunAt = DATEADD(SECOND, DATEDIFF(SECOND, '00:00:00', @AtTime), CAST(@BaseDate AS datetime2(0)));

IF (@NewNextRunAt < SYSUTCDATETIME())
    SET @NewNextRunAt = DATEADD(DAY, 1, @NewNextRunAt);

IF EXISTS (SELECT 1 FROM dbo.TicketSchedules WHERE ScheduleId = @ScheduleId)
BEGIN
    UPDATE dbo.TicketSchedules
    SET
        Name=@Name,
        IsEnabled=@IsEnabled,
        TemplateId=@TemplateId,
        OwnerUserId=@OwnerUserId,
        OwnerSamAccountName=@OwnerSamAccountName,
        StartAt=@StartAt,
        EndAt=@EndAt,
        AtTime=@AtTime,
        TimeZoneId=@TimeZoneId,
        FrequencyType=@FrequencyType,
        IntervalValue=@IntervalValue,
        DaysOfWeekMask=@DaysOfWeekMask,
        DayOfMonth=@DayOfMonth,
        SkipWeekends=@SkipWeekends,
        MoveToNextBusinessDay=@MoveToNextBusinessDay,
        PreventDuplicatesWindowHours=@PreventDuplicatesWindowHours,
        NextRunAt = CASE WHEN NextRunAt IS NULL THEN @NewNextRunAt ELSE NextRunAt END,
        UpdatedByUserId=@ActorUserId,
        UpdatedAt=SYSUTCDATETIME()
    WHERE ScheduleId=@ScheduleId;

    SELECT @ScheduleId;
END
ELSE
BEGIN
    INSERT INTO dbo.TicketSchedules
    (
        Name, IsEnabled, TemplateId,
        OwnerUserId, OwnerSamAccountName,
        StartAt, EndAt, AtTime, TimeZoneId,
        FrequencyType, IntervalValue, DaysOfWeekMask, DayOfMonth,
        SkipWeekends, MoveToNextBusinessDay, PreventDuplicatesWindowHours,
        LastRunAt, NextRunAt,
        CreatedByUserId, CreatedAt, UpdatedByUserId, UpdatedAt
    )
    VALUES
    (
        @Name, @IsEnabled, @TemplateId,
        @OwnerUserId, @OwnerSamAccountName,
        @StartAt, @EndAt, @AtTime, @TimeZoneId,
        @FrequencyType, @IntervalValue, @DaysOfWeekMask, @DayOfMonth,
        @SkipWeekends, @MoveToNextBusinessDay, @PreventDuplicatesWindowHours,
        NULL, @NewNextRunAt,
        @ActorUserId, SYSUTCDATETIME(), @ActorUserId, SYSUTCDATETIME()
    );

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END";

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.Add("@ScheduleId", SqlDbType.Int).Value = model.ScheduleId;
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = (model.Name ?? "").Trim();
        cmd.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = model.IsEnabled;

        cmd.Parameters.Add("@TemplateId", SqlDbType.Int).Value = model.TemplateId;

        cmd.Parameters.Add("@OwnerUserId", SqlDbType.Int).Value = model.OwnerUserId;
        cmd.Parameters.Add("@OwnerSamAccountName", SqlDbType.NVarChar, 256).Value = (object?)NullIfEmpty(model.OwnerSamAccountName) ?? DBNull.Value;

        cmd.Parameters.Add("@StartAt", SqlDbType.DateTime2).Value = model.StartAt;
        cmd.Parameters.Add("@EndAt", SqlDbType.DateTime2).Value = (object?)model.EndAt ?? DBNull.Value;

        cmd.Parameters.Add("@AtTime", SqlDbType.Time).Value = model.AtTime;
        cmd.Parameters.Add("@TimeZoneId", SqlDbType.NVarChar, 100).Value = (object?)NullIfEmpty(model.TimeZoneId) ?? DBNull.Value;

        cmd.Parameters.Add("@FrequencyType", SqlDbType.TinyInt).Value = model.FrequencyType;
        cmd.Parameters.Add("@IntervalValue", SqlDbType.Int).Value = model.IntervalValue;
        cmd.Parameters.Add("@DaysOfWeekMask", SqlDbType.Int).Value = (object?)model.DaysOfWeekMask ?? DBNull.Value;
        cmd.Parameters.Add("@DayOfMonth", SqlDbType.Int).Value = (object?)model.DayOfMonth ?? DBNull.Value;

        cmd.Parameters.Add("@SkipWeekends", SqlDbType.Bit).Value = model.SkipWeekends;
        cmd.Parameters.Add("@MoveToNextBusinessDay", SqlDbType.Bit).Value = model.MoveToNextBusinessDay;
        cmd.Parameters.Add("@PreventDuplicatesWindowHours", SqlDbType.Int).Value = model.PreventDuplicatesWindowHours;

        cmd.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;

        var obj = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(obj);
    }

    public async Task SetScheduleEnabledAsync(int scheduleId, bool enabled, int actorUserId)
    {
        const string sql = @"
UPDATE dbo.TicketSchedules
SET IsEnabled=@Enabled,
    UpdatedByUserId=@ActorUserId,
    UpdatedAt=SYSUTCDATETIME()
WHERE ScheduleId=@Id;";

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.Add("@Enabled", SqlDbType.Bit).Value = enabled;
        cmd.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = scheduleId;

        await cmd.ExecuteNonQueryAsync();
    }

    // ----------------------------
    // Runs
    // ----------------------------

    public async Task<List<TicketScheduleRunDto>> GetRecentRunsAsync(int top)
    {
        const string sql = @"
SELECT TOP (@Top)
    r.RunId,
    r.ScheduleId,
    r.RunAt,
    r.Status,
    r.CreatedTicketId,
    r.Message,
    r.DurationMs,
    s.Name AS ScheduleName
FROM dbo.TicketScheduleRuns r
INNER JOIN dbo.TicketSchedules s ON s.ScheduleId = r.ScheduleId
ORDER BY r.RunAt DESC;";

        var list = new List<TicketScheduleRunDto>();

        await using var conn = CreateConn();
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Top", SqlDbType.Int).Value = top;

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(TicketScheduleRunDto.FromReader(rd));

        return list;
    }
}

/* Lookup DTO */

public sealed class LookupItem<T>
{
    public T Id { get; set; } = default!;
    public string Name { get; set; } = "";
}

public sealed class WeightedLookupItem<T>
{
    public T Id { get; set; } = default!;
    public string Name { get; set; } = "";
    public int Weight { get; set; }
}

/* DTO */

public sealed class TicketTemplateDto
{
    public int TemplateId { get; set; }

    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }

    public string TitleTemplate { get; set; } = "";
    public string? DescriptionTemplate { get; set; }

    public int? CategoryId { get; set; }
    public int? PriorityId { get; set; }

    public byte? Impact { get; set; }
    public byte? Urgency { get; set; }

    public int? QueueId { get; set; }
    public int? AssignedToUserId { get; set; }
    public int? AssignedToGroupId { get; set; }
    public int? DefaultDueInHours { get; set; }

    // Schedule config moved to Template
    public string? ScheduleName { get; set; }
    public bool ScheduleIsEnabled { get; set; } = true;

    public int? ScheduleOwnerUserId { get; set; }
    public string? ScheduleOwnerSamAccountName { get; set; }

    public DateTime ScheduleStartAt { get; set; } = DateTime.Today;
    public DateTime? ScheduleEndAt { get; set; }

    public TimeSpan ScheduleAtTime { get; set; } = new TimeSpan(8, 0, 0);
    public string? ScheduleTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    // FIX: pełna kwalifikacja enuma (konflikt nazwy z property)
    public byte ScheduleFrequencyType { get; set; }
        = (byte)global::ITManager.Services.Scheduling.ScheduleFrequencyType.Monthly;

    public int ScheduleIntervalValue { get; set; } = 1;

    public int? ScheduleDaysOfWeekMask { get; set; }
    public int? ScheduleDayOfMonth { get; set; } = 1;

    public bool ScheduleSkipWeekends { get; set; } = true;
    public bool ScheduleMoveToNextBusinessDay { get; set; } = true;

    public int SchedulePreventDuplicatesWindowHours { get; set; } = 12;

    public DateTime? CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }

    public static TicketTemplateDto CreateNewDefault(int actorUserId)
        => new TicketTemplateDto
        {
            IsEnabled = true,
            Name = "",
            TitleTemplate = "",
            DescriptionTemplate = "Utworzone: {Now}",

            ScheduleName = "",
            ScheduleIsEnabled = true,
            ScheduleOwnerUserId = actorUserId,
            ScheduleStartAt = DateTime.Today,
            ScheduleEndAt = null,
            ScheduleAtTime = new TimeSpan(8, 0, 0),
            ScheduleTimeZoneId = TimeZoneInfo.Local.Id,

            // FIX: pełna kwalifikacja enuma
            ScheduleFrequencyType = (byte)global::ITManager.Services.Scheduling.ScheduleFrequencyType.Monthly,

            ScheduleIntervalValue = 1,
            ScheduleDayOfMonth = 1,
            ScheduleSkipWeekends = true,
            ScheduleMoveToNextBusinessDay = true,
            SchedulePreventDuplicatesWindowHours = 12
        };

    public static TicketTemplateDto FromReader(SqlDataReader rd)
    {
        int Ord(string name) => rd.GetOrdinal(name);

        return new TicketTemplateDto
        {
            TemplateId = rd.GetInt32(Ord("TemplateId")),
            Name = rd.GetString(Ord("Name")),
            IsEnabled = rd.GetBoolean(Ord("IsEnabled")),

            TitleTemplate = rd.IsDBNull(Ord("TitleTemplate")) ? "" : rd.GetString(Ord("TitleTemplate")),
            DescriptionTemplate = rd.IsDBNull(Ord("DescriptionTemplate")) ? null : rd.GetString(Ord("DescriptionTemplate")),

            CategoryId = rd.IsDBNull(Ord("CategoryId")) ? null : rd.GetInt32(Ord("CategoryId")),
            PriorityId = rd.IsDBNull(Ord("PriorityId")) ? null : rd.GetInt32(Ord("PriorityId")),
            Impact = rd.IsDBNull(Ord("Impact")) ? null : rd.GetByte(Ord("Impact")),
            Urgency = rd.IsDBNull(Ord("Urgency")) ? null : rd.GetByte(Ord("Urgency")),

            QueueId = rd.IsDBNull(Ord("QueueId")) ? null : rd.GetInt32(Ord("QueueId")),
            AssignedToUserId = rd.IsDBNull(Ord("AssignedToUserId")) ? null : rd.GetInt32(Ord("AssignedToUserId")),
            AssignedToGroupId = rd.IsDBNull(Ord("AssignedToGroupId")) ? null : rd.GetInt32(Ord("AssignedToGroupId")),
            DefaultDueInHours = rd.IsDBNull(Ord("DefaultDueInHours")) ? null : rd.GetInt32(Ord("DefaultDueInHours")),

            ScheduleName = rd.IsDBNull(Ord("ScheduleName")) ? null : rd.GetString(Ord("ScheduleName")),
            ScheduleIsEnabled = !rd.IsDBNull(Ord("ScheduleIsEnabled")) && rd.GetBoolean(Ord("ScheduleIsEnabled")),

            ScheduleOwnerUserId = rd.IsDBNull(Ord("ScheduleOwnerUserId")) ? null : rd.GetInt32(Ord("ScheduleOwnerUserId")),
            ScheduleOwnerSamAccountName = rd.IsDBNull(Ord("ScheduleOwnerSamAccountName")) ? null : rd.GetString(Ord("ScheduleOwnerSamAccountName")),

            ScheduleStartAt = rd.IsDBNull(Ord("ScheduleStartAt")) ? DateTime.Today : rd.GetDateTime(Ord("ScheduleStartAt")),
            ScheduleEndAt = rd.IsDBNull(Ord("ScheduleEndAt")) ? null : rd.GetDateTime(Ord("ScheduleEndAt")),

            ScheduleAtTime = rd.IsDBNull(Ord("ScheduleAtTime")) ? new TimeSpan(8, 0, 0) : rd.GetTimeSpan(Ord("ScheduleAtTime")),
            ScheduleTimeZoneId = rd.IsDBNull(Ord("ScheduleTimeZoneId")) ? null : rd.GetString(Ord("ScheduleTimeZoneId")),

            // FIX: fallback z pełną kwalifikacją
            ScheduleFrequencyType = rd.IsDBNull(Ord("ScheduleFrequencyType"))
                ? (byte)global::ITManager.Services.Scheduling.ScheduleFrequencyType.Monthly
                : rd.GetByte(Ord("ScheduleFrequencyType")),

            ScheduleIntervalValue = rd.IsDBNull(Ord("ScheduleIntervalValue")) ? 1 : rd.GetInt32(Ord("ScheduleIntervalValue")),

            ScheduleDaysOfWeekMask = rd.IsDBNull(Ord("ScheduleDaysOfWeekMask")) ? null : rd.GetInt32(Ord("ScheduleDaysOfWeekMask")),
            ScheduleDayOfMonth = rd.IsDBNull(Ord("ScheduleDayOfMonth")) ? null : rd.GetInt32(Ord("ScheduleDayOfMonth")),

            ScheduleSkipWeekends = !rd.IsDBNull(Ord("ScheduleSkipWeekends")) && rd.GetBoolean(Ord("ScheduleSkipWeekends")),
            ScheduleMoveToNextBusinessDay = !rd.IsDBNull(Ord("ScheduleMoveToNextBusinessDay")) && rd.GetBoolean(Ord("ScheduleMoveToNextBusinessDay")),

            SchedulePreventDuplicatesWindowHours = rd.IsDBNull(Ord("SchedulePreventDuplicatesWindowHours")) ? 12 : rd.GetInt32(Ord("SchedulePreventDuplicatesWindowHours")),

            CreatedAt = rd.HasColumn("CreatedAt") && !rd.IsDBNull(Ord("CreatedAt")) ? rd.GetDateTime(Ord("CreatedAt")) : null,
            CreatedByUserId = rd.HasColumn("CreatedByUserId") && !rd.IsDBNull(Ord("CreatedByUserId")) ? rd.GetInt32(Ord("CreatedByUserId")) : null,
            UpdatedAt = rd.HasColumn("UpdatedAt") && !rd.IsDBNull(Ord("UpdatedAt")) ? rd.GetDateTime(Ord("UpdatedAt")) : null,
            UpdatedByUserId = rd.HasColumn("UpdatedByUserId") && !rd.IsDBNull(Ord("UpdatedByUserId")) ? rd.GetInt32(Ord("UpdatedByUserId")) : null,
        };
    }
}

public sealed class TicketScheduleDto
{
    public int ScheduleId { get; set; }

    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }

    public int TemplateId { get; set; }

    public int OwnerUserId { get; set; }
    public string? OwnerSamAccountName { get; set; }

    public DateTime StartAt { get; set; } = DateTime.Today;
    public DateTime? EndAt { get; set; }

    public TimeSpan AtTime { get; set; } = new TimeSpan(8, 0, 0);
    public string? TimeZoneId { get; set; }

    // FIX: pełna kwalifikacja enuma
    public byte FrequencyType { get; set; }
        = (byte)global::ITManager.Services.Scheduling.ScheduleFrequencyType.Monthly;

    public int IntervalValue { get; set; } = 1;

    public int? DaysOfWeekMask { get; set; }
    public int? DayOfMonth { get; set; }

    public bool SkipWeekends { get; set; }
    public bool MoveToNextBusinessDay { get; set; }

    public int PreventDuplicatesWindowHours { get; set; } = 12;

    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }

    public static TicketScheduleDto FromReader(SqlDataReader rd)
    {
        int Ord(string name) => rd.GetOrdinal(name);

        return new TicketScheduleDto
        {
            ScheduleId = rd.GetInt32(Ord("ScheduleId")),
            Name = rd.GetString(Ord("Name")),
            IsEnabled = rd.GetBoolean(Ord("IsEnabled")),
            TemplateId = rd.GetInt32(Ord("TemplateId")),

            OwnerUserId = rd.GetInt32(Ord("OwnerUserId")),
            OwnerSamAccountName = rd.IsDBNull(Ord("OwnerSamAccountName")) ? null : rd.GetString(Ord("OwnerSamAccountName")),

            StartAt = rd.GetDateTime(Ord("StartAt")),
            EndAt = rd.IsDBNull(Ord("EndAt")) ? null : rd.GetDateTime(Ord("EndAt")),

            AtTime = rd.GetTimeSpan(Ord("AtTime")),
            TimeZoneId = rd.IsDBNull(Ord("TimeZoneId")) ? null : rd.GetString(Ord("TimeZoneId")),

            FrequencyType = rd.GetByte(Ord("FrequencyType")),
            IntervalValue = rd.GetInt32(Ord("IntervalValue")),

            DaysOfWeekMask = rd.IsDBNull(Ord("DaysOfWeekMask")) ? null : rd.GetInt32(Ord("DaysOfWeekMask")),
            DayOfMonth = rd.IsDBNull(Ord("DayOfMonth")) ? null : rd.GetInt32(Ord("DayOfMonth")),

            SkipWeekends = rd.GetBoolean(Ord("SkipWeekends")),
            MoveToNextBusinessDay = rd.GetBoolean(Ord("MoveToNextBusinessDay")),

            PreventDuplicatesWindowHours = rd.GetInt32(Ord("PreventDuplicatesWindowHours")),

            LastRunAt = rd.IsDBNull(Ord("LastRunAt")) ? null : rd.GetDateTime(Ord("LastRunAt")),
            NextRunAt = rd.IsDBNull(Ord("NextRunAt")) ? null : rd.GetDateTime(Ord("NextRunAt")),

            CreatedAt = rd.HasColumn("CreatedAt") && !rd.IsDBNull(Ord("CreatedAt")) ? rd.GetDateTime(Ord("CreatedAt")) : null,
            CreatedByUserId = rd.HasColumn("CreatedByUserId") && !rd.IsDBNull(Ord("CreatedByUserId")) ? rd.GetInt32(Ord("CreatedByUserId")) : null,
            UpdatedAt = rd.HasColumn("UpdatedAt") && !rd.IsDBNull(Ord("UpdatedAt")) ? rd.GetDateTime(Ord("UpdatedAt")) : null,
            UpdatedByUserId = rd.HasColumn("UpdatedByUserId") && !rd.IsDBNull(Ord("UpdatedByUserId")) ? rd.GetInt32(Ord("UpdatedByUserId")) : null,
        };
    }
}

public sealed class TicketScheduleRunDto
{
    public long RunId { get; set; }
    public int ScheduleId { get; set; }
    public string ScheduleName { get; set; } = "";

    public DateTime RunAt { get; set; }

    public string Status { get; set; } = "Unknown";

    public long? CreatedTicketId { get; set; }
    public string? Message { get; set; }
    public int? DurationMs { get; set; }

    public static TicketScheduleRunDto FromReader(SqlDataReader rd)
    {
        int Ord(string name) => rd.GetOrdinal(name);

        var statusByte = rd.IsDBNull(Ord("Status")) ? (byte)0 : rd.GetByte(Ord("Status"));
        var statusText = statusByte switch
        {
            1 => "Created",
            2 => "Skipped",
            3 => "Failed",
            _ => "Unknown"
        };

        return new TicketScheduleRunDto
        {
            RunId = rd.GetInt64(Ord("RunId")),
            ScheduleId = rd.GetInt32(Ord("ScheduleId")),
            ScheduleName = rd.IsDBNull(Ord("ScheduleName")) ? "" : rd.GetString(Ord("ScheduleName")),
            RunAt = rd.GetDateTime(Ord("RunAt")),
            Status = statusText,
            CreatedTicketId = rd.IsDBNull(Ord("CreatedTicketId")) ? null : rd.GetInt64(Ord("CreatedTicketId")),
            Message = rd.IsDBNull(Ord("Message")) ? null : rd.GetString(Ord("Message")),
            DurationMs = rd.IsDBNull(Ord("DurationMs")) ? null : rd.GetInt32(Ord("DurationMs")),
        };
    }
}

/* Helpers */

internal static class SqlDataReaderExtensions
{
    public static bool HasColumn(this SqlDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
