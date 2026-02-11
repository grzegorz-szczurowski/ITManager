// File: Services/Auth/IActiveDirectoryObjectGuidResolver.cs
// Description: Kontrakt serwisu mapującego SID użytkownika Windows na ObjectGUID w Active Directory.
// Created: 2025-12-15
// Version: 1.00

using System;
using System.Threading.Tasks;

namespace ITManager.Models.Auth
{
    public interface IActiveDirectoryObjectGuidResolver
    {
        Task<Guid?> TryResolveObjectGuidBySidAsync(string? primarySid);
    }
}
