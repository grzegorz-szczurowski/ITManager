// File: Services/Tickets/TicketsQueryService.cs
// Description: Warstwa query dla ticketów (read-only).
// Notes:
//   - Query nie modyfikuje danych.
//   - Każda metoda ma jawny check permission na starcie.
// Version: 1.04
// Updated: 2026-02-09
// Change log:
//   - 1.00 (2026-02-08) MIGRATION: implementacja end-to-end (ADO.NET) + zwracanie stabilnych DTO z TicketsService.
//   - 1.01 (2026-02-08) FIX: słowniki Impact/Urgency dostępne także dla end-user z Tickets.Create.Own (bez Tickets.View.*).
//   - 1.02 (2026-02-08) FIX: odczyt ID słowników Impact/Urgency bez InvalidCastException przy BIGINT (Convert.ToInt32(reader.GetValue(0))).
//   - 1.03 (2026-02-09) SECURITY: zamknięcie IDOR. Odczyt ticketów i akcji ograniczony do scope (Own/Assigned/Team) w SQL.
//   - 1.04 (2026-02-09) FEATURE: odczyt ticketu po TicketCode (np. /tickets/WAL000001) z tym samym filtrem scope.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using ITManager.Models;
using ITManager.Services;
using ITManager.Services.Auth;

namespace ITManager.Services.Tickets;

public sealed class TicketsQueryService
{
    private const string PermViewOwn = "Tickets.View.Own";
    private const string PermViewAssigned = "Tickets.View.Assigned";
    private const string PermViewTeam = "Tickets.View.Team";

    private const string PermCreateOwn = "Tickets.Create.Own";

    private const string PermEditOwn = "Tickets.Edit.Own";
    private const string PermEditAssigned = "Tickets.Edit.Assigned";
    private const string PermEditTeam = "Tickets.Edit.Team";
    private const string PermEditAll = "Tickets.Edit.All";

    private readonly string _connectionString;
    private readonly CurrentUserContextService _ctx;

    public TicketsQueryService(string connectionString, CurrentUserContextService currentUserContextService)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _ctx = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
    }

    public async Task<TicketsService.ImpactUrgencyDictionaryResult> GetImpactAndUrgencyDictionaryAsync()
    {
        GuardCanReadTicketDictionaries();

        var result = new TicketsService.ImpactUrgencyDictionaryResult
        {
            Impacts = new List<TicketsService.ImpactUrgencyItem>(),
            Urgencies = new List<TicketsService.ImpactUrgencyItem>()
        };

        const string sql = @"
SELECT i.id, LTRIM(RTRIM(i.name)) AS name, ISNULL(i.weight, 0) AS weight
FROM dbo.ticket_impacts AS i
ORDER BY ISNULL(i.sort_order, 9999), LTRIM(RTRIM(i.name));

SELECT u.id, LTRIM(RTRIM(u.name)) AS name, ISNULL(u.weight, 0) AS weight
FROM dbo.ticket_urgencies AS u
ORDER BY ISNULL(u.sort_order, 9999), LTRIM(RTRIM(u.name));";

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

        await conn.OpenAsync().ConfigureAwait(false);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Impacts.Add(new TicketsService.ImpactUrgencyItem
            {
                Id = Convert.ToInt32(reader.GetValue(0)),
                Name = (reader["name"] as string ?? string.Empty).Trim(),
                Weight = Convert.ToInt32(reader["weight"])
            });
        }

        if (await reader.NextResultAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Urgencies.Add(new TicketsService.ImpactUrgencyItem
                {
                    Id = Convert.ToInt32(reader.GetValue(0)),
                    Name = (reader["name"] as string ?? string.Empty).Trim(),
                    Weight = Convert.ToInt32(reader["weight"])
                });
            }
        }

        return result;
    }

    public async Task<List<Ticket>> GetTicketsAsync()
    {
        GuardCanViewAnyTickets();

        var ctx = _ctx.CurrentUser;

        var canTeam = ctx.Has(PermViewTeam);
        var canAssigned = ctx.Has(PermViewAssigned);
        var canOwn = ctx.Has(PermViewOwn);

        if (canTeam)
            return await GetTicketsMyTeamAsync().ConfigureAwait(false);

        if (canAssigned)
            return await GetTicketsAssignedToCurrentUserAsync().ConfigureAwait(false);

        if (canOwn)
            return await GetTicketsOwnAsync().ConfigureAwait(false);

        throw new UnauthorizedAccessException("Brak uprawnień do listy ticketów (scope).");
    }

    public async Task<List<string>> GetTicketStatusesAsync()
    {
        GuardCanReadTicketDictionaries();

        var results = new List<string>();

        const string sql = @"
SELECT LTRIM(RTRIM(s.name)) AS name
FROM dbo.ticket_statuses AS s
WHERE s.name IS NOT NULL AND LTRIM(RTRIM(s.name)) <> ''
ORDER BY ISNULL(s.sort_order, 9999), LTRIM(RTRIM(s.name));";

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

        await conn.OpenAsync().ConfigureAwait(false);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader["name"] as string;
            if (!string.IsNullOrWhiteSpace(name))
                results.Add(name.Trim());
        }

        return results;
    }

    public Task<List<Ticket>> GetTicketsQueueAsync()
    {
        GuardCanViewQueue();

        const string sql = @"
SELECT
    id,
    ticket_code,
    ticket_request_date,
    ticket_last_action_at,
    ticket_closing_date,
    ticket_requester_name,
    ticket_category_name,
    ticket_title,
    ticket_operator_name,
    ticket_requester_id,
    ticket_keeper_id,
    ticket_priority_value,
    ticket_statuses_name,
    problem_description,
    problem_solution,
    ticket_impact_name,
    ticket_urgency_name
FROM dbo.vw_tickets
WHERE
    (ticket_statuses_name = N'New' OR ticket_statuses_name = N'NEW')
    AND (ticket_keeper_id IS NULL OR ticket_keeper_id = 0)
ORDER BY
    COALESCE(ticket_last_action_at, ticket_request_date) DESC, id DESC;";

        return ReadTicketsAsync(sql, null);
    }

    public async Task<int> GetTicketsQueueCountAsync()
    {
        GuardCanViewQueue();

        const string sql = @"
SELECT COUNT(1)
FROM dbo.vw_tickets
WHERE
    (ticket_statuses_name = N'New' OR ticket_statuses_name = N'NEW')
    AND (ticket_keeper_id IS NULL OR ticket_keeper_id = 0);";

        return await ExecuteScalarIntAsync(sql, null).ConfigureAwait(false);
    }

    public async Task<int> GetAssignedToMeOpenTicketsCountAsync()
    {
        GuardCanViewAnyTickets();

        var ctx = _ctx.CurrentUser;
        if (ctx.UserId is null)
            return 0;

        const string sql = @"
SELECT COUNT(1)
FROM dbo.vw_tickets
WHERE
    ticket_keeper_id = @UserId
    AND ticket_closing_date IS NULL;";

        var pars = new List<SqlParameter> { new("@UserId", SqlDbType.Int) { Value = ctx.UserId.Value } };
        return await ExecuteScalarIntAsync(sql, pars).ConfigureAwait(false);
    }

    public async Task<int> GetAllOpenTicketsCountAsync()
    {
        GuardCanViewAnyTickets();

        const string sql = @"
SELECT COUNT(1)
FROM dbo.vw_tickets
WHERE ticket_closing_date IS NULL;";

        return await ExecuteScalarIntAsync(sql, null).ConfigureAwait(false);
    }

    public Task<List<Ticket>> GetTicketsAssignedToCurrentUserAsync()
    {
        GuardCanViewAnyTickets();

        var ctx = _ctx.CurrentUser;
        if (ctx.UserId is null)
            return Task.FromResult(new List<Ticket>());

        const string sql = @"
SELECT
    id,
    ticket_code,
    ticket_request_date,
    ticket_last_action_at,
    ticket_closing_date,
    ticket_requester_name,
    ticket_category_name,
    ticket_title,
    ticket_operator_name,
    ticket_requester_id,
    ticket_keeper_id,
    ticket_priority_value,
    ticket_statuses_name,
    problem_description,
    problem_solution,
    ticket_impact_name,
    ticket_urgency_name
FROM dbo.vw_tickets
WHERE
    ticket_keeper_id = @UserId
ORDER BY
    COALESCE(ticket_last_action_at, ticket_request_date) DESC, id DESC;";

        var pars = new List<SqlParameter> { new("@UserId", SqlDbType.Int) { Value = ctx.UserId.Value } };
        return ReadTicketsAsync(sql, pars);
    }

    public Task<List<Ticket>> GetTicketsOwnAsync()
    {
        GuardCanViewAnyTickets();

        var ctx = _ctx.CurrentUser;
        if (ctx.UserId is null)
            return Task.FromResult(new List<Ticket>());

        const string sql = @"
SELECT
    id,
    ticket_code,
    ticket_request_date,
    ticket_last_action_at,
    ticket_closing_date,
    ticket_requester_name,
    ticket_category_name,
    ticket_title,
    ticket_operator_name,
    ticket_requester_id,
    ticket_keeper_id,
    ticket_priority_value,
    ticket_statuses_name,
    problem_description,
    problem_solution,
    ticket_impact_name,
    ticket_urgency_name
FROM dbo.vw_tickets
WHERE
    ticket_requester_id = @UserId
ORDER BY
    COALESCE(ticket_last_action_at, ticket_request_date) DESC, id DESC;";

        var pars = new List<SqlParameter> { new("@UserId", SqlDbType.Int) { Value = ctx.UserId.Value } };
        return ReadTicketsAsync(sql, pars);
    }

    public Task<List<Ticket>> GetTicketsMyTeamAsync()
    {
        GuardCanViewAnyTickets();

        const string sql = @"
SELECT
    id,
    ticket_code,
    ticket_request_date,
    ticket_last_action_at,
    ticket_closing_date,
    ticket_requester_name,
    ticket_category_name,
    ticket_title,
    ticket_operator_name,
    ticket_requester_id,
    ticket_keeper_id,
    ticket_priority_value,
    ticket_statuses_name,
    problem_description,
    problem_solution,
    ticket_impact_name,
    ticket_urgency_name
FROM dbo.vw_tickets
ORDER BY
    COALESCE(ticket_last_action_at, ticket_request_date) DESC, id DESC;";

        return ReadTicketsAsync(sql, null);
    }

    public async Task<Ticket?> GetTicketByIdAsync(int id)
    {
        GuardCanViewAnyTickets();

        var scope = BuildTicketScope("t");

        var sql = @"
SELECT TOP (1)
    t.id,
    t.ticket_code,
    t.ticket_request_date,
    t.ticket_last_action_at,
    t.ticket_closing_date,
    t.ticket_requester_name,
    t.ticket_category_name,
    t.ticket_title,
    t.ticket_operator_name,
    t.ticket_requester_id,
    t.ticket_keeper_id,
    t.ticket_priority_value,
    t.ticket_statuses_name,
    t.problem_description,
    t.problem_solution,
    t.ticket_impact_name,
    t.ticket_urgency_name
FROM dbo.vw_tickets AS t
WHERE
    t.id = @Id
    AND " + scope.Predicate + @";";

        var pars = new List<SqlParameter>
        {
            new("@Id", SqlDbType.Int) { Value = id }
        };
        pars.AddRange(scope.Parameters);

        var list = await ReadTicketsAsync(sql, pars).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public async Task<Ticket?> GetTicketByCodeAsync(string ticketCode)
    {
        GuardCanViewAnyTickets();

        if (string.IsNullOrWhiteSpace(ticketCode))
            return null;

        ticketCode = ticketCode.Trim();

        var scope = BuildTicketScope("t");

        var sql = @"
SELECT TOP (1)
    t.id,
    t.ticket_code,
    t.ticket_request_date,
    t.ticket_last_action_at,
    t.ticket_closing_date,
    t.ticket_requester_name,
    t.ticket_category_name,
    t.ticket_title,
    t.ticket_operator_name,
    t.ticket_requester_id,
    t.ticket_keeper_id,
    t.ticket_priority_value,
    t.ticket_statuses_name,
    t.problem_description,
    t.problem_solution,
    t.ticket_impact_name,
    t.ticket_urgency_name
FROM dbo.vw_tickets AS t
WHERE
    t.ticket_code = @TicketCode
    AND " + scope.Predicate + @";";

        var pars = new List<SqlParameter>
        {
            new("@TicketCode", SqlDbType.NVarChar, 32) { Value = ticketCode }
        };
        pars.AddRange(scope.Parameters);

        var list = await ReadTicketsAsync(sql, pars).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public Task<bool> CanCurrentUserWritePrivateTicketActionsAsync()
    {
        var ctx = _ctx.CurrentUser;
        var ok = ctx.Has(PermEditAll) || ctx.Has(PermEditAssigned);
        return Task.FromResult(ok);
    }

    public async Task<List<TicketsService.TicketActionRow>> GetTicketActionsAsync(long ticketId)
    {
        GuardCanViewAnyTickets();

        var scope = BuildTicketScope("t");

        var sql = @"
SELECT
    a.id,
    a.created_at AS created_at_utc,
    COALESCE(u.DisplayName, N'') AS created_by_display_name,
    COALESCE(a.action_text, N'') AS action_text,
    CAST(ISNULL(a.is_public, 1) AS bit) AS is_public,
    COALESCE(sf.name, N'') AS status_from_name,
    COALESCE(st.name, N'') AS status_to_name,
    COALESCE(a.waiting_reason_code, N'') AS waiting_reason_code
FROM dbo.ticket_actions AS a
INNER JOIN dbo.vw_tickets AS t ON t.id = a.ticket_id
LEFT JOIN dbo.users AS u ON u.id = a.created_by_user_id
LEFT JOIN dbo.ticket_statuses AS sf ON sf.id = a.status_from_id
LEFT JOIN dbo.ticket_statuses AS st ON st.id = a.status_to_id
WHERE
    a.ticket_id = @TicketId
    AND " + scope.Predicate + @"
ORDER BY a.created_at DESC, a.id DESC;";

        var pars = new List<SqlParameter>
        {
            new("@TicketId", SqlDbType.BigInt) { Value = ticketId }
        };
        pars.AddRange(scope.Parameters);

        var list = new List<TicketsService.TicketActionRow>();

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

        foreach (var p in pars)
            cmd.Parameters.Add(p);

        await conn.OpenAsync().ConfigureAwait(false);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var row = new TicketsService.TicketActionRow
            {
                Id = Convert.ToInt64(reader["id"]),
                CreatedAtUtc = reader["created_at_utc"] is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.UtcNow,
                CreatedByDisplayName = (reader["created_by_display_name"] as string ?? string.Empty).Trim(),
                ActionText = (reader["action_text"] as string ?? string.Empty),
                IsPublic = reader["is_public"] is bool b && b,
                StatusFromName = (reader["status_from_name"] as string ?? string.Empty).Trim(),
                StatusToName = (reader["status_to_name"] as string ?? string.Empty).Trim(),
                WaitingReasonCode = (reader["waiting_reason_code"] as string ?? string.Empty).Trim()
            };

            list.Add(row);
        }

        return list;
    }

    public async Task<TicketsService.TicketActionRow?> GetTicketActionByIdAsync(long actionId)
    {
        GuardCanViewAnyTickets();

        var scope = BuildTicketScope("t");

        var sql = @"
SELECT TOP (1)
    a.id,
    a.created_at AS created_at_utc,
    COALESCE(u.DisplayName, N'') AS created_by_display_name,
    COALESCE(a.action_text, N'') AS action_text,
    CAST(ISNULL(a.is_public, 1) AS bit) AS is_public,
    COALESCE(sf.name, N'') AS status_from_name,
    COALESCE(st.name, N'') AS status_to_name,
    COALESCE(a.waiting_reason_code, N'') AS waiting_reason_code
FROM dbo.ticket_actions AS a
INNER JOIN dbo.vw_tickets AS t ON t.id = a.ticket_id
LEFT JOIN dbo.users AS u ON u.id = a.created_by_user_id
LEFT JOIN dbo.ticket_statuses AS sf ON sf.id = a.status_from_id
LEFT JOIN dbo.ticket_statuses AS st ON st.id = a.status_to_id
WHERE
    a.id = @ActionId
    AND " + scope.Predicate + @";";

        var pars = new List<SqlParameter>
        {
            new("@ActionId", SqlDbType.BigInt) { Value = actionId }
        };
        pars.AddRange(scope.Parameters);

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

        foreach (var p in pars)
            cmd.Parameters.Add(p);

        await conn.OpenAsync().ConfigureAwait(false);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            return null;

        return new TicketsService.TicketActionRow
        {
            Id = Convert.ToInt64(reader["id"]),
            CreatedAtUtc = reader["created_at_utc"] is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.UtcNow,
            CreatedByDisplayName = (reader["created_by_display_name"] as string ?? string.Empty).Trim(),
            ActionText = (reader["action_text"] as string ?? string.Empty),
            IsPublic = reader["is_public"] is bool b && b,
            StatusFromName = (reader["status_from_name"] as string ?? string.Empty).Trim(),
            StatusToName = (reader["status_to_name"] as string ?? string.Empty).Trim(),
            WaitingReasonCode = (reader["waiting_reason_code"] as string ?? string.Empty).Trim()
        };
    }

    private void GuardCanViewAnyTickets()
    {
        var ctx = _ctx.CurrentUser;
        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (ctx.Has(PermViewTeam) || ctx.Has(PermViewAssigned) || ctx.Has(PermViewOwn))
            return;

        throw new UnauthorizedAccessException("Brak uprawnień Tickets.View.*.");
    }

    private void GuardCanViewQueue()
    {
        var ctx = _ctx.CurrentUser;
        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (ctx.Has(PermViewTeam) || ctx.Has(PermViewAssigned))
            return;

        throw new UnauthorizedAccessException("Brak uprawnień do kolejki ticketów.");
    }

    private void GuardCanReadTicketDictionaries()
    {
        // Słowniki są potrzebne do tworzenia oraz edycji.
        // End-user może mieć Tickets.Create.Own bez Tickets.View.*.
        var ctx = _ctx.CurrentUser;

        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (ctx.Has(PermViewTeam) || ctx.Has(PermViewAssigned) || ctx.Has(PermViewOwn))
            return;

        if (ctx.Has(PermCreateOwn))
            return;

        if (ctx.Has(PermEditOwn) || ctx.Has(PermEditAssigned) || ctx.Has(PermEditTeam) || ctx.Has(PermEditAll))
            return;

        throw new UnauthorizedAccessException("Brak uprawnień do słowników ticketów.");
    }

    private sealed class TicketScope
    {
        public string Predicate { get; set; } = string.Empty;
        public List<SqlParameter> Parameters { get; set; } = new();
    }

    private TicketScope BuildTicketScope(string ticketAlias)
    {
        var ctx = _ctx.CurrentUser;

        var canTeam = ctx.Has(PermViewTeam);
        var canAssigned = ctx.Has(PermViewAssigned);
        var canOwn = ctx.Has(PermViewOwn);

        var userId = ctx.UserId;

        if (!canTeam && userId is null)
        {
            canAssigned = false;
            canOwn = false;
        }

        var scope = new TicketScope();

        scope.Predicate =
            "(@CanTeam = 1 " +
            "OR (@CanAssigned = 1 AND " + ticketAlias + ".ticket_keeper_id = @UserId) " +
            "OR (@CanOwn = 1 AND " + ticketAlias + ".ticket_requester_id = @UserId))";

        scope.Parameters.Add(new SqlParameter("@CanTeam", SqlDbType.Bit) { Value = canTeam ? 1 : 0 });
        scope.Parameters.Add(new SqlParameter("@CanAssigned", SqlDbType.Bit) { Value = canAssigned ? 1 : 0 });
        scope.Parameters.Add(new SqlParameter("@CanOwn", SqlDbType.Bit) { Value = canOwn ? 1 : 0 });
        scope.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId ?? 0 });

        return scope;
    }

    private async Task<List<Ticket>> ReadTicketsAsync(string sql, List<SqlParameter>? pars)
    {
        var results = new List<Ticket>();

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

        if (pars != null)
        {
            foreach (var p in pars)
                cmd.Parameters.Add(p);
        }

        await conn.OpenAsync().ConfigureAwait(false);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var t = new Ticket
            {
                Id = Convert.ToInt32(reader["id"]),
                TicketCode = (reader["ticket_code"] as string ?? string.Empty).Trim(),
                TicketRequestDate = reader["ticket_request_date"] as DateTime?,
                TicketLastActionAt = reader["ticket_last_action_at"] as DateTime?,
                TicketClosingDate = reader["ticket_closing_date"] as DateTime?,

                TicketRequesterName = (reader["ticket_requester_name"] as string ?? string.Empty).Trim(),
                TicketCategoryName = (reader["ticket_category_name"] as string ?? string.Empty).Trim(),
                TicketTitle = (reader["ticket_title"] as string ?? string.Empty).Trim(),
                TicketOperatorName = (reader["ticket_operator_name"] as string ?? string.Empty).Trim(),

                TicketRequesterId = reader["ticket_requester_id"] is DBNull ? null : Convert.ToInt32(reader["ticket_requester_id"]),
                TicketKeeperId = reader["ticket_keeper_id"] is DBNull ? null : Convert.ToInt32(reader["ticket_keeper_id"]),

                WeightPriority = reader["ticket_priority_value"] is DBNull ? 0 : Convert.ToInt32(reader["ticket_priority_value"]),

                TicketStatusesName = (reader["ticket_statuses_name"] as string ?? string.Empty).Trim(),
                ProblemDescription = (reader["problem_description"] as string ?? string.Empty),
                ProblemSolution = (reader["problem_solution"] as string ?? string.Empty),

                TicketImpactName = (reader["ticket_impact_name"] as string ?? string.Empty).Trim(),
                TicketUrgencyName = (reader["ticket_urgency_name"] as string ?? string.Empty).Trim()
            };

            results.Add(t);
        }

        return results;
    }

    private async Task<int> ExecuteScalarIntAsync(string sql, List<SqlParameter>? pars)
    {
        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

        if (pars != null)
        {
            foreach (var p in pars)
                cmd.Parameters.Add(p);
        }

        await conn.OpenAsync().ConfigureAwait(false);

        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw == null || raw is DBNull)
            return 0;

        return Convert.ToInt32(raw);
    }
}
