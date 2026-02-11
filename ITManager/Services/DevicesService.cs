// File: Models/DevicesService.cs
// Description: Odczyt listy zasobów IT (devices) z bazy ITManager. Zwraca rekordy do widoku DataGrid.
//              Obsługuje odczyt po Id, zapis podstawowych pól oraz słowniki filtrów (comboboxy) ładowane z bazy.
// Created: 2025-12-16
// Version: 1.12
// Change history:
// 1.02 (2025-12-16) - GetMyDevicesAsync(userId).
// 1.03 (2025-12-17) - JOIN users/locations/models, trim Hostname.
// 1.04 (2025-12-18) - Id w DeviceListRow, GetDeviceByIdAsync(id), UpdateDeviceAsync(model) (bezpieczny update).
// 1.05 (2025-12-19) - rozszerzenie DeviceListRow o kolumny do edycji + GetDevicesFilterLookupsAsync().
// 1.06 (2025-12-19) - usunięcie Department (brak w DB, invalid column department_id).
// 1.07 (2025-12-19) - Type/Kind/Status/Producer jako (Id + Name) dla poprawnego filtrowania po Id.
// 1.08 (2025-12-21) - CreateDeviceAsync: INSERT nowego urządzenia + mapowanie Name -> Id słowników.
// 1.09 (2026-01-05) - FIX: UpdateDeviceAsync zapisuje location_id (oraz user_id i pozostałe *_id), a nie tylko legacy tekst.
// 1.10 (2026-01-24) - RBAC: egzekwowanie permissionów w backendzie (Assets.Devices.View/Create/Edit) na początku metod publicznych.
// 1.11 (2026-01-25) - RBAC: View.My + View.All, blokada listy globalnej dla View.My, ochrona GetDeviceByIdAsync + lookups Users.
// 1.12 (2026-02-03) - RBAC: MyAssets page: Assets.MyAssets.View pozwala na podgląd własnych devices (read-only) bez Assets.Devices.View.My.

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
    public sealed class DeviceListRow
    {
        public int Id { get; set; }

        // General info (Id + Name)
        public int? TypeId { get; set; }
        public string Type { get; set; } = string.Empty;

        public int? KindId { get; set; }
        public string Kind { get; set; } = string.Empty;

        public int? StatusId { get; set; }
        public string Status { get; set; } = string.Empty;

        public int? ProducerId { get; set; }
        public string Producer { get; set; } = string.Empty;

        public int? ModelId { get; set; }
        public string Model { get; set; } = string.Empty;

        public string SerialNumber { get; set; } = string.Empty;

        // User / Location
        public int? UserId { get; set; }
        public string User { get; set; } = string.Empty;

        public int? LocationId { get; set; }
        public string Location { get; set; } = string.Empty;

        // Network
        public string Hostname { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string MacEthernet { get; set; } = string.Empty;
        public string MacWlan { get; set; } = string.Empty;
        public string Imei1 { get; set; } = string.Empty;
        public string Imei2 { get; set; } = string.Empty;

        // Warranty
        public DateTime? WarrantyStarts { get; set; }
        public DateTime? WarrantyEnds { get; set; }

        // Asset
        public string InventoryNo { get; set; } = string.Empty;
        public string AssetNo { get; set; } = string.Empty;

        // Note
        public string Note { get; set; } = string.Empty;
    }

    public sealed class DeviceUpdateModel
    {
        public int Id { get; set; }

        // General info
        public string Type { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Producer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;

        // User / Location
        public string User { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;

        // Network
        public string Hostname { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string MacEthernet { get; set; } = string.Empty;
        public string MacWlan { get; set; } = string.Empty;
        public string Imei1 { get; set; } = string.Empty;
        public string Imei2 { get; set; } = string.Empty;

        // Warranty
        public DateTime? WarrantyStarts { get; set; }
        public DateTime? WarrantyEnds { get; set; }

        // Asset
        public string InventoryNo { get; set; } = string.Empty;
        public string AssetNo { get; set; } = string.Empty;

        // Note
        public string Note { get; set; } = string.Empty;
    }

    public sealed class DeviceFilterLookups
    {
        // Te cztery muszą być po Id, bo devices trzyma *_id
        public List<LookupItem> Types { get; set; } = new();
        public List<LookupItem> Kinds { get; set; } = new();
        public List<LookupItem> Statuses { get; set; } = new();
        public List<LookupItem> Producers { get; set; } = new();

        // Te zostają stringowe (bo w UI filtrujesz po nazwie modelu / lokalizacji / user)
        public List<string> Models { get; set; } = new();
        public List<string> Locations { get; set; } = new();
        public List<string> Users { get; set; } = new();
    }

    public sealed class DevicesService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;
        private readonly CurrentUserContextService _currentUserContextService;

        // Permissions (RBAC)
        private const string PermDevicesView = "Assets.Devices.View";
        private const string PermDevicesViewMy = "Assets.Devices.View.My";
        private const string PermDevicesViewAll = "Assets.Devices.View.All";
        private const string PermDevicesCreate = "Assets.Devices.Create";
        private const string PermDevicesEdit = "Assets.Devices.Edit";

        // MyAssets (read-only self scope)
        private const string PermMyAssetsView = "Assets.MyAssets.View";

        public DevicesService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        }

        // =========================
        // RBAC helpers
        // =========================

        private async Task EnsureUserContextInitializedAsync()
        {
            await _currentUserContextService.EnsureInitializedAsync().ConfigureAwait(false);
        }

        private async Task<(bool Ok, string ErrorMessage, int CurrentUserId, bool CanViewAll, bool CanViewMy, bool CanViewMyAssets)> GuardViewAnyAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                return (false, "Brak uprawnień.", 0, false, false, false);

            var userId = (ctx.UserId.HasValue && ctx.UserId.Value > 0) ? ctx.UserId.Value : 0;

            var canViewAll = ctx.Has(PermDevicesViewAll) || ctx.Has(PermDevicesView);
            var canViewMy = ctx.Has(PermDevicesViewMy);
            var canViewMyAssets = ctx.Has(PermMyAssetsView);

            if (!canViewAll && !canViewMy && !canViewMyAssets)
                return (false, "Brak uprawnień do podglądu urządzeń.", userId, false, false, false);

            return (true, string.Empty, userId, canViewAll, canViewMy, canViewMyAssets);
        }

        private async Task<(bool Ok, string ErrorMessage)> GuardCanCreateDevicesAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                return (false, "Brak uprawnień.");

            // Create lub Edit (dla admin/IT często wygodniejsze)
            if (!ctx.Has(PermDevicesCreate) && !ctx.Has(PermDevicesEdit))
                return (false, "Brak uprawnień do tworzenia urządzeń.");

            return (true, string.Empty);
        }

        private async Task<(bool Ok, string ErrorMessage)> GuardCanEditDevicesAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !ctx.Has(PermDevicesEdit))
                return (false, "Brak uprawnień do edycji urządzeń.");

            return (true, string.Empty);
        }

        private async Task<(bool Ok, string ErrorMessage, int CurrentUserId)> GetCurrentUserIdOrErrorAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || ctx.UserId is null || ctx.UserId.Value <= 0)
                return (false, "Brak mapowania użytkownika do dbo.users (UserId).", 0);

            return (true, string.Empty, ctx.UserId.Value);
        }

        public async Task<(bool Ok, string ErrorMessage, List<DeviceListRow> Rows)> GetDevicesAsync()
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new List<DeviceListRow>());

            // Lista globalna tylko dla View lub View.All
            if (!auth.CanViewAll)
                return (false, "Brak uprawnień.", new List<DeviceListRow>());

            return await GetDevicesCoreAsync(userId: null, deviceId: null).ConfigureAwait(false);
        }

        public async Task<(bool Ok, string ErrorMessage, List<DeviceListRow> Rows)> GetMyDevicesAsync(int userId)
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new List<DeviceListRow>());

            // View lub View.All: można pobierać po dowolnym userId (np. IT/admin)
            if (auth.CanViewAll)
                return await GetDevicesCoreAsync(userId: userId, deviceId: null).ConfigureAwait(false);

            // View.My lub MyAssets.View: tylko własne
            if (!auth.CanViewMy && !auth.CanViewMyAssets)
                return (false, "Brak uprawnień.", new List<DeviceListRow>());

            if (auth.CurrentUserId <= 0)
                return (false, "Brak mapowania użytkownika do dbo.users (UserId).", new List<DeviceListRow>());

            if (userId != auth.CurrentUserId)
                return (false, "Brak uprawnień do podglądu urządzeń innego użytkownika.", new List<DeviceListRow>());

            return await GetDevicesCoreAsync(userId: auth.CurrentUserId, deviceId: null).ConfigureAwait(false);
        }

        public async Task<(bool Ok, string ErrorMessage, DeviceListRow? Row)> GetDeviceByIdAsync(int id)
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, null);

            if (id <= 0)
                return (false, "Nieprawidłowe Id urządzenia.", null);

            var result = await GetDevicesCoreAsync(userId: null, deviceId: id).ConfigureAwait(false);
            if (!result.Ok)
                return (false, result.ErrorMessage, null);

            var row = result.Rows.FirstOrDefault();
            if (row == null)
                return (true, string.Empty, null);

            // Bez View.All: dopuszczamy tylko własne (View.My lub MyAssets.View)
            if (!auth.CanViewAll && (auth.CanViewMy || auth.CanViewMyAssets))
            {
                if (auth.CurrentUserId <= 0)
                    return (false, "Brak mapowania użytkownika do dbo.users (UserId).", null);

                if (row.UserId.GetValueOrDefault(0) != auth.CurrentUserId)
                    return (false, "Brak uprawnień do podglądu tego urządzenia.", null);
            }
            else if (!auth.CanViewAll)
            {
                return (false, "Brak uprawnień.", null);
            }

            return (true, string.Empty, row);
        }

        public async Task<(bool Ok, string ErrorMessage, int NewId)> CreateDeviceAsync(DeviceUpdateModel model)
        {
            var guard = await GuardCanCreateDevicesAsync().ConfigureAwait(false);
            if (!guard.Ok)
                return (false, guard.ErrorMessage, 0);

            if (model == null)
                return (false, "Brak danych do zapisu.", 0);

            if (model.Id != 0)
                return (false, "Nieprawidłowe Id dla nowego urządzenia. Oczekiwano Id=0.", 0);

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", 0);

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var typeId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.device_types", "id", "name"),
                        ("dbo.DictDeviceTypes", "id", "name")
                    },
                    name: model.Type).ConfigureAwait(false);

                var kindId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.device_kinds", "id", "name"),
                        ("dbo.DictDeviceKinds", "id", "name")
                    },
                    name: model.Kind).ConfigureAwait(false);

                var statusId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.device_statuses", "id", "name"),
                        ("dbo.DictDeviceStatuses", "id", "name")
                    },
                    name: model.Status).ConfigureAwait(false);

                var producerId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.producers", "id", "name"),
                        ("dbo.DictProducers", "id", "name")
                    },
                    name: model.Producer).ConfigureAwait(false);

                var modelId = await ResolveModelIdByNameAsync(con, model.Model).ConfigureAwait(false);
                var userId = await ResolveUserIdByDisplayNameAsync(con, model.User).ConfigureAwait(false);
                var locationId = await ResolveLocationIdByNameAsync(con, model.Location).ConfigureAwait(false);

                var sql =
@"
DECLARE @NewId int = 0;

DECLARE @HasTypeId bit     = CASE WHEN COL_LENGTH('dbo.devices','device_type_id')    IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasKindId bit     = CASE WHEN COL_LENGTH('dbo.devices','device_kind_id')    IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasStatusId bit   = CASE WHEN COL_LENGTH('dbo.devices','device_status_id')  IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasProducerId bit = CASE WHEN COL_LENGTH('dbo.devices','producer_id')       IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasModelId bit    = CASE WHEN COL_LENGTH('dbo.devices','model_id')          IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasUserId bit     = CASE WHEN COL_LENGTH('dbo.devices','user_id')           IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasLocationId bit = CASE WHEN COL_LENGTH('dbo.devices','location_id')       IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasHostname bit   = CASE WHEN COL_LENGTH('dbo.devices','hostname')          IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasUserName bit   = CASE WHEN COL_LENGTH('dbo.devices','user_name')         IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasLocation bit   = CASE WHEN COL_LENGTH('dbo.devices','location')          IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasModel bit      = CASE WHEN COL_LENGTH('dbo.devices','model')             IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasSerial bit     = CASE WHEN COL_LENGTH('dbo.devices','serial_number')     IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasAsset bit      = CASE WHEN COL_LENGTH('dbo.devices','asset_no')          IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasInventory bit  = CASE WHEN COL_LENGTH('dbo.devices','inventory_no')      IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasIp bit         = CASE WHEN COL_LENGTH('dbo.devices','ip_address')        IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasMacEth bit     = CASE WHEN COL_LENGTH('dbo.devices','mac_ethernet')      IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasMacWlan bit    = CASE WHEN COL_LENGTH('dbo.devices','mac_wlan')          IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasImei1 bit      = CASE WHEN COL_LENGTH('dbo.devices','imei_1')            IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasImei2 bit      = CASE WHEN COL_LENGTH('dbo.devices','imei_2')            IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasWarrantyStarts bit = CASE WHEN COL_LENGTH('dbo.devices','warranty_starts') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasWarrantyEnds bit   = CASE WHEN COL_LENGTH('dbo.devices','warranty_ends')   IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasNote bit       = CASE WHEN COL_LENGTH('dbo.devices','note')              IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @Cols nvarchar(max) = N'';
DECLARE @Vals nvarchar(max) = N'';
DECLARE @Sep nvarchar(10) = N'';

IF @HasTypeId = 1 BEGIN SET @Cols += @Sep + N'device_type_id';   SET @Vals += @Sep + N'@TypeId';      SET @Sep = N', '; END
IF @HasKindId = 1 BEGIN SET @Cols += @Sep + N'device_kind_id';   SET @Vals += @Sep + N'@KindId';      SET @Sep = N', '; END
IF @HasStatusId = 1 BEGIN SET @Cols += @Sep + N'device_status_id'; SET @Vals += @Sep + N'@StatusId';  SET @Sep = N', '; END
IF @HasProducerId = 1 BEGIN SET @Cols += @Sep + N'producer_id';  SET @Vals += @Sep + N'@ProducerId';  SET @Sep = N', '; END
IF @HasModelId = 1 BEGIN SET @Cols += @Sep + N'model_id';        SET @Vals += @Sep + N'@ModelId';     SET @Sep = N', '; END

IF @HasUserId = 1 BEGIN SET @Cols += @Sep + N'user_id';          SET @Vals += @Sep + N'@UserId';      SET @Sep = N', '; END
IF @HasLocationId = 1 BEGIN SET @Cols += @Sep + N'location_id';  SET @Vals += @Sep + N'@LocationId';  SET @Sep = N', '; END

IF @HasHostname = 1 BEGIN SET @Cols += @Sep + N'hostname';       SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Hostname)), N'''')'; SET @Sep = N', '; END
IF @HasUserName = 1 BEGIN SET @Cols += @Sep + N'user_name';      SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@UserName)), N'''')'; SET @Sep = N', '; END
IF @HasLocation = 1 BEGIN SET @Cols += @Sep + N'location';       SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@LocationText)), N'''')'; SET @Sep = N', '; END
IF @HasModel = 1 BEGIN SET @Cols += @Sep + N'model';             SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@ModelText)), N'''')'; SET @Sep = N', '; END

IF @HasSerial = 1 BEGIN SET @Cols += @Sep + N'serial_number';    SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@SerialNumber)), N'''')'; SET @Sep = N', '; END
IF @HasAsset = 1 BEGIN SET @Cols += @Sep + N'asset_no';          SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@AssetNo)), N'''')'; SET @Sep = N', '; END
IF @HasInventory = 1 BEGIN SET @Cols += @Sep + N'inventory_no';  SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@InventoryNo)), N'''')'; SET @Sep = N', '; END

IF @HasIp = 1 BEGIN SET @Cols += @Sep + N'ip_address';           SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@IpAddress)), '''')'; SET @Sep = N', '; END
IF @HasMacEth = 1 BEGIN SET @Cols += @Sep + N'mac_ethernet';      SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@MacEthernet)), N'''')'; SET @Sep = N', '; END
IF @HasMacWlan = 1 BEGIN SET @Cols += @Sep + N'mac_wlan';         SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@MacWlan)), N'''')'; SET @Sep = N', '; END

IF @HasImei1 = 1 BEGIN SET @Cols += @Sep + N'imei_1';             SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Imei1)), '''')'; SET @Sep = N', '; END
IF @HasImei2 = 1 BEGIN SET @Cols += @Sep + N'imei_2';             SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Imei2)), '''')'; SET @Sep = N', '; END

IF @HasWarrantyStarts = 1 BEGIN SET @Cols += @Sep + N'warranty_starts'; SET @Vals += @Sep + N'@WarrantyStarts'; SET @Sep = N', '; END
IF @HasWarrantyEnds = 1 BEGIN SET @Cols += @Sep + N'warranty_ends';     SET @Vals += @Sep + N'@WarrantyEnds';   SET @Sep = N', '; END

IF @HasNote = 1 BEGIN SET @Cols += @Sep + N'note'; SET @Vals += @Sep + N'NULLIF(@Note, N'''')'; SET @Sep = N', '; END

IF LEN(@Cols) = 0 OR LEN(@Vals) = 0
BEGIN
    RAISERROR('No insertable columns detected in dbo.devices.', 16, 1);
    RETURN;
END

DECLARE @Sql nvarchar(max) =
N'INSERT INTO dbo.devices (' + @Cols + N')
  VALUES (' + @Vals + N');

  SELECT CAST(SCOPE_IDENTITY() AS int) AS NewId;';

DECLARE @T table(NewId int);

INSERT INTO @T(NewId)
EXEC sp_executesql
    @Sql,
    N'@TypeId int, @KindId int, @StatusId int, @ProducerId int, @ModelId int,
      @UserId int, @LocationId int,
      @Hostname nvarchar(255), @UserName nvarchar(255), @LocationText nvarchar(255), @ModelText nvarchar(255),
      @SerialNumber nvarchar(255), @AssetNo nvarchar(50), @InventoryNo nvarchar(255),
      @IpAddress varchar(15), @MacEthernet nvarchar(255), @MacWlan nvarchar(255),
      @Imei1 varchar(15), @Imei2 varchar(15),
      @WarrantyStarts date, @WarrantyEnds date,
      @Note nvarchar(max)',
    @TypeId=@TypeId, @KindId=@KindId, @StatusId=@StatusId, @ProducerId=@ProducerId, @ModelId=@ModelId,
    @UserId=@UserId, @LocationId=@LocationId,
    @Hostname=@Hostname, @UserName=@UserName, @LocationText=@LocationText, @ModelText=@ModelText,
    @SerialNumber=@SerialNumber, @AssetNo=@AssetNo, @InventoryNo=@InventoryNo,
    @IpAddress=@IpAddress, @MacEthernet=@MacEthernet, @MacWlan=@MacWlan,
    @Imei1=@Imei1, @Imei2=@Imei2,
    @WarrantyStarts=@WarrantyStarts, @WarrantyEnds=@WarrantyEnds,
    @Note=@Note;

SELECT TOP 1 @NewId = NewId FROM @T;
SELECT @NewId AS NewId;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

                cmd.Parameters.Add(new SqlParameter("@TypeId", SqlDbType.Int) { Value = (object?)typeId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@KindId", SqlDbType.Int) { Value = (object?)kindId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = (object?)statusId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ProducerId", SqlDbType.Int) { Value = (object?)producerId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ModelId", SqlDbType.Int) { Value = (object?)modelId ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)userId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@LocationId", SqlDbType.Int) { Value = (object?)locationId ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Hostname", SqlDbType.NVarChar, 255) { Value = (object?)model.Hostname ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@UserName", SqlDbType.NVarChar, 255) { Value = (object?)model.User ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@LocationText", SqlDbType.NVarChar, 255) { Value = (object?)model.Location ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ModelText", SqlDbType.NVarChar, 255) { Value = (object?)model.Model ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@SerialNumber", SqlDbType.NVarChar, 255) { Value = (object?)model.SerialNumber ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@AssetNo", SqlDbType.NVarChar, 50) { Value = (object?)model.AssetNo ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@InventoryNo", SqlDbType.NVarChar, 255) { Value = (object?)model.InventoryNo ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@IpAddress", SqlDbType.VarChar, 15) { Value = (object?)model.IpAddress ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@MacEthernet", SqlDbType.NVarChar, 255) { Value = (object?)model.MacEthernet ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@MacWlan", SqlDbType.NVarChar, 255) { Value = (object?)model.MacWlan ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Imei1", SqlDbType.VarChar, 15) { Value = (object?)model.Imei1 ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Imei2", SqlDbType.VarChar, 15) { Value = (object?)model.Imei2 ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@WarrantyStarts", SqlDbType.Date) { Value = (object?)model.WarrantyStarts ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@WarrantyEnds", SqlDbType.Date) { Value = (object?)model.WarrantyEnds ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar) { Value = (object?)model.Note ?? DBNull.Value });

                var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                var newId = scalar == null || scalar == DBNull.Value ? 0 : Convert.ToInt32(scalar);

                if (newId <= 0)
                    return (false, "Nie udało się utworzyć urządzenia. Brak nowego Id.", 0);

                return (true, string.Empty, newId);
            }
            catch (SqlException ex)
            {
                return (false, $"Błąd dodawania urządzenia (SQL): {ex.Message}", 0);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd dodawania urządzenia: {ex.Message}", 0);
            }
        }

        public async Task<(bool Ok, string ErrorMessage)> UpdateDeviceAsync(DeviceUpdateModel model)
        {
            var guard = await GuardCanEditDevicesAsync().ConfigureAwait(false);
            if (!guard.Ok)
                return (false, guard.ErrorMessage);

            if (model == null)
                return (false, "Brak danych do zapisu.");

            if (model.Id <= 0)
                return (false, "Nieprawidłowe Id urządzenia.");

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.");

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var typeId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.device_types", "id", "name"),
                        ("dbo.DictDeviceTypes", "id", "name")
                    },
                    name: model.Type).ConfigureAwait(false);

                var kindId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.device_kinds", "id", "name"),
                        ("dbo.DictDeviceKinds", "id", "name")
                    },
                    name: model.Kind).ConfigureAwait(false);

                var statusId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.device_statuses", "id", "name"),
                        ("dbo.DictDeviceStatuses", "id", "name")
                    },
                    name: model.Status).ConfigureAwait(false);

                var producerId = await ResolveLookupIdByNameAsync(
                    con,
                    candidates: new[]
                    {
                        ("dbo.producers", "id", "name"),
                        ("dbo.DictProducers", "id", "name")
                    },
                    name: model.Producer).ConfigureAwait(false);

                var modelId = await ResolveModelIdByNameAsync(con, model.Model).ConfigureAwait(false);
                var userId = await ResolveUserIdByDisplayNameAsync(con, model.User).ConfigureAwait(false);
                var locationId = await ResolveLocationIdByNameAsync(con, model.Location).ConfigureAwait(false);

                var sql =
@"
DECLARE @DeviceId int = @Id;

IF NOT EXISTS (SELECT 1 FROM dbo.devices WHERE id = @DeviceId)
BEGIN
    RAISERROR('Device not found.', 16, 1);
    RETURN;
END

DECLARE @Set nvarchar(max) = N'';
DECLARE @Sep nvarchar(10) = N'';

DECLARE @HasTypeId bit       = CASE WHEN COL_LENGTH('dbo.devices','device_type_id')   IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasKindId bit       = CASE WHEN COL_LENGTH('dbo.devices','device_kind_id')   IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasStatusId bit     = CASE WHEN COL_LENGTH('dbo.devices','device_status_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasProducerId bit   = CASE WHEN COL_LENGTH('dbo.devices','producer_id')      IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasModelId bit      = CASE WHEN COL_LENGTH('dbo.devices','model_id')         IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasUserId bit       = CASE WHEN COL_LENGTH('dbo.devices','user_id')          IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasLocationId bit   = CASE WHEN COL_LENGTH('dbo.devices','location_id')      IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasHostname bit     = CASE WHEN COL_LENGTH('dbo.devices','hostname')         IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasUserName bit     = CASE WHEN COL_LENGTH('dbo.devices','user_name')        IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasLocation bit     = CASE WHEN COL_LENGTH('dbo.devices','location')         IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasModelText bit    = CASE WHEN COL_LENGTH('dbo.devices','model')            IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasSerial bit       = CASE WHEN COL_LENGTH('dbo.devices','serial_number')    IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasAsset bit        = CASE WHEN COL_LENGTH('dbo.devices','asset_no')         IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasInventory bit    = CASE WHEN COL_LENGTH('dbo.devices','inventory_no')     IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasIp bit           = CASE WHEN COL_LENGTH('dbo.devices','ip_address')       IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasMacEth bit       = CASE WHEN COL_LENGTH('dbo.devices','mac_ethernet')     IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasMacWlan bit      = CASE WHEN COL_LENGTH('dbo.devices','mac_wlan')         IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasImei1 bit        = CASE WHEN COL_LENGTH('dbo.devices','imei_1')           IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasImei2 bit        = CASE WHEN COL_LENGTH('dbo.devices','imei_2')           IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasWarrantyStarts bit = CASE WHEN COL_LENGTH('dbo.devices','warranty_starts') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasWarrantyEnds bit   = CASE WHEN COL_LENGTH('dbo.devices','warranty_ends')   IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @HasNote bit         = CASE WHEN COL_LENGTH('dbo.devices','note')             IS NOT NULL THEN 1 ELSE 0 END;

IF @HasTypeId = 1 BEGIN SET @Set += @Sep + N'device_type_id = @TypeId'; SET @Sep = N', '; END
IF @HasKindId = 1 BEGIN SET @Set += @Sep + N'device_kind_id = @KindId'; SET @Sep = N', '; END
IF @HasStatusId = 1 BEGIN SET @Set += @Sep + N'device_status_id = @StatusId'; SET @Sep = N', '; END
IF @HasProducerId = 1 BEGIN SET @Set += @Sep + N'producer_id = @ProducerId'; SET @Sep = N', '; END
IF @HasModelId = 1 BEGIN SET @Set += @Sep + N'model_id = @ModelId'; SET @Sep = N', '; END
IF @HasUserId = 1 BEGIN SET @Set += @Sep + N'user_id = @UserId'; SET @Sep = N', '; END
IF @HasLocationId = 1 BEGIN SET @Set += @Sep + N'location_id = @LocationId'; SET @Sep = N', '; END

IF @HasHostname = 1 BEGIN SET @Set += @Sep + N'hostname = NULLIF(LTRIM(RTRIM(@Hostname)), N'''')'; SET @Sep = N', '; END
IF @HasUserName = 1 BEGIN SET @Set += @Sep + N'user_name = NULLIF(LTRIM(RTRIM(@UserName)), N'''')'; SET @Sep = N', '; END
IF @HasLocation = 1 BEGIN SET @Set += @Sep + N'location = NULLIF(LTRIM(RTRIM(@LocationText)), N'''')'; SET @Sep = N', '; END
IF @HasModelText = 1 BEGIN SET @Set += @Sep + N'model = NULLIF(LTRIM(RTRIM(@ModelText)), N'''')'; SET @Sep = N', '; END

IF @HasSerial = 1 BEGIN SET @Set += @Sep + N'serial_number = NULLIF(LTRIM(RTRIM(@SerialNumber)), N'''')'; SET @Sep = N', '; END
IF @HasAsset = 1 BEGIN SET @Set += @Sep + N'asset_no = NULLIF(LTRIM(RTRIM(@AssetNo)), N'''')'; SET @Sep = N', '; END
IF @HasInventory = 1 BEGIN SET @Set += @Sep + N'inventory_no = NULLIF(LTRIM(RTRIM(@InventoryNo)), N'''')'; SET @Sep = N', '; END

IF @HasIp = 1 BEGIN SET @Set += @Sep + N'ip_address = NULLIF(LTRIM(RTRIM(@IpAddress)), '''')'; SET @Sep = N', '; END
IF @HasMacEth = 1 BEGIN SET @Set += @Sep + N'mac_ethernet = NULLIF(LTRIM(RTRIM(@MacEthernet)), N'''')'; SET @Sep = N', '; END
IF @HasMacWlan = 1 BEGIN SET @Set += @Sep + N'mac_wlan = NULLIF(LTRIM(RTRIM(@MacWlan)), N'''')'; SET @Sep = N', '; END

IF @HasImei1 = 1 BEGIN SET @Set += @Sep + N'imei_1 = NULLIF(LTRIM(RTRIM(@Imei1)), '''')'; SET @Sep = N', '; END
IF @HasImei2 = 1 BEGIN SET @Set += @Sep + N'imei_2 = NULLIF(LTRIM(RTRIM(@Imei2)), '''')'; SET @Sep = N', '; END

IF @HasWarrantyStarts = 1 BEGIN SET @Set += @Sep + N'warranty_starts = @WarrantyStarts'; SET @Sep = N', '; END
IF @HasWarrantyEnds = 1 BEGIN SET @Set += @Sep + N'warranty_ends = @WarrantyEnds'; SET @Sep = N', '; END

IF @HasNote = 1 BEGIN SET @Set += @Sep + N'note = NULLIF(@Note, N'''')'; SET @Sep = N', '; END

IF LEN(@Set) = 0
BEGIN
    RAISERROR('No editable columns detected in dbo.devices.', 16, 1);
    RETURN;
END

DECLARE @Sql nvarchar(max) =
N'UPDATE dbo.devices
  SET ' + @Set + N'
  WHERE id = @DeviceId;';

EXEC sp_executesql
    @Sql,
    N'@DeviceId int,
      @TypeId int, @KindId int, @StatusId int, @ProducerId int, @ModelId int,
      @UserId int, @LocationId int,
      @Hostname nvarchar(255), @UserName nvarchar(255), @LocationText nvarchar(255), @ModelText nvarchar(255),
      @SerialNumber nvarchar(255), @AssetNo nvarchar(50), @InventoryNo nvarchar(255),
      @IpAddress varchar(15), @MacEthernet nvarchar(255), @MacWlan nvarchar(255),
      @Imei1 varchar(15), @Imei2 varchar(15),
      @WarrantyStarts date, @WarrantyEnds date,
      @Note nvarchar(max)',
    @DeviceId=@DeviceId,
    @TypeId=@TypeId, @KindId=@KindId, @StatusId=@StatusId, @ProducerId=@ProducerId, @ModelId=@ModelId,
    @UserId=@UserId, @LocationId=@LocationId,
    @Hostname=@Hostname, @UserName=@UserName, @LocationText=@LocationText, @ModelText=@ModelText,
    @SerialNumber=@SerialNumber, @AssetNo=@AssetNo, @InventoryNo=@InventoryNo,
    @IpAddress=@IpAddress, @MacEthernet=@MacEthernet, @MacWlan=@MacWlan,
    @Imei1=@Imei1, @Imei2=@Imei2,
    @WarrantyStarts=@WarrantyStarts, @WarrantyEnds=@WarrantyEnds,
    @Note=@Note;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = model.Id });

                cmd.Parameters.Add(new SqlParameter("@TypeId", SqlDbType.Int) { Value = (object?)typeId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@KindId", SqlDbType.Int) { Value = (object?)kindId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@StatusId", SqlDbType.Int) { Value = (object?)statusId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ProducerId", SqlDbType.Int) { Value = (object?)producerId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ModelId", SqlDbType.Int) { Value = (object?)modelId ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)userId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@LocationId", SqlDbType.Int) { Value = (object?)locationId ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Hostname", SqlDbType.NVarChar, 255) { Value = (object?)model.Hostname ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@UserName", SqlDbType.NVarChar, 255) { Value = (object?)model.User ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@LocationText", SqlDbType.NVarChar, 255) { Value = (object?)model.Location ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ModelText", SqlDbType.NVarChar, 255) { Value = (object?)model.Model ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@SerialNumber", SqlDbType.NVarChar, 255) { Value = (object?)model.SerialNumber ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@AssetNo", SqlDbType.NVarChar, 50) { Value = (object?)model.AssetNo ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@InventoryNo", SqlDbType.NVarChar, 255) { Value = (object?)model.InventoryNo ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@IpAddress", SqlDbType.VarChar, 15) { Value = (object?)model.IpAddress ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@MacEthernet", SqlDbType.NVarChar, 255) { Value = (object?)model.MacEthernet ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@MacWlan", SqlDbType.NVarChar, 255) { Value = (object?)model.MacWlan ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Imei1", SqlDbType.VarChar, 15) { Value = (object?)model.Imei1 ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Imei2", SqlDbType.VarChar, 15) { Value = (object?)model.Imei2 ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@WarrantyStarts", SqlDbType.Date) { Value = (object?)model.WarrantyStarts ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@WarrantyEnds", SqlDbType.Date) { Value = (object?)model.WarrantyEnds ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar) { Value = (object?)model.Note ?? DBNull.Value });

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return (true, string.Empty);
            }
            catch (SqlException ex)
            {
                return (false, $"Błąd zapisu danych devices (SQL): {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Błąd zapisu danych devices: {ex.Message}");
            }
        }

        public async Task<(bool Ok, string ErrorMessage, DeviceFilterLookups Lookups)> GetDevicesFilterLookupsAsync()
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new DeviceFilterLookups());

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new DeviceFilterLookups());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var types = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[]
                    {
                        ("dbo.device_types", "id", "name"),
                        ("dbo.DictDeviceTypes", "id", "name")
                    }).ConfigureAwait(false);

                var kinds = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[]
                    {
                        ("dbo.device_kinds", "id", "name"),
                        ("dbo.DictDeviceKinds", "id", "name")
                    }).ConfigureAwait(false);

                var statuses = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[]
                    {
                        ("dbo.device_statuses", "id", "name"),
                        ("dbo.DictDeviceStatuses", "id", "name")
                    }).ConfigureAwait(false);

                var producers = await LoadLookupItemsFromFirstExistingTableAsync(
                    con,
                    new[]
                    {
                        ("dbo.producers", "id", "name"),
                        ("dbo.DictProducers", "id", "name")
                    }).ConfigureAwait(false);

                var models = await LoadFromFirstExistingTableAsync(
                    con,
                    new[]
                    {
                        ("dbo.models", "name")
                    }).ConfigureAwait(false);

                var locations = await LoadFromFirstExistingTableAsync(
                    con,
                    new[]
                    {
                        ("dbo.locations", "name")
                    }).ConfigureAwait(false);

                var users = await LoadUsersDisplayNamesAsync(con).ConfigureAwait(false);

                // Self scope (View.My lub MyAssets.View): nie ułatwiamy enumeracji userów w filtrze
                if (!auth.CanViewAll && (auth.CanViewMy || auth.CanViewMyAssets))
                {
                    users = new List<string>();

                    if (auth.CurrentUserId > 0)
                    {
                        var me = await LoadUserDisplayNameByIdAsync(con, auth.CurrentUserId).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(me))
                            users.Add(me.Trim());
                    }
                }

                var lookups = new DeviceFilterLookups
                {
                    Types = types,
                    Kinds = kinds,
                    Statuses = statuses,
                    Producers = producers,
                    Models = models,
                    Locations = locations,
                    Users = users
                };

                return (true, string.Empty, lookups);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu słowników filtrów devices: {ex.Message}", new DeviceFilterLookups());
            }
        }

        private static async Task<string> LoadUserDisplayNameByIdAsync(SqlConnection con, int userId)
        {
            if (userId <= 0)
                return string.Empty;

            if (!await TableExistsAsync(con, "dbo.users").ConfigureAwait(false))
                return string.Empty;

            var sql = @"
SELECT TOP 1
    Val = LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255))))
FROM dbo.users
WHERE id = @Id
  AND DisplayName IS NOT NULL
  AND LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255)))) <> N'';
";
            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = userId });

            var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return v == null || v == DBNull.Value ? string.Empty : (Convert.ToString(v) ?? string.Empty);
        }

        private static async Task<int?> ResolveLookupIdByNameAsync(
            SqlConnection con,
            (string TableName, string IdColumn, string NameColumn)[] candidates,
            string? name)
        {
            var clean = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean))
                return null;

            foreach (var c in candidates)
            {
                if (!await TableExistsAsync(con, c.TableName).ConfigureAwait(false))
                    continue;

                var sql =
$@"
SELECT TOP 1
    Id = CAST([{c.IdColumn}] AS int)
FROM {c.TableName}
WHERE [{c.NameColumn}] IS NOT NULL
  AND LTRIM(RTRIM(CAST([{c.NameColumn}] AS nvarchar(255)))) = @Name;
";
                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 255) { Value = clean });

                var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (v != null && v != DBNull.Value)
                {
                    var id = Convert.ToInt32(v);
                    if (id > 0)
                        return id;
                }
            }

            return null;
        }

        private static async Task<int?> ResolveModelIdByNameAsync(SqlConnection con, string? modelName)
        {
            var clean = (modelName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean))
                return null;

            if (!await TableExistsAsync(con, "dbo.models").ConfigureAwait(false))
                return null;

            var sql =
@"
SELECT TOP 1
    Id = CAST(id AS int)
FROM dbo.models
WHERE name IS NOT NULL
  AND LTRIM(RTRIM(CAST(name AS nvarchar(255)))) = @Name;
";
            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 255) { Value = clean });

            var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (v == null || v == DBNull.Value)
                return null;

            var id = Convert.ToInt32(v);
            return id > 0 ? id : null;
        }

        private static async Task<int?> ResolveUserIdByDisplayNameAsync(SqlConnection con, string? displayName)
        {
            var clean = (displayName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean))
                return null;

            if (!await TableExistsAsync(con, "dbo.users").ConfigureAwait(false))
                return null;

            var sql =
@"
SELECT TOP 1
    Id = CAST(id AS int)
FROM dbo.users
WHERE DisplayName IS NOT NULL
  AND LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255)))) = @Name;
";
            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 255) { Value = clean });

            var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (v == null || v == DBNull.Value)
                return null;

            var id = Convert.ToInt32(v);
            return id > 0 ? id : null;
        }

        private static async Task<int?> ResolveLocationIdByNameAsync(SqlConnection con, string? locationName)
        {
            var clean = (locationName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean))
                return null;

            if (!await TableExistsAsync(con, "dbo.locations").ConfigureAwait(false))
                return null;

            var sql =
@"
SELECT TOP 1
    Id = CAST(id AS int)
FROM dbo.locations
WHERE name IS NOT NULL
  AND LTRIM(RTRIM(CAST(name AS nvarchar(255)))) = @Name;
";
            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 255) { Value = clean });

            var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (v == null || v == DBNull.Value)
                return null;

            var id = Convert.ToInt32(v);
            return id > 0 ? id : null;
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
                        $"SELECT DISTINCT " +
                        $"  Id  = CAST([{c.IdColumn}] AS int), " +
                        $"  Name = LTRIM(RTRIM(CAST([{c.NameColumn}] AS nvarchar(255)))) " +
                        $"FROM {c.TableName} " +
                        $"WHERE [{c.NameColumn}] IS NOT NULL AND LTRIM(RTRIM(CAST([{c.NameColumn}] AS nvarchar(255)))) <> N'' " +
                        $"ORDER BY Name;";

                    return await LoadLookupItemListAsync(con, safeSql).ConfigureAwait(false);
                }
            }

            return new List<LookupItem>();
        }

        private static async Task<List<string>> LoadFromFirstExistingTableAsync(SqlConnection con, (string TableName, string ColumnName)[] candidates)
        {
            foreach (var c in candidates)
            {
                if (await TableExistsAsync(con, c.TableName).ConfigureAwait(false))
                {
                    var safeSql =
                        $"SELECT DISTINCT Val = LTRIM(RTRIM(CAST([{c.ColumnName}] AS nvarchar(255)))) " +
                        $"FROM {c.TableName} " +
                        $"WHERE [{c.ColumnName}] IS NOT NULL AND LTRIM(RTRIM(CAST([{c.ColumnName}] AS nvarchar(255)))) <> N'' " +
                        $"ORDER BY Val;";

                    return await LoadStringListAsync(con, safeSql).ConfigureAwait(false);
                }
            }

            return new List<string>();
        }

        private static async Task<List<string>> LoadUsersDisplayNamesAsync(SqlConnection con)
        {
            if (!await TableExistsAsync(con, "dbo.users").ConfigureAwait(false))
                return new List<string>();

            var sql =
@"
SELECT DISTINCT
    Val = LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255))))
FROM dbo.users
WHERE DisplayName IS NOT NULL
  AND LTRIM(RTRIM(CAST(DisplayName AS nvarchar(255)))) <> N''
ORDER BY Val;
";
            return await LoadStringListAsync(con, sql).ConfigureAwait(false);
        }

        private static async Task<bool> TableExistsAsync(SqlConnection con, string fullName)
        {
            var sql = "SELECT CASE WHEN OBJECT_ID(@n, 'U') IS NOT NULL THEN 1 ELSE 0 END;";
            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 256) { Value = fullName });

            var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(v) == 1;
        }

        private static async Task<List<string>> LoadStringListAsync(SqlConnection con, string sql)
        {
            var list = new List<string>();

            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                var s = Convert.ToString(r["Val"]) ?? string.Empty;
                s = s.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }

            return list;
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
                {
                    list.Add(new LookupItem
                    {
                        Id = id.Value,
                        Name = name.Trim()
                    });
                }
            }

            return list;
        }

        private async Task<(bool Ok, string ErrorMessage, List<DeviceListRow> Rows)> GetDevicesCoreAsync(int? userId, int? deviceId)
        {
            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new List<DeviceListRow>());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql =
@"
DECLARE @HasTypeDict bit = 0;
DECLARE @HasKindDict bit = 0;
DECLARE @HasStatusDict bit = 0;
DECLARE @HasProducerDict bit = 0;

DECLARE @HasUsers bit = 0;
DECLARE @HasLocations bit = 0;
DECLARE @HasModels bit = 0;

IF OBJECT_ID('dbo.device_types', 'U') IS NOT NULL SET @HasTypeDict = 1;
IF OBJECT_ID('dbo.DictDeviceTypes', 'U') IS NOT NULL SET @HasTypeDict = 1;

IF OBJECT_ID('dbo.device_kinds', 'U') IS NOT NULL SET @HasKindDict = 1;
IF OBJECT_ID('dbo.DictDeviceKinds', 'U') IS NOT NULL SET @HasKindDict = 1;

IF OBJECT_ID('dbo.device_statuses', 'U') IS NOT NULL SET @HasStatusDict = 1;
IF OBJECT_ID('dbo.DictDeviceStatuses', 'U') IS NOT NULL SET @HasStatusDict = 1;

IF OBJECT_ID('dbo.producers', 'U') IS NOT NULL SET @HasProducerDict = 1;
IF OBJECT_ID('dbo.DictProducers', 'U') IS NOT NULL SET @HasProducerDict = 1;

IF OBJECT_ID('dbo.users', 'U') IS NOT NULL SET @HasUsers = 1;
IF OBJECT_ID('dbo.locations', 'U') IS NOT NULL SET @HasLocations = 1;
IF OBJECT_ID('dbo.models', 'U') IS NOT NULL SET @HasModels = 1;

DECLARE @Where nvarchar(max) = N' WHERE 1=1 ';

IF @UserId IS NOT NULL
    SET @Where += N' AND d.user_id = @UserId ';

IF @DeviceId IS NOT NULL
    SET @Where += N' AND d.id = @DeviceId ';

DECLARE @Sql nvarchar(max) = N'
SELECT
    Id = d.id,

    TypeId = CAST(d.device_type_id AS int),
    Type = ' + CASE
        WHEN @HasTypeDict = 1 THEN N'ISNULL(dt.name, CAST(d.device_type_id AS nvarchar(50)))'
        ELSE N'CAST(d.device_type_id AS nvarchar(50))'
    END + N',

    KindId = CAST(d.device_kind_id AS int),
    Kind = ' + CASE
        WHEN @HasKindDict = 1 THEN N'ISNULL(k.name, CAST(d.device_kind_id AS nvarchar(50)))'
        ELSE N'CAST(d.device_kind_id AS nvarchar(50))'
    END + N',

    StatusId = CAST(d.device_status_id AS int),
    [Status] = ' + CASE
        WHEN @HasStatusDict = 1 THEN N'ISNULL(s.name, CAST(d.device_status_id AS nvarchar(50)))'
        ELSE N'CAST(d.device_status_id AS nvarchar(50))'
    END + N',

    ProducerId = CAST(d.producer_id AS int),
    Producer = ' + CASE
        WHEN @HasProducerDict = 1 THEN N'ISNULL(p.name, CAST(d.producer_id AS nvarchar(50)))'
        ELSE N'CAST(d.producer_id AS nvarchar(50))'
    END + N',

    ModelId = CAST(d.model_id AS int),
    Model = ' + CASE
        WHEN @HasModels = 1 THEN N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(m.name AS nvarchar(255)))), N''''), N'''')'
        ELSE N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.model AS nvarchar(255)))), N''''), N'''')'
    END + N',

    SerialNumber = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.serial_number AS nvarchar(255)))), N''''), N''''),

    UserId = CAST(d.user_id AS int),
    [User] = ' + CASE
        WHEN @HasUsers = 1 THEN N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(u.DisplayName AS nvarchar(255)))), N''''), N'''')'
        ELSE N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.user_name AS nvarchar(255)))), N''''), N'''')'
    END + N',

    LocationId = CAST(d.location_id AS int),
    Location = ' + CASE
        WHEN @HasLocations = 1 THEN N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(l.name AS nvarchar(255)))), N''''), N'''')'
        ELSE N'ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.location AS nvarchar(255)))), N''''), N'''')'
    END + N',

    Hostname = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.hostname AS nvarchar(255)))), N''''), N''''),
    IpAddress = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.ip_address AS varchar(15)))), ''''), ''''),
    MacEthernet = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.mac_ethernet AS nvarchar(255)))), N''''), N''''),
    MacWlan = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.mac_wlan AS nvarchar(255)))), N''''), N''''),
    Imei1 = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.imei_1 AS varchar(15)))), ''''), ''''),
    Imei2 = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.imei_2 AS varchar(15)))), ''''), ''''),

    WarrantyStarts = d.warranty_starts,
    WarrantyEnds = d.warranty_ends,

    InventoryNo = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.inventory_no AS nvarchar(255)))), N''''), N''''),
    AssetNo      = ISNULL(NULLIF(LTRIM(RTRIM(CAST(d.asset_no AS nvarchar(50)))),  N''''), N''''),

    Note = ISNULL(CAST(d.note AS nvarchar(max)), N'''')
FROM dbo.devices d
';

IF @HasTypeDict = 1
BEGIN
    IF OBJECT_ID('dbo.device_types', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.device_types dt ON dt.id = d.device_type_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictDeviceTypes dt ON dt.id = d.device_type_id ';
END

IF @HasKindDict = 1
BEGIN
    IF OBJECT_ID('dbo.device_kinds', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.device_kinds k ON k.id = d.device_kind_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictDeviceKinds k ON k.id = d.device_kind_id ';
END

IF @HasProducerDict = 1
BEGIN
    IF OBJECT_ID('dbo.producers', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.producers p ON p.id = d.producer_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictProducers p ON p.id = d.producer_id ';
END

IF @HasStatusDict = 1
BEGIN
    IF OBJECT_ID('dbo.device_statuses', 'U') IS NOT NULL
        SET @Sql += N'LEFT JOIN dbo.device_statuses s ON s.id = d.device_status_id ';
    ELSE
        SET @Sql += N'LEFT JOIN dbo.DictDeviceStatuses s ON s.id = d.device_status_id ';
END

IF @HasUsers = 1
BEGIN
    SET @Sql += N'LEFT JOIN dbo.users u ON u.id = d.user_id ';
END

IF @HasLocations = 1
BEGIN
    SET @Sql += N'LEFT JOIN dbo.locations l ON l.id = d.location_id ';
END

IF @HasModels = 1
BEGIN
    SET @Sql += N'LEFT JOIN dbo.models m ON m.id = d.model_id ';
END

SET @Sql += @Where;
SET @Sql += N' ORDER BY Hostname, Id;';

EXEC sp_executesql @Sql, N'@UserId int, @DeviceId int', @UserId=@UserId, @DeviceId=@DeviceId;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)userId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@DeviceId", SqlDbType.Int) { Value = (object?)deviceId ?? DBNull.Value });

                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                var rows = new List<DeviceListRow>();
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new DeviceListRow
                    {
                        Id = Convert.ToInt32(r["Id"]),

                        TypeId = r["TypeId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["TypeId"]),
                        Type = Convert.ToString(r["Type"]) ?? string.Empty,

                        KindId = r["KindId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["KindId"]),
                        Kind = Convert.ToString(r["Kind"]) ?? string.Empty,

                        StatusId = r["StatusId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["StatusId"]),
                        Status = Convert.ToString(r["Status"]) ?? string.Empty,

                        ProducerId = r["ProducerId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["ProducerId"]),
                        Producer = Convert.ToString(r["Producer"]) ?? string.Empty,

                        ModelId = r["ModelId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["ModelId"]),
                        Model = Convert.ToString(r["Model"]) ?? string.Empty,

                        SerialNumber = Convert.ToString(r["SerialNumber"]) ?? string.Empty,

                        UserId = r["UserId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["UserId"]),
                        User = Convert.ToString(r["User"]) ?? string.Empty,

                        LocationId = r["LocationId"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["LocationId"]),
                        Location = Convert.ToString(r["Location"]) ?? string.Empty,

                        Hostname = Convert.ToString(r["Hostname"]) ?? string.Empty,
                        IpAddress = Convert.ToString(r["IpAddress"]) ?? string.Empty,
                        MacEthernet = Convert.ToString(r["MacEthernet"]) ?? string.Empty,
                        MacWlan = Convert.ToString(r["MacWlan"]) ?? string.Empty,
                        Imei1 = Convert.ToString(r["Imei1"]) ?? string.Empty,
                        Imei2 = Convert.ToString(r["Imei2"]) ?? string.Empty,

                        WarrantyStarts = r["WarrantyStarts"] == DBNull.Value ? null : (DateTime?)Convert.ToDateTime(r["WarrantyStarts"]),
                        WarrantyEnds = r["WarrantyEnds"] == DBNull.Value ? null : (DateTime?)Convert.ToDateTime(r["WarrantyEnds"]),

                        InventoryNo = Convert.ToString(r["InventoryNo"]) ?? string.Empty,
                        AssetNo = Convert.ToString(r["AssetNo"]) ?? string.Empty,

                        Note = Convert.ToString(r["Note"]) ?? string.Empty
                    });
                }

                return (true, string.Empty, rows);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu danych devices: {ex.Message}", new List<DeviceListRow>());
            }
        }
    }
}
