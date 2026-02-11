// File: Services/UserRolesAdminService.cs
// Description: Admin: przypisywanie ról do użytkowników (multi-role) w ITManager.
//              Reads: dbo.vw_users_roles_agg, dbo.Roles
//              Writes: dbo.UserRoles (replace user roles).
// Guarded by: Permissions.Manage
// Version: 1.00
// Created: 2025-12-25
// Change log:
// - 1.00 (2025-12-25) Initial.

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using ITManager.Services.Auth;

namespace ITManager.Services
{
    public sealed class UserRolesAdminService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;
        private readonly CurrentUserContextService _currentUserContextService;

        public UserRolesAdminService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        }

        private async Task GuardCanManageAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has("Permissions.Manage"))
                throw new UnauthorizedAccessException("Brak uprawnień do zarządzania rolami użytkowników.");
        }

        private string GetCs()
        {
            var cs = _configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException($"Brak ConnectionString '{ConnectionStringName}'.");
            return cs;
        }

        public sealed class UserAggRow
        {
            public int UserId { get; set; }
            public Guid? ObjectGuid { get; set; }
            public string UserDisplayName { get; set; } = string.Empty;
            public bool UserIsActive { get; set; }
            public string RolesCsv { get; set; } = string.Empty;
            public int RolesCount { get; set; }
        }

        public sealed class RoleRow
        {
            public int RoleId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public int SortOrder { get; set; }
            public bool IsActive { get; set; }
        }

        public async Task<List<UserAggRow>> GetUsersAsync()
        {
            await GuardCanManageAsync().ConfigureAwait(false);

            var rows = new List<UserAggRow>();

            using var con = new SqlConnection(GetCs());
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;

            cmd.CommandText = @"
SELECT
    UserId,
    ObjectGuid,
    UserDisplayName,
    UserIsActive,
    ISNULL(RolesCsv, '') AS RolesCsv,
    ISNULL(RolesCount, 0) AS RolesCount
FROM dbo.vw_users_roles_agg
ORDER BY UserDisplayName ASC;";

            await con.OpenAsync().ConfigureAwait(false);

            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new UserAggRow
                {
                    UserId = ReadInt(r, "UserId"),
                    ObjectGuid = ReadGuidNullable(r, "ObjectGuid"),
                    UserDisplayName = ReadString(r, "UserDisplayName").Trim(),
                    UserIsActive = ReadBoolFlexible(r, "UserIsActive"),
                    RolesCsv = ReadString(r, "RolesCsv").Trim(),
                    RolesCount = ReadInt(r, "RolesCount")
                });
            }

            return rows;
        }

        public async Task<List<RoleRow>> GetRolesAsync(bool onlyActive = true)
        {
            await GuardCanManageAsync().ConfigureAwait(false);

            var rows = new List<RoleRow>();

            using var con = new SqlConnection(GetCs());
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;

            cmd.CommandText = @"
SELECT
    r.id AS RoleId,
    LTRIM(RTRIM(r.[name])) AS [name],
    LTRIM(RTRIM(r.display_name)) AS display_name,
    ISNULL(r.sort_order, 999999) AS sort_order,
    ISNULL(r.is_active, 0) AS is_active
FROM dbo.Roles r
WHERE (@OnlyActive = 0) OR (ISNULL(r.is_active, 0) = 1)
ORDER BY ISNULL(r.sort_order, 999999) ASC, r.display_name ASC, r.[name] ASC;";

            cmd.Parameters.Add(new SqlParameter("@OnlyActive", SqlDbType.Bit) { Value = onlyActive ? 1 : 0 });

            await con.OpenAsync().ConfigureAwait(false);

            using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rd.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new RoleRow
                {
                    RoleId = ReadInt(rd, "RoleId"),
                    Name = ReadString(rd, "name").Trim(),
                    DisplayName = ReadString(rd, "display_name").Trim(),
                    SortOrder = ReadInt(rd, "sort_order"),
                    IsActive = ReadBoolFlexible(rd, "is_active")
                });
            }

            return rows;
        }

        public async Task<HashSet<int>> GetUserRoleIdsAsync(int userId)
        {
            await GuardCanManageAsync().ConfigureAwait(false);

            var set = new HashSet<int>();

            if (userId <= 0)
                return set;

            using var con = new SqlConnection(GetCs());
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;

            cmd.CommandText = @"
SELECT ur.RoleId
FROM dbo.UserRoles ur
WHERE ur.UserId = @UserId;";

            cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

            await con.OpenAsync().ConfigureAwait(false);

            using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rd.ReadAsync().ConfigureAwait(false))
            {
                if (!rd.IsDBNull(0))
                    set.Add(Convert.ToInt32(rd.GetValue(0)));
            }

            return set;
        }

        public async Task ReplaceUserRolesAsync(int userId, IEnumerable<int> roleIds)
        {
            await GuardCanManageAsync().ConfigureAwait(false);

            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId));

            var ids = (roleIds ?? Enumerable.Empty<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            using var con = new SqlConnection(GetCs());
            await con.OpenAsync().ConfigureAwait(false);

            using var tx = con.BeginTransaction();

            try
            {
                // Validate user exists
                using (var chkUser = con.CreateCommand())
                {
                    chkUser.Transaction = tx;
                    chkUser.CommandType = CommandType.Text;
                    chkUser.CommandText = @"SELECT COUNT(1) FROM dbo.users WHERE id = @UserId;";
                    chkUser.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

                    var exists = Convert.ToInt32(await chkUser.ExecuteScalarAsync().ConfigureAwait(false));
                    if (exists <= 0)
                        throw new InvalidOperationException($"UserId={userId} nie istnieje w dbo.users.");
                }

                // Optionally validate roles exist and active
                if (ids.Count > 0)
                {
                    using var chkRoles = con.CreateCommand();
                    chkRoles.Transaction = tx;
                    chkRoles.CommandType = CommandType.Text;

                    var paramNames = new List<string>();
                    for (var i = 0; i < ids.Count; i++)
                        paramNames.Add($"@r{i}");

                    chkRoles.CommandText = $@"
SELECT COUNT(1)
FROM dbo.Roles r
WHERE r.id IN ({string.Join(", ", paramNames)})
  AND ISNULL(r.is_active, 0) = 1;";

                    for (var i = 0; i < ids.Count; i++)
                        chkRoles.Parameters.Add(new SqlParameter(paramNames[i], SqlDbType.Int) { Value = ids[i] });

                    var okCount = Convert.ToInt32(await chkRoles.ExecuteScalarAsync().ConfigureAwait(false));
                    if (okCount != ids.Count)
                        throw new InvalidOperationException("Co najmniej jedna z wybranych ról nie istnieje lub jest nieaktywna.");
                }

                // Replace
                using (var del = con.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandType = CommandType.Text;
                    del.CommandText = @"DELETE FROM dbo.UserRoles WHERE UserId = @UserId;";
                    del.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

                    await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (ids.Count > 0)
                {
                    using var ins = con.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandType = CommandType.Text;
                    ins.CommandText = @"
INSERT INTO dbo.UserRoles (UserId, RoleId, GrantedAt)
VALUES (@UserId, @RoleId, SYSUTCDATETIME());";

                    ins.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

                    var pRoleId = new SqlParameter("@RoleId", SqlDbType.Int);
                    ins.Parameters.Add(pRoleId);

                    foreach (var rid in ids)
                    {
                        pRoleId.Value = rid;
                        await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private static int ReadInt(SqlDataReader r, string col)
        {
            var idx = r.GetOrdinal(col);
            if (r.IsDBNull(idx))
                return 0;
            return Convert.ToInt32(r.GetValue(idx));
        }

        private static string ReadString(SqlDataReader r, string col)
        {
            var idx = r.GetOrdinal(col);
            if (r.IsDBNull(idx))
                return string.Empty;
            return Convert.ToString(r.GetValue(idx)) ?? string.Empty;
        }

        private static bool ReadBoolFlexible(SqlDataReader r, string col)
        {
            var idx = r.GetOrdinal(col);
            if (r.IsDBNull(idx))
                return false;

            var val = r.GetValue(idx);
            if (val is bool b)
                return b;

            return Convert.ToInt32(val) == 1;
        }

        private static Guid? ReadGuidNullable(SqlDataReader r, string col)
        {
            var idx = r.GetOrdinal(col);
            if (r.IsDBNull(idx))
                return null;

            var val = r.GetValue(idx);
            if (val is Guid g)
                return g;

            if (Guid.TryParse(Convert.ToString(val), out var parsed))
                return parsed;

            return null;
        }
    }
}
