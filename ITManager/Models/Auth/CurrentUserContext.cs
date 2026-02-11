// File: Models/Auth/CurrentUserContext.cs
// Description: Kontekst aktualnego użytkownika (Windows Auth + ObjectGUID + RBAC).
//              Źródłem praw są PermissionCodes (RBAC).
//              Od v1.10: użytkownik może mieć wiele ról (dbo.UserRoles).
// Created: 2025-12-15
// Updated: 2025-12-16 - dodanie UserId (dbo.users.id).
// Updated: 2025-12-23 - RBAC: PermissionCodes + PermissionsVersion + helpery Has(...).
// Updated: 2025-12-23 - RBAC only: usunięcie legacy UserRolePermissions.
// Version: 1.10
// Change log:
// - 1.04 (2025-12-23) RBAC only: usunięcie legacy can_*.
// - 1.10 (2025-12-25) Multi-role: dodano Roles + RoleIds, RoleName staje się listą (CSV) dla kompatybilności.

using System;
using System.Collections.Generic;

namespace ITManager.Models.Auth
{
    public sealed class CurrentUserContext
    {
        public const string Version = "1.10";

        /* ========================================
           Stan uwierzytelnienia (Windows Auth)
        ======================================== */

        public bool IsAuthenticated { get; set; } = false;

        public string Login { get; set; } = string.Empty;

        public string PrimarySid { get; set; } = string.Empty;

        public Guid? ObjectGuid { get; set; } = null;

        /* ========================================
           Dane z bazy ITManager (dbo.users)
        ======================================== */

        public int? UserId { get; set; } = null;

        public bool? IsActive { get; set; } = null;

        /// <summary>
        /// Primary role id (kompatybilność wstecz). Przy multi-role ustawiane jako pierwsza rola po sort_order.
        /// </summary>
        public int? RoleId { get; set; } = null;

        /* ========================================
           Dane z bazy ITManager (legacy opis)
        ======================================== */

        /// <summary>
        /// Primary role name (kompatybilność wstecz).
        /// Przy multi-role ustawiane jako CSV z nazw ról, np. "IT Operator, Permissions Admin".
        /// </summary>
        public string RoleName { get; set; } = string.Empty;

        /// <summary>
        /// Lista ról użytkownika (źródło prawdy dla UI i diagnostyki).
        /// </summary>
        public List<AuthorizationInfo.RoleInfo> Roles { get; set; } = new List<AuthorizationInfo.RoleInfo>();

        /// <summary>
        /// Lista RoleId (pomocnicza) do szybkiego sprawdzania.
        /// </summary>
        public List<int> RoleIds { get; set; } = new List<int>();

        // RBAC
        public int PermissionsVersion { get; set; } = 0;

        public HashSet<string> PermissionCodes { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /* ========================================
           Runtime
        ======================================== */

        public bool IsInitialized { get; set; } = false;

        /* ========================================
           RBAC helpers
        ======================================== */

        public bool Has(string permissionCode)
        {
            if (string.IsNullOrWhiteSpace(permissionCode))
                return false;

            return PermissionCodes.Contains(permissionCode);
        }

        public bool HasAny(params string[] permissionCodes)
        {
            if (permissionCodes == null || permissionCodes.Length == 0)
                return false;

            foreach (var code in permissionCodes)
            {
                if (PermissionCodes.Contains(code))
                    return true;
            }

            return false;
        }

        public bool HasAll(params string[] permissionCodes)
        {
            if (permissionCodes == null || permissionCodes.Length == 0)
                return false;

            foreach (var code in permissionCodes)
            {
                if (!PermissionCodes.Contains(code))
                    return false;
            }

            return true;
        }

        /* ========================================
           Reset
        ======================================== */

        public void Reset()
        {
            IsAuthenticated = false;
            Login = string.Empty;
            PrimarySid = string.Empty;
            ObjectGuid = null;

            UserId = null;
            IsActive = null;
            RoleId = null;
            RoleName = string.Empty;
            Roles.Clear();
            RoleIds.Clear();

            PermissionsVersion = 0;
            PermissionCodes.Clear();

            IsInitialized = false;
        }
    }
}
