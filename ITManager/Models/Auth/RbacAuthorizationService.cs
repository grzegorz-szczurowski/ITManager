// File: Services/Auth/RbacAuthorizationService.cs
// Description: RBAC: pobiera PermissionCodes dla użytkownika z bazy ITManager.
//              Multi-role: role użytkownika z dbo.UserRoles (N:N), a PermissionCodes to suma uprawnień z ról.
// Notes:
//   - Windows only.
//   - UserRoles only: brak fallback do dbo.users.role_id.
//   - IsOperator nie jest używane (legacy).
// Created: 2025-12-23
// Updated: 2025-12-24 - przełączenie źródła ról z dbo.user_roles na dbo.Roles.
// Updated: 2025-12-25 - Multi-role: dbo.UserRoles + UNION permissionów z wielu ról.
// Updated: 2026-01-09 - wersja 1.31 - Windows only: usunięto Guest.
// Updated: 2026-01-09 - wersja 1.40 - UserRoles only: usunięto legacy users.role_id fallback.
// Updated: 2026-01-25 - wersja 1.41:
//   - NEW: cache AuthorizationInfo per ObjectGUID (MemoryCache) dla wydajności Blazor Server.
//   - CHANGE: ILogger zamiast Console.Error.
// Updated: 2026-02-02 - wersja 1.42:
//   - FIX: normalizacja kodów permissionów z DB (trim + akceptacja "Perm:" prefix).
// Version: 1.42

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ITManager.Models.Auth;

namespace ITManager.Services.Auth
{
    public sealed class RbacAuthorizationService : IAppAuthorizationService
    {
        private const string ConnectionStringName = "ITManagerConnection";

        // Cache: w Blazor Server autoryzacja jest wołana często, więc robimy krótki TTL.
        private const int CacheSlidingMinutes = 1;
        private const int CacheAbsoluteMinutes = 2;

        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;
        private readonly ILogger<RbacAuthorizationService> _logger;

        public RbacAuthorizationService(IConfiguration configuration, IMemoryCache cache, ILogger<RbacAuthorizationService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AuthorizationInfo> GetAuthorizationAsync(Guid objectGuid)
        {
            if (objectGuid == Guid.Empty)
                return NewEmpty();

            var cacheKey = GetCacheKey(objectGuid);
            if (_cache.TryGetValue(cacheKey, out AuthorizationInfo cached) && cached != null)
                return cached;

            var empty = NewEmpty();

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                {
                    _logger.LogError("[Auth] Brak ConnectionString '{ConnectionStringName}'.", ConnectionStringName);
                    CacheResult(cacheKey, empty);
                    return empty;
                }

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                // 1) Użytkownik
                var user = await LoadUserAsync(con, objectGuid).ConfigureAwait(false);
                if (user.UserId <= 0)
                {
                    CacheResult(cacheKey, empty);
                    return empty;
                }

                // dbo.Roles nie ma PermissionsVersion, więc stała.
                const int permissionsVersion = 1;

                // 2) Role (UserRoles only)
                var roles = await LoadUserRolesAsync(con, user.UserId).ConfigureAwait(false);

                // Primary role do kompatybilności (jeśli jest więcej ról)
                var primaryRole = roles
                    .Where(r => r != null)
                    .OrderBy(r => r.SortOrder)
                    .ThenBy(r => r.DisplayName)
                    .FirstOrDefault();

                var primaryRoleId = primaryRole?.RoleId ?? 0;

                // RoleName jako CSV, bo część kodu mapuje role po nazwie
                var roleNameCsv = string.Join(", ", roles
                    .Where(r => r != null)
                    .Select(r => !string.IsNullOrWhiteSpace(r.DisplayName) ? r.DisplayName : r.RoleName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                // User nieaktywny: zwracamy puste permissiony
                if (!user.IsActive)
                {
                    var inactive = new AuthorizationInfo
                    {
                        Ok = true,
                        UserId = user.UserId,
                        IsActive = false,
                        Roles = roles,
                        RoleId = primaryRoleId,
                        RoleName = roleNameCsv,
                        PermissionsVersion = permissionsVersion,
                        PermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    };

                    CacheResult(cacheKey, inactive);
                    return inactive;
                }

                // Brak ról: user aktywny, ale bez uprawnień (UserRoles only)
                if (roles.Count == 0)
                {
                    _logger.LogWarning("[Auth] RBAC: user bez ról w dbo.UserRoles. UserId={UserId}, ObjectGUID={ObjectGuid}.", user.UserId, objectGuid);

                    var noRoles = new AuthorizationInfo
                    {
                        Ok = true,
                        UserId = user.UserId,
                        IsActive = true,
                        Roles = roles,
                        RoleId = 0,
                        RoleName = string.Empty,
                        PermissionsVersion = permissionsVersion,
                        PermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    };

                    CacheResult(cacheKey, noRoles);
                    return noRoles;
                }

                // 3) PermissionCodes (suma z wielu ról)
                var roleIds = roles
                    .Select(r => r.RoleId)
                    .Where(x => x > 0)
                    .Distinct()
                    .ToList();

                var codes = await LoadPermissionCodesForRolesAsync(con, roleIds).ConfigureAwait(false);

                var result = new AuthorizationInfo
                {
                    Ok = true,
                    UserId = user.UserId,
                    IsActive = true,
                    Roles = roles,
                    RoleId = primaryRoleId,
                    RoleName = roleNameCsv,
                    PermissionsVersion = permissionsVersion,
                    PermissionCodes = codes
                };

                CacheResult(cacheKey, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Auth] RBAC: Błąd pobierania uprawnień. ObjectGUID={ObjectGuid}.", objectGuid);
                CacheResult(cacheKey, empty);
                return empty;
            }
        }

        private static string GetCacheKey(Guid objectGuid) => $"rbac_auth:{objectGuid:D}";

        private void CacheResult(string cacheKey, AuthorizationInfo value)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || value == null)
                return;

            var options = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(CacheSlidingMinutes),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheAbsoluteMinutes)
            };

            _cache.Set(cacheKey, value, options);
        }

        private static AuthorizationInfo NewEmpty() =>
            new AuthorizationInfo
            {
                Ok = false,
                UserId = 0,
                IsActive = false,
                Roles = new List<AuthorizationInfo.RoleInfo>(),
                RoleId = 0,
                RoleName = string.Empty,
                PermissionsVersion = 0,
                PermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };

        private sealed class UserRow
        {
            public int UserId { get; set; }
            public bool IsActive { get; set; }
        }

        private static async Task<UserRow> LoadUserAsync(SqlConnection con, Guid objectGuid)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText =
@"
SELECT
    u.id AS user_id,
    ISNULL(u.IsActive, 0) AS IsActive
FROM dbo.users u
WHERE u.ObjectGUID = @ObjectGUID;
";
            cmd.Parameters.Add(new SqlParameter("@ObjectGUID", SqlDbType.UniqueIdentifier) { Value = objectGuid });

            using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow).ConfigureAwait(false);
            if (!await r.ReadAsync().ConfigureAwait(false))
            {
                return new UserRow
                {
                    UserId = 0,
                    IsActive = false
                };
            }

            return new UserRow
            {
                UserId = ReadInt(r, "user_id"),
                IsActive = ReadBoolFlexible(r, "IsActive")
            };
        }

        private static async Task<List<AuthorizationInfo.RoleInfo>> LoadUserRolesAsync(SqlConnection con, int userId)
        {
            var roles = new List<AuthorizationInfo.RoleInfo>();

            if (userId <= 0)
                return roles;

            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText =
@"
SELECT
    r.id,
    LTRIM(RTRIM(r.[name])) AS [name],
    LTRIM(RTRIM(r.display_name)) AS display_name,
    ISNULL(r.sort_order, 999999) AS sort_order,
    ISNULL(r.is_active, 0) AS is_active
FROM dbo.UserRoles ur
INNER JOIN dbo.Roles r ON r.id = ur.RoleId
WHERE ur.UserId = @UserId
ORDER BY ISNULL(r.sort_order, 999999) ASC, r.display_name ASC;
";
            cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

            using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rd.ReadAsync().ConfigureAwait(false))
            {
                roles.Add(new AuthorizationInfo.RoleInfo
                {
                    RoleId = rd.GetInt32(0),
                    RoleName = rd.IsDBNull(1) ? string.Empty : (rd.GetString(1) ?? string.Empty).Trim(),
                    DisplayName = rd.IsDBNull(2) ? string.Empty : (rd.GetString(2) ?? string.Empty).Trim(),
                    SortOrder = rd.IsDBNull(3) ? 999999 : Convert.ToInt32(rd.GetValue(3)),
                    IsActive = !rd.IsDBNull(4) && Convert.ToInt32(rd.GetValue(4)) == 1
                });
            }

            // Tylko aktywne role wpływają na uprawnienia
            return roles.Where(r => r.IsActive).ToList();
        }

        private static async Task<HashSet<string>> LoadPermissionCodesForRolesAsync(SqlConnection con, List<int> roleIds)
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (roleIds == null || roleIds.Count == 0)
                return codes;

            // Parametry @r0, @r1, ...
            var paramNames = new List<string>();
            for (var i = 0; i < roleIds.Count; i++)
                paramNames.Add($"@r{i}");

            using var cmd = con.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText =
$@"
SELECT DISTINCT p.Code
FROM dbo.RolePermissions rp
INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE rp.RoleId IN ({string.Join(", ", paramNames)})
  AND p.IsActive = 1
ORDER BY p.Code;
";

            for (var i = 0; i < roleIds.Count; i++)
                cmd.Parameters.Add(new SqlParameter(paramNames[i], SqlDbType.Int) { Value = roleIds[i] });

            using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rd.ReadAsync().ConfigureAwait(false))
            {
                var raw = rd.IsDBNull(0) ? string.Empty : (Convert.ToString(rd.GetValue(0)) ?? string.Empty);

                var normalized = NormalizePermissionCode(raw);
                if (!string.IsNullOrWhiteSpace(normalized))
                    codes.Add(normalized);
            }

            return codes;
        }

        private static string NormalizePermissionCode(string? value)
        {
            var s = (value ?? string.Empty).Trim();
            if (s.Length == 0)
                return string.Empty;

            // Jeśli w DB ktoś trzyma "Perm:Tickets.View", to normalizujemy do "Tickets.View"
            if (s.StartsWith("Perm:", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Perm:".Length).Trim();

            return s;
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
    }
}
