// File: Services/Notifications/NotificationsRepository.cs
// Description: Repozytorium ADO.NET dla in-app notifications (dbo.UserNotifications).
// Notes:
//   - Repo nie robi RBAC (to robi NotificationsService).
//   - Wszystkie czasy zapisujemy w UTC.
// Version: 1.11
// Updated: 2026-02-12
// Change log:
//   - 1.10 (2026-02-11) Initial version.
//   - 1.11 (2026-02-12) FIX: MapRow: TicketId jako long?, TicketEventId non-null, EventType ustawiany, TicketTitle null-safe.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using ITManager.Models.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace ITManager.Services.Notifications;

public sealed class NotificationsRepository
{
    // EventType wartości muszą odpowiadać temu, co zapisujesz w tabeli TicketEvents (tinyint).
    // Tu trzymamy minimum wymagane przez UI.
    private const byte EventTypeTicketPublicCreated = 10;
    private const byte EventTypeTicketPublicCommentAdded = 11;

    private readonly string _cs;
    private readonly ILogger<NotificationsRepository> _logger;

    public NotificationsRepository(IConfiguration configuration, ILogger<NotificationsRepository> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        _cs = configuration.GetConnectionString("ITManagerConnection")
              ?? throw new InvalidOperationException("Brak connection stringa 'ITManagerConnection'.");
    }

    public async Task<int> GetUnreadCountAsync(int userId, CancellationToken ct = default)
    {
        const string sql = @"
select count(1)
from dbo.UserNotifications n
where n.UserId = @UserId
  and n.IsRead = 0;";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;

        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is int i ? i : Convert.ToInt32(obj);
    }

    public async Task<IReadOnlyList<NotificationRow>> GetLatestAsync(int userId, int top, CancellationToken ct = default)
    {
        const string sql = @"
select top (@Top)
    n.Id,
    n.UserId,
    n.CreatedAtUtc,
    n.IsRead,
    n.ReadAtUtc,
    n.EventType,
    n.TicketId,
    t.ticket_code as TicketNumber,
    t.ticket_title as TicketTitle,
    n.TicketEventId
from dbo.UserNotifications n
left join dbo.vw_tickets t on t.id = n.TicketId
where n.UserId = @UserId
order by n.CreatedAtUtc desc, n.Id desc;";

        var list = new List<NotificationRow>(Math.Min(top, 200));

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@Top", SqlDbType.Int).Value = top;

        // WAŻNE: bez SequentialAccess, bo czytamy kilka małych pól i chcemy stabilności.
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.Default, ct);

        while (await r.ReadAsync(ct))
        {
            list.Add(MapRow(r));
        }

        return list;
    }


    public async Task<int> MarkAsReadAsync(long notificationId, int userId, CancellationToken ct = default)
    {
        const string sql = @"
update dbo.UserNotifications
set IsRead = 1,
    ReadAtUtc = coalesce(ReadAtUtc, sysutcdatetime())
where Id = @Id
  and UserId = @UserId
  and IsRead = 0;";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = notificationId;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> MarkAllAsReadAsync(int userId, CancellationToken ct = default)
    {
        const string sql = @"
update dbo.UserNotifications
set IsRead = 1,
    ReadAtUtc = coalesce(ReadAtUtc, sysutcdatetime())
where UserId = @UserId
  and IsRead = 0;";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CreateForRecipientsAsync(
        long ticketEventId,
        long? ticketId,
        byte eventType,
        IReadOnlyList<int> recipientUserIds,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        if (recipientUserIds is null || recipientUserIds.Count == 0)
            return 0;

        const string sql = @"
insert into dbo.UserNotifications
(
    UserId,
    CreatedAtUtc,
    IsRead,
    ReadAtUtc,
    EventType,
    TicketId,
    TicketEventId
)
values
(
    @UserId,
    @CreatedAtUtc,
    0,
    null,
    @EventType,
    @TicketId,
    @TicketEventId
);";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);

        await using var tx = con.BeginTransaction();

        try
        {
            var inserted = 0;

            foreach (var userId in recipientUserIds)
            {
                await using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;

                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                cmd.Parameters.Add("@CreatedAtUtc", SqlDbType.DateTime2).Value = createdAtUtc;
                cmd.Parameters.Add("@EventType", SqlDbType.TinyInt).Value = eventType;
                cmd.Parameters.Add("@TicketId", SqlDbType.BigInt).Value = (object?)ticketId ?? DBNull.Value;
                cmd.Parameters.Add("@TicketEventId", SqlDbType.BigInt).Value = ticketEventId;

                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected > 0) inserted += 1;
            }

            tx.Commit();
            return inserted;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            _logger.LogError(ex, "NotificationsRepository.CreateForRecipientsAsync failed (TicketEventId={TicketEventId}, TicketId={TicketId})", ticketEventId, ticketId);
            throw;
        }
    }

    private static NotificationRow MapRow(SqlDataReader r)
    {
        // Kolumny z GetLatestAsync:
        // 0 Id (bigint)
        // 1 UserId (int)
        // 2 CreatedAtUtc (datetime2)
        // 3 IsRead (bit)
        // 4 ReadAtUtc (datetime2 null)
        // 5 EventType (tinyint)
        // 6 TicketId (bigint null)
        // 7 TicketNumber (nvarchar null)
        // 8 TicketTitle (nvarchar null)
        // 9 TicketEventId (bigint, NOT NULL)
        var eventType = r.IsDBNull(5) ? (byte)0 : r.GetByte(5);

        return new NotificationRow
        {
            Id = r.GetInt64(0),
            UserId = r.GetInt32(1),
            CreatedAtUtc = r.GetDateTime(2),
            IsRead = r.GetBoolean(3),
            ReadAtUtc = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4),
            EventType = eventType,
            TypeCode = MapEventTypeToTypeCode(eventType),
            TicketId = r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
            TicketNumber = r.IsDBNull(7) ? null : r.GetString(7),
            TicketTitle = r.IsDBNull(8) ? string.Empty : r.GetString(8),
            TicketEventId = r.GetInt64(9)
        };
    }

    private static string MapEventTypeToTypeCode(byte eventType)
    {
        // Stabilne kody dla UI (resx/labelki)
        return eventType switch
        {
            EventTypeTicketPublicCreated => "Ticket.PublicEvent.Created",
            EventTypeTicketPublicCommentAdded => "Ticket.PublicEvent.CommentAdded",
            _ => "Unknown"
        };
    }
}
