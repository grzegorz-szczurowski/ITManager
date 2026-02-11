// File: Models/SystemsService.cs
// Description: Odczyt listy systemów z bazy ITManager (dbo.systems + słowniki). Dane do widoku MudDataGrid.
// Version: 1.11
// Created: 2025-12-16
// Change log:
// - 1.00 (2025-12-16) Pierwsza wersja: lista systemów + słowniki bazowe.
// - 1.10 (2025-12-27) Dodane pola TISAX/admin: backup frequency/status jako FK do słowników, klasyfikacja, auth, lifecycle, flags.
//                    Dodana metoda GetSystemsLookupsAsync (Dict* + users) pod filtry i edycję.
// - 1.11 (2025-12-27) Naprawa konfliktu typów: zmiana nazw LookupItem/UserLookupItem na SystemsLookupItem/SystemsUserLookupItem.

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace ITManager.Models
{
    public sealed class SystemListRow
    {
        public int Id { get; set; }

        public string SystemName { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string Criticality { get; set; } = string.Empty;

        public string DataOwner { get; set; } = string.Empty;
        public string PrimaryAdministrator { get; set; } = string.Empty;
        public string SecondaryAdministrator { get; set; } = string.Empty;

        public string HostingType { get; set; } = string.Empty;

        // Backup (nowe FK + nazwy)
        public int? BackupFrequencyId { get; set; }
        public string BackupFrequencyName { get; set; } = string.Empty;

        public int? LastBackupStatusId { get; set; }
        public string LastBackupStatusName { get; set; } = string.Empty;

        public DateTime? LastBackupDate { get; set; }

        // TISAX / admin (minimal)
        public int? InformationClassificationId { get; set; }
        public string InformationClassificationName { get; set; } = string.Empty;

        public bool? ContainsPersonalData { get; set; }
        public bool? ContainsSpecialCategories { get; set; }

        public int? AuthTypeId { get; set; }
        public string AuthTypeName { get; set; } = string.Empty;

        public bool? MfaRequired { get; set; }
        public bool? LoggingEnabled { get; set; }

        public int? LifecycleStatusId { get; set; }
        public string LifecycleStatusName { get; set; } = string.Empty;

        // Do utrzymania (na później w UI, ale warto mieć od razu)
        public int? RtoMinutes { get; set; }
        public int? RpoMinutes { get; set; }

        public DateTime? EndOfSupportDate { get; set; }

        public string ApplicationUrl { get; set; } = string.Empty;
        public string DocumentationUrl { get; set; } = string.Empty;

        // Kompatybilność z obecną kolumną "Status" w UI
        public string Status { get; set; } = string.Empty;
    }

    // Unikalne nazwy typów, żeby nie kolidowały z innymi modelami w projekcie
    public sealed class SystemsLookupItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int? SortOrder { get; set; }
    }

    public sealed class SystemsUserLookupItem
    {
        public int Id { get; set; }
        public string Display { get; set; } = string.Empty;
    }

    public sealed class SystemsLookups
    {
        public List<SystemsLookupItem> BackupFrequencies { get; set; } = new();
        public List<SystemsLookupItem> BackupStatuses { get; set; } = new();
        public List<SystemsLookupItem> InformationClassifications { get; set; } = new();
        public List<SystemsLookupItem> AuthTypes { get; set; } = new();
        public List<SystemsLookupItem> LifecycleStatuses { get; set; } = new();

        public List<SystemsUserLookupItem> Users { get; set; } = new();
    }

    public sealed class SystemsService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;

        public SystemsService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<(bool Ok, string ErrorMessage, List<SystemListRow> Rows)> GetSystemsAsync()
        {
            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new List<SystemListRow>());

                using var con = new SqlConnection(cs);
                await con.OpenAsync();

                var sql =
@"
DECLARE @UsersDisplayCol sysname = NULL;

DECLARE @Candidates TABLE(Col sysname, Ord int);
INSERT INTO @Candidates(Col, Ord) VALUES
('DisplayName', 1),
('full_name', 2),
('user_name', 3),
('login', 4),
('name', 5),
('display_name', 6),
('email', 7);

SELECT TOP 1 @UsersDisplayCol = c.Col
FROM @Candidates c
WHERE COL_LENGTH('dbo.users', c.Col) IS NOT NULL
ORDER BY c.Ord;

IF @UsersDisplayCol IS NULL
    SET @UsersDisplayCol = 'id';

DECLARE @Sql nvarchar(max) = N'
SELECT
    s.id,

    SystemName = ISNULL(s.name, N''''),

    [Type] = ISNULL(st.name, CAST(s.system_type_id AS nvarchar(50))),
    [Scope] = ISNULL(ss.name, CAST(s.system_scope_id AS nvarchar(50))),
    Criticality = ISNULL(sc.name, CAST(s.system_criticality_id AS nvarchar(50))),

    DataOwner = ISNULL(CAST(uo.' + QUOTENAME(@UsersDisplayCol) + N' AS nvarchar(255)), CAST(s.data_owner_id AS nvarchar(50))),
    PrimaryAdministrator = ISNULL(CAST(up.' + QUOTENAME(@UsersDisplayCol) + N' AS nvarchar(255)), CAST(s.primary_administrator_id AS nvarchar(50))),
    SecondaryAdministrator = ISNULL(CAST(us.' + QUOTENAME(@UsersDisplayCol) + N' AS nvarchar(255)), CAST(s.secondary_administrator_id AS nvarchar(50))),

    HostingType = ISNULL(ht.name, CAST(s.system_hosting_type_id AS nvarchar(50))),

    BackupFrequencyId = s.backup_frequency_id,
    BackupFrequencyName = ISNULL(bf.Name, N''''),

    LastBackupStatusId = s.last_backup_status_id,
    LastBackupStatusName = ISNULL(bs.Name, N''''),

    LastBackupDate = s.last_backup_date,

    InformationClassificationId = s.information_classification_id,
    InformationClassificationName = ISNULL(ic.Name, N''''),

    ContainsPersonalData = s.contains_personal_data,
    ContainsSpecialCategories = s.contains_special_categories,

    AuthTypeId = s.auth_type_id,
    AuthTypeName = ISNULL(atp.Name, N''''),

    MfaRequired = s.mfa_required,
    LoggingEnabled = s.logging_enabled,

    LifecycleStatusId = s.lifecycle_status_id,
    LifecycleStatusName = ISNULL(ls.Name, N''''),

    RtoMinutes = s.rto_minutes,
    RpoMinutes = s.rpo_minutes,

    EndOfSupportDate = s.end_of_support_date,

    ApplicationUrl = ISNULL(s.application_url, N''''),
    DocumentationUrl = ISNULL(s.documentation_url, N''''),

    [Status] = COALESCE(bs.Name, s.last_backup_status, N'''')
FROM dbo.systems s
LEFT JOIN dbo.system_type st ON st.id = s.system_type_id
LEFT JOIN dbo.system_scope ss ON ss.id = s.system_scope_id
LEFT JOIN dbo.system_criticality sc ON sc.id = s.system_criticality_id
LEFT JOIN dbo.system_hosting_type ht ON ht.id = s.system_hosting_type_id

LEFT JOIN dbo.DictBackupFrequency bf ON bf.Id = s.backup_frequency_id
LEFT JOIN dbo.DictBackupStatus bs ON bs.Id = s.last_backup_status_id
LEFT JOIN dbo.DictInformationClassification ic ON ic.Id = s.information_classification_id
LEFT JOIN dbo.DictAuthType atp ON atp.Id = s.auth_type_id
LEFT JOIN dbo.DictLifecycleStatus ls ON ls.Id = s.lifecycle_status_id

LEFT JOIN dbo.users uo ON uo.id = s.data_owner_id
LEFT JOIN dbo.users up ON up.id = s.primary_administrator_id
LEFT JOIN dbo.users us ON us.id = s.secondary_administrator_id
ORDER BY s.name;';

EXEC sp_executesql @Sql;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                using var r = await cmd.ExecuteReaderAsync();

                var rows = new List<SystemListRow>();
                while (await r.ReadAsync())
                {
                    rows.Add(new SystemListRow
                    {
                        Id = SafeInt(r, "id"),
                        SystemName = SafeString(r, "SystemName"),

                        Type = SafeString(r, "Type"),
                        Scope = SafeString(r, "Scope"),
                        Criticality = SafeString(r, "Criticality"),

                        DataOwner = SafeString(r, "DataOwner"),
                        PrimaryAdministrator = SafeString(r, "PrimaryAdministrator"),
                        SecondaryAdministrator = SafeString(r, "SecondaryAdministrator"),

                        HostingType = SafeString(r, "HostingType"),

                        BackupFrequencyId = SafeNullableInt(r, "BackupFrequencyId"),
                        BackupFrequencyName = SafeString(r, "BackupFrequencyName"),

                        LastBackupStatusId = SafeNullableInt(r, "LastBackupStatusId"),
                        LastBackupStatusName = SafeString(r, "LastBackupStatusName"),

                        LastBackupDate = SafeNullableDateTime(r, "LastBackupDate"),

                        InformationClassificationId = SafeNullableInt(r, "InformationClassificationId"),
                        InformationClassificationName = SafeString(r, "InformationClassificationName"),

                        ContainsPersonalData = SafeNullableBool(r, "ContainsPersonalData"),
                        ContainsSpecialCategories = SafeNullableBool(r, "ContainsSpecialCategories"),

                        AuthTypeId = SafeNullableInt(r, "AuthTypeId"),
                        AuthTypeName = SafeString(r, "AuthTypeName"),

                        MfaRequired = SafeNullableBool(r, "MfaRequired"),
                        LoggingEnabled = SafeNullableBool(r, "LoggingEnabled"),

                        LifecycleStatusId = SafeNullableInt(r, "LifecycleStatusId"),
                        LifecycleStatusName = SafeString(r, "LifecycleStatusName"),

                        RtoMinutes = SafeNullableInt(r, "RtoMinutes"),
                        RpoMinutes = SafeNullableInt(r, "RpoMinutes"),

                        EndOfSupportDate = SafeNullableDateTime(r, "EndOfSupportDate"),

                        ApplicationUrl = SafeString(r, "ApplicationUrl"),
                        DocumentationUrl = SafeString(r, "DocumentationUrl"),

                        Status = SafeString(r, "Status"),
                    });
                }

                return (true, string.Empty, rows);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu danych Systems: {ex.Message}", new List<SystemListRow>());
            }
        }

        public async Task<(bool Ok, string ErrorMessage, SystemsLookups Lookups)> GetSystemsLookupsAsync()
        {
            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new SystemsLookups());

                using var con = new SqlConnection(cs);
                await con.OpenAsync();

                var lookups = new SystemsLookups
                {
                    BackupFrequencies = await LoadDictAsync(con, "dbo.DictBackupFrequency"),
                    BackupStatuses = await LoadDictAsync(con, "dbo.DictBackupStatus"),
                    InformationClassifications = await LoadDictAsync(con, "dbo.DictInformationClassification"),
                    AuthTypes = await LoadDictAsync(con, "dbo.DictAuthType"),
                    LifecycleStatuses = await LoadDictAsync(con, "dbo.DictLifecycleStatus"),
                    Users = await LoadUsersLookupAsync(con)
                };

                return (true, string.Empty, lookups);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu lookupów Systems: {ex.Message}", new SystemsLookups());
            }
        }

        private static async Task<List<SystemsLookupItem>> LoadDictAsync(SqlConnection con, string tableName)
        {
            var sql = $@"
SELECT
    Id = CAST(Id AS int),
    Name = CAST(Name AS nvarchar(255)),
    SortOrder = CASE WHEN COL_LENGTH('{tableName}', 'SortOrder') IS NOT NULL THEN CAST(SortOrder AS int) ELSE NULL END
FROM {tableName}
ORDER BY
    CASE WHEN COL_LENGTH('{tableName}', 'SortOrder') IS NOT NULL THEN SortOrder ELSE 2147483647 END,
    Name;";

            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            using var r = await cmd.ExecuteReaderAsync();

            var items = new List<SystemsLookupItem>();
            while (await r.ReadAsync())
            {
                items.Add(new SystemsLookupItem
                {
                    Id = SafeInt(r, "Id"),
                    Name = SafeString(r, "Name"),
                    SortOrder = SafeNullableInt(r, "SortOrder")
                });
            }

            return items;
        }

        private static async Task<List<SystemsUserLookupItem>> LoadUsersLookupAsync(SqlConnection con)
        {
            var sql =
@"
DECLARE @UsersDisplayCol sysname = NULL;

DECLARE @Candidates TABLE(Col sysname, Ord int);
INSERT INTO @Candidates(Col, Ord) VALUES
('DisplayName', 1),
('full_name', 2),
('user_name', 3),
('login', 4),
('name', 5),
('display_name', 6),
('email', 7);

SELECT TOP 1 @UsersDisplayCol = c.Col
FROM @Candidates c
WHERE COL_LENGTH('dbo.users', c.Col) IS NOT NULL
ORDER BY c.Ord;

IF @UsersDisplayCol IS NULL
    SET @UsersDisplayCol = 'id';

DECLARE @Sql nvarchar(max) = N'
SELECT
    u.id,
    Display = CAST(u.' + QUOTENAME(@UsersDisplayCol) + N' AS nvarchar(255))
FROM dbo.users u
WHERE (u.IsActive IS NULL OR u.IsActive = 1)
ORDER BY Display;';

EXEC sp_executesql @Sql;
";

            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            using var r = await cmd.ExecuteReaderAsync();

            var users = new List<SystemsUserLookupItem>();
            while (await r.ReadAsync())
            {
                users.Add(new SystemsUserLookupItem
                {
                    Id = SafeInt(r, "id"),
                    Display = SafeString(r, "Display")
                });
            }

            users.RemoveAll(x => string.IsNullOrWhiteSpace(x.Display));

            return users;
        }

        private static string SafeString(SqlDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                if (r.IsDBNull(idx))
                    return string.Empty;

                return Convert.ToString(r.GetValue(idx)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int SafeInt(SqlDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                if (r.IsDBNull(idx))
                    return 0;

                return Convert.ToInt32(r.GetValue(idx));
            }
            catch
            {
                return 0;
            }
        }

        private static int? SafeNullableInt(SqlDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                if (r.IsDBNull(idx))
                    return null;

                return Convert.ToInt32(r.GetValue(idx));
            }
            catch
            {
                return null;
            }
        }

        private static bool? SafeNullableBool(SqlDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                if (r.IsDBNull(idx))
                    return null;

                return Convert.ToBoolean(r.GetValue(idx));
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? SafeNullableDateTime(SqlDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                if (r.IsDBNull(idx))
                    return null;

                return Convert.ToDateTime(r.GetValue(idx));
            }
            catch
            {
                return null;
            }
        }
    }
}
