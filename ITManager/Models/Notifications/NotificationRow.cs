// File: Models/Notifications/NotificationRow.cs
// Description: DTO dla listy powiadomień (in-app).
// Notes:
//   - Dopasowane do dbo.UserNotifications gdzie EventType jest tinyint i TicketEventId jest NOT NULL.
//   - TicketNumber i TicketTitle są dociągane przez join do dbo.vw_tickets (nie są przechowywane w UserNotifications).
// Version: 1.02
// Updated: 2026-02-12
// Change log:
//   - 1.01 (2026-02-12) FIX: TicketId jako long, dodany EventType (tinyint) + TypeCode wyliczany w repo.
//   - 1.02 (2026-02-12) FIX: HasTicketLink (UI) + wzmocnione null safety.

using System;

namespace ITManager.Models.Notifications;

public sealed class NotificationRow
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public long? TicketId { get; set; }

    public long TicketEventId { get; set; }

    public byte EventType { get; set; }

    // Kod logiczny używany w UI do labelki i routingu
    public string TypeCode { get; set; } = string.Empty;

    // Dane prezentacyjne, dociągane z vw_tickets
    public string? TicketNumber { get; set; }

    public string TicketTitle { get; set; } = string.Empty;

    // UI helper: czy mamy sensowny link do ticketa
    public bool HasTicketLink => TicketId.HasValue && TicketId.Value > 0;

    public bool IsRead { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ReadAtUtc { get; set; }
}
