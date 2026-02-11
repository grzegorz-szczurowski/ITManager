// File: Services/Auth/ActiveDirectoryObjectGuidResolver.cs
// Description: Serwis mapujący SID (S-1-5-21-...) na ObjectGUID w Active Directory.
//              Działa tylko na Windows. Na innych OS zwraca null.
// Created: 2025-12-15
// Updated:
// - 2025-12-15 - wersja 1.00
// - 2026-02-02 - wersja 1.10:
//   - FIX: bezpieczniejsze połączenie z AD (Secure + Signing + Sealing)
//   - FIX: limity czasowe DirectorySearcher
//   - FIX: przeniesienie synchronicznych wywołań LDAP do Task.Run (mniej blokowania w Blazor Server)
//   - LOG: czytelniejsze logowanie błędów
// Version: 1.10

using System;
using System.DirectoryServices;
using System.Security.Principal;
using System.Threading.Tasks;

namespace ITManager.Models.Auth
{
    public sealed class ActiveDirectoryObjectGuidResolver : IActiveDirectoryObjectGuidResolver
    {
        // Sensowne timeouty dla środowiska firmowego
        private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ServerTimeLimit = TimeSpan.FromSeconds(5);

        // W IIS ważne: to idzie na tożsamości procesu (AppPool)
        private const AuthenticationTypes AuthTypes =
            AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;

        public Task<Guid?> TryResolveObjectGuidBySidAsync(string? primarySid)
        {
            if (!OperatingSystem.IsWindows())
                return Task.FromResult<Guid?>(null);

            if (string.IsNullOrWhiteSpace(primarySid))
                return Task.FromResult<Guid?>(null);

            // DirectoryServices nie ma sensownego async, więc izolujemy sync I/O w Task.Run
            return Task.Run(() =>
            {
                try
                {
                    var sid = new SecurityIdentifier(primarySid);

                    var sidBytes = new byte[sid.BinaryLength];
                    sid.GetBinaryForm(sidBytes, 0);

                    var sidFilter = BuildLdapSidFilter(sidBytes);

                    using var rootDse = new DirectoryEntry("LDAP://RootDSE", null, null, AuthTypes);
                    var defaultNamingContext = rootDse.Properties["defaultNamingContext"]?.Value?.ToString();

                    if (string.IsNullOrWhiteSpace(defaultNamingContext))
                        return (Guid?)null;

                    using var searchRoot = new DirectoryEntry($"LDAP://{defaultNamingContext}", null, null, AuthTypes);
                    using var searcher = new DirectorySearcher(searchRoot)
                    {
                        Filter = $"(objectSid={sidFilter})",
                        SearchScope = SearchScope.Subtree,
                        PageSize = 1,
                        SizeLimit = 1,
                        CacheResults = false,
                        ClientTimeout = ClientTimeout,
                        ServerTimeLimit = ServerTimeLimit
                    };

                    searcher.PropertiesToLoad.Clear();
                    searcher.PropertiesToLoad.Add("objectGUID");

                    var result = searcher.FindOne();
                    if (result == null)
                        return (Guid?)null;

                    if (result.Properties["objectGUID"] == null || result.Properties["objectGUID"].Count == 0)
                        return (Guid?)null;

                    var guidBytes = result.Properties["objectGUID"][0] as byte[];
                    if (guidBytes == null || guidBytes.Length != 16)
                        return (Guid?)null;

                    // AD zwraca objectGUID jako byte[16] w formacie zgodnym z Guid(byte[])
                    return new Guid(guidBytes);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AD] Mapowanie SID -> ObjectGUID nieudane. SID={primarySid}. Błąd: {ex}");
                    return (Guid?)null;
                }
            });
        }

        private static string BuildLdapSidFilter(byte[] sidBytes)
        {
            // Zamiana bajtów SID na format \AA\BB\CC wymagany w LDAP filter.
            var chars = new char[sidBytes.Length * 3];

            var idx = 0;
            for (var i = 0; i < sidBytes.Length; i++)
            {
                chars[idx++] = '\\';

                var b = sidBytes[i];
                var hi = (b >> 4) & 0x0F;
                var lo = b & 0x0F;

                chars[idx++] = ToHexChar(hi);
                chars[idx++] = ToHexChar(lo);
            }

            return new string(chars);
        }

        private static char ToHexChar(int value)
        {
            if (value < 10)
                return (char)('0' + value);

            return (char)('A' + (value - 10));
        }
    }
}
