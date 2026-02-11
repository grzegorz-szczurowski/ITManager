// File: Services/Projects/ProjectsRepository.cs
// Description: Dostęp do danych dla modułu Projects (SQL Server, ADO.NET).
// Version: 1.08
// Created: 2026-01-25
// Updated:
// - 2026-01-26 - Lookups: ProjectFormLookupsDto (Statuses/Priorities/Owners + scoring lookups) + scoring mapping.
// - 2026-01-27 - Added: GetProjectStatusNameByIdAsync(projectStatusId) do walidacji etapów biznesowych w serwisie.
// - 2026-01-28 - CLEANUP: usunięcie legacy ProgressPercent z logiki closed_at, brak zapisu progress_percent w Create/Update, uproszczenie zapytań (bez COL_LENGTH).
// - 2026-01-28 - FIX: typy scoringu rzutowane na int (SQL CAST), aby uniknąć błędu Byte -> Int32.
// - 2026-01-28 - Tickets: kandydaci do powiązania (project_id IS NULL) + transakcyjne powiązanie ticketów z projektem.
// Notes:
// - UpdatedAtUtc w liście ticketów projektu mapowana z ticket_request_date (brak t.updated_at w Twoim schemacie).

using Microsoft.Extensions.Configuration;
using ITManager.Models.Projects;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace ITManager.Services.Projects
{
    public sealed class ProjectsRepository
    {
        private readonly string _connectionString;

        public ProjectsRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("ITManagerConnection")
                ?? throw new InvalidOperationException("Missing connection string: ITManagerConnection");
        }

        public async Task<ProjectFormLookupsDto> GetProjectFormLookupsAsync()
        {
            var dto = new ProjectFormLookupsDto();

            var sqlStatuses = @"
SELECT s.id, s.name
FROM dbo.project_statuses s
ORDER BY
    ISNULL(s.sort_order, 0) ASC,
    s.name ASC;";

            var sqlPriorities = @"
SELECT p.id, p.name
FROM dbo.project_priorities p
ORDER BY
    ISNULL(p.sort_order, 0) ASC,
    p.name ASC;";

            var sqlOwners = @"
SELECT u.id, ISNULL(u.DisplayName, (N'#' + CAST(u.id AS NVARCHAR(20)))) AS name
FROM dbo.users u
WHERE ISNULL(u.IsActive, 1) = 1
ORDER BY ISNULL(u.DisplayName, (N'#' + CAST(u.id AS NVARCHAR(20)))) ASC;";

            var sqlImpacts = @"
IF OBJECT_ID('dbo.project_impact_lookup', 'U') IS NULL
BEGIN
    SELECT 1 AS id, N'Low' AS name
    UNION ALL SELECT 2, N'Medium'
    UNION ALL SELECT 3, N'High'
    UNION ALL SELECT 4, N'Very high'
    UNION ALL SELECT 5, N'Critical'
    ORDER BY id;
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.project_impact_lookup','sort_order') IS NULL
        SELECT id, name FROM dbo.project_impact_lookup ORDER BY id;
    ELSE
        SELECT id, name FROM dbo.project_impact_lookup ORDER BY ISNULL(sort_order, id), id;
END;";

            var sqlUrgencies = @"
IF OBJECT_ID('dbo.project_urgency_lookup', 'U') IS NULL
BEGIN
    SELECT 1 AS id, N'Low' AS name
    UNION ALL SELECT 2, N'Normal'
    UNION ALL SELECT 3, N'High'
    UNION ALL SELECT 4, N'ASAP'
    UNION ALL SELECT 5, N'Immediate'
    ORDER BY id;
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.project_urgency_lookup','sort_order') IS NULL
        SELECT id, name FROM dbo.project_urgency_lookup ORDER BY id;
    ELSE
        SELECT id, name FROM dbo.project_urgency_lookup ORDER BY ISNULL(sort_order, id), id;
END;";

            var sqlScopes = @"
IF OBJECT_ID('dbo.project_scope_lookup', 'U') IS NULL
BEGIN
    SELECT 1 AS id, N'Small' AS name
    UNION ALL SELECT 2, N'Medium'
    UNION ALL SELECT 3, N'Large'
    ORDER BY id;
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.project_scope_lookup','sort_order') IS NULL
        SELECT id, name FROM dbo.project_scope_lookup ORDER BY id;
    ELSE
        SELECT id, name FROM dbo.project_scope_lookup ORDER BY ISNULL(sort_order, id), id;
END;";

            var sqlDelayCosts = @"
IF OBJECT_ID('dbo.project_delay_cost_lookup', 'U') IS NULL
BEGIN
    SELECT 1 AS id, N'Low' AS name
    UNION ALL SELECT 2, N'Medium'
    UNION ALL SELECT 3, N'High'
    ORDER BY id;
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.project_delay_cost_lookup','sort_order') IS NULL
        SELECT id, name FROM dbo.project_delay_cost_lookup ORDER BY id;
    ELSE
        SELECT id, name FROM dbo.project_delay_cost_lookup ORDER BY ISNULL(sort_order, id), id;
END;";

            var sqlEfforts = @"
IF OBJECT_ID('dbo.project_effort_lookup', 'U') IS NULL
BEGIN
    SELECT 1 AS id, N'Small' AS name
    UNION ALL SELECT 2, N'Medium'
    UNION ALL SELECT 3, N'Large'
    ORDER BY id;
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.project_effort_lookup','sort_order') IS NULL
        SELECT id, name FROM dbo.project_effort_lookup ORDER BY id;
    ELSE
        SELECT id, name FROM dbo.project_effort_lookup ORDER BY ISNULL(sort_order, id), id;
END;";

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            dto.Statuses = await ReadLookupAsync(conn, sqlStatuses).ConfigureAwait(false);
            dto.Priorities = await ReadLookupAsync(conn, sqlPriorities).ConfigureAwait(false);
            dto.Owners = await ReadLookupAsync(conn, sqlOwners).ConfigureAwait(false);

            dto.Impacts = await ReadLookupAsync(conn, sqlImpacts).ConfigureAwait(false);
            dto.Urgencies = await ReadLookupAsync(conn, sqlUrgencies).ConfigureAwait(false);
            dto.Scopes = await ReadLookupAsync(conn, sqlScopes).ConfigureAwait(false);
            dto.DelayCosts = await ReadLookupAsync(conn, sqlDelayCosts).ConfigureAwait(false);
            dto.Efforts = await ReadLookupAsync(conn, sqlEfforts).ConfigureAwait(false);

            return dto;
        }

        public async Task<string?> GetProjectStatusNameByIdAsync(int projectStatusId)
        {
            var sql = @"
SELECT TOP (1) s.name
FROM dbo.project_statuses s
WHERE s.id = @Id;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", projectStatusId);

            await conn.OpenAsync().ConfigureAwait(false);
            var val = await cmd.ExecuteScalarAsync().ConfigureAwait(false);

            return val == null || val == DBNull.Value ? null : Convert.ToString(val);
        }

        public async Task<List<ProjectLinkableTicketDto>> GetTicketsLinkCandidatesAsync(int projectId, int currentUserId, bool canViewAll, string search)
        {
            var result = new List<ProjectLinkableTicketDto>();

            var sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.projects p
    WHERE p.id = @ProjectId
      AND p.is_active = 1
      AND
      (
          @CanViewAll = 1
          OR EXISTS
          (
              SELECT 1
              FROM dbo.project_members pm
              WHERE pm.project_id = p.id
                AND pm.user_id = @UserId
                AND pm.is_active = 1
          )
      )
)
BEGIN
    RAISERROR(N'Project not found or no access.', 16, 1);
    RETURN;
END;

DECLARE @Search NVARCHAR(200) = @SearchParam;

SELECT TOP (200)
    t.id AS ticket_id,
    ISNULL(t.ticket_title, N'') AS title,
    ISNULL(ts.name, N'') AS status_name,
    CAST(t.ticket_priority_value AS NVARCHAR(20)) AS priority_name,
    ISNULL(ku.DisplayName, N'') AS assigned_to_display_name
FROM dbo.tickets t
LEFT JOIN dbo.ticket_statuses ts ON ts.id = t.ticket_status_id
LEFT JOIN dbo.users ku ON ku.id = t.ticket_keeper_id
WHERE
    t.project_id IS NULL
    AND
    (
        @Search IS NULL
        OR LTRIM(RTRIM(@Search)) = N''
        OR CAST(t.id AS NVARCHAR(30)) LIKE N'%' + @Search + N'%'
        OR ISNULL(t.ticket_title, N'') LIKE N'%' + @Search + N'%'
        OR ISNULL(ts.name, N'') LIKE N'%' + @Search + N'%'
        OR CAST(t.ticket_priority_value AS NVARCHAR(20)) LIKE N'%' + @Search + N'%'
        OR ISNULL(ku.DisplayName, N'') LIKE N'%' + @Search + N'%'
    )
ORDER BY
    t.ticket_request_date DESC;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);

            cmd.Parameters.AddWithValue("@ProjectId", projectId);
            cmd.Parameters.AddWithValue("@UserId", currentUserId);
            cmd.Parameters.AddWithValue("@CanViewAll", canViewAll ? 1 : 0);
            cmd.Parameters.AddWithValue("@SearchParam", (object?)search ?? DBNull.Value);

            await conn.OpenAsync().ConfigureAwait(false);

            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new ProjectLinkableTicketDto
                {
                    TicketId = reader.GetInt64(reader.GetOrdinal("ticket_id")),
                    Title = reader.IsDBNull(reader.GetOrdinal("title")) ? string.Empty : reader.GetString(reader.GetOrdinal("title")),
                    StatusName = reader.IsDBNull(reader.GetOrdinal("status_name")) ? string.Empty : reader.GetString(reader.GetOrdinal("status_name")),
                    PriorityName = reader.IsDBNull(reader.GetOrdinal("priority_name")) ? string.Empty : reader.GetString(reader.GetOrdinal("priority_name")),
                    AssignedToDisplayName = reader.IsDBNull(reader.GetOrdinal("assigned_to_display_name")) ? string.Empty : reader.GetString(reader.GetOrdinal("assigned_to_display_name"))
                });
            }

            return result;
        }

        public async Task LinkTicketsToProjectAsync(int projectId, List<long> ticketIds, int actorUserId, string? actorDisplayName, bool canViewAll)
        {
            if (ticketIds == null || ticketIds.Count == 0)
                throw new ArgumentException("ticketIds is required.", nameof(ticketIds));

            var ids = ticketIds.Where(x => x > 0).Distinct().ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException("No valid ticket ids.");

            const int chunkSize = 900;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using var tx = conn.BeginTransaction();

            try
            {
                await EnsureProjectAccessOrThrowAsync(conn, tx, projectId, actorUserId, canViewAll).ConfigureAwait(false);

                for (var offset = 0; offset < ids.Count; offset += chunkSize)
                {
                    var chunk = ids.Skip(offset).Take(chunkSize).ToList();

                    var parameters = new List<SqlParameter>
                    {
                        new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }
                    };

                    var inClause = BuildInClause(chunk, parameters, prefix: "@Tid");

                    var sql = $@"
UPDATE t
SET
    t.project_id = @ProjectId
FROM dbo.tickets t
WHERE
    t.project_id IS NULL
    AND t.id IN ({inClause});";

                    using var cmd = new SqlCommand(sql, conn, tx);
                    cmd.Parameters.AddRange(parameters.ToArray());

                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        private static async Task EnsureProjectAccessOrThrowAsync(SqlConnection openConn, SqlTransaction tx, int projectId, int userId, bool canViewAll)
        {
            var sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.projects p
    WHERE p.id = @ProjectId
      AND p.is_active = 1
      AND
      (
          @CanViewAll = 1
          OR EXISTS
          (
              SELECT 1
              FROM dbo.project_members pm
              WHERE pm.project_id = p.id
                AND pm.user_id = @UserId
                AND pm.is_active = 1
          )
      )
)
BEGIN
    RAISERROR(N'Project not found or no access.', 16, 1);
    RETURN;
END;";

            using var cmd = new SqlCommand(sql, openConn, tx);
            cmd.Parameters.AddWithValue("@ProjectId", projectId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@CanViewAll", canViewAll ? 1 : 0);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static string BuildInClause(List<long> ids, List<SqlParameter> parameters, string prefix)
        {
            var names = new List<string>(ids.Count);

            for (var i = 0; i < ids.Count; i++)
            {
                var pName = prefix + i;
                names.Add(pName);

                parameters.Add(new SqlParameter(pName, SqlDbType.BigInt) { Value = ids[i] });
            }

            return string.Join(",", names);
        }

        public async Task<List<ProjectListItemDto>> GetProjectsForUserAsync(int currentUserId, bool canViewAll)
        {
            var result = new List<ProjectListItemDto>();

            var sql = @"
SELECT
    p.id,
    p.name,
    p.project_status_id,
    ps.name AS project_status_name,
    p.project_priority_id,
    pp.name AS project_priority_name,
    p.owner_user_id,
    ISNULL(u.DisplayName, (N'#' + CAST(p.owner_user_id AS NVARCHAR(20)))) AS owner_display_name,
    p.planned_start_date,
    p.deadline_date,
    p.updated_at AS updated_at_utc,
    p.updated_by,
    p.is_active,
    ISNULL(ot.open_tickets_count, 0) AS open_tickets_count,

    CAST(ISNULL(p.impact, 3) AS int) AS impact,
    CAST(ISNULL(p.urgency, 3) AS int) AS urgency,
    CAST(ISNULL(p.scope, 2) AS int) AS scope,
    CAST(ISNULL(p.cost_of_delay, 2) AS int) AS cost_of_delay,
    CAST(ISNULL(p.effort, 2) AS int) AS effort,

    CAST(
        (
            CAST(ISNULL(p.impact, 3) AS int) * CAST(ISNULL(p.urgency, 3) AS int)
        )
        + CAST(ISNULL(p.scope, 2) AS int)
        + CAST(ISNULL(p.cost_of_delay, 2) AS int)
        - CAST(ISNULL(p.effort, 2) AS int)
    AS int) AS priority_score
FROM dbo.projects p
INNER JOIN dbo.project_statuses ps ON ps.id = p.project_status_id
INNER JOIN dbo.project_priorities pp ON pp.id = p.project_priority_id
LEFT JOIN dbo.users u ON u.id = p.owner_user_id
LEFT JOIN
(
    SELECT
        t.project_id,
        COUNT(1) AS open_tickets_count
    FROM dbo.tickets t
    WHERE t.project_id IS NOT NULL
      AND t.ticket_closing_date IS NULL
    GROUP BY t.project_id
) ot ON ot.project_id = p.id
WHERE
    p.is_active = 1
    AND
    (
        @CanViewAll = 1
        OR EXISTS
        (
            SELECT 1
            FROM dbo.project_members pm
            WHERE pm.project_id = p.id
              AND pm.user_id = @UserId
              AND pm.is_active = 1
        )
    )
ORDER BY
    ISNULL(pp.sort_order, 0) DESC,
    p.deadline_date ASC,
    p.updated_at DESC;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", currentUserId);
            cmd.Parameters.AddWithValue("@CanViewAll", canViewAll ? 1 : 0);

            await conn.OpenAsync().ConfigureAwait(false);
            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection).ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new ProjectListItemDto
                {
                    Id = reader.GetInt32(reader.GetOrdinal("id")),
                    Name = reader.GetString(reader.GetOrdinal("name")),
                    ProjectStatusId = reader.GetInt32(reader.GetOrdinal("project_status_id")),
                    ProjectStatusName = reader.GetString(reader.GetOrdinal("project_status_name")),
                    ProjectPriorityId = reader.GetInt32(reader.GetOrdinal("project_priority_id")),
                    ProjectPriorityName = reader.GetString(reader.GetOrdinal("project_priority_name")),
                    OwnerUserId = reader.GetInt32(reader.GetOrdinal("owner_user_id")),
                    OwnerDisplayName = reader.GetString(reader.GetOrdinal("owner_display_name")),
                    PlannedStartDate = reader.IsDBNull(reader.GetOrdinal("planned_start_date")) ? null : reader.GetDateTime(reader.GetOrdinal("planned_start_date")),
                    DeadlineDate = reader.IsDBNull(reader.GetOrdinal("deadline_date")) ? null : reader.GetDateTime(reader.GetOrdinal("deadline_date")),
                    OpenTicketsCount = reader.GetInt32(reader.GetOrdinal("open_tickets_count")),
                    UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc")),
                    UpdatedBy = reader.IsDBNull(reader.GetOrdinal("updated_by")) ? null : reader.GetString(reader.GetOrdinal("updated_by")),
                    IsActive = reader.GetBoolean(reader.GetOrdinal("is_active")),

                    Impact = reader.GetInt32(reader.GetOrdinal("impact")),
                    Urgency = reader.GetInt32(reader.GetOrdinal("urgency")),
                    Scope = reader.GetInt32(reader.GetOrdinal("scope")),
                    CostOfDelay = reader.GetInt32(reader.GetOrdinal("cost_of_delay")),
                    Effort = reader.GetInt32(reader.GetOrdinal("effort")),
                    PriorityScore = reader.GetInt32(reader.GetOrdinal("priority_score"))
                });
            }

            return result;
        }

        public async Task<ProjectDetailsDto?> GetProjectDetailsAsync(int projectId, int currentUserId, bool canViewAll)
        {
            ProjectDetailsDto? project = null;

            var sqlProject = @"
SELECT
    p.id,
    p.name,
    p.description,
    p.project_status_id,
    ps.name AS project_status_name,
    p.project_priority_id,
    pp.name AS project_priority_name,
    p.owner_user_id,
    ISNULL(u.DisplayName, (N'#' + CAST(p.owner_user_id AS NVARCHAR(20)))) AS owner_display_name,
    p.planned_start_date,
    p.deadline_date,
    p.closed_at AS closed_at_utc,
    p.last_update_note,
    p.created_at AS created_at_utc,
    p.created_by,
    p.updated_at AS updated_at_utc,
    p.updated_by,
    p.is_active,

    CAST(ISNULL(p.impact, 3) AS int) AS impact,
    CAST(ISNULL(p.urgency, 3) AS int) AS urgency,
    CAST(ISNULL(p.scope, 2) AS int) AS scope,
    CAST(ISNULL(p.cost_of_delay, 2) AS int) AS cost_of_delay,
    CAST(ISNULL(p.effort, 2) AS int) AS effort,

    CAST(
        (
            CAST(ISNULL(p.impact, 3) AS int) * CAST(ISNULL(p.urgency, 3) AS int)
        )
        + CAST(ISNULL(p.scope, 2) AS int)
        + CAST(ISNULL(p.cost_of_delay, 2) AS int)
        - CAST(ISNULL(p.effort, 2) AS int)
    AS int) AS priority_score
FROM dbo.projects p
INNER JOIN dbo.project_statuses ps ON ps.id = p.project_status_id
INNER JOIN dbo.project_priorities pp ON pp.id = p.project_priority_id
LEFT JOIN dbo.users u ON u.id = p.owner_user_id
WHERE
    p.id = @ProjectId
    AND p.is_active = 1
    AND
    (
        @CanViewAll = 1
        OR EXISTS
        (
            SELECT 1
            FROM dbo.project_members pm
            WHERE pm.project_id = p.id
              AND pm.user_id = @UserId
              AND pm.is_active = 1
        )
    );";

            var sqlTickets = @"
SELECT
    t.id AS ticket_id,
    ISNULL(t.ticket_title, N'') AS title,
    ISNULL(ts.name, N'') AS status_name,
    CAST(t.ticket_priority_value AS NVARCHAR(20)) AS priority_name,
    t.ticket_request_date AS updated_at_utc,
    ku.DisplayName AS assigned_to_display_name
FROM dbo.tickets t
LEFT JOIN dbo.ticket_statuses ts ON ts.id = t.ticket_status_id
LEFT JOIN dbo.users ku ON ku.id = t.ticket_keeper_id
WHERE
    t.project_id = @ProjectId
ORDER BY
    t.ticket_request_date DESC;";

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using (var cmd = new SqlCommand(sqlProject, conn))
            {
                cmd.Parameters.AddWithValue("@ProjectId", projectId);
                cmd.Parameters.AddWithValue("@UserId", currentUserId);
                cmd.Parameters.AddWithValue("@CanViewAll", canViewAll ? 1 : 0);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    project = new ProjectDetailsDto
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("id")),
                        Name = reader.GetString(reader.GetOrdinal("name")),
                        Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                        ProjectStatusId = reader.GetInt32(reader.GetOrdinal("project_status_id")),
                        ProjectStatusName = reader.GetString(reader.GetOrdinal("project_status_name")),
                        ProjectPriorityId = reader.GetInt32(reader.GetOrdinal("project_priority_id")),
                        ProjectPriorityName = reader.GetString(reader.GetOrdinal("project_priority_name")),
                        OwnerUserId = reader.GetInt32(reader.GetOrdinal("owner_user_id")),
                        OwnerDisplayName = reader.GetString(reader.GetOrdinal("owner_display_name")),
                        PlannedStartDate = reader.IsDBNull(reader.GetOrdinal("planned_start_date")) ? null : reader.GetDateTime(reader.GetOrdinal("planned_start_date")),
                        DeadlineDate = reader.IsDBNull(reader.GetOrdinal("deadline_date")) ? null : reader.GetDateTime(reader.GetOrdinal("deadline_date")),
                        ClosedAtUtc = reader.IsDBNull(reader.GetOrdinal("closed_at_utc")) ? null : reader.GetDateTime(reader.GetOrdinal("closed_at_utc")),
                        LastUpdateNote = reader.IsDBNull(reader.GetOrdinal("last_update_note")) ? null : reader.GetString(reader.GetOrdinal("last_update_note")),
                        CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
                        CreatedBy = reader.IsDBNull(reader.GetOrdinal("created_by")) ? null : reader.GetString(reader.GetOrdinal("created_by")),
                        UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc")),
                        UpdatedBy = reader.IsDBNull(reader.GetOrdinal("updated_by")) ? null : reader.GetString(reader.GetOrdinal("updated_by")),
                        IsActive = reader.GetBoolean(reader.GetOrdinal("is_active")),

                        Impact = reader.GetInt32(reader.GetOrdinal("impact")),
                        Urgency = reader.GetInt32(reader.GetOrdinal("urgency")),
                        Scope = reader.GetInt32(reader.GetOrdinal("scope")),
                        CostOfDelay = reader.GetInt32(reader.GetOrdinal("cost_of_delay")),
                        Effort = reader.GetInt32(reader.GetOrdinal("effort")),
                        PriorityScore = reader.GetInt32(reader.GetOrdinal("priority_score"))
                    };
                }
            }

            if (project == null)
                return null;

            using (var cmd = new SqlCommand(sqlTickets, conn))
            {
                cmd.Parameters.AddWithValue("@ProjectId", projectId);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    project.Tickets.Add(new ProjectTicketListItemDto
                    {
                        TicketId = reader.GetInt64(reader.GetOrdinal("ticket_id")),
                        Title = reader.IsDBNull(reader.GetOrdinal("title")) ? string.Empty : reader.GetString(reader.GetOrdinal("title")),
                        StatusName = reader.IsDBNull(reader.GetOrdinal("status_name")) ? string.Empty : reader.GetString(reader.GetOrdinal("status_name")),
                        PriorityName = reader.IsDBNull(reader.GetOrdinal("priority_name")) ? string.Empty : reader.GetString(reader.GetOrdinal("priority_name")),
                        AssignedToDisplayName = reader.IsDBNull(reader.GetOrdinal("assigned_to_display_name")) ? null : reader.GetString(reader.GetOrdinal("assigned_to_display_name")),
                        UpdatedAtUtc = reader.IsDBNull(reader.GetOrdinal("updated_at_utc")) ? DateTime.MinValue : reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
                    });
                }
            }

            return project;
        }

        public async Task<List<ProjectLookupItemDto>> GetProjectsLookupForUserAsync(int currentUserId, bool canViewAll)
        {
            var result = new List<ProjectLookupItemDto>();

            var sql = @"
SELECT
    p.id,
    p.name,
    p.project_status_id,
    ps.name AS project_status_name
FROM dbo.projects p
INNER JOIN dbo.project_statuses ps ON ps.id = p.project_status_id
WHERE
    p.is_active = 1
    AND
    (
        @CanViewAll = 1
        OR EXISTS
        (
            SELECT 1
            FROM dbo.project_members pm
            WHERE pm.project_id = p.id
              AND pm.user_id = @UserId
              AND pm.is_active = 1
        )
    )
ORDER BY p.name ASC;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", currentUserId);
            cmd.Parameters.AddWithValue("@CanViewAll", canViewAll ? 1 : 0);

            await conn.OpenAsync().ConfigureAwait(false);
            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection).ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new ProjectLookupItemDto
                {
                    Id = reader.GetInt32(reader.GetOrdinal("id")),
                    Name = reader.GetString(reader.GetOrdinal("name")),
                    ProjectStatusId = reader.GetInt32(reader.GetOrdinal("project_status_id")),
                    ProjectStatusName = reader.GetString(reader.GetOrdinal("project_status_name"))
                });
            }

            return result;
        }

        public async Task<int> CreateProjectAsync(ProjectUpsertRequest request, int createdByUserId, string? createdBy)
        {
            var ownerUserId = request.OwnerUserId > 0 ? request.OwnerUserId : createdByUserId;

            var sql = @"
INSERT INTO dbo.projects
(
    name,
    description,
    project_status_id,
    project_priority_id,
    owner_user_id,
    planned_start_date,
    deadline_date,
    closed_at,
    last_update_note,
    created_at,
    created_by,
    updated_at,
    updated_by,
    is_active,
    impact,
    urgency,
    scope,
    cost_of_delay,
    effort
)
OUTPUT INSERTED.id
VALUES
(
    @Name,
    @Description,
    @ProjectStatusId,
    @ProjectPriorityId,
    @OwnerUserId,
    @PlannedStartDate,
    @DeadlineDate,
    NULL,
    @LastUpdateNote,
    SYSUTCDATETIME(),
    @CreatedBy,
    SYSUTCDATETIME(),
    @CreatedBy,
    @IsActive,
    @Impact,
    @Urgency,
    @Scope,
    @CostOfDelay,
    @Effort
);";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);

            cmd.Parameters.AddWithValue("@Name", (request.Name ?? string.Empty).Trim());
            cmd.Parameters.AddWithValue("@Description", (object?)request.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ProjectStatusId", request.ProjectStatusId);
            cmd.Parameters.AddWithValue("@ProjectPriorityId", request.ProjectPriorityId);
            cmd.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
            cmd.Parameters.AddWithValue("@PlannedStartDate", (object?)request.PlannedStartDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DeadlineDate", (object?)request.DeadlineDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LastUpdateNote", (object?)request.LastUpdateNote ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IsActive", request.IsActive);

            cmd.Parameters.AddWithValue("@Impact", request.Impact);
            cmd.Parameters.AddWithValue("@Urgency", request.Urgency);
            cmd.Parameters.AddWithValue("@Scope", request.Scope);
            cmd.Parameters.AddWithValue("@CostOfDelay", request.CostOfDelay);
            cmd.Parameters.AddWithValue("@Effort", request.Effort);

            await conn.OpenAsync().ConfigureAwait(false);
            var newIdObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);

            if (newIdObj == null || newIdObj == DBNull.Value)
                throw new InvalidOperationException("Failed to create project (no id returned).");

            var newId = Convert.ToInt32(newIdObj);

            await EnsureOwnerIsMemberAsync(conn, newId, ownerUserId, createdBy).ConfigureAwait(false);

            return newId;
        }

        public async Task UpdateProjectAsync(ProjectUpsertRequest request, string? updatedBy)
        {
            if (request.Id == null)
                throw new ArgumentException("Project id is required for update.", nameof(request));

            var sql = @"
UPDATE dbo.projects
SET
    name = @Name,
    description = @Description,
    project_status_id = @ProjectStatusId,
    project_priority_id = @ProjectPriorityId,
    owner_user_id = @OwnerUserId,
    planned_start_date = @PlannedStartDate,
    deadline_date = @DeadlineDate,
    last_update_note = @LastUpdateNote,
    updated_at = SYSUTCDATETIME(),
    updated_by = @UpdatedBy,
    is_active = @IsActive,
    impact = @Impact,
    urgency = @Urgency,
    scope = @Scope,
    cost_of_delay = @CostOfDelay,
    effort = @Effort
WHERE
    id = @Id;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);

            cmd.Parameters.AddWithValue("@Id", request.Id.Value);
            cmd.Parameters.AddWithValue("@Name", (request.Name ?? string.Empty).Trim());
            cmd.Parameters.AddWithValue("@Description", (object?)request.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ProjectStatusId", request.ProjectStatusId);
            cmd.Parameters.AddWithValue("@ProjectPriorityId", request.ProjectPriorityId);
            cmd.Parameters.AddWithValue("@OwnerUserId", request.OwnerUserId);
            cmd.Parameters.AddWithValue("@PlannedStartDate", (object?)request.PlannedStartDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DeadlineDate", (object?)request.DeadlineDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LastUpdateNote", (object?)request.LastUpdateNote ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedBy", (object?)updatedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IsActive", request.IsActive);

            cmd.Parameters.AddWithValue("@Impact", request.Impact);
            cmd.Parameters.AddWithValue("@Urgency", request.Urgency);
            cmd.Parameters.AddWithValue("@Scope", request.Scope);
            cmd.Parameters.AddWithValue("@CostOfDelay", request.CostOfDelay);
            cmd.Parameters.AddWithValue("@Effort", request.Effort);

            await conn.OpenAsync().ConfigureAwait(false);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            await EnsureOwnerIsMemberAsync(conn, request.Id.Value, request.OwnerUserId, updatedBy).ConfigureAwait(false);
        }

        private static async Task EnsureOwnerIsMemberAsync(SqlConnection openConnection, int projectId, int ownerUserId, string? createdByOrUpdatedBy)
        {
            var sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.project_members pm
    WHERE pm.project_id = @ProjectId
      AND pm.user_id = @OwnerUserId
      AND pm.is_active = 1
)
BEGIN
    INSERT INTO dbo.project_members
    (
        project_id,
        user_id,
        role_in_project,
        is_active,
        created_at,
        created_by
    )
    VALUES
    (
        @ProjectId,
        @OwnerUserId,
        N'Owner',
        1,
        SYSUTCDATETIME(),
        @CreatedBy
    );
END;";

            using var cmd = new SqlCommand(sql, openConnection);
            cmd.Parameters.AddWithValue("@ProjectId", projectId);
            cmd.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdByOrUpdatedBy ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task<List<LookupItemDto>> ReadLookupAsync(SqlConnection openConn, string sql)
        {
            var list = new List<LookupItemDto>();

            using var cmd = new SqlCommand(sql, openConn);
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new LookupItemDto
                {
                    Id = r.GetInt32(r.GetOrdinal("id")),
                    Name = r.IsDBNull(r.GetOrdinal("name")) ? string.Empty : r.GetString(r.GetOrdinal("name"))
                });
            }

            return list;
        }
    }
}
