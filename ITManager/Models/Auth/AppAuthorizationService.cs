// File: Services/Auth/AppAuthorizationService.cs
// Description: Legacy RBAC (single-role): pobiera role oraz PermissionCodes użytkownika z bazy ITManager
//              (dbo.users + dbo.user_roles + dbo.RolePermissions + dbo.Permissions).
//              Dodano wsparcie Guest: pobieranie permissionów po nazwie roli.
// Created: 2025-12-15
// Updated: 2025-12-16 - dodanie user_id.
// Updated: 2025-12-23 - RBAC only: usunięcie legacy can_* oraz GetPermissionsAsync.
// Updated: 2026-01-08 - wersja 1.10 - implementacja IAppAuthorizationService.GetAuthorizationForRoleAsync(roleName).
// Version: 1.10
// Change log:
// - 1.03 (2025-12-23) RBAC only: serwis zwraca wyłącznie AuthorizationInfo.
// - 1.10 (2026-01-08) Guest: GetAuthorizationForRoleAsync (role by name/display_name) + filtr IsActive.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using ITManager.Models.Auth;

namespace ITManager.Services.Auth
{
    public sealed class AppAuthorizationService : IAppAuthorizationService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;

        public AppAuthorizationService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<AuthorizationInfo> GetAuthorizationAsync(Guid objectGuid)
        {
            var empty = NewEmpty();

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                {
                    Console.Error.WriteLine($"[Auth] Brak ConnectionString '{ConnectionStringName}'.");
                    return empty;
                }

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                // 1) user + role (legacy single-role)
                int userId;
                bool isActive;
                int roleId;
                string roleName;

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandText =
@"
SELECT
    u.id AS user_id,
    ISNULL(u.IsActive, 0) AS IsActive,
    ISNULL(u.role_id, 0) AS role_id,
    LTRIM(RTRIM(ur.role_name)) AS role_name
FROM dbo.users u
INNER JOIN dbo.user_roles ur ON ur.id = u.role_id
WHERE u.ObjectGUID = @ObjectGUID;
";
                    cmd.Parameters.Add(new SqlParameter("@ObjectGUID", SqlDbType.UniqueIdentifier) { Value = objectGuid });

                    using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow).ConfigureAwait(false);
                    if (!await r.ReadAsync().ConfigureAwait(false))
                        return empty;

                    userId = ReadInt(r, "user_id");
                    isActive = ReadBoolFlexible(r, "IsActive");
                    roleId = ReadInt(r, "role_id");
                    roleName = ReadString(r, "role_name");
                }

                if (!isActive)
                {
                    return new AuthorizationInfo
                    {
                        Ok = true,
                        UserId = userId,
                        IsActive = false,
                        RoleId = roleId,
                        RoleName = roleName,
                        PermissionsVersion = 1,
                        PermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    };
                }

                // 2) RBAC codes
                var permissionCodes = await LoadPermissionCodesForRoleAsync(con, roleId).ConfigureAwait(false);

                return new AuthorizationInfo
                {
                    Ok = true,
                    UserId = userId,
                    IsActive = true,
                    RoleId = roleId,
                    RoleName = roleName,
                    PermissionsVersion = 1,
                    PermissionCodes = permissionCodes
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Auth] Błąd pobierania uprawnień. ObjectGUID={objectGuid}. Błąd: {ex}");
                return empty;
            }
        }

        public async Task<AuthorizationInfo> GetAuthorizationForRoleAsync(string roleName)
        {
            var empty = NewEmpty();

            try
            {
                var roleKey = (roleName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(roleKey))
                    return empty;

                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                {
                    Console.Error.WriteLine($"[Auth] Brak ConnectionString '{ConnectionStringName}'.");
                    return empty;
                }

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                // Legacy: role definicje są w dbo.user_roles (id + role_name)
                int roleId = 0;
                string resolvedRoleName = string.Empty;

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandText =
@"
SELECT TOP 1
    ur.id AS role_id,
    LTRIM(RTRIM(ur.role_name)) AS role_name
FROM dbo.user_roles ur
WHERE LOWER(LTRIM(RTRIM(ur.role_name))) = LOWER(@RoleName);
";
                    cmd.Parameters.Add(new SqlParameter("@RoleName", SqlDbType.NVarChar, 200) { Value = roleKey });

                    using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow).ConfigureAwait(false);
                    if (!await r.ReadAsync().ConfigureAwait(false))
                        return empty;

                    roleId = ReadInt(r, "role_id");
                    resolvedRoleName = ReadString(r, "role_name");
                }

                if (roleId <= 0)
                    return empty;

                var permissionCodes = await LoadPermissionCodesForRoleAsync(con, roleId).ConfigureAwait(false);

                return new AuthorizationInfo
                {
                    Ok = true,
                    UserId = 0,
                    IsActive = true,
                    RoleId = roleId,
                    RoleName = resolvedRoleName,
                    PermissionsVersion = 1,
                    PermissionCodes = permissionCodes
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Auth] Błąd pobierania uprawnień roli. RoleName={roleName}. Błąd: {ex}");
                return empty;
            }
        }

        private static AuthorizationInfo NewEmpty()
        {
            return new AuthorizationInfo
            {
                Ok = false,
                UserId = 0,
                IsActive = false,
                RoleId = 0,
                RoleName = string.Empty,
                PermissionsVersion = 0,
                PermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static async Task<HashSet<string>> LoadPermissionCodesForRoleAsync(SqlConnection con, int roleId)
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (roleId <= 0)
                return codes;

            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText =
@"
SELECT p.Code
FROM dbo.RolePermissions rp
INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE rp.RoleId = @RoleId
  AND ISNULL(p.IsActive, 1) = 1
ORDER BY p.Code;
";
            cmd.Parameters.Add(new SqlParameter("@RoleId", SqlDbType.Int) { Value = roleId });

            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                var code = r.IsDBNull(0) ? string.Empty : Convert.ToString(r.GetValue(0)) ?? string.Empty;
                code = (code ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(code))
                    codes.Add(code);
            }

            return codes;
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
    }
}
