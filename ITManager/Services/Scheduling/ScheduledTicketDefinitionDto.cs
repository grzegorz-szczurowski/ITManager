// Path: Services/Scheduling/ScheduledTicketDefinitionDto.cs
// File: ScheduledTicketDefinitionDto.cs
// Description: DTO dla dbo.ScheduledTicketDefinitions (UI + repo).
// Notes:
// - Wartości *Utc* powinny mieć DateTimeKind.Utc na wejściu/wyjściu.
// - FromReader ustawia Kind na Utc dla pól *Utc* (żeby uniknąć cichych konwersji).
// Created: 2026-01-03
// Updated: 2026-01-04 - Ustawianie DateTimeKind.Utc w FromReader.
// Updated: 2026-01-04 - FIX: bit fields czytane przez Convert.ToBoolean (stabilne dla bool/byte/int).
// Version: 1.02

using System;
using System.Data.SqlClient;

namespace ITManager.Services.Scheduling;

public sealed class ScheduledTicketDefinitionDto
{
    public int DefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }

    public string TitleTemplate { get; set; } = string.Empty;
    public string? DescriptionTemplate { get; set; }

    public int? CategoryId { get; set; }
    public byte? Impact { get; set; }
    public byte? Urgency { get; set; }
    public int? QueueId { get; set; }
    public int RequesterUserId { get; set; }

    public bool ScheduleIsEnabled { get; set; }
    public DateTime ScheduleStartAtUtc { get; set; }
    public DateTime? ScheduleEndAtUtc { get; set; }
    public TimeSpan ScheduleAtTime { get; set; }
    public string? ScheduleTimeZoneId { get; set; }

    public byte ScheduleFrequencyType { get; set; }
    public int ScheduleIntervalValue { get; set; }
    public int? ScheduleDaysOfWeekMask { get; set; }
    public int? ScheduleDayOfMonth { get; set; }
    public int? ScheduleMonthOfYear { get; set; }

    public bool ScheduleSkipWeekends { get; set; }
    public bool ScheduleMoveToNextBusinessDay { get; set; }

    public DateTime? ScheduleLastRunAtUtc { get; set; }
    public DateTime? ScheduleNextRunAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int UpdatedByUserId { get; set; }

    private static DateTime Utc(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    private static DateTime? UtcN(DateTime? dt) => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : null;

    private static bool Bool(SqlDataReader rd, string col)
        => Convert.ToBoolean(rd[col]);

    public static ScheduledTicketDefinitionDto FromReader(SqlDataReader rd)
    {
        int O(string n) => rd.GetOrdinal(n);

        var startUtc = rd.GetDateTime(O("ScheduleStartAtUtc"));
        DateTime? endUtc = rd.IsDBNull(O("ScheduleEndAtUtc")) ? null : rd.GetDateTime(O("ScheduleEndAtUtc"));
        DateTime? lastRunUtc = rd.IsDBNull(O("ScheduleLastRunAtUtc")) ? null : rd.GetDateTime(O("ScheduleLastRunAtUtc"));
        DateTime? nextRunUtc = rd.IsDBNull(O("ScheduleNextRunAtUtc")) ? null : rd.GetDateTime(O("ScheduleNextRunAtUtc"));

        var createdUtc = rd.GetDateTime(O("CreatedAtUtc"));
        var updatedUtc = rd.GetDateTime(O("UpdatedAtUtc"));

        return new ScheduledTicketDefinitionDto
        {
            DefinitionId = rd.GetInt32(O("DefinitionId")),
            Name = rd.GetString(O("Name")),
            IsEnabled = Bool(rd, "IsEnabled"),

            TitleTemplate = rd.GetString(O("TitleTemplate")),
            DescriptionTemplate = rd.IsDBNull(O("DescriptionTemplate")) ? null : rd.GetString(O("DescriptionTemplate")),

            CategoryId = rd.IsDBNull(O("CategoryId")) ? null : rd.GetInt32(O("CategoryId")),
            Impact = rd.IsDBNull(O("Impact")) ? null : rd.GetByte(O("Impact")),
            Urgency = rd.IsDBNull(O("Urgency")) ? null : rd.GetByte(O("Urgency")),
            QueueId = rd.IsDBNull(O("QueueId")) ? null : rd.GetInt32(O("QueueId")),
            RequesterUserId = rd.GetInt32(O("RequesterUserId")),

            ScheduleIsEnabled = Bool(rd, "ScheduleIsEnabled"),
            ScheduleStartAtUtc = Utc(startUtc),
            ScheduleEndAtUtc = UtcN(endUtc),
            ScheduleAtTime = rd.GetTimeSpan(O("ScheduleAtTime")),
            ScheduleTimeZoneId = rd.IsDBNull(O("ScheduleTimeZoneId")) ? null : rd.GetString(O("ScheduleTimeZoneId")),

            ScheduleFrequencyType = rd.GetByte(O("ScheduleFrequencyType")),
            ScheduleIntervalValue = rd.GetInt32(O("ScheduleIntervalValue")),
            ScheduleDaysOfWeekMask = rd.IsDBNull(O("ScheduleDaysOfWeekMask")) ? null : rd.GetInt32(O("ScheduleDaysOfWeekMask")),
            ScheduleDayOfMonth = rd.IsDBNull(O("ScheduleDayOfMonth")) ? null : rd.GetInt32(O("ScheduleDayOfMonth")),
            ScheduleMonthOfYear = rd.IsDBNull(O("ScheduleMonthOfYear")) ? null : rd.GetInt32(O("ScheduleMonthOfYear")),

            ScheduleSkipWeekends = Bool(rd, "ScheduleSkipWeekends"),
            ScheduleMoveToNextBusinessDay = Bool(rd, "ScheduleMoveToNextBusinessDay"),

            ScheduleLastRunAtUtc = UtcN(lastRunUtc),
            ScheduleNextRunAtUtc = UtcN(nextRunUtc),

            CreatedAtUtc = Utc(createdUtc),
            CreatedByUserId = rd.GetInt32(O("CreatedByUserId")),
            UpdatedAtUtc = Utc(updatedUtc),
            UpdatedByUserId = rd.GetInt32(O("UpdatedByUserId")),
        };
    }
}
