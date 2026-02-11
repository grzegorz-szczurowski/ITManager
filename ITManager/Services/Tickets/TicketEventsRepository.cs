// File: Services/Tickets/TicketEventsRepository.cs
// Description: Repozytorium do zapisu zdarzeń ticketowych (outbox) do tabeli dbo.TicketEvents.
// Notes:
//   - Zdarzenia zapisujemy w tej samej transakcji co zmiana danych ticketa (atomiczność).
//   - Repo ma overloady z SqlConnection/SqlTransaction, żeby nie rozrywać transakcji komend.
//   - Tabela dbo.TicketEvents jest wymagana (skrypt na dole pliku).
// Created: 2026-02-07
// Version: 1.00

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

    public async Task InsertEventAsync(
        SqlConnection conn,
        SqlTransaction tx,
        TicketEventType eventType,
        long ticketId,
        int byUserId,
        string byDisplayName,
        object? payload,
        CancellationToken ct)
    {
        if (conn == null) throw new ArgumentNullException(nameof(conn));
        if (tx == null) throw new ArgumentNullException(nameof(tx));

        var payloadJson = payload == null
            ? null
            : JsonSerializer.Serialize(payload, JsonOptions);

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
);";

        await using var cmd = new SqlCommand(sql, conn, tx);

        cmd.Parameters.AddWithValue("@TicketId", ticketId);
        cmd.Parameters.AddWithValue("@EventType", (byte)eventType);
        cmd.Parameters.AddWithValue("@ByUserId", byUserId);
        cmd.Parameters.AddWithValue("@ByDisplayName", byDisplayName ?? string.Empty);

        cmd.Parameters.Add("@PayloadJson", SqlDbType.NVarChar, -1).Value =
            payloadJson == null ? (object)DBNull.Value : payloadJson;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/*
Wymagany obiekt DB:

CREATE TABLE dbo.TicketEvents
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TicketEvents PRIMARY KEY,
    TicketId     BIGINT NOT NULL,
    EventType    TINYINT NOT NULL,
    OccurredAtUtc DATETIME2(0) NOT NULL,
    ByUserId     INT NOT NULL,
    ByDisplayName NVARCHAR(200) NOT NULL,
    PayloadJson  NVARCHAR(MAX) NULL
);

CREATE INDEX IX_TicketEvents_TicketId_OccurredAtUtc
ON dbo.TicketEvents(TicketId, OccurredAtUtc DESC);

-- Opcjonalnie, jeśli chcesz łatwo filtrować po typie:
CREATE INDEX IX_TicketEvents_EventType_OccurredAtUtc
ON dbo.TicketEvents(EventType, OccurredAtUtc DESC);
*/
