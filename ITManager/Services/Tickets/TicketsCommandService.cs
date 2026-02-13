// File: Services/Tickets/TicketsCommandService.cs
// Description: Warstwa command dla ticketów (create/update).
// Notes:
//   - Każda metoda ma jawny check permission na starcie.
//   - Zdarzenia (outbox) zapisujemy w tej samej transakcji co zmiana danych ticketa.
//   - Priority jest liczone w backendzie jako jedyne źródło prawdy: impact.weight * urgency.weight.
//   - In-app notifications (UserNotifications): tworzymy atomowo w tej samej transakcji dla publicznych zdarzeń.
// Version: 1.09
// Updated: 2026-02-12
// Change log:
//   - 1.09 (2026-02-12) FIX: dopasowanie do schematu dbo.UserNotifications:
//                            - EventType tinyint zamiast TypeCode nvarchar
//                            - brak TicketNumber/TicketTitle (UI pobiera przez join do vw_tickets)
//                            - TicketEventId NOT NULL ustawiane na ID z dbo.TicketEvents.
//   - 1.08 (2026-02-11) FIX: próba dopasowania INSERT do dbo.UserNotifications (TypeCode nvarchar) - wycofane, bo kolumny nie istnieją w DB.
//   - 1.07 (2026-02-11) FEATURE: tworzenie rekordów dbo.UserNotifications dla publicznych zdarzeń
//                               (ticket created + public action added) dla requester/keeper (bez autora).
//   - 1.06 (2026-02-11) FIX: ticket_created_by_user_id oraz ticket_created_at ustawiane na aktualnego użytkownika i czas utworzenia (nie requester).
//   - 1.05 (2026-02-09) FIX: naliczanie priorytetu w backendzie (impact.weight * urgency.weight) dla Create i Update
//                            + ignorowanie weightPriority z UI (source of truth w backendzie).

using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using ITManager.Services;
using ITManager.Services.Auth;

namespace ITManager.Services.Tickets;

public sealed class TicketsCommandService
{
    private const string PermCreateOwn = "Tickets.Create.Own";

    private const string PermEditAll = "Tickets.Edit.All";
    private const string PermEditAssigned = "Tickets.Edit.Assigned";
    private const string PermEditOwn = "Tickets.Edit.Own";

    private const string StatusNewName = "New";
    private const string StatusAssignedName = "Assigned";

    private const int PriorityMin = 1;
    private const int PriorityMax = 120;

    // EventType (tinyint) w dbo.UserNotifications
    // Uwaga: to jest kontrakt DB. UI mapuje to przez NotificationsRepository -> TypeCode.
    private const byte NotifEventTypeTicketPublicCreated = 1;
    private const byte NotifEventTypeTicketPublicCommentAdded = 2;

    private readonly string _connectionString;
    private readonly CurrentUserContextService _ctx;
    private readonly TicketEventsRepository _events;

    public TicketsCommandService(
        string connectionString,
        CurrentUserContextService currentUserContextService,
        TicketEventsRepository ticketEventsRepository)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _ctx = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        _events = ticketEventsRepository ?? throw new ArgumentNullException(nameof(ticketEventsRepository));
    }

    public async Task<long> CreateTicketAsync(TicketsService.CreateTicketRequest req)
    {
        if (req == null) throw new ArgumentNullException(nameof(req));

        GuardCanCreateTicket();

        var title = (req.Title ?? string.Empty).Trim();
        var firstAction = (req.FirstActionText ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title jest wymagany.", nameof(req));

        if (string.IsNullOrWhiteSpace(firstAction))
            throw new ArgumentException("Opis pierwszej akcji jest wymagany.", nameof(req));

        var current = _ctx.CurrentUser;
        if (current.UserId is null)
            throw new UnauthorizedAccessException("Brak UserId w kontekście użytkownika.");

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            var categoryId = await GetCategoryIdByNameAsync(conn, tx, req.CategoryName).ConfigureAwait(false);
            var impactId = await GetImpactIdByNameAsync(conn, tx, req.ImpactName).ConfigureAwait(false);
            var urgencyId = await GetUrgencyIdByNameAsync(conn, tx, req.UrgencyName).ConfigureAwait(false);

            var impactWeight = await GetImpactWeightByIdAsync(conn, tx, impactId).ConfigureAwait(false);
            var urgencyWeight = await GetUrgencyWeightByIdAsync(conn, tx, urgencyId).ConfigureAwait(false);
            var weightPriority = ComputeWeightPriority(impactWeight, urgencyWeight);

            var requesterId = current.UserId.Value;

            if (!string.IsNullOrWhiteSpace(req.RequesterDisplayName))
            {
                var requestedRequesterId = await FindUserIdByDisplayNameAsync(conn, tx, req.RequesterDisplayName!.Trim()).ConfigureAwait(false);
                if (requestedRequesterId != null)
                    requesterId = requestedRequesterId.Value;
            }

            var statusIdNew = await GetStatusIdByNameAsync(conn, tx, StatusNewName).ConfigureAwait(false);

            var nowUtc = DateTime.UtcNow;

            var ticketId = await InsertTicketAsync(
                conn,
                tx,
                title,
                requesterId,
                keeperId: null,
                categoryId,
                statusIdNew,
                impactId,
                urgencyId,
                requestDateUtc: nowUtc,
                closingDateUtc: null,
                problemDescription: string.Empty,
                problemSolution: string.Empty,
                weightPriority: weightPriority,
                createdByUserId: current.UserId.Value,
                createdAtUtc: nowUtc).ConfigureAwait(false);

            await InsertTicketActionAsync(
                conn,
                tx,
                ticketId,
                createdByUserId: current.UserId.Value,
                actionText: firstAction,
                isPublic: true,
                statusFromId: statusIdNew,
                statusToId: statusIdNew,
                waitingReasonCode: null,
                createdAtUtc: nowUtc).ConfigureAwait(false);

            var byDisplayName = await GetUserDisplayNameByIdAsync(conn, tx, current.UserId.Value).ConfigureAwait(false);

            var createdEventId = await _events.InsertEventAsync(
                conn,
                tx,
                TicketEventType.Created,
                ticketId,
                current.UserId.Value,
                byDisplayName,
                payload: new { ImpactId = impactId, UrgencyId = urgencyId, WeightPriority = weightPriority },
                ct: CancellationToken.None).ConfigureAwait(false);

            await CreateNotificationsForTicketParticipantsAsync(
                conn,
                tx,
                ticketId: ticketId,
                requesterId: requesterId,
                keeperId: null,
                actorUserId: current.UserId.Value,
                eventType: NotifEventTypeTicketPublicCreated,
                ticketEventId: createdEventId,
                createdAtUtc: nowUtc).ConfigureAwait(false);

            tx.Commit();
            return ticketId;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task AssignTicketToCurrentUserAsync(long ticketId)
    {
        GuardCanAssignToMe();

        var current = _ctx.CurrentUser;
        if (current.UserId is null)
            throw new UnauthorizedAccessException("Brak UserId w kontekście użytkownika.");

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            var ticket = await GetTicketCoreAsync(conn, tx, ticketId).ConfigureAwait(false);
            if (ticket == null)
                throw new InvalidOperationException("Ticket nie istnieje.");

            if (ticket.KeeperId != null && ticket.KeeperId.Value > 0)
                throw new InvalidOperationException("Ticket jest już przypisany.");

            var statusFromId = ticket.StatusId;

            var statusAssignedId = await TryGetStatusIdByNameAsync(conn, tx, StatusAssignedName).ConfigureAwait(false);
            var statusToId = statusAssignedId ?? statusFromId;

            await UpdateTicketKeeperAsync(conn, tx, ticketId, current.UserId.Value).ConfigureAwait(false);

            if (statusToId != statusFromId)
                await UpdateTicketStatusAsync(conn, tx, ticketId, statusToId).ConfigureAwait(false);

            var nowUtc = DateTime.UtcNow;

            await InsertTicketActionAsync(
                conn,
                tx,
                ticketId,
                createdByUserId: current.UserId.Value,
                actionText: "Assigned to me",
                isPublic: true,
                statusFromId: statusFromId,
                statusToId: statusToId,
                waitingReasonCode: null,
                createdAtUtc: nowUtc).ConfigureAwait(false);

            var byDisplayName = await GetUserDisplayNameByIdAsync(conn, tx, current.UserId.Value).ConfigureAwait(false);

            await _events.InsertEventAsync(
                conn,
                tx,
                TicketEventType.Assigned,
                ticketId,
                current.UserId.Value,
                byDisplayName,
                payload: new { StatusFromId = statusFromId, StatusToId = statusToId },
                ct: CancellationToken.None).ConfigureAwait(false);

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task UpdateTicketAsync(
        long id,
        string title,
        string problemDescription,
        string problemSolution,
        string categoryName,
        string statusName,
        string requesterName,
        string operatorName,
        string impactName,
        string urgencyName,
        DateTime? requestDate,
        DateTime? closingDate,
        int weightPriority)
    {
        _ = weightPriority;

        GuardCanEditTicket();

        var current = _ctx.CurrentUser;
        if (current.UserId is null)
            throw new UnauthorizedAccessException("Brak UserId w kontekście użytkownika.");

        var titleTrim = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(titleTrim))
            throw new ArgumentException("Title jest wymagany.", nameof(title));

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            var ticket = await GetTicketCoreAsync(conn, tx, id).ConfigureAwait(false);
            if (ticket == null)
                throw new InvalidOperationException("Ticket nie istnieje.");

            GuardCanEditTicketForTarget(ticket);

            var categoryId = await GetCategoryIdByNameAsync(conn, tx, categoryName).ConfigureAwait(false);
            var statusIdRequested = await GetStatusIdByNameAsync(conn, tx, statusName).ConfigureAwait(false);
            var impactId = await GetImpactIdByNameAsync(conn, tx, impactName).ConfigureAwait(false);
            var urgencyId = await GetUrgencyIdByNameAsync(conn, tx, urgencyName).ConfigureAwait(false);

            var impactWeight = await GetImpactWeightByIdAsync(conn, tx, impactId).ConfigureAwait(false);
            var urgencyWeight = await GetUrgencyWeightByIdAsync(conn, tx, urgencyId).ConfigureAwait(false);
            var computedPriority = ComputeWeightPriority(impactWeight, urgencyWeight);

            int requesterId = ticket.RequesterId;
            if (!string.IsNullOrWhiteSpace(requesterName))
            {
                var requestedRequesterId = await FindUserIdByDisplayNameAsync(conn, tx, requesterName.Trim()).ConfigureAwait(false);
                if (requestedRequesterId != null)
                    requesterId = requestedRequesterId.Value;
            }

            int? keeperId = ticket.KeeperId;
            int? requestedKeeperId = null;

            if (!string.IsNullOrWhiteSpace(operatorName))
            {
                requestedKeeperId = await FindUserIdByDisplayNameAsync(conn, tx, operatorName.Trim()).ConfigureAwait(false);
                if (requestedKeeperId != null)
                    keeperId = requestedKeeperId.Value;
            }

            if (!current.Has(PermEditAll))
            {
                var changingKeeper = (ticket.KeeperId != keeperId);
                if (changingKeeper)
                {
                    if (keeperId != null && keeperId.Value != current.UserId.Value)
                        throw new UnauthorizedAccessException("Możesz przypisać ticket tylko do siebie.");

                    if (keeperId == null)
                        throw new UnauthorizedAccessException("Nie możesz usuwać przypisania operatora.");
                }
            }

            var statusIdFinal = statusIdRequested;

            var wasUnassigned = ticket.KeeperId == null || ticket.KeeperId.Value <= 0;
            var isNowAssigned = keeperId != null && keeperId.Value > 0;

            if (wasUnassigned && isNowAssigned)
            {
                var statusNewId = await GetStatusIdByNameAsync(conn, tx, StatusNewName).ConfigureAwait(false);
                if (statusIdRequested == statusNewId)
                {
                    var assignedId = await TryGetStatusIdByNameAsync(conn, tx, StatusAssignedName).ConfigureAwait(false);
                    if (assignedId != null)
                        statusIdFinal = assignedId.Value;
                }
            }

            await UpdateTicketFieldsAsync(
                conn,
                tx,
                id,
                titleTrim,
                requesterId,
                keeperId,
                categoryId,
                statusIdFinal,
                impactId,
                urgencyId,
                requestDateUtc: requestDate,
                closingDateUtc: closingDate,
                problemDescription: problemDescription ?? string.Empty,
                problemSolution: problemSolution ?? string.Empty,
                weightPriority: computedPriority).ConfigureAwait(false);

            if (wasUnassigned && isNowAssigned)
            {
                var statusFromId = ticket.StatusId;
                var nowUtc = DateTime.UtcNow;

                var keeperDisplayName = keeperId == null
                    ? string.Empty
                    : await GetUserDisplayNameByIdAsync(conn, tx, keeperId.Value).ConfigureAwait(false);

                var actionText2 = string.IsNullOrWhiteSpace(keeperDisplayName)
                    ? "Assigned"
                    : $"Assigned to {keeperDisplayName}";

                await InsertTicketActionAsync(
                    conn,
                    tx,
                    id,
                    createdByUserId: current.UserId.Value,
                    actionText: actionText2,
                    isPublic: true,
                    statusFromId: statusFromId,
                    statusToId: statusIdFinal,
                    waitingReasonCode: null,
                    createdAtUtc: nowUtc).ConfigureAwait(false);
            }

            var byDisplayName = await GetUserDisplayNameByIdAsync(conn, tx, current.UserId.Value).ConfigureAwait(false);

            await _events.InsertEventAsync(
                conn,
                tx,
                TicketEventType.ActionUpdated,
                id,
                current.UserId.Value,
                byDisplayName,
                payload: new { Kind = "Ticket.Updated", KeeperId = keeperId, StatusId = statusIdFinal, WeightPriority = computedPriority },
                ct: CancellationToken.None).ConfigureAwait(false);

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task<long> AddTicketActionAsync(long ticketId, string actionText, bool isPublic, string? targetStatusName, string? waitingReasonCode)
    {
        GuardCanEditTicket();

        var current = _ctx.CurrentUser;
        if (current.UserId is null)
            throw new UnauthorizedAccessException("Brak UserId w kontekście użytkownika.");

        var text = (actionText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("ActionText jest wymagany.", nameof(actionText));

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            var ticket = await GetTicketCoreAsync(conn, tx, ticketId).ConfigureAwait(false);
            if (ticket == null)
                throw new InvalidOperationException("Ticket nie istnieje.");

            GuardCanEditTicketForTarget(ticket);
            GuardCanEditTicketActions(isPublic);

            var statusFromId = ticket.StatusId;
            var statusToId = statusFromId;

            if (!string.IsNullOrWhiteSpace(targetStatusName))
                statusToId = await GetStatusIdByNameAsync(conn, tx, targetStatusName!.Trim()).ConfigureAwait(false);

            var nowUtc = DateTime.UtcNow;

            var actionId = await InsertTicketActionAsync(
                conn,
                tx,
                ticketId,
                createdByUserId: current.UserId.Value,
                actionText: text,
                isPublic: isPublic,
                statusFromId: statusFromId,
                statusToId: statusToId,
                waitingReasonCode: string.IsNullOrWhiteSpace(waitingReasonCode) ? null : waitingReasonCode!.Trim(),
                createdAtUtc: nowUtc).ConfigureAwait(false);

            if (statusToId != statusFromId)
                await UpdateTicketStatusAsync(conn, tx, ticketId, statusToId).ConfigureAwait(false);

            var byDisplayName = await GetUserDisplayNameByIdAsync(conn, tx, current.UserId.Value).ConfigureAwait(false);

            var actionEventId = await _events.InsertEventAsync(
                conn,
                tx,
                TicketEventType.ActionAdded,
                ticketId,
                current.UserId.Value,
                byDisplayName,
                payload: new { ActionId = actionId, IsPublic = isPublic, StatusToId = statusToId, WaitingReasonCode = waitingReasonCode },
                ct: CancellationToken.None).ConfigureAwait(false);

            if (isPublic)
            {
                await CreateNotificationsForTicketParticipantsAsync(
                    conn,
                    tx,
                    ticketId: ticketId,
                    requesterId: ticket.RequesterId,
                    keeperId: ticket.KeeperId,
                    actorUserId: current.UserId.Value,
                    eventType: NotifEventTypeTicketPublicCommentAdded,
                    ticketEventId: actionEventId,
                    createdAtUtc: nowUtc).ConfigureAwait(false);
            }

            tx.Commit();
            return actionId;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public async Task<long> UpdateTicketActionAsync(long ticketId, long actionId, string actionText, bool isPublic, string? targetStatusName, string? waitingReasonCode)
    {
        GuardCanEditTicket();

        var current = _ctx.CurrentUser;
        if (current.UserId is null)
            throw new UnauthorizedAccessException("Brak UserId w kontekście użytkownika.");

        var text = (actionText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("ActionText jest wymagany.", nameof(actionText));

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            var ticket = await GetTicketCoreAsync(conn, tx, ticketId).ConfigureAwait(false);
            if (ticket == null)
                throw new InvalidOperationException("Ticket nie istnieje.");

            GuardCanEditTicketForTarget(ticket);
            GuardCanEditTicketActions(isPublic);

            var statusFromId = ticket.StatusId;
            var statusToId = statusFromId;

            if (!string.IsNullOrWhiteSpace(targetStatusName))
                statusToId = await GetStatusIdByNameAsync(conn, tx, targetStatusName!.Trim()).ConfigureAwait(false);

            await UpdateTicketActionRowAsync(
                conn,
                tx,
                ticketId,
                actionId,
                text,
                isPublic,
                statusFromId,
                statusToId,
                string.IsNullOrWhiteSpace(waitingReasonCode) ? null : waitingReasonCode!.Trim()).ConfigureAwait(false);

            if (statusToId != statusFromId)
                await UpdateTicketStatusAsync(conn, tx, ticketId, statusToId).ConfigureAwait(false);

            var byDisplayName = await GetUserDisplayNameByIdAsync(conn, tx, current.UserId.Value).ConfigureAwait(false);

            await _events.InsertEventAsync(
                conn,
                tx,
                TicketEventType.ActionUpdated,
                ticketId,
                current.UserId.Value,
                byDisplayName,
                payload: new { ActionId = actionId, IsPublic = isPublic, StatusToId = statusToId, WaitingReasonCode = waitingReasonCode },
                ct: CancellationToken.None).ConfigureAwait(false);

            tx.Commit();
            return actionId;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    /* =========================
       Notifications (in-app)
    ========================= */

    private async Task CreateNotificationsForTicketParticipantsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long ticketId,
        int requesterId,
        int? keeperId,
        int actorUserId,
        byte eventType,
        long ticketEventId,
        DateTime createdAtUtc)
    {
        if (ticketEventId <= 0)
            throw new InvalidOperationException("TicketEventId musi być dodatnie (dbo.UserNotifications.TicketEventId jest NOT NULL).");

        // requester
        if (requesterId > 0 && requesterId != actorUserId)
        {
            await InsertUserNotificationAsync(conn, tx, requesterId, ticketId, ticketEventId, eventType, createdAtUtc).ConfigureAwait(false);
        }

        // keeper
        if (keeperId != null && keeperId.Value > 0 && keeperId.Value != requesterId && keeperId.Value != actorUserId)
        {
            await InsertUserNotificationAsync(conn, tx, keeperId.Value, ticketId, ticketEventId, eventType, createdAtUtc).ConfigureAwait(false);
        }
    }

    private async Task InsertUserNotificationAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        long ticketId,
        long ticketEventId,
        byte eventType,
        DateTime createdAtUtc)
    {
        // Dopasowane do aktualnego schematu DB (wnioskowanie z błędów):
        // UserId, TicketId, TicketEventId (NOT NULL), EventType (tinyint), IsRead, CreatedAtUtc, ReadAtUtc
        const string sql = @"
INSERT INTO dbo.UserNotifications
(
    UserId,
    TicketId,
    TicketEventId,
    EventType,
    IsRead,
    CreatedAtUtc,
    ReadAtUtc
)
VALUES
(
    @UserId,
    @TicketId,
    @TicketEventId,
    @EventType,
    0,
    @CreatedAtUtc,
    NULL
);";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };

        cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        cmd.Parameters.Add(new SqlParameter("@TicketId", SqlDbType.BigInt) { Value = ticketId });
        cmd.Parameters.Add(new SqlParameter("@TicketEventId", SqlDbType.BigInt) { Value = ticketEventId });
        cmd.Parameters.Add(new SqlParameter("@EventType", SqlDbType.TinyInt) { Value = eventType });
        cmd.Parameters.Add(new SqlParameter("@CreatedAtUtc", SqlDbType.DateTime2) { Value = createdAtUtc });

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /* =========================
       Guards
    ========================= */

    private void GuardCanCreateTicket()
    {
        var ctx = _ctx.CurrentUser;
        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (!ctx.Has(PermCreateOwn))
            throw new UnauthorizedAccessException("Brak uprawnień do tworzenia ticketów.");
    }

    private void GuardCanAssignToMe()
    {
        var ctx = _ctx.CurrentUser;
        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (ctx.Has(PermEditAll) || ctx.Has(PermEditAssigned))
            return;

        throw new UnauthorizedAccessException("Brak uprawnień do przypisywania ticketów do siebie.");
    }

    private void GuardCanEditTicket()
    {
        var ctx = _ctx.CurrentUser;
        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (ctx.Has(PermEditAll) || ctx.Has(PermEditAssigned) || ctx.Has(PermEditOwn))
            return;

        throw new UnauthorizedAccessException("Brak uprawnień do edycji ticketów.");
    }

    private void GuardCanEditTicketForTarget(TicketCore ticket)
    {
        var ctx = _ctx.CurrentUser;

        if (ctx.Has(PermEditAll))
            return;

        if (ctx.Has(PermEditAssigned) && ctx.UserId != null)
        {
            if (ticket.KeeperId == null || ticket.KeeperId.Value <= 0)
                return;

            if (ticket.KeeperId.Value == ctx.UserId.Value)
                return;
        }

        if (ctx.Has(PermEditOwn) && ctx.UserId != null && ticket.RequesterId == ctx.UserId.Value)
            return;

        throw new UnauthorizedAccessException("Brak uprawnień do edycji wskazanego ticketa.");
    }

    private void GuardCanEditTicketActions(bool isPublic)
    {
        if (isPublic)
            return;

        var ctx = _ctx.CurrentUser;

        if (ctx.Has(PermEditAll) || ctx.Has(PermEditAssigned))
            return;

        throw new UnauthorizedAccessException("Brak uprawnień do dodawania lub edycji prywatnych akcji.");
    }

    /* =========================
       Priority helpers
    ========================= */

    private static int ComputeWeightPriority(int impactWeight, int urgencyWeight)
    {
        if (impactWeight <= 0 || urgencyWeight <= 0)
            return 0;

        long raw = (long)impactWeight * (long)urgencyWeight;
        if (raw < PriorityMin) raw = PriorityMin;
        if (raw > PriorityMax) raw = PriorityMax;
        return (int)raw;
    }

    private async Task<int> GetImpactWeightByIdAsync(SqlConnection conn, SqlTransaction tx, int impactId)
    {
        const string sql = @"
SELECT TOP (1) ISNULL(i.weight, 0)
FROM dbo.ticket_impacts AS i
WHERE i.id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = impactId });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            return 0;

        return Convert.ToInt32(raw);
    }

    private async Task<int> GetUrgencyWeightByIdAsync(SqlConnection conn, SqlTransaction tx, int urgencyId)
    {
        const string sql = @"
SELECT TOP (1) ISNULL(u.weight, 0)
FROM dbo.ticket_urgencies AS u
WHERE u.id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = urgencyId });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            return 0;

        return Convert.ToInt32(raw);
    }

    /* =========================
       Data access helpers
    ========================= */

    private sealed class TicketCore
    {
        public long Id { get; set; }
        public int RequesterId { get; set; }
        public int? KeeperId { get; set; }
        public int StatusId { get; set; }
    }

    private async Task<TicketCore?> GetTicketCoreAsync(SqlConnection conn, SqlTransaction tx, long ticketId)
    {
        const string sql = @"
SELECT TOP (1)
    t.id,
    t.ticket_requester_id,
    t.ticket_keeper_id,
    t.ticket_status_id
FROM dbo.tickets AS t
WHERE t.id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = ticketId });

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            return null;

        return new TicketCore
        {
            Id = Convert.ToInt64(reader["id"]),
            RequesterId = Convert.ToInt32(reader["ticket_requester_id"]),
            KeeperId = reader["ticket_keeper_id"] is DBNull ? null : Convert.ToInt32(reader["ticket_keeper_id"]),
            StatusId = Convert.ToInt32(reader["ticket_status_id"])
        };
    }

    private async Task<string> GetUserDisplayNameByIdAsync(SqlConnection conn, SqlTransaction tx, int userId)
    {
        const string sql = @"
SELECT TOP (1) LTRIM(RTRIM(DisplayName))
FROM dbo.users
WHERE id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = userId });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        var name = raw as string;

        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
    }

    private async Task<int> GetCategoryIdByNameAsync(SqlConnection conn, SqlTransaction tx, string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            throw new ArgumentException("CategoryName jest wymagany.", nameof(name));

        const string sql = @"
SELECT TOP (1) id
FROM dbo.ticket_categories
WHERE LTRIM(RTRIM(name)) = @Name;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = n });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            throw new InvalidOperationException("Nie znaleziono kategorii o podanej nazwie.");

        return Convert.ToInt32(raw);
    }

    private async Task<int> GetImpactIdByNameAsync(SqlConnection conn, SqlTransaction tx, string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            throw new ArgumentException("ImpactName jest wymagany.", nameof(name));

        const string sql = @"
SELECT TOP (1) id
FROM dbo.ticket_impacts
WHERE LTRIM(RTRIM(name)) = @Name;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = n });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            throw new InvalidOperationException("Nie znaleziono impact o podanej nazwie.");

        return Convert.ToInt32(raw);
    }

    private async Task<int> GetUrgencyIdByNameAsync(SqlConnection conn, SqlTransaction tx, string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            throw new ArgumentException("UrgencyName jest wymagany.", nameof(name));

        const string sql = @"
SELECT TOP (1) id
FROM dbo.ticket_urgencies
WHERE LTRIM(RTRIM(name)) = @Name;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = n });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            throw new InvalidOperationException("Nie znaleziono urgency o podanej nazwie.");

        return Convert.ToInt32(raw);
    }

    private async Task<int> GetStatusIdByNameAsync(SqlConnection conn, SqlTransaction tx, string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            throw new ArgumentException("StatusName jest wymagany.", nameof(name));

        const string sql = @"
SELECT TOP (1) id
FROM dbo.ticket_statuses
WHERE LTRIM(RTRIM(name)) = @Name;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = n });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            throw new InvalidOperationException("Nie znaleziono statusu o podanej nazwie.");

        return Convert.ToInt32(raw);
    }

    private async Task<int?> TryGetStatusIdByNameAsync(SqlConnection conn, SqlTransaction tx, string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            return null;

        const string sql = @"
SELECT TOP (1) id
FROM dbo.ticket_statuses
WHERE LTRIM(RTRIM(name)) = @Name;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = n });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            return null;

        return Convert.ToInt32(raw);
    }

    private async Task<int?> FindUserIdByDisplayNameAsync(SqlConnection conn, SqlTransaction tx, string displayName)
    {
        var n = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            return null;

        const string sql = @"
SELECT TOP (1) id
FROM dbo.users
WHERE
    LTRIM(RTRIM(DisplayName)) = @Name
    OR
    REPLACE(LTRIM(RTRIM(DisplayName)), ',', '') = REPLACE(@Name, ',', '')
    OR
    UPPER(
        REPLACE(
            REPLACE(
                REPLACE(LTRIM(RTRIM(DisplayName)), CHAR(160), ''),
            ' ', ''),
        ',', '')
    ) = UPPER(
        REPLACE(
            REPLACE(
                REPLACE(@Name, CHAR(160), ''),
            ' ', ''),
        ',', '')
    );";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = n });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            return null;

        return Convert.ToInt32(raw);
    }

    private async Task<long> InsertTicketAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string title,
        int requesterId,
        int? keeperId,
        int categoryId,
        int statusId,
        int impactId,
        int urgencyId,
        DateTime requestDateUtc,
        DateTime? closingDateUtc,
        string problemDescription,
        string problemSolution,
        int weightPriority,
        int createdByUserId,
        DateTime createdAtUtc)
    {
        const string sql = @"
INSERT INTO dbo.tickets
(
    ticket_request_date,
    ticket_closing_date,
    ticket_title,
    problem_description,
    problem_solution,
    ticket_category_id,
    ticket_status_id,
    ticket_requester_id,
    ticket_keeper_id,
    ticket_impact_id,
    ticket_urgency_id,
    ticket_priority_value,
    ticket_created_by_user_id,
    ticket_created_at
)
OUTPUT INSERTED.id
VALUES
(
    @RequestDateUtc,
    @ClosingDateUtc,
    @Title,
    @ProblemDescription,
    @ProblemSolution,
    @CategoryId,
    @StatusId,
    @RequesterId,
    @KeeperId,
    @ImpactId,
    @UrgencyId,
    @WeightPriority,
    @CreatedByUserId,
    @CreatedAtUtc
);";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };

        cmd.Parameters.Add(new SqlParameter("@RequestDateUtc", SqlDbType.DateTime2) { Value = requestDateUtc });
        cmd.Parameters.Add(new SqlParameter("@ClosingDateUtc", SqlDbType.DateTime2) { Value = (object?)closingDateUtc ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@Title", SqlDbType.NVarChar, 400) { Value = title });
        cmd.Parameters.Add(new SqlParameter("@ProblemDescription", SqlDbType.NVarChar) { Value = problemDescription ?? string.Empty });
        cmd.Parameters.Add(new SqlParameter("@ProblemSolution", SqlDbType.NVarChar) { Value = problemSolution ?? string.Empty });

        cmd.Parameters.Add(new SqlParameter("@CategoryId", SqlDbType.Int) { Value = categoryId });
        cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = statusId });
        cmd.Parameters.Add(new SqlParameter("@RequesterId", SqlDbType.Int) { Value = requesterId });
        cmd.Parameters.Add(new SqlParameter("@KeeperId", SqlDbType.Int) { Value = (object?)keeperId ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@ImpactId", SqlDbType.BigInt) { Value = impactId });
        cmd.Parameters.Add(new SqlParameter("@UrgencyId", SqlDbType.BigInt) { Value = urgencyId });
        cmd.Parameters.Add(new SqlParameter("@WeightPriority", SqlDbType.Int) { Value = weightPriority });

        cmd.Parameters.Add(new SqlParameter("@CreatedByUserId", SqlDbType.Int) { Value = createdByUserId });
        cmd.Parameters.Add(new SqlParameter("@CreatedAtUtc", SqlDbType.DateTime2) { Value = createdAtUtc });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            throw new InvalidOperationException("Insert ticket zwrócił null id.");

        return Convert.ToInt64(raw);
    }

    private async Task<long> InsertTicketActionAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long ticketId,
        int createdByUserId,
        string actionText,
        bool isPublic,
        int statusFromId,
        int statusToId,
        string? waitingReasonCode,
        DateTime createdAtUtc)
    {
        const string sql = @"
INSERT INTO dbo.ticket_actions
(
    ticket_id,
    created_at,
    created_by_user_id,
    created_by_display_name,
    action_text,
    is_public,
    status_from_id,
    status_to_id,
    waiting_reason_code
)
OUTPUT INSERTED.id
VALUES
(
    @TicketId,
    @CreatedAtUtc,
    @CreatedByUserId,
    @CreatedByDisplayName,
    @ActionText,
    @IsPublic,
    @StatusFromId,
    @StatusToId,
    @WaitingReasonCode
);";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };

        cmd.Parameters.Add(new SqlParameter("@TicketId", SqlDbType.BigInt) { Value = ticketId });
        cmd.Parameters.Add(new SqlParameter("@CreatedAtUtc", SqlDbType.DateTime2) { Value = createdAtUtc });
        cmd.Parameters.Add(new SqlParameter("@CreatedByUserId", SqlDbType.Int) { Value = createdByUserId });

        var displayName = await GetUserDisplayNameByIdAsync(conn, tx, createdByUserId).ConfigureAwait(false);
        cmd.Parameters.Add(new SqlParameter("@CreatedByDisplayName", SqlDbType.NVarChar, 200) { Value = displayName });

        cmd.Parameters.Add(new SqlParameter("@ActionText", SqlDbType.NVarChar, 2000) { Value = actionText });

        cmd.Parameters.Add(new SqlParameter("@IsPublic", SqlDbType.Bit) { Value = isPublic });
        cmd.Parameters.Add(new SqlParameter("@StatusFromId", SqlDbType.Int) { Value = statusFromId });
        cmd.Parameters.Add(new SqlParameter("@StatusToId", SqlDbType.Int) { Value = statusToId });

        cmd.Parameters.Add(new SqlParameter("@WaitingReasonCode", SqlDbType.VarChar, 10) { Value = (object?)waitingReasonCode ?? DBNull.Value });

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            throw new InvalidOperationException("Insert ticket action zwrócił null id.");

        return Convert.ToInt64(raw);
    }

    private async Task UpdateTicketKeeperAsync(SqlConnection conn, SqlTransaction tx, long ticketId, int keeperId)
    {
        const string sql = @"
UPDATE dbo.tickets
SET ticket_keeper_id = @KeeperId
WHERE id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@KeeperId", SqlDbType.Int) { Value = keeperId });
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = ticketId });

        var rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (rows != 1)
            throw new InvalidOperationException("Update ticket keeper nie zmienił 1 wiersza.");
    }

    private async Task UpdateTicketStatusAsync(SqlConnection conn, SqlTransaction tx, long ticketId, int statusId)
    {
        const string sql = @"
UPDATE dbo.tickets
SET ticket_status_id = @StatusId
WHERE id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = statusId });
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = ticketId });

        var rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (rows != 1)
            throw new InvalidOperationException("Update ticket status nie zmienił 1 wiersza.");
    }

    private async Task UpdateTicketFieldsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long ticketId,
        string title,
        int requesterId,
        int? keeperId,
        int categoryId,
        int statusId,
        int impactId,
        int urgencyId,
        DateTime? requestDateUtc,
        DateTime? closingDateUtc,
        string problemDescription,
        string problemSolution,
        int weightPriority)
    {
        const string sql = @"
UPDATE dbo.tickets
SET
    ticket_title = @Title,
    ticket_requester_id = @RequesterId,
    ticket_keeper_id = @KeeperId,
    ticket_category_id = @CategoryId,
    ticket_status_id = @StatusId,
    ticket_impact_id = @ImpactId,
    ticket_urgency_id = @UrgencyId,
    ticket_request_date = @RequestDateUtc,
    ticket_closing_date = @ClosingDateUtc,
    problem_description = @ProblemDescription,
    problem_solution = @ProblemSolution,
    ticket_priority_value = @WeightPriority
WHERE id = @Id;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };

        cmd.Parameters.Add(new SqlParameter("@Title", SqlDbType.NVarChar, 400) { Value = title });
        cmd.Parameters.Add(new SqlParameter("@RequesterId", SqlDbType.Int) { Value = requesterId });
        cmd.Parameters.Add(new SqlParameter("@KeeperId", SqlDbType.Int) { Value = (object?)keeperId ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@CategoryId", SqlDbType.Int) { Value = categoryId });
        cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = statusId });
        cmd.Parameters.Add(new SqlParameter("@ImpactId", SqlDbType.BigInt) { Value = impactId });
        cmd.Parameters.Add(new SqlParameter("@UrgencyId", SqlDbType.BigInt) { Value = urgencyId });

        cmd.Parameters.Add(new SqlParameter("@RequestDateUtc", SqlDbType.DateTime2) { Value = (object?)requestDateUtc ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ClosingDateUtc", SqlDbType.DateTime2) { Value = (object?)closingDateUtc ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@ProblemDescription", SqlDbType.NVarChar) { Value = problemDescription ?? string.Empty });
        cmd.Parameters.Add(new SqlParameter("@ProblemSolution", SqlDbType.NVarChar) { Value = problemSolution ?? string.Empty });

        cmd.Parameters.Add(new SqlParameter("@WeightPriority", SqlDbType.Int) { Value = weightPriority });
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = ticketId });

        var rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (rows != 1)
            throw new InvalidOperationException("Update ticket fields nie zmienił 1 wiersza.");
    }

    private async Task UpdateTicketActionRowAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long ticketId,
        long actionId,
        string actionText,
        bool isPublic,
        int statusFromId,
        int statusToId,
        string? waitingReasonCode)
    {
        const string sql = @"
UPDATE dbo.ticket_actions
SET
    action_text = @ActionText,
    is_public = @IsPublic,
    status_from_id = @StatusFromId,
    status_to_id = @StatusToId,
    waiting_reason_code = @WaitingReasonCode
WHERE
    id = @Id
    AND ticket_id = @TicketId;";

        using var cmd = new SqlCommand(sql, conn, tx) { CommandType = CommandType.Text, CommandTimeout = 30 };

        cmd.Parameters.Add(new SqlParameter("@ActionText", SqlDbType.NVarChar, 2000) { Value = actionText });
        cmd.Parameters.Add(new SqlParameter("@IsPublic", SqlDbType.Bit) { Value = isPublic });

        cmd.Parameters.Add(new SqlParameter("@StatusFromId", SqlDbType.Int) { Value = statusFromId });
        cmd.Parameters.Add(new SqlParameter("@StatusToId", SqlDbType.Int) { Value = statusToId });
        cmd.Parameters.Add(new SqlParameter("@WaitingReasonCode", SqlDbType.VarChar, 10) { Value = (object?)waitingReasonCode ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = actionId });
        cmd.Parameters.Add(new SqlParameter("@TicketId", SqlDbType.BigInt) { Value = ticketId });

        var rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (rows != 1)
            throw new InvalidOperationException("Update ticket action nie zmienił 1 wiersza.");
    }
}
