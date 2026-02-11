// File: Models/LicensesService.cs
// Description: Licenses: list + lookups + read by id + create + update.
// Created: 2025-12-16
// Updated: 2026-01-08 - FIX: bez duplikowania DTO (są w LicensesDtos.cs), poprawki CommandType, stabilne SQL.
// Updated: 2026-01-25 - RBAC: backend guard dla View/View.My/View.All + Create/Edit.
// Updated: 2026-02-03 - RBAC: MyAssets page: Assets.MyAssets.View pozwala na podgląd własnych licencji (read-only) bez Assets.Licenses.View.My.
// Version: 1.02
// Change history:
// 1.00 (2025-12-16) - Initial version
// 1.01 (2026-01-25) - RBAC guards + blokada podglądu cudzych danych dla View.My
// 1.02 (2026-02-03) - MyAssets.View: self-scope read-only + ochrona GetMyLicensesAsync i GetLicenseByIdAsync + filtr Users ograniczony do bieżącego

using Microsoft.Extensions.Configuration;
using ITManager.Services.Auth;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace ITManager.Models
{
    public sealed class LicensesService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;
        private readonly CurrentUserContextService _currentUserContextService;

        private const string PermLicensesView = "Assets.Licenses.View";
        private const string PermLicensesViewMy = "Assets.Licenses.View.My";
        private const string PermLicensesViewAll = "Assets.Licenses.View.All";
        private const string PermLicensesCreate = "Assets.Licenses.Create";
        private const string PermLicensesEdit = "Assets.Licenses.Edit";

        // MyAssets (read-only self scope)
        private const string PermMyAssetsView = "Assets.MyAssets.View";

        public LicensesService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        }

        public async Task<(bool Ok, string ErrorMessage, List<LicenseListRow> Rows)> GetLicensesAsync()
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new List<LicenseListRow>());

            // lista globalna tylko dla View lub View.All
            if (!auth.CanViewAll)
                return (false, "Brak uprawnień.", new List<LicenseListRow>());

            return await GetLicensesCoreAsync(userId: null, licenseId: null).ConfigureAwait(false);
        }

        public async Task<(bool Ok, string ErrorMessage, List<LicenseListRow> Rows)> GetMyLicensesAsync(int userId)
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new List<LicenseListRow>());

            // jeśli ktoś ma View lub View.All, może wskazać dowolny userId (np. admin, operator)
            if (auth.CanViewAll)
                return await GetLicensesCoreAsync(userId: userId, licenseId: null).ConfigureAwait(false);

            // View.My lub MyAssets.View: tylko własne
            if (!auth.CanViewMy && !auth.CanViewMyAssets)
                return (false, "Brak uprawnień.", new List<LicenseListRow>());

            if (auth.CurrentUserId <= 0)
                return (false, "Brak mapowania użytkownika do dbo.users (UserId).", new List<LicenseListRow>());

            if (userId != auth.CurrentUserId)
                return (false, "Brak uprawnień do podglądu licencji innego użytkownika.", new List<LicenseListRow>());

            return await GetLicensesCoreAsync(userId: auth.CurrentUserId, licenseId: null).ConfigureAwait(false);
        }

        public async Task<(bool Ok, string ErrorMessage, LicenseListRow? Row)> GetLicenseByIdAsync(int id)
        {
            if (id <= 0)
                return (false, "Nieprawidłowe Id licencji.", null);

            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, null);

            var result = await GetLicensesCoreAsync(userId: null, licenseId: id).ConfigureAwait(false);
            if (!result.Ok)
                return (false, result.ErrorMessage, null);

            var row = result.Rows.FirstOrDefault();
            if (row == null)
                return (true, string.Empty, null);

            // Self-scope (View.My lub MyAssets.View): dopuszczamy tylko własne
            if (!auth.CanViewAll && (auth.CanViewMy || auth.CanViewMyAssets))
            {
                if (auth.CurrentUserId <= 0)
                    return (false, "Brak mapowania użytkownika do dbo.users (UserId).", null);

                if (row.UserId.GetValueOrDefault(0) != auth.CurrentUserId)
                    return (false, "Brak uprawnień do podglądu tej licencji.", null);
            }
            else if (!auth.CanViewAll)
            {
                // brak View.All i brak self-scope oznacza brak uprawnień
                return (false, "Brak uprawnień.", null);
            }

            return (true, string.Empty, row);
        }

        public async Task<(bool Ok, string ErrorMessage, int NewId)> CreateLicenseAsync(LicenseUpdateModel model)
        {
            var auth = await GuardCreateOrEditAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, 0);

            if (model == null)
                return (false, "Brak danych do zapisu.", 0);

            if (model.Id != 0)
                return (false, "Dla nowej licencji oczekiwano Id=0.", 0);

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", 0);

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = @"
DECLARE @Cols nvarchar(max) = N'';
DECLARE @Vals nvarchar(max) = N'';
DECLARE @Sep nvarchar(10) = N'';

DECLARE @HasTypeId bit     = CASE WHEN COL_LENGTH('dbo.licenses','type_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasStatusId bit   = CASE WHEN COL_LENGTH('dbo.licenses','status_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasProducerId bit = CASE WHEN COL_LENGTH('dbo.licenses','producer_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasModelId bit    = CASE WHEN COL_LENGTH('dbo.licenses','model_id') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasName bit       = CASE WHEN COL_LENGTH('dbo.licenses','name') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasInventory bit  = CASE WHEN COL_LENGTH('dbo.licenses','inventory_no') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasUserId bit     = CASE WHEN COL_LENGTH('dbo.licenses','user_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasUserName bit   = CASE WHEN COL_LENGTH('dbo.licenses','user_name') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasSubStart bit   = CASE WHEN COL_LENGTH('dbo.licenses','subscription_starts') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasSubEnd bit     = CASE WHEN COL_LENGTH('dbo.licenses','subscription_ends') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasNote bit       = CASE WHEN COL_LENGTH('dbo.licenses','note') IS NOT NULL THEN 1 ELSE 0 END;

IF @HasTypeId = 1 BEGIN
  SET @Cols += @Sep + N'type_id';
  SET @Vals += @Sep + N'@TypeId';
  SET @Sep = N', ';
END

IF @HasStatusId = 1 BEGIN
  SET @Cols += @Sep + N'status_id';
  SET @Vals += @Sep + N'@StatusId';
  SET @Sep = N', ';
END

IF @HasProducerId = 1 BEGIN
  SET @Cols += @Sep + N'producer_id';
  SET @Vals += @Sep + N'@ProducerId';
  SET @Sep = N', ';
END

IF @HasModelId = 1 BEGIN
  SET @Cols += @Sep + N'model_id';
  SET @Vals += @Sep + N'@ModelId';
  SET @Sep = N', ';
END

IF @HasName = 1 BEGIN
  SET @Cols += @Sep + N'name';
  SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Name)), N'''')';
  SET @Sep = N', ';
END

IF @HasInventory = 1 BEGIN
  SET @Cols += @Sep + N'inventory_no';
  SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@InventoryNo)), N'''')';
  SET @Sep = N', ';
END

IF @HasUserId = 1 BEGIN
  SET @Cols += @Sep + N'user_id';
  SET @Vals += @Sep + N'@UserId';
  SET @Sep = N', ';
END

IF @HasUserName = 1 BEGIN
  SET @Cols += @Sep + N'user_name';
  SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@UserName)), N'''')';
  SET @Sep = N', ';
END

IF @HasSubStart = 1 BEGIN
  SET @Cols += @Sep + N'subscription_starts';
  SET @Vals += @Sep + N'@SubscriptionStarts';
  SET @Sep = N', ';
END

IF @HasSubEnd = 1 BEGIN
  SET @Cols += @Sep + N'subscription_ends';
  SET @Vals += @Sep + N'@SubscriptionEnds';
  SET @Sep = N', ';
END

IF @HasNote = 1 BEGIN
  SET @Cols += @Sep + N'note';
  SET @Vals += @Sep + N'NULLIF(@Note, N'''')';
  SET @Sep = N', ';
END

IF LEN(@Cols) = 0
BEGIN
  RAISERROR('No insertable columns detected in dbo.licenses.', 16, 1);
  RETURN;
END

DECLARE @Sql nvarchar(max) =
N'INSERT INTO dbo.licenses (' + @Cols + N')
  VALUES (' + @Vals + N');
  SELECT CAST(SCOPE_IDENTITY() AS int) AS NewId;';

EXEC sp_executesql
  @Sql,
  N'@TypeId int, @StatusId int, @ProducerId int, @ModelId int,
    @Name nvarchar(255), @InventoryNo nvarchar(255),
    @UserId int, @UserName nvarchar(255),
    @SubscriptionStarts date, @SubscriptionEnds date,
    @Note nvarchar(max)',
  @TypeId=@TypeId, @StatusId=@StatusId, @ProducerId=@ProducerId, @ModelId=@ModelId,
  @Name=@Name, @InventoryNo=@InventoryNo,
  @UserId=@UserId, @UserName=@UserName,
  @SubscriptionStarts=@SubscriptionStarts, @SubscriptionEnds=@SubscriptionEnds,
  @Note=@Note;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

                cmd.Parameters.Add(new SqlParameter("@TypeId", SqlDbType.Int) { Value = (object?)model.TypeId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = (object?)model.StatusId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ProducerId", SqlDbType.Int) { Value = (object?)model.ProducerId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ModelId", SqlDbType.Int) { Value = (object?)model.ModelId ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 255) { Value = (object?)model.Name ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@InventoryNo", SqlDbType.NVarChar, 255) { Value = (object?)model.InventoryNo ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)model.UserId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@UserName", SqlDbType.NVarChar, 255) { Value = (object?)model.UserName ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@SubscriptionStarts", SqlDbType.Date) { Value = (object?)model.SubscriptionStarts ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@SubscriptionEnds", SqlDbType.Date) { Value = (object?)model.SubscriptionEnds ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar) { Value = (object?)model.Note ?? DBNull.Value });

                var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                var newId = scalar == null || scalar == DBNull.Value ? 0 : Convert.ToInt32(scalar);

                if (newId <= 0)
                    return (false, "Nie udało się utworzyć licencji. Brak nowego Id.", 0);

                return (true, string.Empty, newId);
            }
            catch (SqlException ex)
            {
                return (false, $"Błąd dodawania licencji (SQL): {ex.Message}", 0);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd dodawania licencji: {ex.Message}", 0);
            }
        }

        public async Task<(bool Ok, string ErrorMessage)> UpdateLicenseAsync(LicenseUpdateModel model)
        {
            var auth = await GuardEditAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage);

            if (model == null)
                return (false, "Brak danych do zapisu.");

            if (model.Id <= 0)
                return (false, "Nieprawidłowe Id licencji.");

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.");

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = @"
DECLARE @LicenseId int = @Id;

IF NOT EXISTS (SELECT 1 FROM dbo.licenses WHERE id = @LicenseId)
BEGIN
    RAISERROR('License not found.', 16, 1);
    RETURN;
END

DECLARE @Set nvarchar(max) = N'';
DECLARE @Sep nvarchar(10) = N'';

DECLARE @HasTypeId bit     = CASE WHEN COL_LENGTH('dbo.licenses','type_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasStatusId bit   = CASE WHEN COL_LENGTH('dbo.licenses','status_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasProducerId bit = CASE WHEN COL_LENGTH('dbo.licenses','producer_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasModelId bit    = CASE WHEN COL_LENGTH('dbo.licenses','model_id') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasName bit       = CASE WHEN COL_LENGTH('dbo.licenses','name') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasInventory bit  = CASE WHEN COL_LENGTH('dbo.licenses','inventory_no') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasUserId bit     = CASE WHEN COL_LENGTH('dbo.licenses','user_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasUserName bit   = CASE WHEN COL_LENGTH('dbo.licenses','user_name') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasSubStart bit   = CASE WHEN COL_LENGTH('dbo.licenses','subscription_starts') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasSubEnd bit     = CASE WHEN COL_LENGTH('dbo.licenses','subscription_ends') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasNote bit       = CASE WHEN COL_LENGTH('dbo.licenses','note') IS NOT NULL THEN 1 ELSE 0 END;

IF @HasTypeId = 1 BEGIN
    SET @Set += @Sep + N'type_id = @TypeId';
    SET @Sep = N', ';
END

IF @HasStatusId = 1 BEGIN
    SET @Set += @Sep + N'status_id = @StatusId';
    SET @Sep = N', ';
END

IF @HasProducerId = 1 BEGIN
    SET @Set += @Sep + N'producer_id = @ProducerId';
    SET @Sep = N', ';
END

IF @HasModelId = 1 BEGIN
    SET @Set += @Sep + N'model_id = @ModelId';
    SET @Sep = N', ';
END

IF @HasName = 1 BEGIN
    SET @Set += @Sep + N'name = NULLIF(LTRIM(RTRIM(@Name)), N'''')';
    SET @Sep = N', ';
END

IF @HasInventory = 1 BEGIN
    SET @Set += @Sep + N'inventory_no = NULLIF(LTRIM(RTRIM(@InventoryNo)), N'''')';
    SET @Sep = N', ';
END

IF @HasUserId = 1 BEGIN
    SET @Set += @Sep + N'user_id = @UserId';
    SET @Sep = N', ';
END

IF @HasUserName = 1 BEGIN
    SET @Set += @Sep + N'user_name = NULLIF(LTRIM(RTRIM(@UserName)), N'''')';
    SET @Sep = N', ';
END

IF @HasSubStart = 1 BEGIN
    SET @Set += @Sep + N'subscription_starts = @SubscriptionStarts';
    SET @Sep = N', ';
END

IF @HasSubEnd = 1 BEGIN
    SET @Set += @Sep + N'subscription_ends = @SubscriptionEnds';
    SET @Sep = N', ';
END

IF @HasNote = 1 BEGIN
    SET @Set += @Sep + N'note = NULLIF(@Note, N'''')';
    SET @Sep = N', ';
END

IF LEN(@Set) = 0
BEGIN
    RAISERROR('No editable columns detected in dbo.licenses.', 16, 1);
    RETURN;
END

DECLARE @Sql nvarchar(max) =
N'UPDATE dbo.licenses
  SET ' + @Set + N'
  WHERE id = @LicenseId;';

EXEC sp_executesql
    @Sql,
    N'@LicenseId int,
      @TypeId int, @StatusId int, @ProducerId int, @ModelId int,
      @Name nvarchar(255), @InventoryNo nvarchar(255),
      @UserId int, @UserName nvarchar(255),
      @SubscriptionStarts date, @SubscriptionEnds date,
      @Note nvarchar(max)',
    @LicenseId=@LicenseId,
    @TypeId=@TypeId, @StatusId=@StatusId, @ProducerId=@ProducerId, @ModelId=@ModelId,
    @Name=@Name, @InventoryNo=@InventoryNo,
    @UserId=@UserId, @UserName=@UserName,
    @SubscriptionStarts=@SubscriptionStarts, @SubscriptionEnds=@SubscriptionEnds,
    @Note=@Note;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = model.Id });

                cmd.Parameters.Add(new SqlParameter("@TypeId", SqlDbType.Int) { Value = (object?)model.TypeId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = (object?)model.StatusId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ProducerId", SqlDbType.Int) { Value = (object?)model.ProducerId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ModelId", SqlDbType.Int) { Value = (object?)model.ModelId ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 255) { Value = (object?)model.Name ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@InventoryNo", SqlDbType.NVarChar, 255) { Value = (object?)model.InventoryNo ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)model.UserId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@UserName", SqlDbType.NVarChar, 255) { Value = (object?)model.UserName ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@SubscriptionStarts", SqlDbType.Date) { Value = (object?)model.SubscriptionStarts ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@SubscriptionEnds", SqlDbType.Date) { Value = (object?)model.SubscriptionEnds ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar) { Value = (object?)model.Note ?? DBNull.Value });

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return (true, string.Empty);
            }
            catch (SqlException ex)
            {
                return (false, $"Błąd zapisu danych licenses (SQL): {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Błąd zapisu danych licenses: {ex.Message}");
            }
        }

        public async Task<(bool Ok, string ErrorMessage, LicenseFilterLookups Lookups)> GetLicensesFilterLookupsAsync()
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new LicenseFilterLookups());

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new LicenseFilterLookups());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var types = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[] { ("dbo.license_types", "id", "name"), ("dbo.DictLicenseTypes", "id", "name") }).ConfigureAwait(false);

                var statuses = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[] { ("dbo.license_statuses", "id", "name"), ("dbo.DictLicenseStatuses", "id", "name") }).ConfigureAwait(false);

                var producers = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[] { ("dbo.producers", "id", "name"), ("dbo.DictProducers", "id", "name") }).ConfigureAwait(false);

                var models = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[] { ("dbo.models", "id", "name"), ("dbo.DictModels", "id", "name") }).ConfigureAwait(false);

                var users = await LoadUsersLookupAsync(con).ConfigureAwait(false);

                // Self-scope (View.My lub MyAssets.View): nie pokazujemy listy wszystkich Users w filtrze (anti-enumeration).
                if (!auth.CanViewAll && (auth.CanViewMy || auth.CanViewMyAssets))
                {
                    users = users
                        .Where(x => x.Id == auth.CurrentUserId)
                        .ToList();
                }

                return (true, string.Empty, new LicenseFilterLookups
                {
                    Types = types,
                    Statuses = statuses,
                    Producers = producers,
                    Models = models,
                    Users = users
                });
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu słowników licenses: {ex.Message}", new LicenseFilterLookups());
            }
        }

        private async Task<(bool Ok, string ErrorMessage, List<LicenseListRow> Rows)> GetLicensesCoreAsync(int? userId, int? licenseId)
        {
            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new List<LicenseListRow>());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = @"
DECLARE @HasTypeDict bit = 0;
DECLARE @HasStatusDict bit = 0;
DECLARE @HasProducerDict bit = 0;
DECLARE @HasModelDict bit = 0;
DECLARE @HasUsers bit = 0;

IF OBJECT_ID('dbo.license_types', 'U') IS NOT NULL SET @HasTypeDict = 1;
IF OBJECT_ID('dbo.DictLicenseTypes', 'U') IS NOT NULL SET @HasTypeDict = 1;

IF OBJECT_ID('dbo.license_statuses', 'U') IS NOT NULL SET @HasStatusDict = 1;
IF OBJECT_ID('dbo.DictLicenseStatuses', 'U') IS NOT NULL SET @HasStatusDict = 1;

IF OBJECT_ID('dbo.producers', 'U') IS NOT NULL SET @HasProducerDict = 1;
IF OBJECT_ID('dbo.DictProducers', 'U') IS NOT NULL SET @HasProducerDict = 1;

IF OBJECT_ID('dbo.models', 'U') IS NOT NULL SET @HasModelDict = 1;
IF OBJECT_ID('dbo.DictModels', 'U') IS NOT NULL SET @HasModelDict = 1;

IF OBJECT_ID('dbo.users', 'U') IS NOT NULL SET @HasUsers = 1;

DECLARE @Where nvarchar(max) = N' WHERE 1=1 ';
IF @UserId IS NOT NULL
    SET @Where += N' AND l.user_id = @UserId ';
IF @LicenseId IS NOT NULL
    SET @Where += N' AND l.id = @LicenseId ';

DECLARE @Sql nvarchar(max) = N'
SELECT
    Id = l.id,

    TypeId = TRY_CAST(l.type_id AS int),
    [Type] = ' + CASE
        WHEN @HasTypeDict = 1 THEN N'ISNULL(t.name, CAST(l.type_id AS nvarchar(50)))'
        ELSE N'CAST(l.type_id AS nvarchar(50))'
    END + N',

    StatusId = TRY_CAST(l.status_id AS int),
    [Status] = ' + CASE
        WHEN @HasStatusDict = 1 THEN N'ISNULL(s.name, CAST(l.status_id AS nvarchar(50)))'
        ELSE N'CAST(l.status_id AS nvarchar(50))'
    END + N',

    ProducerId = TRY_CAST(l.producer_id AS int),
    Producer = ' + CASE
        WHEN @HasProducerDict = 1 THEN N'ISNULL(p.name, CAST(l.producer_id AS nvarchar(50)))'
        ELSE N'CAST(l.producer_id AS nvarchar(50))'
    END + N',

    ModelId = TRY_CAST(l.model_id AS int),
    Model = ' + CASE
        WHEN @HasModelDict = 1 THEN N'ISNULL(m.name, CAST(l.model_id AS nvarchar(50)))'
        ELSE N'CAST(l.model_id AS nvarchar(50))'
    END + N',

    [Name] = ISNULL(NULLIF(LTRIM(RTRIM(CAST(l.name AS nvarchar(255)))), N''''), N''''),
    InventoryNo = ISNULL(NULLIF(LTRIM(RTRIM(CAST(l.inventory_no AS nvarchar(255)))), N''''), N''''),
    UserId = TRY_CAST(l.user_id AS int),
    [User] = ' + CASE
        WHEN @HasUsers = 1 THEN N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(u.DisplayName AS nvarchar(255)))), N''''), N'''')'
        ELSE N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(l.user_name AS nvarchar(255)))), N''''), N'''')'
    END + N',

    SubscriptionStarts = l.subscription_starts,
    SubscriptionEnds = l.subscription_ends,

    Note = ISNULL(CAST(l.note AS nvarchar(max)), N'''')
FROM dbo.licenses l
';

IF @HasTypeDict = 1
BEGIN
    IF OBJECT_ID('dbo.license_types', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.license_types t ON t.id = l.type_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictLicenseTypes t ON t.id = l.type_id ';
END

IF @HasStatusDict = 1
BEGIN
    IF OBJECT_ID('dbo.license_statuses', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.license_statuses s ON s.id = l.status_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictLicenseStatuses s ON s.id = l.status_id ';
END

IF @HasProducerDict = 1
BEGIN
    IF OBJECT_ID('dbo.producers', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.producers p ON p.id = l.producer_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictProducers p ON p.id = l.producer_id ';
END

IF @HasModelDict = 1
BEGIN
    IF OBJECT_ID('dbo.models', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.models m ON m.id = l.model_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictModels m ON m.id = l.model_id ';
END

IF @HasUsers = 1
BEGIN
    SET @Sql += N'LEFT JOIN dbo.users u ON u.id = l.user_id ';
END

SET @Sql += @Where;
SET @Sql += N' ORDER BY l.name, l.id;';

EXEC sp_executesql @Sql, N'@UserId int, @LicenseId int', @UserId=@UserId, @LicenseId=@LicenseId;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)userId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@LicenseId", SqlDbType.Int) { Value = (object?)licenseId ?? DBNull.Value });

                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                var rows = new List<LicenseListRow>();
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new LicenseListRow
                    {
                        Id = SafeInt(r, "Id"),

                        TypeId = SafeNullableInt(r, "TypeId"),
                        Type = SafeString(r, "Type"),

                        StatusId = SafeNullableInt(r, "StatusId"),
                        Status = SafeString(r, "Status"),

                        ProducerId = SafeNullableInt(r, "ProducerId"),
                        Producer = SafeString(r, "Producer"),

                        ModelId = SafeNullableInt(r, "ModelId"),
                        Model = SafeString(r, "Model"),

                        Name = SafeString(r, "Name"),
                        InventoryNo = SafeString(r, "InventoryNo"),

                        UserId = SafeNullableInt(r, "UserId"),
                        User = SafeString(r, "User"),

                        SubscriptionStarts = SafeDate(r, "SubscriptionStarts"),
                        SubscriptionEnds = SafeDate(r, "SubscriptionEnds"),

                        Note = SafeString(r, "Note"),
                    });
                }

                return (true, string.Empty, rows);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu danych licenses: {ex.Message}", new List<LicenseListRow>());
            }
        }

        // =========================
        // RBAC
        // =========================

        private async Task<(bool Ok, string ErrorMessage, int CurrentUserId, bool CanViewAll, bool CanViewMy, bool CanViewMyAssets)> GuardViewAnyAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                return (false, "Brak uprawnień.", 0, false, false, false);

            var currentUserId = (ctx.UserId.HasValue && ctx.UserId.Value > 0) ? ctx.UserId.Value : 0;

            var canViewAll = ctx.Has(PermLicensesViewAll) || ctx.Has(PermLicensesView);
            var canViewMy = ctx.Has(PermLicensesViewMy);
            var canViewMyAssets = ctx.Has(PermMyAssetsView);

            if (!canViewAll && !canViewMy && !canViewMyAssets)
                return (false, "Brak uprawnień.", currentUserId, false, false, false);

            // Dla self-scope i części metod wymagamy UserId, ale nie blokujemy całkiem,
            // żeby komunikat był spójny z resztą (w GetMy* i GetById dołożone jest sprawdzenie).
            if (currentUserId <= 0 && (canViewMy || canViewMyAssets))
                return (false, "Brak mapowania użytkownika do dbo.users (UserId).", 0, canViewAll, canViewMy, canViewMyAssets);

            return (true, string.Empty, currentUserId, canViewAll, canViewMy, canViewMyAssets);
        }

        private async Task<(bool Ok, string ErrorMessage)> GuardEditAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                return (false, "Brak uprawnień.");

            if (!ctx.Has(PermLicensesEdit))
                return (false, "Brak uprawnień.");

            return (true, string.Empty);
        }

        private async Task<(bool Ok, string ErrorMessage)> GuardCreateOrEditAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                return (false, "Brak uprawnień.");

            var allowed = ctx.Has(PermLicensesCreate) || ctx.Has(PermLicensesEdit);
            if (!allowed)
                return (false, "Brak uprawnień.");

            return (true, string.Empty);
        }

        // =========================
        // Lookups and helpers
        // =========================

        private static async Task<List<LookupItem>> LoadUsersLookupAsync(SqlConnection con)
        {
            if (!await TableExistsAsync(con, "dbo.users").ConfigureAwait(false))
                return new List<LookupItem>();

            var sql = @"
SELECT DISTINCT
    Id = CAST(id AS int),
    Name = LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255))))
FROM dbo.users
WHERE DisplayName IS NOT NULL AND LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255)))) <> N''
ORDER BY Name;";

            return await LoadLookupItemListAsync(con, sql).ConfigureAwait(false);
        }

        private static async Task<List<LookupItem>> LoadLookupItemsFromFirstExistingTableAsync(
            SqlConnection con,
            (string TableName, string IdColumn, string NameColumn)[] candidates)
        {
            foreach (var c in candidates)
            {
                if (await TableExistsAsync(con, c.TableName).ConfigureAwait(false))
                {
                    var safeSql =
                        "SELECT DISTINCT " +
                        $"  Id = CAST([{c.IdColumn}] AS int), " +
                        $"  Name = LTRIM(RTRIM(CAST([{c.NameColumn}] AS nvarchar(255)))) " +
                        $"FROM {c.TableName} " +
                        $"WHERE [{c.NameColumn}] IS NOT NULL AND LTRIM(RTRIM(CAST([{c.NameColumn}] AS nvarchar(255)))) <> N'' " +
                        $"ORDER BY Name;";

                    return await LoadLookupItemListAsync(con, safeSql).ConfigureAwait(false);
                }
            }

            return new List<LookupItem>();
        }

        private static async Task<bool> TableExistsAsync(SqlConnection con, string fullName)
        {
            var sql = "SELECT CASE WHEN OBJECT_ID(@n, 'U') IS NOT NULL THEN 1 ELSE 0 END;";
            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 256) { Value = fullName });

            var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(v) == 1;
        }

        private static async Task<List<LookupItem>> LoadLookupItemListAsync(SqlConnection con, string sql)
        {
            var list = new List<LookupItem>();

            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                var idObj = r["Id"];
                var nameObj = r["Name"];

                var id = idObj == DBNull.Value ? (int?)null : Convert.ToInt32(idObj);
                var name = Convert.ToString(nameObj) ?? string.Empty;

                if (id.HasValue && id.Value > 0 && !string.IsNullOrWhiteSpace(name))
                    list.Add(new LookupItem { Id = id.Value, Name = name.Trim() });
            }

            return list;
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

        private static DateTime? SafeDate(SqlDataReader r, string col)
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
