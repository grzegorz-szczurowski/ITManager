// File: Services/Auth/ActiveDirectoryObjectGuidResolverExtensions.cs
// Description: Extension methods for resolving AD ObjectGUID for current user.
//              Windows auth: uses PrimarySid (or SID) claim from ClaimsPrincipal and resolves ObjectGUID via resolver.
// Created: 2026-01-09
// Version: 1.02
// Change log:
// - 1.01 (2026-01-09) Usunięto reflection. Stabilne mapowanie ClaimsPrincipal -> SID -> ObjectGUID.
// - 1.02 (2026-02-02) FIX: fallback SID z WindowsIdentity, gdy brak claimów PrimarySid/Sid (stabilniej dla Blazor Server/IIS).

using System;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using ITManager.Models.Auth;

namespace ITManager.Models.Auth
{
    public static class ActiveDirectoryObjectGuidResolverExtensions
    {
        /// <summary>
        /// Resolves AD ObjectGUID for given ClaimsPrincipal.
        /// Uses PrimarySid (preferred) or SID claim and maps it to ObjectGUID via resolver.
        /// Fallback: if claims are missing, tries to read SID from WindowsIdentity attached to principal.
        /// </summary>
        public static async Task<Guid?> ResolveAsync(this IActiveDirectoryObjectGuidResolver resolver, ClaimsPrincipal user)
        {
            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));

            if (user == null)
                return null;

            // IIS Windows Auth zwykle daje PrimarySid, czasem tylko Sid
            var sid =
                user.FindFirstValue(ClaimTypes.PrimarySid) ??
                user.FindFirstValue(ClaimTypes.Sid);

            // Fallback: niektóre konfiguracje Blazor Server/IIS nie podają tych claimów,
            // ale ClaimsPrincipal.Identity bywa WindowsIdentity i ma SID w User.
            if (string.IsNullOrWhiteSpace(sid))
            {
                if (user.Identity is WindowsIdentity winIdentity && winIdentity.User != null)
                    sid = winIdentity.User.Value;
            }

            if (string.IsNullOrWhiteSpace(sid))
                return null;

            return await resolver.TryResolveObjectGuidBySidAsync(sid).ConfigureAwait(false);
        }
    }
}
