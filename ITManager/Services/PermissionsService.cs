// File: Services/PermissionsService.cs
// Description: RBAC permissions admin: roles (dbo.Roles), permissions (dbo.Permissions),
//              mapping (dbo.RolePermissions). Guarded by Permissions.Manage.
// Version: 1.11
// Created: 2025-12-24
// Updated: 2025-12-25 - przełączenie źródła ról na dbo.Roles (name/display_name) oraz mapowania na dbo.RolePermissions.
// Updated: 2026-02-03 - Permissions: dodano pobieranie i ekspozycję pola Description (dbo.Permissions.Description).
// Change log:
// - 1.11 (2026-02-03) Permissions: PermissionRow.Description + select/map w GetPermissionsAsync.
// - 1.10 (2025-12-25) Roles: dbo.Roles (display_name fallback: name). RolePermissions: dbo.RolePermissions.

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
    public sealed class PermissionsService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;
        private readonly CurrentUserContextService _currentUserContextService;

        public PermissionsService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        }

        private async Task GuardCanManagePermissionsAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has("Permissions.Manage"))
                throw new UnauthorizedAccessException("Brak uprawnień do zarządzania uprawnieniami.");
        }

        private string GetCs()
        {
            var cs = _configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException($"Brak ConnectionString '{ConnectionStringName}'.");
            return cs;
        }

        public sealed class RoleRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public int SortOrder { get; set; }
            public bool IsActive { get; set; }
        }

        public sealed class PermissionRow
        {
            public int Id { get; set; }
            public string Code { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Module { get; set; } = string.Empty;
            public int SortOrder { get; set; }
            public bool IsActive { get; set; }
        }

        public async Task<List<RoleRow>> GetRolesAsync()
        {
            await GuardCanManagePermissionsAsync().ConfigureAwait(false);

            var rows = new List<RoleRow>();

            using var con = new SqlConnection(GetCs());
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;

            cmd.CommandText = @"
SELECT
    r.id,
    LTRIM(RTRIM(r.[name])) AS [name],
    LTRIM(RTRIM(r.display_name)) AS display_name,
    ISNULL(r.sort_order, 999999) AS sort_order,
    ISNULL(r.is_active, 0) AS is_active
FROM dbo.Roles r
ORDER BY ISNULL(r.sort_order, 999999) ASC, r.display_name ASC;";

            await con.OpenAsync().ConfigureAwait(false);

            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new RoleRow
                {
                    Id = r.GetInt32(0),
                    Name = r.IsDBNull(1) ? string.Empty : (r.GetString(1) ?? string.Empty).Trim(),
                    DisplayName = r.IsDBNull(2) ? string.Empty : (r.GetString(2) ?? string.Empty).Trim(),
                    SortOrder = r.IsDBNull(3) ? 999999 : Convert.ToInt32(r.GetValue(3)),
                    IsActive = !r.IsDBNull(4) && Convert.ToInt32(r.GetValue(4)) == 1
                });
            }

            return rows;
        }

        public async Task<List<PermissionRow>> GetPermissionsAsync()
        {
            await GuardCanManagePermissionsAsync().ConfigureAwait(false);

            var rows = new List<PermissionRow>();

            using var con = new SqlConnection(GetCs());
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;

            cmd.CommandText = @"
SELECT
    p.Id,
    LTRIM(RTRIM(p.Code)) AS Code,
    LTRIM(RTRIM(p.[Name])) AS [Name],
    ISNULL(LTRIM(RTRIM(p.[Description])), '') AS [Description],
    ISNULL(LTRIM(RTRIM(p.[module])), '') AS [module],
    ISNULL(p.sort_order, 999999) AS sort_order,
    ISNULL(p.IsActive, 0) AS IsActive
FROM dbo.Permissions p
ORDER BY ISNULL(p.sort_order, 999999) ASC, p.Code ASC;";

            await con.OpenAsync().ConfigureAwait(false);

            using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rd.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new PermissionRow
                {
                    Id = rd.GetInt32(0),
                    Code = rd.IsDBNull(1) ? string.Empty : (rd.GetString(1) ?? string.Empty).Trim(),
                    Name = rd.IsDBNull(2) ? string.Empty : (rd.GetString(2) ?? string.Empty).Trim(),
                    Description = rd.IsDBNull(3) ? string.Empty : (rd.GetString(3) ?? string.Empty).Trim(),
                    Module = rd.IsDBNull(4) ? string.Empty : (rd.GetString(4) ?? string.Empty).Trim(),
                    SortOrder = rd.IsDBNull(5) ? 999999 : Convert.ToInt32(rd.GetValue(5)),
                    IsActive = !rd.IsDBNull(6) && Convert.ToInt32(rd.GetValue(6)) == 1
                });
            }

            return rows;
        }

        public async Task<HashSet<int>> GetRolePermissionIdsAsync(int roleId)
        {
            await GuardCanManagePermissionsAsync().ConfigureAwait(false);

            var set = new HashSet<int>();

            if (roleId <= 0)
                return set;

            using var con = new SqlConnection(GetCs());
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;

            cmd.CommandText = @"
SELECT rp.PermissionId
FROM dbo.RolePermissions rp
WHERE rp.RoleId = @RoleId;";

            cmd.Parameters.Add(new SqlParameter("@RoleId", SqlDbType.Int) { Value = roleId });

            await con.OpenAsync().ConfigureAwait(false);

            using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rd.ReadAsync().ConfigureAwait(false))
            {
                if (!rd.IsDBNull(0))
                    set.Add(rd.GetInt32(0));
            }

            return set;
        }

        public async Task ReplaceRolePermissionsAsync(int roleId, IEnumerable<int> permissionIds)
        {
            await GuardCanManagePermissionsAsync().ConfigureAwait(false);

            if (roleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(roleId));

            var ids = (permissionIds ?? Enumerable.Empty<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            using var con = new SqlConnection(GetCs());
            await con.OpenAsync().ConfigureAwait(false);

            using var tx = con.BeginTransaction();

            try
            {
                using (var del = con.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandType = CommandType.Text;
                    del.CommandText = @"DELETE FROM dbo.RolePermissions WHERE RoleId = @RoleId;";
                    del.Parameters.Add(new SqlParameter("@RoleId", SqlDbType.Int) { Value = roleId });

                    await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (ids.Count > 0)
                {
                    using var ins = con.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandType = CommandType.Text;
                    ins.CommandText = @"
INSERT INTO dbo.RolePermissions (RoleId, PermissionId, GrantedAt)
VALUES (@RoleId, @PermissionId, SYSUTCDATETIME());";

                    ins.Parameters.Add(new SqlParameter("@RoleId", SqlDbType.Int) { Value = roleId });

                    var pPerm = new SqlParameter("@PermissionId", SqlDbType.Int);
                    ins.Parameters.Add(pPerm);

                    foreach (var id in ids)
                    {
                        pPerm.Value = id;
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
    }
}
