// File: Services/Auth/PermissionPolicyProvider.cs
// Description: Dynamiczny provider policy: "Perm:<CODE>".
// Created: 2026-01-09
// Updated: 2026-01-25 - wersja 1.01:
//   - NEW: cache policy dla "Perm:<CODE>", żeby nie budować ich przy każdym wywołaniu.
// Version: 1.01

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace ITManager.Models.Auth
{
    public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public const string PolicyPrefix = "Perm:";

        private static readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.OrdinalIgnoreCase);

        public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
            : base(options)
        {
        }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            if (!string.IsNullOrWhiteSpace(policyName) &&
                policyName.StartsWith(PolicyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<AuthorizationPolicy?>(_cache.GetOrAdd(policyName, BuildPolicy));
            }

            return base.GetPolicyAsync(policyName);
        }

        private static AuthorizationPolicy BuildPolicy(string policyName)
        {
            var code = policyName.Substring(PolicyPrefix.Length).Trim();

            // Pusta policy ma sens tylko jako "nie autoryzuj", więc zostawiamy RequireAuthenticatedUser i requirement.
            // Jeśli code jest puste, requirement dostanie pusty string i handler zwróci false.
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(code))
                .Build();
        }
    }
}
