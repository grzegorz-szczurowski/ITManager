// File: Services/Auth/PermissionRequirement.cs
// Description: Requirement dla policy typu "Perm:<CODE>".
// Created: 2026-01-09
// Version: 1.00

using Microsoft.AspNetCore.Authorization;
using System;

namespace ITManager.Models.Auth
{
    public sealed class PermissionRequirement : IAuthorizationRequirement
    {
        public PermissionRequirement(string permissionCode)
        {
            PermissionCode = (permissionCode ?? string.Empty).Trim();
        }

        public string PermissionCode { get; }
    }
}
