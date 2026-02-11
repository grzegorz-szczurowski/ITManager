// File: Services/Auth/PermissionAuthorizationHandler.cs
// Description: Handler sprawdzający permission code z bazy RBAC dla bieżącego użytkownika.
//              Policy name: "Perm:<CODE>".
// Created: 2026-01-09
// Updated: 2026-01-11 - wersja 1.01:
//   - Fix: nie używaj IHttpContextAccessor w handlerze, bo HttpContext bywa null w Blazor Server przy nawigacji.
//   - Użyj context.User (ClaimsPrincipal) zamiast http.HttpContext.User.
// Updated: 2026-01-25 - wersja 1.02:
//   - FIX: porównanie permission code bez wrażliwości na wielkość liter + trimming.
//   - NEW: wsparcie wildcardów: "*" oraz "<PREFIX>.*" po stronie posiadanych uprawnień.
// Updated: 2026-02-02 - wersja 1.03:
//   - NEW: wsparcie agregacji po prefiksie po stronie WYMAGANEGO permissiona
//          (np. wymagane "tickets.view" pozwala, gdy user ma "tickets.view.own/assigned/team/all").
// Version: 1.03

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace ITManager.Models.Auth
{
    public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public PermissionAuthorizationHandler(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (context?.User?.Identity?.IsAuthenticated != true)
                return;

            var requestedCode = (requirement?.PermissionCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedCode))
                return;

            using var scope = _scopeFactory.CreateScope();

            var resolver = scope.ServiceProvider.GetService<IActiveDirectoryObjectGuidResolver>();
            var rbac = scope.ServiceProvider.GetService<IAppAuthorizationService>();

            if (resolver == null || rbac == null)
                return;

            var objectGuid = await resolver.ResolveAsync(context.User).ConfigureAwait(false);
            if (!objectGuid.HasValue || objectGuid.Value == Guid.Empty)
                return;

            var auth = await rbac.GetAuthorizationAsync(objectGuid.Value).ConfigureAwait(false);
            if (auth == null || !auth.IsActive)
                return;

            if (HasPermission(auth.PermissionCodes, requestedCode))
                context.Succeed(requirement);
        }

        private static bool HasPermission(System.Collections.Generic.IReadOnlyCollection<string>? permissionCodes, string requestedCode)
        {
            if (permissionCodes == null || permissionCodes.Count == 0)
                return false;

            var req = (requestedCode ?? string.Empty).Trim();
            if (req.Length == 0)
                return false;

            foreach (var raw in permissionCodes)
            {
                var p = (raw ?? string.Empty).Trim();
                if (p.Length == 0)
                    continue;

                // Global allow
                if (string.Equals(p, "*", StringComparison.Ordinal))
                    return true;

                // Exact match (case-insensitive)
                if (string.Equals(p, req, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Posiadane uprawnienie jako wildcard prefix: "tickets.*" pasuje do "tickets.view.own", itd.
                if (p.EndsWith(".*", StringComparison.Ordinal))
                {
                    var prefix = p.Substring(0, p.Length - 2).Trim();
                    if (prefix.Length == 0)
                        continue;

                    if (req.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) || string.Equals(req, prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // NEW: wymagane uprawnienie jako prefiks agregujący:
                // wymagane "tickets.view" pasuje do posiadanego "tickets.view.own", "tickets.view.assigned", ...
                if (p.StartsWith(req + ".", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
