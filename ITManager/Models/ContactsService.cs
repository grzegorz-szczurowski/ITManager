// File: Models/ContactsService.cs
// Description: Model danych kontaktów i serwis do komunikacji z bazą ITManager
//              z wykorzystaniem tabel dbo.Contacts_PBX oraz dbo.Contacts_AD.
// Created: 2025-12-05
// Updated: 2025-12-21 - dodanie pobierania kontaktu po ObjectGUID (DB identyfikacja usera) + pola opcjonalne w modelu Contact
// Updated: 2025-12-21 - poprawa GetContactByObjectGuidAsync: dopasowanie do faktycznego schematu kolumn (bez IsActive/Surname/EmailAddress jeśli nie istnieją)
// Updated: 2025-12-23 - dodanie LogPageVisitAsync (naprawa błędu CS0103: brak metody w kontekście)
// Updated: 2026-01-13 - migracja na ITManagerConnection + dbo. prefiksy + filtr kont serwisowych (AccountTypeId <> 2) po stronie SQL
// Updated: 2026-01-25 - RBAC: backend guard dla Contacts.View oraz metoda pod druk (Contacts.Print)
// Updated: 2026-02-03 - DisplayName format: "Nazwisko, Imię" (spójnie z UI wymaganiem)
// Version: 1.06
// Change history:
// 1.00 (2025-12-05) - Initial version
// 1.01 (2025-12-21) - GetContactByObjectGuidAsync(Guid) + opcjonalne pola: ObjectGuid, DisplayName
// 1.02 (2025-12-21) - GetContactByObjectGuidAsync: wykrywanie dostępnych kolumn w Contacts_AD i budowanie SQL pod realny schemat
// 1.03 (2025-12-23) - LogPageVisitAsync: zapis wizyty do PageVisits (zabezpieczone try/catch)
// 1.04 (2026-01-13) - ITManagerConnection + dbo + filtr AccountTypeId <> 2
// 1.05 (2026-01-25) - RBAC: GuardCanViewContactsAsync + GuardCanPrintContactsAsync, GetContactsForPrintAsync
// 1.06 (2026-02-03) - DisplayName: "Nazwisko, Imię" zamiast "Imię Nazwisko"

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using ITManager.Services.Auth;

namespace ITManager.Models
{
    public class Contact
    {
        // Pola używane w UI, zostają bez zmian
        public required string Nazwisko { get; set; }
        public required string Imie { get; set; }
        public required string Dzial { get; set; }
        public required string Stanowisko { get; set; }
        public required string Wewnetrzny { get; set; }
        public required string Stacjonarny { get; set; }
        public required string Komorkowy { get; set; }
        public required string Email { get; set; }

        // Nowe pola opcjonalne
        public Guid? ObjectGuid { get; set; }
        public string? DisplayName { get; set; }
    }

    public class ContactsService
    {
        private readonly IConfiguration _configuration;
        private readonly CurrentUserContextService _currentUserContextService;

        private const string PermContactsView = "Contacts.View";
        private const string PermContactsPrint = "Contacts.Print";

        // Lista hostów, dla których nie logujemy wizyt (np. localhost, stanowiska IT)
        private readonly List<string> excludedHostNames = new()
        {
            "::1",
            "127.0.0.1",
            "TFTWALWKS004",
            "TFTWALWKS123"
        };

        public ContactsService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        }

        // =========================
        // RBAC + context
        // =========================

        private async Task EnsureUserContextInitializedAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
        }

        private async Task GuardCanViewContactsAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has(PermContactsView))
                throw new UnauthorizedAccessException("Brak uprawnień do podglądu kontaktów.");
        }

        private async Task GuardCanPrintContactsAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has(PermContactsPrint))
                throw new UnauthorizedAccessException("Brak uprawnień do drukowania kontaktów.");
        }

        private string GetItManagerConnectionStringOrThrow()
        {
            var connectionString = _configuration.GetConnectionString("ITManagerConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Brak connection stringa 'ITManagerConnection'. Ustaw go w User Secrets lub zmiennej środowiskowej.");
            }

            return connectionString;
        }

        /// <summary>
        /// Pobiera listę kontaktów z połączonych tabel dbo.Contacts_PBX oraz dbo.Contacts_AD.
        /// Filtrowanie działa po imieniu, nazwisku, dziale, stanowisku, numerach telefonów i mailu.
        /// Dodatkowo: ukrywa konta serwisowe na podstawie dbo.users.AccountTypeId = 2.
        /// RBAC: wymaga Contacts.View.
        /// </summary>
        public async Task<List<Contact>> GetContactsAsync(string filter = "")
        {
            await GuardCanViewContactsAsync().ConfigureAwait(false);

            var results = new List<Contact>();
            var connectionString = GetItManagerConnectionStringOrThrow();

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                // W bazie funkcjonują dwa różne schematy Contacts_AD (kolumny mają nazwy
                // z AD lub starsze, lowercase). Pobieramy listę kolumn i budujemy zapytanie
                // które nie odwołuje się do nieistniejących nazw.
                var availableColumns = await GetContactsAdColumnsAsync(connection);

                var hasIsActive = availableColumns.Contains("IsActive");
                var hasStatusNew = availableColumns.Contains("Status");
                var hasSurname = availableColumns.Contains("Surname");
                var hasGivenName = availableColumns.Contains("GivenName");
                var hasEmailAddress = availableColumns.Contains("EmailAddress");
                var hasTelephoneNumber = availableColumns.Contains("TelephoneNumber");
                var hasMobile = availableColumns.Contains("Mobile");
                var hasDepartment = availableColumns.Contains("Department");
                var hasTitle = availableColumns.Contains("Title");
                var hasDisplayName = availableColumns.Contains("DisplayName");
                var hasObjectGuid = availableColumns.Contains("ObjectGUID");
                var hasFullName = availableColumns.Contains("FullName");

                var hasSn = availableColumns.Contains("sn");
                var hasGivenNameOld = availableColumns.Contains("givenName");
                var hasMail = availableColumns.Contains("mail");
                var hasTelephoneNumberOld = availableColumns.Contains("telephoneNumber");
                var hasMobileOld = availableColumns.Contains("mobile");
                var hasDepartmentOld = availableColumns.Contains("department");
                var hasTitleOld = availableColumns.Contains("title");
                var hasStatusOld = availableColumns.Contains("status");

                var adSurname = hasSurname ? "AD.Surname" : hasSn ? "AD.sn" : "NULL";
                var adGivenName = hasGivenName ? "AD.GivenName" : hasGivenNameOld ? "AD.givenName" : "NULL";
                var adDepartment = hasDepartment ? "AD.Department" : hasDepartmentOld ? "AD.department" : "NULL";
                var adTitle = hasTitle ? "AD.Title" : hasTitleOld ? "AD.title" : "NULL";
                var adTelephone = hasTelephoneNumber ? "AD.TelephoneNumber" : hasTelephoneNumberOld ? "AD.telephoneNumber" : "NULL";
                var adMobile = hasMobile ? "AD.Mobile" : hasMobileOld ? "AD.mobile" : "NULL";
                var adEmail = hasEmailAddress ? "AD.EmailAddress" : hasMail ? "AD.mail" : "NULL";
                var adDisplayName = hasDisplayName ? "AD.DisplayName" : hasFullName ? "AD.FullName" : "NULL";
                var adObjectGuid = hasObjectGuid ? "AD.ObjectGUID" : "NULL";

                var adWewnetrzny = adTelephone == "NULL" ? "NULL" : $"RIGHT({adTelephone}, 3)";

                var statusFilters = new List<string>();
                if (hasIsActive)
                    statusFilters.Add("(AD.IsActive = 1 OR AD.IsActive IS NULL)");

                if (hasStatusNew)
                    statusFilters.Add("(AD.Status = '1' OR AD.Status IS NULL OR AD.Status = 1)");

                if (hasStatusOld)
                    statusFilters.Add("(AD.status = 1 OR AD.status IS NULL OR AD.status = '1')");

                var statusClause = statusFilters.Count > 0
                    ? string.Join(" AND ", statusFilters)
                    : "1 = 1";

                var searchConditions = new List<string>
                {
                    "PBX.FirstName     LIKE '%' + @search + '%'",
                    "PBX.Surname       LIKE '%' + @search + '%'",
                    "PBX.ExtNo         LIKE '%' + @search + '%'",
                    "PBX.Number        LIKE '%' + @search + '%'",
                };

                if (adGivenName != "NULL")
                    searchConditions.Add($"{adGivenName} LIKE '%' + @search + '%'");
                if (adSurname != "NULL")
                    searchConditions.Add($"{adSurname} LIKE '%' + @search + '%'");
                if (adDepartment != "NULL")
                    searchConditions.Add($"{adDepartment} LIKE '%' + @search + '%'");
                if (adTitle != "NULL")
                    searchConditions.Add($"{adTitle} LIKE '%' + @search + '%'");
                if (adTelephone != "NULL")
                    searchConditions.Add($"{adTelephone} LIKE '%' + @search + '%'");
                if (adMobile != "NULL")
                    searchConditions.Add($"{adMobile} LIKE '%' + @search + '%'");
                if (adEmail != "NULL")
                    searchConditions.Add($"{adEmail} LIKE '%' + @search + '%'");

                var joinCondition = hasFullName ? "PBX.FullName = AD.FullName" : "1 = 0";
                var searchClause = string.Join(" OR ", searchConditions);

                // Filtr kont serwisowych:
                // - jeśli mamy AD.ObjectGUID, łączymy do dbo.users po ObjectGUID
                // - ukrywamy tylko wtedy, gdy istnieje rekord w dbo.users i AccountTypeId = 2
                // - brak rekordu w dbo.users => kontakt zostaje
                var usersJoin = hasObjectGuid
                    ? "LEFT JOIN dbo.[users] U ON U.ObjectGUID = AD.ObjectGUID"
                    : "LEFT JOIN dbo.[users] U ON 1 = 0";

                var accountTypeClause = "(U.AccountTypeId IS NULL OR U.AccountTypeId <> 2)";

                // DisplayName: wymuszamy "Nazwisko, Imię" (z fallbackami na dostępne źródła).
                // Najpierw bierzemy Nazwisko/Imię z PBX lub AD (tak jak dla pól Nazwisko/Imie),
                // a jeśli nie ma danych, to zostawiamy adDisplayName lub PBX.FullName.
                var sql = $@"
                    SELECT
                        COALESCE(PBX.Surname, {adSurname}) AS Nazwisko,
                        COALESCE(PBX.FirstName, {adGivenName}) AS Imie,
                        COALESCE({adDepartment}, '') AS Dzial,
                        COALESCE({adTitle}, '') AS Stanowisko,
                        COALESCE(PBX.ExtNo, {adWewnetrzny}) AS Wewnetrzny,
                        COALESCE({adTelephone}, '') AS Stacjonarny,
                        COALESCE({adMobile}, PBX.Number) AS Komorkowy,
                        COALESCE({adEmail}, '') AS Email,
                        {adObjectGuid} AS ObjectGuid,
                        CASE
                            WHEN LTRIM(RTRIM(COALESCE(PBX.Surname, {adSurname}, ''))) <> ''
                                 AND LTRIM(RTRIM(COALESCE(PBX.FirstName, {adGivenName}, ''))) <> ''
                                THEN
                                    LTRIM(RTRIM(COALESCE(PBX.Surname, {adSurname}))) + ', ' + LTRIM(RTRIM(COALESCE(PBX.FirstName, {adGivenName})))
                            WHEN LTRIM(RTRIM(COALESCE(PBX.Surname, {adSurname}, ''))) <> ''
                                THEN
                                    LTRIM(RTRIM(COALESCE(PBX.Surname, {adSurname})))
                            WHEN LTRIM(RTRIM(COALESCE(PBX.FirstName, {adGivenName}, ''))) <> ''
                                THEN
                                    LTRIM(RTRIM(COALESCE(PBX.FirstName, {adGivenName})))
                            ELSE
                                COALESCE({adDisplayName}, PBX.FullName)
                        END AS DisplayName
                    FROM dbo.Contacts_PBX PBX
                    FULL JOIN dbo.Contacts_AD AD ON {joinCondition}
                    {usersJoin}
                    WHERE {statusClause}
                      AND {accountTypeClause}
                      AND (@search = '' OR {searchClause})
                    ORDER BY Nazwisko, Imie;";

                await using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@search", filter ?? string.Empty);

                await using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var contact = new Contact
                    {
                        Nazwisko = reader["Nazwisko"]?.ToString() ?? string.Empty,
                        Imie = reader["Imie"]?.ToString() ?? string.Empty,
                        Dzial = reader["Dzial"]?.ToString() ?? string.Empty,
                        Stanowisko = reader["Stanowisko"]?.ToString() ?? string.Empty,
                        Wewnetrzny = reader["Wewnetrzny"]?.ToString() ?? string.Empty,
                        Stacjonarny = reader["Stacjonarny"]?.ToString() ?? string.Empty,
                        Komorkowy = reader["Komorkowy"]?.ToString() ?? string.Empty,
                        Email = reader["Email"]?.ToString() ?? string.Empty,
                        ObjectGuid = Guid.TryParse(reader["ObjectGuid"]?.ToString(), out var parsedGuid)
                            ? parsedGuid
                            : null,
                        DisplayName = reader["DisplayName"]?.ToString()
                    };

                    results.Add(contact);
                }

                await LogPageVisitAsync(connection);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ContactsService.GetContactsAsync] Błąd odczytu kontaktów: {ex}");
                throw;
            }

            return results;
        }

        /// <summary>
        /// Metoda pod druk kontaktów.
        /// RBAC: wymaga Contacts.Print.
        /// </summary>
        public async Task<List<Contact>> GetContactsForPrintAsync(string filter = "")
        {
            await GuardCanPrintContactsAsync().ConfigureAwait(false);
            return await GetContactsAsync(filter).ConfigureAwait(false);
        }

        /// <summary>
        /// Pobiera pojedynczy kontakt po ObjectGUID.
        /// Dodatkowo: ukrywa konto, jeśli dbo.users.AccountTypeId = 2.
        /// RBAC: wymaga Contacts.View.
        /// </summary>
        public async Task<Contact?> GetContactByObjectGuidAsync(Guid objectGuid)
        {
            await GuardCanViewContactsAsync().ConfigureAwait(false);

            if (objectGuid == Guid.Empty)
                return null;

            var connectionString = GetItManagerConnectionStringOrThrow();

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                var availableColumns = await GetContactsAdColumnsAsync(connection);

                var hasIsActive = availableColumns.Contains("IsActive");
                var hasSurname = availableColumns.Contains("Surname");
                var hasGivenName = availableColumns.Contains("GivenName");
                var hasEmailAddress = availableColumns.Contains("EmailAddress");
                var hasTelephoneNumber = availableColumns.Contains("TelephoneNumber");
                var hasMobile = availableColumns.Contains("Mobile");
                var hasDepartment = availableColumns.Contains("Department");
                var hasTitle = availableColumns.Contains("Title");
                var hasDisplayName = availableColumns.Contains("DisplayName");

                var hasSn = availableColumns.Contains("sn");
                var hasGivenNameOld = availableColumns.Contains("givenName");
                var hasMail = availableColumns.Contains("mail");
                var hasTelephoneNumberOld = availableColumns.Contains("telephoneNumber");
                var hasMobileOld = availableColumns.Contains("mobile");
                var hasDepartmentOld = availableColumns.Contains("department");
                var hasTitleOld = availableColumns.Contains("title");
                var hasStatusOld = availableColumns.Contains("status");
                var hasFullNameOld = availableColumns.Contains("FullName");

                var selectNazwisko = hasSurname ? "COALESCE(AD.Surname, '')"
                    : hasSn ? "COALESCE(AD.sn, '')"
                    : "''";

                var selectImie = hasGivenName ? "COALESCE(AD.GivenName, '')"
                    : hasGivenNameOld ? "COALESCE(AD.givenName, '')"
                    : "''";

                var selectDzial = hasDepartment ? "COALESCE(AD.Department, '')"
                    : hasDepartmentOld ? "COALESCE(AD.department, '')"
                    : "''";

                var selectStanowisko = hasTitle ? "COALESCE(AD.Title, '')"
                    : hasTitleOld ? "COALESCE(AD.title, '')"
                    : "''";

                var selectWewnetrzny = (hasTelephoneNumber || hasTelephoneNumberOld)
                    ? $"COALESCE(RIGHT(COALESCE({(hasTelephoneNumber ? "AD.TelephoneNumber" : "AD.telephoneNumber")}, ''), 3), '')"
                    : "''";

                var selectStacjonarny = hasTelephoneNumber ? "COALESCE(AD.TelephoneNumber, '')"
                    : hasTelephoneNumberOld ? "COALESCE(AD.telephoneNumber, '')"
                    : "''";

                var selectKomorkowy = hasMobile ? "COALESCE(AD.Mobile, '')"
                    : hasMobileOld ? "COALESCE(AD.mobile, '')"
                    : "''";

                var selectEmail = hasEmailAddress ? "COALESCE(AD.EmailAddress, '')"
                    : hasMail ? "COALESCE(AD.mail, '')"
                    : "''";

                // DisplayName: wymuszamy "Nazwisko, Imię" jeśli mamy oba pola, w przeciwnym razie fallback.
                var selectDisplayNameFallback = hasDisplayName ? "COALESCE(AD.DisplayName, '')"
                    : hasFullNameOld ? "COALESCE(AD.FullName, '')"
                    : "''";

                var selectDisplayName = $@"
                    CASE
                        WHEN LTRIM(RTRIM({selectNazwisko})) <> '' AND LTRIM(RTRIM({selectImie})) <> ''
                            THEN LTRIM(RTRIM({selectNazwisko})) + ', ' + LTRIM(RTRIM({selectImie}))
                        WHEN LTRIM(RTRIM({selectNazwisko})) <> ''
                            THEN LTRIM(RTRIM({selectNazwisko}))
                        WHEN LTRIM(RTRIM({selectImie})) <> ''
                            THEN LTRIM(RTRIM({selectImie}))
                        ELSE {selectDisplayNameFallback}
                    END";

                var whereParts = new List<string>
                {
                    "AD.ObjectGUID = @ObjectGUID"
                };

                if (hasIsActive)
                    whereParts.Add("(AD.IsActive = 1 OR AD.IsActive IS NULL)");

                if (availableColumns.Contains("Status"))
                    whereParts.Add("(AD.Status = '1' OR AD.Status IS NULL OR AD.Status = 1)");

                if (hasStatusOld)
                    whereParts.Add("(AD.status = 1 OR AD.status IS NULL OR AD.status = '1')");

                // filtr kont serwisowych
                whereParts.Add("(U.AccountTypeId IS NULL OR U.AccountTypeId <> 2)");

                var sql = $@"
                    SELECT TOP 1
                        {selectNazwisko} AS Nazwisko,
                        {selectImie} AS Imie,
                        {selectDzial} AS Dzial,
                        {selectStanowisko} AS Stanowisko,
                        {selectWewnetrzny} AS Wewnetrzny,
                        {selectStacjonarny} AS Stacjonarny,
                        {selectKomorkowy} AS Komorkowy,
                        {selectEmail} AS Email,
                        AD.ObjectGUID AS ObjectGuid,
                        {selectDisplayName} AS DisplayName
                    FROM dbo.Contacts_AD AD
                    LEFT JOIN dbo.[users] U ON U.ObjectGUID = AD.ObjectGUID
                    WHERE {string.Join(" AND ", whereParts)};";

                await using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@ObjectGUID", objectGuid);

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                    return null;

                return new Contact
                {
                    Nazwisko = reader["Nazwisko"]?.ToString() ?? string.Empty,
                    Imie = reader["Imie"]?.ToString() ?? string.Empty,
                    Dzial = reader["Dzial"]?.ToString() ?? string.Empty,
                    Stanowisko = reader["Stanowisko"]?.ToString() ?? string.Empty,
                    Wewnetrzny = reader["Wewnetrzny"]?.ToString() ?? string.Empty,
                    Stacjonarny = reader["Stacjonarny"]?.ToString() ?? string.Empty,
                    Komorkowy = reader["Komorkowy"]?.ToString() ?? string.Empty,
                    Email = reader["Email"]?.ToString() ?? string.Empty,
                    DisplayName = reader["DisplayName"]?.ToString(),
                    ObjectGuid = objectGuid
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ContactsService.GetContactByObjectGuidAsync] Błąd SQL: {ex}");
                throw;
            }
        }

        private static async Task<HashSet<string>> GetContactsAdColumnsAsync(SqlConnection connection)
        {
            // Wymuszamy dbo, bo inaczej przy innym default schema można trafić w zły obiekt.
            const string sql = @"
                SELECT c.name
                FROM sys.columns c
                INNER JOIN sys.objects o ON o.object_id = c.object_id
                INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.type = 'U'
                  AND s.name = 'dbo'
                  AND o.name = 'Contacts_AD';";

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var name = reader["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    columns.Add(name.Trim());
            }

            return columns;
        }

        private async Task LogPageVisitAsync(SqlConnection connection)
        {
            if (connection == null)
                return;

            const string sql = @"
                IF OBJECT_ID('dbo.PageVisits', 'U') IS NULL
                    RETURN;

                INSERT INTO dbo.PageVisits (VisitDate, HostName, UserName)
                VALUES (GETDATE(), HOST_NAME(), SUSER_SNAME());";

            try
            {
                await using var command = new SqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ContactsService.LogPageVisitAsync] Nie udało się zapisać wizyty: {ex.Message}");
            }
        }

        public async Task IncreaseVisitCountAsync(string host, string userName)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            if (excludedHostNames.Contains(host, StringComparer.OrdinalIgnoreCase))
                return;

            var connectionString = _configuration.GetConnectionString("ITManagerConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine("[ContactsService.IncreaseVisitCountAsync] Brak connection stringa 'ITManagerConnection'.");
                return;
            }

            const string sql = @"
                INSERT INTO dbo.PageVisits (VisitDate, HostName, UserName)
                VALUES (GETDATE(), @HostName, @UserName);";

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await using var command = new SqlCommand(sql, connection);

                command.Parameters.AddWithValue("@HostName", host);
                command.Parameters.AddWithValue("@UserName", string.IsNullOrWhiteSpace(userName) ? "anonymous" : userName);

                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ContactsService.IncreaseVisitCountAsync] Błąd podczas logowania wizyty: {ex}");
            }
        }
    }
}
