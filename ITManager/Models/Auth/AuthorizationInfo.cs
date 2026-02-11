// File: Models/Auth/AuthorizationInfo.cs
// Description: Wynik autoryzacji użytkownika (RBAC):
//              role (wiele), aktywność, wersja uprawnień oraz zestaw PermissionCodes.
// Created: 2025-12-23
// Updated: 2025-12-25 - Multi-role: lista ról użytkownika (dbo.UserRoles) oraz backward-compatible RoleId/RoleName.
// Version: 1.10
// Change log:
// - 1.01 (2025-12-23) RBAC only, brak legacy.
// - 1.10 (2025-12-25) Multi-role: dodano Roles (lista), RoleId/RoleName pozostają jako "primary" do kompatybilności.

using System;
using System.Collections.Generic;

namespace ITManager.Models.Auth
{
    public sealed class AuthorizationInfo
    {
        public sealed class RoleInfo
        {
            public int RoleId { get; set; }
            public string RoleName { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public int SortOrder { get; set; }
            public bool IsActive { get; set; }
        }

        public bool Ok { get; set; }

        public int UserId { get; set; }

        public bool IsActive { get; set; }

        /// <summary>
        /// Lista ról użytkownika. Przy multi-role to jest źródło prawdy.
        /// RoleId/RoleName poniżej zostawiamy dla kompatybilności z istniejącym kodem.
        /// </summary>
        public List<RoleInfo> Roles { get; set; } = new List<RoleInfo>();

        /// <summary>
        /// Primary role id (kompatybilność wstecz).
        /// </summary>
        public int RoleId { get; set; }

        /// <summary>
        /// Primary role name (kompatybilność wstecz).
        /// Przy multi-role ustawiane jako CSV z nazw ról.
        /// </summary>
        public string RoleName { get; set; } = string.Empty;

        public int PermissionsVersion { get; set; }

        public HashSet<string> PermissionCodes { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
