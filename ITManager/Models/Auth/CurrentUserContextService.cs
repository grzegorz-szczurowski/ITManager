// File: Services/Auth/CurrentUserContextService.cs
// Description: Inicjalizuje CurrentUserContext na podstawie HttpContext (Windows Auth) i bazy ITManager (RBAC).
// Notes:
//   - Windows only.
//   - UserRoles only: brak logiki IsOperator i brak fallback do users.role_id (to jest po stronie RbacAuthorizationService).
//   - Udostępnia helpery Can/CanAny/CanAll i HasAccess.
// Version: 1.31
// Updated: 2026-02-02 - FIX: PrimarySid albo Sid + wspólna ścieżka resolve ObjectGuid jak w handlerze policy.
// Updated: 2026-01-09 - Windows only: rozdzielenie stanów: Anonymous vs Windows authenticated bez dostępu.
// Updated: 2026-01-09 - v1.30: helpery permissionów + HasAccess + stabilizacja stanów.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ITManager.Models.Auth;

namespace ITManager.Services.Auth
{
    public sealed class CurrentUserContextService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly CurrentUserContext _currentUser;
        private readonly IActiveDirectoryObjectGuidResolver _adResolver;
        private readonly IAppAuthorizationService _authService;

        private bool _effectivePermsLoaded;
        private HashSet<string>? _effectivePermCodes;

        public CurrentUserContextService(
            IHttpContextAccessor httpContextAccessor,
            CurrentUserContext currentUser,
            IActiveDirectoryObjectGuidResolver adResolver,
            IAppAuthorizationService authService)
        {
            _httpContextAccessor = httpContextAccessor;
            _currentUser = currentUser;
            _adResolver = adResolver;
            _authService = authService;
        }

        public CurrentUserContext CurrentUser => _currentUser;

        public bool HasAccess =>
            _currentUser.IsInitialized
            && _currentUser.IsAuthenticated
            && _currentUser.UserId.HasValue
            && _currentUser.IsActive == true;

        public async Task EnsureInitializedAsync()
        {
            if (_currentUser.IsInitialized)
                return;

            try
            {
                var httpUser = _httpContextAccessor.HttpContext?.User;

                if (httpUser?.Identity?.IsAuthenticated != true)
                {
                    InitializeAnonymous();
                    return;
                }

                _currentUser.Reset();

                _currentUser.IsAuthenticated = true;
                _currentUser.Login = httpUser.Identity?.Name ?? string.Empty;

                // FIX: PrimarySid albo Sid (spójnie z resolver extension)
                var sid =
                    httpUser.FindFirstValue(ClaimTypes.PrimarySid) ??
                    httpUser.FindFirstValue(ClaimTypes.Sid);

                _currentUser.PrimarySid = sid ?? string.Empty;

                if (string.IsNullOrWhiteSpace(_currentUser.PrimarySid))
                {
                    InitializeNoAccess();
                    return;
                }

                // Spójna ścieżka: resolver.ResolveAsync robi fallback claimów i mapowanie SID -> ObjectGuid
                var objectGuid = await _adResolver.ResolveAsync(httpUser).ConfigureAwait(false);
                _currentUser.ObjectGuid = objectGuid;

                if (objectGuid == null)
                {
                    InitializeNoAccess();
                    return;
                }

                var auth = await _authService.GetAuthorizationAsync(objectGuid.Value).ConfigureAwait(false);

                if (!auth.Ok || auth.UserId <= 0 || auth.IsActive == false || auth.Roles == null || auth.Roles.Count == 0)
                {
                    InitializeNoAccess();
                    return;
                }

                _currentUser.UserId = auth.UserId;
                _currentUser.IsActive = auth.IsActive;

                _currentUser.Roles = auth.Roles ?? new List<AuthorizationInfo.RoleInfo>();
                _currentUser.RoleIds = new List<int>();

                foreach (var rr in _currentUser.Roles)
                {
                    if (rr != null && rr.RoleId > 0)
                        _currentUser.RoleIds.Add(rr.RoleId);
                }

                _currentUser.RoleId = auth.RoleId;
                _currentUser.RoleName = auth.RoleName;

                _currentUser.PermissionsVersion = auth.PermissionsVersion;
                _currentUser.PermissionCodes = auth.PermissionCodes
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                _currentUser.IsInitialized = true;

                _effectivePermsLoaded = false;
                _effectivePermCodes = null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Auth] Błąd inicjalizacji CurrentUserContext: {ex}");
                InitializeAnonymous();
            }
        }

        public async Task<HashSet<string>> GetEffectivePermissionCodesAsync()
        {
            if (_effectivePermsLoaded && _effectivePermCodes != null)
                return _effectivePermCodes;

            await EnsureInitializedAsync().ConfigureAwait(false);

            _effectivePermCodes = _currentUser.PermissionCodes != null
                ? new HashSet<string>(_currentUser.PermissionCodes, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _effectivePermsLoaded = true;
            return _effectivePermCodes;
        }

        public async Task<bool> CanAsync(string permissionCode)
        {
            if (string.IsNullOrWhiteSpace(permissionCode))
                return false;

            var perms = await GetEffectivePermissionCodesAsync().ConfigureAwait(false);
            return perms.Contains(permissionCode.Trim());
        }

        public async Task<bool> CanAnyAsync(params string[] permissionCodes)
        {
            if (permissionCodes == null || permissionCodes.Length == 0)
                return false;

            var perms = await GetEffectivePermissionCodesAsync().ConfigureAwait(false);

            foreach (var p in permissionCodes)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                if (perms.Contains(p.Trim()))
                    return true;
            }

            return false;
        }

        public async Task<bool> CanAllAsync(params string[] permissionCodes)
        {
            if (permissionCodes == null || permissionCodes.Length == 0)
                return false;

            var perms = await GetEffectivePermissionCodesAsync().ConfigureAwait(false);

            foreach (var p in permissionCodes)
            {
                if (string.IsNullOrWhiteSpace(p))
                    return false;

                if (!perms.Contains(p.Trim()))
                    return false;
            }

            return true;
        }

        public bool Can(string permissionCode)
        {
            if (!_effectivePermsLoaded || _effectivePermCodes == null)
                return false;

            if (string.IsNullOrWhiteSpace(permissionCode))
                return false;

            return _effectivePermCodes.Contains(permissionCode.Trim());
        }

        public bool CanAny(params string[] permissionCodes)
        {
            if (!_effectivePermsLoaded || _effectivePermCodes == null)
                return false;

            if (permissionCodes == null || permissionCodes.Length == 0)
                return false;

            foreach (var p in permissionCodes)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                if (_effectivePermCodes.Contains(p.Trim()))
                    return true;
            }

            return false;
        }

        public bool CanAll(params string[] permissionCodes)
        {
            if (!_effectivePermsLoaded || _effectivePermCodes == null)
                return false;

            if (permissionCodes == null || permissionCodes.Length == 0)
                return false;

            foreach (var p in permissionCodes)
            {
                if (string.IsNullOrWhiteSpace(p))
                    return false;

                if (!_effectivePermCodes.Contains(p.Trim()))
                    return false;
            }

            return true;
        }

        private void InitializeAnonymous()
        {
            _currentUser.Reset();
            _currentUser.IsInitialized = true;

            _effectivePermsLoaded = false;
            _effectivePermCodes = null;
        }

        private void InitializeNoAccess()
        {
            _currentUser.UserId = null;
            _currentUser.IsActive = false;

            _currentUser.RoleName = string.Empty;
            _currentUser.RoleId = null;
            _currentUser.Roles = new List<AuthorizationInfo.RoleInfo>();
            _currentUser.RoleIds = new List<int>();

            _currentUser.PermissionsVersion = 0;
            _currentUser.PermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _currentUser.IsInitialized = true;

            _effectivePermsLoaded = false;
            _effectivePermCodes = null;
        }
    }
}
