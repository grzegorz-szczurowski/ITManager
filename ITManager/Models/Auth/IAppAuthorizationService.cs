// File: Services/Auth/IAppAuthorizationService.cs
// Description: Kontrakt pobierania uprawnień użytkownika z bazy ITManager (RBAC).
//              Windows only: autoryzacja wyłącznie dla uwierzytelnionych użytkowników domenowych.
// Created: 2025-12-15
// Updated: 2025-12-16 - dodanie userId (dbo.users.id).
// Updated: 2025-12-23 - RBAC: GetAuthorizationAsync (permission codes).
// Updated: 2025-12-23 - RBAC only: usunięcie legacy GetPermissionsAsync.
// Updated: 2026-01-09 - wersja 1.20 - Windows only: usunięto Guest / GetAuthorizationForRoleAsync.
// Version: 1.20
// Change log:
// - 1.03 (2025-12-23) RBAC only: kontrakt tylko na AuthorizationInfo.
// - 1.20 (2026-01-09) Windows only: usunięcie kontraktu Guest.

using System;
using System.Threading.Tasks;
using ITManager.Models.Auth;

namespace ITManager.Models.Auth
{
    public interface IAppAuthorizationService
    {
        /// <summary>
        /// Pobiera uprawnienia RBAC dla uwierzytelnionego użytkownika
        /// identyfikowanego przez ObjectGUID z Active Directory.
        /// </summary>
        Task<AuthorizationInfo> GetAuthorizationAsync(Guid objectGuid);
    }
}
