// File: Services/Tickets/TicketEventsRepository.cs
// Description: Repozytorium do zapisu zdarzeń ticketowych (outbox) do tabeli dbo.TicketEvents.
// Notes:
//   - Zdarzenia zapisujemy w tej samej transakcji co zmiana danych ticketa (atomiczność).
//   - Repo ma overloady z SqlConnection/SqlTransaction, żeby nie rozrywać transakcji komend.
//   - Insert zwraca Id nowo utworzonego zdarzenia (przydatne pod powiązane powiadomienia in-app).
//   - Tabela dbo.TicketEvents jest wymagana (skrypt na dole pliku).
// Created: 2026-02-07
// Version: 1.10
// Updated: 2026-02-11
// Change log:
//   - 1.10 (2026-02-11) Insert zwraca EventId, parametry bez AddWithValue, bezpieczne limity i walidacje.

using System;
using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ITManager.Services.Tickets;

public enum TicketEventType : byte
{
    Created = 1,
    Assigned = 2,
    ActionAdded = 3,
    ActionUpdated = 4
}

public sealed class TicketEventsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<long> InsertEventAsync(
        SqlConnection conn,
        SqlTransaction tx,
        TicketEventType eventType,
        long ticketId,
        int byUserId,
        string? byDisplayName,
        object? payload,
        CancellationToken ct)
    {
        if (conn == null) throw new ArgumentNullException(nameof(conn));
        if (tx == null) throw new ArgumentNullException(nameof(tx));

        return InsertEventInternalAsync(conn, tx, eventType, ticketId, byUserId, byDisplayName, payload, ct);
    }

    public Task<long> InsertEventAsync(
        SqlConnection conn,
        TicketEventType eventType,
        long ticketId,
        int byUserId,
        string? byDisplayName,
        object? payload,
        CancellationToken ct)
    {
        if (conn == null) throw new ArgumentNullException(nameof(conn));

        return InsertEventInternalAsync(conn, tx: null, eventType, ticketId, byUserId, byDisplayName, payload, ct);
    }

    private static async Task<long> InsertEventInternalAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        TicketEventType eventType,
        long ticketId,
        int byUserId,
        string? byDisplayName,
        object? payload,
        CancellationToken ct)
    {
        if (ticketId <= 0) throw new ArgumentOutOfRangeException(nameof(ticketId), "TicketId musi być > 0.");
        if (byUserId <= 0) throw new ArgumentOutOfRangeException(nameof(byUserId), "ByUserId musi być > 0.");

        var displayName = (byDisplayName ?? string.Empty).Trim();
        if (displayName.Length > 200)
        {
            displayName = displayName.Substring(0, 200);
        }

        string? payloadJson = null;
        if (payload != null)
        {
            payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        }

        const string sql = @"
INSERT INTO dbo.TicketEvents
(
    TicketId,
    EventType,
    OccurredAtUtc,
    ByUserId,
    ByDisplayName,
    PayloadJson
)
VALUES
(
    @TicketId,
    @EventType,
    SYSUTCDATETIME(),
    @ByUserId,
    @ByDisplayName,
    @PayloadJson
);

SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        await using var cmd = new SqlCommand(sql, conn, tx);

        var pTicketId = cmd.Parameters.Add("@TicketId", SqlDbType.BigInt);
        pTicketId.Value = ticketId;

        var pEventType = cmd.Parameters.Add("@EventType", SqlDbType.TinyInt);
        pEventType.Value = (byte)eventType;

        var pByUserId = cmd.Parameters.Add("@ByUserId", SqlDbType.Int);
        pByUserId.Value = byUserId;

        var pByDisplayName = cmd.Parameters.Add("@ByDisplayName", SqlDbType.NVarChar, 200);
        pByDisplayName.Value = displayName;

        var pPayload = cmd.Parameters.Add("@PayloadJson", SqlDbType.NVarChar, -1);
        pPayload.Value = payloadJson == null ? DBNull.Value : payloadJson;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result == null || result == DBNull.Value)
        {
            throw new InvalidOperationException("Nie udało się pobrać Id nowo utworzonego TicketEvent.");
        }

        return Convert.ToInt64(result);
    }
}

/*
Wymagany obiekt DB:

CREATE TABLE dbo.TicketEvents
(
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TicketEvents PRIMARY KEY,
    TicketId      BIGINT NOT NULL,
    EventType     TINYINT NOT NULL,
    OccurredAtUtc DATETIME2(0) NOT NULL,
    ByUserId      INT NOT NULL,
    ByDisplayName NVARCHAR(200) NOT NULL,
    PayloadJson   NVARCHAR(MAX) NULL
);

CREATE INDEX IX_TicketEvents_TicketId_OccurredAtUtc
ON dbo.TicketEvents(TicketId, OccurredAtUtc DESC);

-- Opcjonalnie, jeśli chcesz łatwo filtrować po typie:
CREATE INDEX IX_TicketEvents_EventType_OccurredAtUtc
ON dbo.TicketEvents(EventType, OccurredAtUtc DESC);
*/
