// File: Services/UsersService.cs
// Version: 1.17
// Change history:
// 1.02 (2025-12-30) - Added: GetUserEditDataAsync(userId)
// 1.03 (2025-12-30) - Added: UpdateUserAsync(model)
// 1.04 (2025-12-30) - FIX: UpdateUserAsync zapisuje tylko pola lokalne (AD pola nie są modyfikowane)
// 1.05 (2025-12-30) - REFACTOR: DTO przeniesione do UsersService (bez zależności od UI)
// 1.06 (2025-12-30) - FIX: GetUserEditDataAsync zwraca powiązane Devices i Tickets (vw_devices, vw_tickets)
// 1.07 (2025-12-30) - FIX: RelatedTickets działa poprawnie (parametr @UserId)
// 1.08 (2025-12-30) - FIX: RelatedDevices działa poprawnie (vw_devices kolumny + filtr po user_id)
// 1.09 (2026-01-08) - Added: GetActiveUserDisplayNamesAsync (dla TicketEdit requester lookup)
// 1.10 (2026-01-08) - FIX: usunięto odwołania do kolumny dbo.users.Status (kolumna usunięta z bazy)
// 1.11 (2026-01-08) - Added: Account types dictionary (dbo.account_types) + update AccountTypeId w dbo.users
// 1.12 (2026-01-08) - FIX: GetUsersAsync zwraca AccountTypeId/Name/Code (JOIN dbo.account_types) -> grid nie jest pusty
// 1.13 (2026-01-21) - NEW: RelatedDevices zwraca SerialNumber (Equipment grid)
// 1.14 (2026-01-25) - RBAC: backend guard dla Assets.Users.View/Edit + bezpieczny guard dla lookupów w ticketach
// 1.15 (2026-01-27) - CHANGE: IsOperator usunięte z dbo.users. Operator wynika z dbo.UserRoles/dbo.Roles.
//                       Lookup operatorów z dbo.vw_ticket_operators. UpdateUserAsync zarządza wpisem w dbo.UserRoles.
// 1.16 (2026-01-27) - FIX: usunięto legacy odwołanie do nieistniejącej kolumny dbo.users.role_id w GetUsersAsync.
// 1.17 (2026-02-02) - RBAC: migracja lookupów do nowej architektury tickets permissions (Tickets.View.* / Tickets.Edit.* / Tickets.Create.Own / Tickets.Assign.Team).

using ITManager.Models;
using ITManager.Models.Auth;
using ITManager.Services.Auth;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace ITManager.Services
{
    public sealed class UsersService
    {
        private readonly string _connectionString;
        private readonly CurrentUserContextService _currentUserContextService;

        private const string PermUsersView = "Assets.Users.View";
        private const string PermUsersEdit = "Assets.Users.Edit";

        // =========================
        // Tickets RBAC (nowa architektura)
        // =========================
        private const string PermViewOwn = "Tickets.View.Own";
        private const string PermViewAssigned = "Tickets.View.Assigned";
        private const string PermViewTeam = "Tickets.View.Team";

        private const string PermCreateOwn = "Tickets.Create.Own";

        private const string PermEditOwn = "Tickets.Edit.Own";
        private const string PermEditAssigned = "Tickets.Edit.Assigned";
        private const string PermEditTeam = "Tickets.Edit.Team";
        private const string PermEditAll = "Tickets.Edit.All";

        private const string PermAssignTeam = "Tickets.Assign.Team";

        private const string RoleItOperatorName = "IT Operator";

        public UsersService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));

            _connectionString = configuration.GetConnectionString("ITManagerConnection")
                ?? throw new InvalidOperationException("Missing connection string: ITManagerConnection");
        }

        // =========================
        // RBAC helpers
        // =========================

        private async Task EnsureUserContextInitializedAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
        }

        private static bool HasAny(CurrentUserContext ctx, params string[] perms)
        {
            if (ctx == null || perms == null || perms.Length == 0)
                return false;

            foreach (var p in perms)
            {
                if (!string.IsNullOrWhiteSpace(p) && ctx.Has(p))
                    return true;
            }

            return false;
        }

        private async Task GuardCanViewUsersAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has(PermUsersView))
                throw new UnauthorizedAccessException("Brak uprawnień do podglądu użytkowników.");
        }

        private async Task GuardCanEditUsersAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has(PermUsersEdit))
                throw new UnauthorizedAccessException("Brak uprawnień do edycji użytkowników.");
        }

        private async Task GuardCanReadUserLookupsForTicketsAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                throw new UnauthorizedAccessException("Brak uprawnień.");

            // Lookupi userów są potrzebne do Tickets (requester/operator),
            // ale dopuszczamy też wejście od strony Assets.Users.*
            var allowed =
                ctx.Has(PermUsersView) ||
                ctx.Has(PermUsersEdit) ||
                HasAny(ctx,
                    PermViewOwn, PermViewAssigned, PermViewTeam,
                    PermCreateOwn,
                    PermEditOwn, PermEditAssigned, PermEditTeam, PermEditAll,
                    PermAssignTeam);

            if (!allowed)
                throw new UnauthorizedAccessException("Brak uprawnień do listy użytkowników (lookup).");
        }

        public async Task<List<string>> GetOperatorDisplayNamesAsync()
        {
            await GuardCanReadUserLookupsForTicketsAsync().ConfigureAwait(false);

            var results = new List<string>();

            // Operatorzy wynikają z ról. Widok dbo.vw_ticket_operators jest jednym miejscem prawdy.
            const string sql = @"
SELECT
    LTRIM(RTRIM(DisplayName)) AS DisplayName
FROM dbo.vw_ticket_operators
WHERE
    DisplayName IS NOT NULL
    AND LTRIM(RTRIM(DisplayName)) <> ''
ORDER BY
    DisplayName;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

            await conn.OpenAsync().ConfigureAwait(false);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var displayName = reader["DisplayName"] as string;
                if (!string.IsNullOrWhiteSpace(displayName))
                    results.Add(displayName.Trim());
            }

            return results;
        }

        // requester lookup dla TicketEdit
        public async Task<List<string>> GetActiveUserDisplayNamesAsync()
        {
            await GuardCanReadUserLookupsForTicketsAsync().ConfigureAwait(false);

            var results = new List<string>();

            const string sql = @"
SELECT
    LTRIM(RTRIM(DisplayName)) AS DisplayName
FROM dbo.users
WHERE
    (IsActive = 1 OR IsActive IS NULL)
    AND DisplayName IS NOT NULL
    AND LTRIM(RTRIM(DisplayName)) <> ''
ORDER BY
    DisplayName;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text, CommandTimeout = 30 };

            await conn.OpenAsync().ConfigureAwait(false);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var displayName = reader["DisplayName"] as string;
                if (!string.IsNullOrWhiteSpace(displayName))
                    results.Add(displayName.Trim());
            }

            return results;
        }

        // Słownik typów kont (dbo.account_types)
        public async Task<List<AccountTypeDto>> GetAccountTypesAsync(bool onlyActive = true)
        {
            await GuardCanEditUsersAsync().ConfigureAwait(false);

            var list = new List<AccountTypeDto>();

            const string sql = @"
SELECT
    at.id,
    ISNULL(at.code, '') AS Code,
    ISNULL(at.name, '') AS Name,
    ISNULL(at.description, '') AS Description,
    at.is_active AS IsActive
FROM dbo.account_types at
WHERE
    (@OnlyActive = 0) OR (at.is_active = 1)
ORDER BY
    ISNULL(at.name, ''),
    at.id;";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn)
            {
                CommandType = CommandType.Text,
                CommandTimeout = 30
            };

            cmd.Parameters.Add(new SqlParameter("@OnlyActive", SqlDbType.Bit) { Value = onlyActive ? 1 : 0 });

            await conn.OpenAsync().ConfigureAwait(false);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new AccountTypeDto
                {
                    Id = SafeGetInt(reader, "id"),
                    Code = SafeGetString(reader, "Code"),
                    Name = SafeGetString(reader, "Name"),
                    Description = SafeGetString(reader, "Description"),
                    IsActive = SafeGetBoolNullable(reader, "IsActive")
                });
            }

            return list;
        }

        public async Task<UsersQueryResult> GetUsersAsync()
        {
            await GuardCanViewUsersAsync().ConfigureAwait(false);

            // CHANGE: IsOperator jest wyliczany z ról (vw_ticket_operators).
            // FIX: dbo.users.role_id nie istnieje, więc nie może być częścią listy.
            const string sql = @"
SELECT
    u.id,
    ISNULL(u.DisplayName, '')       AS DisplayName,
    ISNULL(u.GivenName, '')         AS GivenName,
    ISNULL(u.Surname, '')           AS Surname,
    ISNULL(u.EmailAddress, '')      AS EmailAddress,
    ISNULL(u.TelephoneNumber, '')   AS TelephoneNumber,
    ISNULL(u.Mobile, '')            AS Mobile,
    ISNULL(u.Department, '')        AS Department,
    ISNULL(u.Title, '')             AS Title,
    u.IsActive,

    CASE WHEN op.UserId IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS IsOperator,

    u.AccountTypeId                 AS AccountTypeId,
    ISNULL(at.name, '')             AS AccountTypeName,
    ISNULL(at.code, '')             AS AccountTypeCode
FROM dbo.users u
LEFT JOIN dbo.account_types at
    ON at.id = u.AccountTypeId
LEFT JOIN dbo.vw_ticket_operators op
    ON op.UserId = u.id
ORDER BY
    ISNULL(u.DisplayName, ''),
    ISNULL(u.Surname, ''),
    u.id;";

            try
            {
                var rows = new List<UserListRow>();

                using var conn = new SqlConnection(_connectionString);
                using var cmd = new SqlCommand(sql, conn)
                {
                    CommandType = CommandType.Text,
                    CommandTimeout = 30
                };

                await conn.OpenAsync().ConfigureAwait(false);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new UserListRow
                    {
                        Id = SafeGetInt(reader, "id"),
                        DisplayName = SafeGetString(reader, "DisplayName"),
                        GivenName = SafeGetString(reader, "GivenName"),
                        Surname = SafeGetString(reader, "Surname"),
                        EmailAddress = SafeGetString(reader, "EmailAddress"),
                        TelephoneNumber = SafeGetString(reader, "TelephoneNumber"),
                        Mobile = SafeGetString(reader, "Mobile"),
                        Department = SafeGetString(reader, "Department"),
                        Title = SafeGetString(reader, "Title"),
                        IsActive = SafeGetBoolNullable(reader, "IsActive"),
                        IsOperator = SafeGetBoolNullable(reader, "IsOperator"),

                        AccountTypeId = SafeGetIntNullable(reader, "AccountTypeId"),
                        AccountTypeName = SafeGetString(reader, "AccountTypeName"),
                        AccountTypeCode = SafeGetString(reader, "AccountTypeCode"),
                    });
                }

                return UsersQueryResult.Success(rows);
            }
            catch (SqlException ex)
            {
                return UsersQueryResult.Fail($"Błąd SQL podczas wczytywania users: {ex.Message}");
            }
            catch (Exception ex)
            {
                return UsersQueryResult.Fail($"Błąd podczas wczytywania users: {ex.Message}");
            }
        }

        public async Task<UserEditDataResult> GetUserEditDataAsync(int userId)
        {
            await GuardCanEditUsersAsync().ConfigureAwait(false);

            // CHANGE: IsOperator jest wyliczany z ról (vw_ticket_operators).
            const string sql = @"
SELECT TOP 1
    u.id,
    ISNULL(u.DisplayName, '')       AS DisplayName,
    ISNULL(u.EmailAddress, '')      AS EmailAddress,
    ISNULL(u.TelephoneNumber, '')   AS TelephoneNumber,
    ISNULL(u.Mobile, '')            AS Mobile,
    ISNULL(u.Department, '')        AS Department,
    ISNULL(u.Title, '')             AS Title,
    u.IsActive,

    CASE WHEN op.UserId IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS IsOperator,

    u.AccountTypeId                 AS AccountTypeId,
    ISNULL(at.name, '')             AS AccountTypeName
FROM dbo.users u
LEFT JOIN dbo.account_types at ON at.id = u.AccountTypeId
LEFT JOIN dbo.vw_ticket_operators op ON op.UserId = u.id
WHERE u.id = @id;";

            try
            {
                using var conn = new SqlConnection(_connectionString);
                using var cmd = new SqlCommand(sql, conn)
                {
                    CommandType = CommandType.Text,
                    CommandTimeout = 30
                };

                cmd.Parameters.AddWithValue("@id", userId);

                await conn.OpenAsync().ConfigureAwait(false);

                UserHeaderDto header;
                UserEditModel model;

                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync().ConfigureAwait(false))
                        return UserEditDataResult.Fail("User not found.");

                    header = new UserHeaderDto
                    {
                        Id = SafeGetInt(reader, "id"),
                        DisplayName = SafeGetString(reader, "DisplayName"),
                        EmailAddress = SafeGetString(reader, "EmailAddress"),
                        Department = SafeGetString(reader, "Department"),
                        Title = SafeGetString(reader, "Title"),
                        Status = string.Empty, // legacy
                        IsActive = SafeGetBoolNullable(reader, "IsActive"),
                        IsOperator = SafeGetBoolNullable(reader, "IsOperator"),
                        AccountTypeId = SafeGetIntNullable(reader, "AccountTypeId"),
                        AccountTypeName = SafeGetString(reader, "AccountTypeName")
                    };

                    model = new UserEditModel
                    {
                        Id = header.Id,
                        DisplayName = header.DisplayName,
                        EmailAddress = header.EmailAddress,
                        TelephoneNumber = SafeGetString(reader, "TelephoneNumber"),
                        Mobile = SafeGetString(reader, "Mobile"),
                        Department = header.Department,
                        Title = header.Title,
                        Status = header.Status,
                        IsActive = header.IsActive == true,
                        IsOperator = header.IsOperator == true,
                        AccountTypeId = header.AccountTypeId
                    };
                }

                var relatedDevices = await GetUserRelatedDevicesAsync(conn, header.Id).ConfigureAwait(false);
                var relatedTickets = await GetUserRelatedTicketsAsync(conn, header.Id).ConfigureAwait(false);

                return UserEditDataResult.Success(header, model, relatedDevices, relatedTickets);
            }
            catch (SqlException ex)
            {
                return UserEditDataResult.Fail($"Błąd SQL: {ex.Message}");
            }
            catch (Exception ex)
            {
                return UserEditDataResult.Fail($"Błąd: {ex.Message}");
            }
        }

        public async Task<SimpleResult> UpdateUserAsync(UserEditModel model)
        {
            await GuardCanEditUsersAsync().ConfigureAwait(false);

            if (model == null || model.Id <= 0)
                return SimpleResult.Fail("Invalid model.");

            const string sqlUpdateUser = @"
UPDATE dbo.users
SET
    AccountTypeId = @AccountTypeId
WHERE id = @Id;";

            const string sqlGetItOperatorRoleId = @"
SELECT TOP 1 r.id
FROM dbo.Roles r
WHERE
    r.is_active = 1
    AND r.name = @RoleName;";

            const string sqlEnsureUserRole = @"
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = @UserId AND ur.RoleId = @RoleId)
BEGIN
    INSERT INTO dbo.UserRoles (UserId, RoleId, GrantedAt)
    VALUES (@UserId, @RoleId, SYSUTCDATETIME());
END
";

            const string sqlRemoveUserRole = @"
DELETE FROM dbo.UserRoles
WHERE UserId = @UserId AND RoleId = @RoleId;";

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = conn.BeginTransaction();

                try
                {
                    // 1) update local field
                    using (var cmd = new SqlCommand(sqlUpdateUser, conn, tx)
                    {
                        CommandType = CommandType.Text,
                        CommandTimeout = 30
                    })
                    {
                        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = model.Id });
                        cmd.Parameters.Add(new SqlParameter("@AccountTypeId", SqlDbType.Int)
                        {
                            Value = (object?)model.AccountTypeId ?? DBNull.Value
                        });

                        var affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        if (affected <= 0)
                        {
                            tx.Rollback();
                            return SimpleResult.Fail("Update failed.");
                        }
                    }

                    // 2) resolve RoleId for IT Operator
                    int roleId;
                    using (var cmd = new SqlCommand(sqlGetItOperatorRoleId, conn, tx)
                    {
                        CommandType = CommandType.Text,
                        CommandTimeout = 30
                    })
                    {
                        cmd.Parameters.Add(new SqlParameter("@RoleName", SqlDbType.NVarChar, 255) { Value = RoleItOperatorName });

                        var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                        if (scalar == null || scalar == DBNull.Value || !int.TryParse(scalar.ToString(), out roleId) || roleId <= 0)
                        {
                            tx.Rollback();
                            return SimpleResult.Fail($"Nie znaleziono aktywnej roli: {RoleItOperatorName}.");
                        }
                    }

                    // 3) apply mapping based on model.IsOperator
                    if (model.IsOperator)
                    {
                        using var cmd = new SqlCommand(sqlEnsureUserRole, conn, tx)
                        {
                            CommandType = CommandType.Text,
                            CommandTimeout = 30
                        };

                        cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = model.Id });
                        cmd.Parameters.Add(new SqlParameter("@RoleId", SqlDbType.Int) { Value = roleId });

                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        using var cmd = new SqlCommand(sqlRemoveUserRole, conn, tx)
                        {
                            CommandType = CommandType.Text,
                            CommandTimeout = 30
                        };

                        cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = model.Id });
                        cmd.Parameters.Add(new SqlParameter("@RoleId", SqlDbType.Int) { Value = roleId });

                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tx.Commit();
                    return SimpleResult.Ok();
                }
                catch (SqlException ex)
                {
                    try { tx.Rollback(); } catch { }
                    return SimpleResult.Fail($"Błąd SQL: {ex.Message}");
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    return SimpleResult.Fail($"Błąd: {ex.Message}");
                }
            }
            catch (SqlException ex)
            {
                return SimpleResult.Fail($"Błąd SQL: {ex.Message}");
            }
            catch (Exception ex)
            {
                return SimpleResult.Fail($"Błąd: {ex.Message}");
            }
        }

        private static async Task<List<UserRelatedDeviceRow>> GetUserRelatedDevicesAsync(SqlConnection conn, int userId)
        {
            const string sql = @"
SELECT TOP 200
    d.id AS Id,
    ISNULL(d.asset_no, ISNULL(d.inventory_no, '')) AS AssetTag,
    ISNULL(d.device_kind_name, ISNULL(d.device_type_name, '')) AS Type,
    LTRIM(RTRIM(CONCAT(
        NULLIF(ISNULL(d.device_producers_name, ''), ''),
        CASE WHEN ISNULL(d.device_producers_name, '') <> '' AND ISNULL(d.model, '') <> '' THEN ' ' ELSE '' END,
        NULLIF(ISNULL(d.model, ''), '')
    ))) AS ProducerModel,

    LTRIM(RTRIM(ISNULL(CAST(d.serial_number AS nvarchar(255)), ''))) AS SerialNumber,

    ISNULL(d.device_statuses_name, '') AS Status,
    ISNULL(d.location, '') AS Location
FROM dbo.vw_devices d
WHERE
    d.user_id = @UserId
ORDER BY
    ISNULL(d.asset_no, ISNULL(d.inventory_no, '')),
    d.id;";

            var list = new List<UserRelatedDeviceRow>();

            using var cmd = new SqlCommand(sql, conn)
            {
                CommandType = CommandType.Text,
                CommandTimeout = 30
            };

            cmd.Parameters.AddWithValue("@UserId", userId);

            try
            {
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(new UserRelatedDeviceRow
                    {
                        Id = SafeGetInt(reader, "Id"),
                        AssetTag = SafeGetString(reader, "AssetTag"),
                        Type = SafeGetString(reader, "Type"),
                        ProducerModel = SafeGetString(reader, "ProducerModel"),
                        SerialNumber = SafeGetString(reader, "SerialNumber"),
                        Status = SafeGetString(reader, "Status"),
                        Location = SafeGetString(reader, "Location")
                    });
                }
            }
            catch
            {
                return new List<UserRelatedDeviceRow>();
            }

            return list;
        }

        private static async Task<List<UserRelatedTicketRow>> GetUserRelatedTicketsAsync(SqlConnection conn, int userId)
        {
            const string sql = @"
SELECT TOP 200
    t.id AS Id,
    CAST(t.id AS varchar(20)) AS TicketNo,
    ISNULL(t.ticket_title, '') AS Title,
    ISNULL(t.ticket_statuses_name, '') AS Status,
    ISNULL(CAST(t.ticket_priority_value AS varchar(20)), '') AS Priority,
    t.ticket_request_date AS UpdatedAt
FROM dbo.vw_tickets t
WHERE
    t.ticket_requester_id = @UserId
    OR t.ticket_keeper_id = @UserId
ORDER BY
    t.ticket_request_date DESC;";

            var list = new List<UserRelatedTicketRow>();

            using var cmd = new SqlCommand(sql, conn)
            {
                CommandType = CommandType.Text,
                CommandTimeout = 30
            };

            cmd.Parameters.AddWithValue("@UserId", userId);

            try
            {
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(new UserRelatedTicketRow
                    {
                        Id = SafeGetInt(reader, "Id"),
                        TicketNo = SafeGetString(reader, "TicketNo"),
                        Title = SafeGetString(reader, "Title"),
                        Status = SafeGetString(reader, "Status"),
                        Priority = SafeGetString(reader, "Priority"),
                        UpdatedAt = reader.IsDBNull(reader.GetOrdinal("UpdatedAt"))
                            ? null
                            : reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
                    });
                }
            }
            catch
            {
                return new List<UserRelatedTicketRow>();
            }

            return list;
        }

        private static int? SafeGetIntNullable(SqlDataReader reader, string col)
        {
            var ordinal = reader.GetOrdinal(col);
            if (reader.IsDBNull(ordinal))
                return null;

            var val = reader.GetValue(ordinal);
            if (val is int i)
                return i;

            if (int.TryParse(val?.ToString(), out var parsed))
                return parsed;

            return null;
        }

        private static int SafeGetInt(SqlDataReader reader, string col)
        {
            var ordinal = reader.GetOrdinal(col);
            if (reader.IsDBNull(ordinal))
                return 0;

            var val = reader.GetValue(ordinal);
            if (val is int i)
                return i;

            if (int.TryParse(val?.ToString(), out var parsed))
                return parsed;

            return 0;
        }

        private static string SafeGetString(SqlDataReader reader, string col)
        {
            var ordinal = reader.GetOrdinal(col);
            if (reader.IsDBNull(ordinal))
                return string.Empty;

            return (reader.GetValue(ordinal)?.ToString() ?? string.Empty).Trim();
        }

        private static bool? SafeGetBoolNullable(SqlDataReader reader, string col)
        {
            var ordinal = reader.GetOrdinal(col);
            if (reader.IsDBNull(ordinal))
                return null;

            var val = reader.GetValue(ordinal);

            if (val is bool b)
                return b;

            if (val is byte bt)
                return bt != 0;

            if (int.TryParse(val?.ToString(), out var parsedInt))
                return parsedInt != 0;

            if (bool.TryParse(val?.ToString(), out var parsedBool))
                return parsedBool;

            return null;
        }

        // DTO dla dialogu edycji użytkownika
        public sealed class UserHeaderDto
        {
            public int Id { get; set; }
            public string? DisplayName { get; set; }
            public string? EmailAddress { get; set; }
            public string? Department { get; set; }
            public string? Title { get; set; }
            public string? Status { get; set; } // legacy
            public bool? IsActive { get; set; }
            public bool? IsOperator { get; set; }

            public int? AccountTypeId { get; set; }
            public string? AccountTypeName { get; set; }
        }

        public sealed class UserEditModel
        {
            public int Id { get; set; }
            public string? DisplayName { get; set; }
            public string? EmailAddress { get; set; }
            public string? TelephoneNumber { get; set; }
            public string? Mobile { get; set; }
            public string? Department { get; set; }
            public string? Title { get; set; }
            public string? Status { get; set; } // legacy
            public bool IsActive { get; set; }
            public bool IsOperator { get; set; }

            public int? AccountTypeId { get; set; }
        }

        public sealed class UserRelatedDeviceRow
        {
            public int Id { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Hostname { get; set; } = string.Empty;
            public string ProducerModel { get; set; } = string.Empty;
            public string SerialNumber { get; set; } = string.Empty;
            public string AssetTag { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string Location { get; set; } = string.Empty;
        }

        public sealed class UserRelatedTicketRow
        {
            public int Id { get; set; }
            public string? TicketNo { get; set; }
            public string TicketCode { get; set; } = "";
            public string? Title { get; set; }
            public string? Status { get; set; }
            public string? Priority { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }

        public sealed class AccountTypeDto
        {
            public int Id { get; set; }
            public string Code { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public bool? IsActive { get; set; }
        }
    }

    public sealed class UsersQueryResult
    {
        public bool IsOk { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;
        public List<UserListRow> Rows { get; private set; } = new();

        public static UsersQueryResult Success(List<UserListRow> rows)
        {
            return new UsersQueryResult
            {
                IsOk = true,
                Rows = rows ?? new List<UserListRow>(),
                ErrorMessage = string.Empty
            };
        }

        public static UsersQueryResult Fail(string message)
        {
            return new UsersQueryResult
            {
                IsOk = false,
                Rows = new List<UserListRow>(),
                ErrorMessage = message ?? "Nieznany błąd."
            };
        }
    }

    public sealed class UserEditDataResult
    {
        public bool IsOk { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        public UsersService.UserHeaderDto? User { get; private set; }
        public UsersService.UserEditModel? EditModel { get; private set; }

        public List<UsersService.UserRelatedDeviceRow>? RelatedDevices { get; private set; }
        public List<UsersService.UserRelatedTicketRow>? RelatedTickets { get; private set; }

        public static UserEditDataResult Success(
            UsersService.UserHeaderDto user,
            UsersService.UserEditModel editModel,
            List<UsersService.UserRelatedDeviceRow> relatedDevices,
            List<UsersService.UserRelatedTicketRow> relatedTickets)
        {
            return new UserEditDataResult
            {
                IsOk = true,
                ErrorMessage = string.Empty,
                User = user,
                EditModel = editModel,
                RelatedDevices = relatedDevices,
                RelatedTickets = relatedTickets
            };
        }

        public static UserEditDataResult Fail(string message)
        {
            return new UserEditDataResult
            {
                IsOk = false,
                ErrorMessage = message ?? "Nieznany błąd."
            };
        }
    }

    public sealed class SimpleResult
    {
        public bool IsOk { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        public static SimpleResult Ok() => new SimpleResult { IsOk = true };
        public static SimpleResult Fail(string message) => new SimpleResult { IsOk = false, ErrorMessage = message ?? "Nieznany błąd." };
    }
}
