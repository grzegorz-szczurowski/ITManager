// File: Services/SimCardsService.cs
// Description: SIM cards: lista + lookups + get by id + create + update (bezpieczne, wykrywanie kolumn).
// Version: 1.27 (2026-02-03)
// Change history:
// 1.21 (2025-12-21) - Baza: lista + lookups + get by id + create + update (wykrywanie kolumn).
// 1.22 (2025-12-21) - Fix: stabilny dynamiczny SELECT (bez błędów cudzysłowów), brakujące kolumny zwracają pusty string.
// 1.23 (2025-12-23) - Fix: Type/Operator/Plan/Status pobierane przez FK (sim_card_*_id) z tabel słownikowych
//                    + wykrywanie kolumn sim_card_*_name oraz obsługa tabeli dbo.sim_card_operator (singular).
// 1.24 (2026-01-06) - Fix: Lookups pobierane z tabel słownikowych (types/operators/plans/statuses) oraz dbo.users,
//                    a nie z dbo.sim_cards (żeby listy były kompletne).
// 1.25 (2026-01-25) - RBAC: egzekwowanie permissionów w backendzie (Assets.SimCards.View/Create/Edit) na początku metod publicznych
//                    + ochrona GetMySimCardsAsync(userId) przed pobraniem kart innego użytkownika bez Edit.
// 1.26 (2026-01-25) - FIX: RBAC fallback na stare kody permissionów (Assets.Sim.View/Create/Edit), bo w bazie są "Assets.Sim.*".
// 1.27 (2026-02-03) - RBAC: MyAssets page: Assets.MyAssets.View pozwala na podgląd własnych kart SIM (read-only) bez Assets.SimCards.View
//                    + ochrona GetMySimCardsAsync i GetSimCardByIdAsync + ograniczenie Users w filtrach (anti-enumeration).

using Microsoft.Extensions.Configuration;
using ITManager.Models;
using ITManager.Models.Auth;
using ITManager.Services.Auth;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace ITManager.Services
{
    public sealed class SimCardsService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;
        private readonly CurrentUserContextService _currentUserContextService;

        // Permissions (RBAC) - nowe
        private const string PermSimCardsView = "Assets.SimCards.View";
        private const string PermSimCardsCreate = "Assets.SimCards.Create";
        private const string PermSimCardsEdit = "Assets.SimCards.Edit";

        // Permissions (RBAC) - stare (w Twojej bazie/GUI: Assets.Sim.*)
        private const string PermSimViewLegacy = "Assets.Sim.View";
        private const string PermSimCreateLegacy = "Assets.Sim.Create";
        private const string PermSimEditLegacy = "Assets.Sim.Edit";

        // MyAssets (read-only self scope)
        private const string PermMyAssetsView = "Assets.MyAssets.View";

        public SimCardsService(IConfiguration configuration, CurrentUserContextService currentUserContextService)
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

        private static bool HasAny(CurrentUserContext ctx, string permNew, string permLegacy)
        {
            return ctx.Has(permNew) || ctx.Has(permLegacy);
        }

        private async Task<(bool Ok, string ErrorMessage, int CurrentUserId, bool CanViewAll, bool CanViewMyAssets)> GuardViewAnyAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true)
                return (false, "Brak uprawnień do podglądu kart SIM.", 0, false, false);

            var canViewAll = HasAny(ctx, PermSimCardsView, PermSimViewLegacy);
            var canViewMyAssets = ctx.Has(PermMyAssetsView);

            if (!canViewAll && !canViewMyAssets)
                return (false, "Brak uprawnień do podglądu kart SIM.", 0, false, false);

            var currentUserId = (ctx.UserId.HasValue && ctx.UserId.Value > 0) ? ctx.UserId.Value : 0;

            if (canViewMyAssets && currentUserId <= 0)
                return (false, "Brak mapowania użytkownika do dbo.users (UserId).", 0, canViewAll, canViewMyAssets);

            return (true, string.Empty, currentUserId, canViewAll, canViewMyAssets);
        }

        private async Task<(bool Ok, string ErrorMessage)> GuardCanCreateSimCardsAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !HasAny(ctx, PermSimCardsCreate, PermSimCreateLegacy))
                return (false, "Brak uprawnień do tworzenia kart SIM.");

            return (true, string.Empty);
        }

        private async Task<(bool Ok, string ErrorMessage)> GuardCanEditSimCardsAsync()
        {
            await EnsureUserContextInitializedAsync().ConfigureAwait(false);
            var ctx = _currentUserContextService.CurrentUser;

            if (!ctx.IsAuthenticated || ctx.IsActive != true || !HasAny(ctx, PermSimCardsEdit, PermSimEditLegacy))
                return (false, "Brak uprawnień do edycji kart SIM.");

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

        // =========================
        // Public API
        // =========================

        public async Task<(bool Ok, string ErrorMessage, List<SimCardEditModel> Rows)> GetSimCardsAsync()
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new List<SimCardEditModel>());

            // lista globalna tylko dla View (nowe lub legacy)
            if (!auth.CanViewAll)
                return (false, "Brak uprawnień do podglądu listy kart SIM.", new List<SimCardEditModel>());

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new List<SimCardEditModel>());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = BuildSelectListSql(whereClause: null, top1: false);

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = DBNull.Value });

                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                var rows = new List<SimCardEditModel>();
                while (await r.ReadAsync().ConfigureAwait(false))
                    rows.Add(MapRow(r));

                return (true, string.Empty, rows);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu SIM: {ex.Message}", new List<SimCardEditModel>());
            }
        }

        public async Task<(bool Ok, string ErrorMessage, List<SimCardEditModel> Rows)> GetMySimCardsAsync(int userId)
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new List<SimCardEditModel>());

            if (userId <= 0)
                return (false, "Nieprawidłowe UserId.", new List<SimCardEditModel>());

            // Ochrona przed pobraniem "czyichś" kart metodą GetMySimCardsAsync(userId):
            // - jeśli ktoś ma View (nowe/legacy), domyślnie dopuszczamy CurrentUserId,
            //   wyjątek: użytkownik z Edit może pobrać po innym userId (np. admin/IT)
            // - jeśli ktoś ma tylko Assets.MyAssets.View, dopuszczamy wyłącznie CurrentUserId
            if (auth.CanViewMyAssets && !auth.CanViewAll)
            {
                if (auth.CurrentUserId <= 0)
                    return (false, "Brak mapowania użytkownika do dbo.users (UserId).", new List<SimCardEditModel>());

                if (userId != auth.CurrentUserId)
                    return (false, "Brak uprawnień do podglądu kart SIM innego użytkownika.", new List<SimCardEditModel>());
            }
            else
            {
                var current = await GetCurrentUserIdOrErrorAsync().ConfigureAwait(false);
                if (!current.Ok)
                    return (false, current.ErrorMessage, new List<SimCardEditModel>());

                if (userId != current.CurrentUserId)
                {
                    var canEdit = await GuardCanEditSimCardsAsync().ConfigureAwait(false);
                    if (!canEdit.Ok)
                        return (false, "Brak uprawnień do podglądu kart SIM innego użytkownika.", new List<SimCardEditModel>());
                }
            }

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new List<SimCardEditModel>());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var whereSql = await BuildUserOwnershipWhereClauseAsync(con, userId).ConfigureAwait(false);
                var sql = BuildSelectListSql(whereSql, top1: false);

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                var rows = new List<SimCardEditModel>();
                while (await r.ReadAsync().ConfigureAwait(false))
                    rows.Add(MapRow(r));

                return (true, string.Empty, rows);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu SIM: {ex.Message}", new List<SimCardEditModel>());
            }
        }

        public async Task<(bool Ok, string ErrorMessage, SimCardEditModel Row)> GetSimCardByIdAsync(int id)
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new SimCardEditModel());

            if (id <= 0)
                return (false, "Nieprawidłowe Id SIM.", new SimCardEditModel());

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new SimCardEditModel());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                // Jeśli nie ma View (nowe/legacy), a jest tylko MyAssets.View, to dopuszczamy wyłącznie własne
                string where;
                int? userIdParam;

                if (auth.CanViewAll)
                {
                    where = "s.id = @Id";
                    userIdParam = null;
                }
                else
                {
                    if (auth.CurrentUserId <= 0)
                        return (false, "Brak mapowania użytkownika do dbo.users (UserId).", new SimCardEditModel());

                    var ownWhere = await BuildUserOwnershipWhereClauseAsync(con, auth.CurrentUserId).ConfigureAwait(false);
                    where = $"({ownWhere}) AND s.id = @Id";
                    userIdParam = auth.CurrentUserId;
                }

                var sql = BuildSelectListSql(whereClause: where, top1: true);

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = (object?)userIdParam ?? DBNull.Value });

                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (!await r.ReadAsync().ConfigureAwait(false))
                {
                    // Jeśli to self-scope i nic nie zwróciło, traktujemy jako brak dostępu do tego rekordu
                    if (!auth.CanViewAll)
                        return (false, "Brak uprawnień do podglądu tej karty SIM.", new SimCardEditModel());

                    return (true, string.Empty, new SimCardEditModel());
                }

                return (true, string.Empty, MapRow(r));
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu SIM: {ex.Message}", new SimCardEditModel());
            }
        }

        public async Task<(bool Ok, string ErrorMessage, int NewId)> CreateSimCardAsync(SimCardEditModel model)
        {
            var guard = await GuardCanCreateSimCardsAsync().ConfigureAwait(false);
            if (!guard.Ok)
                return (false, guard.ErrorMessage, 0);

            if (model == null)
                return (false, "Brak danych do zapisu.", 0);

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", 0);

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = @"
DECLARE @Table nvarchar(256) = N'dbo.sim_cards';
IF OBJECT_ID(@Table, 'U') IS NULL
BEGIN
  RAISERROR('Missing table dbo.sim_cards.', 16, 1);
  RETURN;
END

DECLARE @Cols nvarchar(max) = N'';
DECLARE @Vals nvarchar(max) = N'';
DECLARE @Sep  nvarchar(10) = N'';

DECLARE @AddColType bit = CASE WHEN COL_LENGTH(@Table,'type') IS NOT NULL OR COL_LENGTH(@Table,'type_name') IS NOT NULL OR COL_LENGTH(@Table,'sim_type') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColOperator bit = CASE WHEN COL_LENGTH(@Table,'operator') IS NOT NULL OR COL_LENGTH(@Table,'operator_name') IS NOT NULL OR COL_LENGTH(@Table,'provider') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColPlan bit = CASE WHEN COL_LENGTH(@Table,'plan') IS NOT NULL OR COL_LENGTH(@Table,'plan_name') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColStatus bit = CASE WHEN COL_LENGTH(@Table,'status') IS NOT NULL OR COL_LENGTH(@Table,'status_name') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColUser bit = CASE WHEN COL_LENGTH(@Table,'user_name') IS NOT NULL OR COL_LENGTH(@Table,'assigned_to') IS NOT NULL OR COL_LENGTH(@Table,'user') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColMobile bit = CASE WHEN COL_LENGTH(@Table,'mobile_number') IS NOT NULL OR COL_LENGTH(@Table,'phone_number') IS NOT NULL OR COL_LENGTH(@Table,'msisdn') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColSim bit = CASE WHEN COL_LENGTH(@Table,'sim_card_number') IS NOT NULL OR COL_LENGTH(@Table,'sim_number') IS NOT NULL OR COL_LENGTH(@Table,'sim') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @AddColNote bit = CASE WHEN COL_LENGTH(@Table,'note') IS NOT NULL THEN 1 ELSE 0 END;

DECLARE @ColType sysname = CASE WHEN COL_LENGTH(@Table,'type') IS NOT NULL THEN 'type'
                               WHEN COL_LENGTH(@Table,'type_name') IS NOT NULL THEN 'type_name'
                               WHEN COL_LENGTH(@Table,'sim_type') IS NOT NULL THEN 'sim_type'
                               ELSE NULL END;

DECLARE @ColOperator sysname = CASE WHEN COL_LENGTH(@Table,'operator') IS NOT NULL THEN 'operator'
                                   WHEN COL_LENGTH(@Table,'operator_name') IS NOT NULL THEN 'operator_name'
                                   WHEN COL_LENGTH(@Table,'provider') IS NOT NULL THEN 'provider'
                                   ELSE NULL END;

DECLARE @ColPlan sysname = CASE WHEN COL_LENGTH(@Table,'plan') IS NOT NULL THEN 'plan'
                               WHEN COL_LENGTH(@Table,'plan_name') IS NOT NULL THEN 'plan_name'
                               ELSE NULL END;

DECLARE @ColStatus sysname = CASE WHEN COL_LENGTH(@Table,'status') IS NOT NULL THEN 'status'
                                 WHEN COL_LENGTH(@Table,'status_name') IS NOT NULL THEN 'status_name'
                                 ELSE NULL END;

DECLARE @ColUser sysname = CASE WHEN COL_LENGTH(@Table,'user_name') IS NOT NULL THEN 'user_name'
                               WHEN COL_LENGTH(@Table,'assigned_to') IS NOT NULL THEN 'assigned_to'
                               WHEN COL_LENGTH(@Table,'user') IS NOT NULL THEN 'user'
                               ELSE NULL END;

DECLARE @ColMobile sysname = CASE WHEN COL_LENGTH(@Table,'mobile_number') IS NOT NULL THEN 'mobile_number'
                                 WHEN COL_LENGTH(@Table,'phone_number') IS NOT NULL THEN 'phone_number'
                                 WHEN COL_LENGTH(@Table,'msisdn') IS NOT NULL THEN 'msisdn'
                                 ELSE NULL END;

DECLARE @ColSim sysname = CASE WHEN COL_LENGTH(@Table,'sim_card_number') IS NOT NULL THEN 'sim_card_number'
                              WHEN COL_LENGTH(@Table,'sim_number') IS NOT NULL THEN 'sim_number'
                              WHEN COL_LENGTH(@Table,'sim') IS NOT NULL THEN 'sim'
                              ELSE NULL END;

IF @AddColType = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColType); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Type)), N'''')'; SET @Sep = N', '; END
IF @AddColOperator = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColOperator); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Operator)), N'''')'; SET @Sep = N', '; END
IF @AddColPlan = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColPlan); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Plan)), N'''')'; SET @Sep = N', '; END
IF @AddColStatus = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColStatus); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@Status)), N'''')'; SET @Sep = N', '; END
IF @AddColUser = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColUser); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@User)), N'''')'; SET @Sep = N', '; END
IF @AddColMobile = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColMobile); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@MobileNumber)), N'''')'; SET @Sep = N', '; END
IF @AddColSim = 1 BEGIN SET @Cols += @Sep + QUOTENAME(@ColSim); SET @Vals += @Sep + N'NULLIF(LTRIM(RTRIM(@SimCardNumber)), N'''')'; SET @Sep = N', '; END
IF @AddColNote = 1 BEGIN SET @Cols += @Sep + N'[note]'; SET @Vals += @Sep + N'NULLIF(@Note, N'''')'; SET @Sep = N', '; END

IF LEN(@Cols) = 0
BEGIN
  RAISERROR('No insertable columns detected in dbo.sim_cards.', 16, 1);
  RETURN;
END

DECLARE @Sql nvarchar(max) = N'
INSERT INTO dbo.sim_cards (' + @Cols + N') VALUES (' + @Vals + N');
SELECT CAST(SCOPE_IDENTITY() AS int) AS NewId;';

EXEC sp_executesql
  @Sql,
  N'@Type nvarchar(255), @Operator nvarchar(255), @Plan nvarchar(255), @Status nvarchar(255), @User nvarchar(255),
    @MobileNumber nvarchar(255), @SimCardNumber nvarchar(255), @Note nvarchar(max)',
  @Type=@Type, @Operator=@Operator, @Plan=@Plan, @Status=@Status, @User=@User,
  @MobileNumber=@MobileNumber, @SimCardNumber=@SimCardNumber, @Note=@Note;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

                cmd.Parameters.Add(new SqlParameter("@Type", SqlDbType.NVarChar, 255) { Value = (object?)model.Type ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Operator", SqlDbType.NVarChar, 255) { Value = (object?)model.Operator ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Plan", SqlDbType.NVarChar, 255) { Value = (object?)model.Plan ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 255) { Value = (object?)model.Status ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 255) { Value = (object?)model.User ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@MobileNumber", SqlDbType.NVarChar, 255) { Value = (object?)model.MobileNumber ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@SimCardNumber", SqlDbType.NVarChar, 255) { Value = (object?)model.SimCardNumber ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar) { Value = (object?)model.Note ?? DBNull.Value });

                var newIdObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                var newId = newIdObj == null || newIdObj == DBNull.Value ? 0 : Convert.ToInt32(newIdObj);

                if (newId <= 0)
                    return (false, "Nie udało się uzyskać nowego Id.", 0);

                return (true, string.Empty, newId);
            }
            catch (SqlException ex)
            {
                return (false, $"Błąd dodawania SIM (SQL): {ex.Message}", 0);
            }
            catch (Exception ex)
            {
                return (false, $"Błąd dodawania SIM: {ex.Message}", 0);
            }
        }

        public async Task<(bool Ok, string ErrorMessage)> UpdateSimCardAsync(SimCardEditModel model)
        {
            var guard = await GuardCanEditSimCardsAsync().ConfigureAwait(false);
            if (!guard.Ok)
                return (false, guard.ErrorMessage);

            if (model == null)
                return (false, "Brak danych do zapisu.");

            if (model.Id <= 0)
                return (false, "Nieprawidłowe Id SIM.");

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.");

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = @"
DECLARE @Table nvarchar(256) = N'dbo.sim_cards';
IF OBJECT_ID(@Table, 'U') IS NULL
BEGIN
  RAISERROR('Missing table dbo.sim_cards.', 16, 1);
  RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.sim_cards WHERE id = @Id)
BEGIN
  RAISERROR('SIM not found.', 16, 1);
  RETURN;
END

DECLARE @ColType sysname = CASE WHEN COL_LENGTH(@Table,'type') IS NOT NULL THEN 'type'
                               WHEN COL_LENGTH(@Table,'type_name') IS NOT NULL THEN 'type_name'
                               WHEN COL_LENGTH(@Table,'sim_type') IS NOT NULL THEN 'sim_type'
                               ELSE NULL END;

DECLARE @ColOperator sysname = CASE WHEN COL_LENGTH(@Table,'operator') IS NOT NULL THEN 'operator'
                                   WHEN COL_LENGTH(@Table,'operator_name') IS NOT NULL THEN 'operator_name'
                                   WHEN COL_LENGTH(@Table,'provider') IS NOT NULL THEN 'provider'
                                   ELSE NULL END;

DECLARE @ColPlan sysname = CASE WHEN COL_LENGTH(@Table,'plan') IS NOT NULL THEN 'plan'
                               WHEN COL_LENGTH(@Table,'plan_name') IS NOT NULL THEN 'plan_name'
                               ELSE NULL END;

DECLARE @ColStatus sysname = CASE WHEN COL_LENGTH(@Table,'status') IS NOT NULL THEN 'status'
                                 WHEN COL_LENGTH(@Table,'status_name') IS NOT NULL THEN 'status_name'
                                 ELSE NULL END;

DECLARE @ColUser sysname = CASE WHEN COL_LENGTH(@Table,'user_name') IS NOT NULL THEN 'user_name'
                               WHEN COL_LENGTH(@Table,'assigned_to') IS NOT NULL THEN 'assigned_to'
                               WHEN COL_LENGTH(@Table,'user') IS NOT NULL THEN 'user'
                               ELSE NULL END;

DECLARE @ColMobile sysname = CASE WHEN COL_LENGTH(@Table,'mobile_number') IS NOT NULL THEN 'mobile_number'
                                 WHEN COL_LENGTH(@Table,'phone_number') IS NOT NULL THEN 'phone_number'
                                 WHEN COL_LENGTH(@Table,'msisdn') IS NOT NULL THEN 'msisdn'
                                 ELSE NULL END;

DECLARE @ColSim sysname = CASE WHEN COL_LENGTH(@Table,'sim_card_number') IS NOT NULL THEN 'sim_card_number'
                              WHEN COL_LENGTH(@Table,'sim_number') IS NOT NULL THEN 'sim_number'
                              WHEN COL_LENGTH(@Table,'sim') IS NOT NULL THEN 'sim'
                              ELSE NULL END;

DECLARE @Set nvarchar(max) = N'';
DECLARE @Sep nvarchar(10) = N'';

IF @ColType IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColType) + N' = NULLIF(LTRIM(RTRIM(@Type)), N'''')'; SET @Sep = N', '; END
IF @ColOperator IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColOperator) + N' = NULLIF(LTRIM(RTRIM(@Operator)), N'''')'; SET @Sep = N', '; END
IF @ColPlan IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColPlan) + N' = NULLIF(LTRIM(RTRIM(@Plan)), N'''')'; SET @Sep = N', '; END
IF @ColStatus IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColStatus) + N' = NULLIF(LTRIM(RTRIM(@Status)), N'''')'; SET @Sep = N', '; END
IF @ColUser IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColUser) + N' = NULLIF(LTRIM(RTRIM(@User)), N'''')'; SET @Sep = N', '; END
IF @ColMobile IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColMobile) + N' = NULLIF(LTRIM(RTRIM(@MobileNumber)), N'''')'; SET @Sep = N', '; END
IF @ColSim IS NOT NULL BEGIN SET @Set += @Sep + QUOTENAME(@ColSim) + N' = NULLIF(LTRIM(RTRIM(@SimCardNumber)), N'''')'; SET @Sep = N', '; END
IF COL_LENGTH(@Table,'note') IS NOT NULL BEGIN SET @Set += @Sep + N'[note] = NULLIF(@Note, N'''')'; SET @Sep = N', '; END

IF LEN(@Set) = 0
BEGIN
  RAISERROR('No editable columns detected in dbo.sim_cards.', 16, 1);
  RETURN;
END

DECLARE @Sql nvarchar(max) = N'UPDATE dbo.sim_cards SET ' + @Set + N' WHERE id = @Id;';

EXEC sp_executesql
  @Sql,
  N'@Id int, @Type nvarchar(255), @Operator nvarchar(255), @Plan nvarchar(255), @Status nvarchar(255), @User nvarchar(255),
    @MobileNumber nvarchar(255), @SimCardNumber nvarchar(255), @Note nvarchar(max)',
  @Id=@Id, @Type=@Type, @Operator=@Operator, @Plan=@Plan, @Status=@Status, @User=@User,
  @MobileNumber=@MobileNumber, @SimCardNumber=@SimCardNumber, @Note=@Note;
";

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = model.Id });
                cmd.Parameters.Add(new SqlParameter("@Type", SqlDbType.NVarChar, 255) { Value = (object?)model.Type ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Operator", SqlDbType.NVarChar, 255) { Value = (object?)model.Operator ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Plan", SqlDbType.NVarChar, 255) { Value = (object?)model.Plan ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 255) { Value = (object?)model.Status ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 255) { Value = (object?)model.User ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@MobileNumber", SqlDbType.NVarChar, 255) { Value = (object?)model.MobileNumber ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@SimCardNumber", SqlDbType.NVarChar, 255) { Value = (object?)model.SimCardNumber ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar) { Value = (object?)model.Note ?? DBNull.Value });

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return (true, string.Empty);
            }
            catch (SqlException ex)
            {
                return (false, $"Błąd zapisu SIM (SQL): {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Błąd zapisu SIM: {ex.Message}");
            }
        }

        public async Task<(bool Ok, string ErrorMessage, SimCardsFilterLookups Lookups)> GetSimCardsFilterLookupsAsync()
        {
            var auth = await GuardViewAnyAsync().ConfigureAwait(false);
            if (!auth.Ok)
                return (false, auth.ErrorMessage, new SimCardsFilterLookups());

            try
            {
                var cs = _configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(cs))
                    return (false, $"Brak ConnectionString '{ConnectionStringName}'.", new SimCardsFilterLookups());

                using var con = new SqlConnection(cs);
                await con.OpenAsync().ConfigureAwait(false);

                var sql = BuildLookupsSql();

                using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var plans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var statuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    var kind = Convert.ToString(r["Kind"]) ?? string.Empty;
                    var val = (Convert.ToString(r["Val"]) ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(val))
                        continue;

                    if (string.Equals(kind, "types", StringComparison.OrdinalIgnoreCase)) types.Add(val);
                    else if (string.Equals(kind, "operators", StringComparison.OrdinalIgnoreCase)) ops.Add(val);
                    else if (string.Equals(kind, "plans", StringComparison.OrdinalIgnoreCase)) plans.Add(val);
                    else if (string.Equals(kind, "statuses", StringComparison.OrdinalIgnoreCase)) statuses.Add(val);
                    else if (string.Equals(kind, "users", StringComparison.OrdinalIgnoreCase)) users.Add(val);
                }

                // Self-scope (MyAssets.View bez View): ograniczamy Users do bieżącego, żeby nie ułatwiać enumeracji
                if (auth.CanViewMyAssets && !auth.CanViewAll)
                {
                    users.Clear();
                    var me = await TryLoadUserDisplayNameAsync(con, auth.CurrentUserId).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(me))
                        users.Add(me.Trim());
                }

                return (true, string.Empty, new SimCardsFilterLookups
                {
                    Types = new List<string>(types),
                    Operators = new List<string>(ops),
                    Plans = new List<string>(plans),
                    Statuses = new List<string>(statuses),
                    Users = new List<string>(users),
                });
            }
            catch (Exception ex)
            {
                return (false, $"Błąd odczytu słowników SIM: {ex.Message}", new SimCardsFilterLookups());
            }
        }

        // =========================
        // Mapping + SQL builders
        // =========================

        private static SimCardEditModel MapRow(SqlDataReader r)
        {
            return new SimCardEditModel
            {
                Id = r["Id"] == DBNull.Value ? 0 : Convert.ToInt32(r["Id"]),
                Type = Convert.ToString(r["Type"]) ?? string.Empty,
                Operator = Convert.ToString(r["Operator"]) ?? string.Empty,
                Plan = Convert.ToString(r["Plan"]) ?? string.Empty,
                Status = Convert.ToString(r["Status"]) ?? string.Empty,
                User = Convert.ToString(r["User"]) ?? string.Empty,
                MobileNumber = Convert.ToString(r["MobileNumber"]) ?? string.Empty,
                SimCardNumber = Convert.ToString(r["SimCardNumber"]) ?? string.Empty,
                Note = Convert.ToString(r["Note"]) ?? string.Empty
            };
        }

        private static string BuildSelectListSql(string? whereClause, bool top1)
        {
            var whereLiteral = string.IsNullOrWhiteSpace(whereClause)
                ? "NULL"
                : "N'" + whereClause.Replace("'", "''") + "'";

            var top1Literal = top1 ? "1" : "0";

            return $@"
DECLARE @Table nvarchar(256) = N'dbo.sim_cards';
IF OBJECT_ID(@Table, 'U') IS NULL
BEGIN
  RAISERROR('Missing table dbo.sim_cards.', 16, 1);
  RETURN;
END

DECLARE @Top1 bit = {top1Literal};
DECLARE @WhereClause nvarchar(max) = {whereLiteral};

-- Tekstowe kolumny (stare warianty)
DECLARE @ColTypeText sysname = CASE WHEN COL_LENGTH(@Table,'type') IS NOT NULL THEN 'type'
                                   WHEN COL_LENGTH(@Table,'type_name') IS NOT NULL THEN 'type_name'
                                   WHEN COL_LENGTH(@Table,'sim_type') IS NOT NULL THEN 'sim_type'
                                   ELSE NULL END;

DECLARE @ColOperatorText sysname = CASE WHEN COL_LENGTH(@Table,'operator') IS NOT NULL THEN 'operator'
                                       WHEN COL_LENGTH(@Table,'operator_name') IS NOT NULL THEN 'operator_name'
                                       WHEN COL_LENGTH(@Table,'provider') IS NOT NULL THEN 'provider'
                                       ELSE NULL END;

DECLARE @ColPlanText sysname = CASE WHEN COL_LENGTH(@Table,'plan') IS NOT NULL THEN 'plan'
                                   WHEN COL_LENGTH(@Table,'plan_name') IS NOT NULL THEN 'plan_name'
                                   ELSE NULL END;

DECLARE @ColStatusText sysname = CASE WHEN COL_LENGTH(@Table,'status') IS NOT NULL THEN 'status'
                                     WHEN COL_LENGTH(@Table,'status_name') IS NOT NULL THEN 'status_name'
                                     ELSE NULL END;

DECLARE @ColUserText sysname = CASE WHEN COL_LENGTH(@Table,'user_name') IS NOT NULL THEN 'user_name'
                                   WHEN COL_LENGTH(@Table,'assigned_to') IS NOT NULL THEN 'assigned_to'
                                   WHEN COL_LENGTH(@Table,'user') IS NOT NULL THEN 'user'
                                   ELSE NULL END;

DECLARE @ColMobile sysname = CASE WHEN COL_LENGTH(@Table,'mobile_number') IS NOT NULL THEN 'mobile_number'
                                 WHEN COL_LENGTH(@Table,'phone_number') IS NOT NULL THEN 'phone_number'
                                 WHEN COL_LENGTH(@Table,'msisdn') IS NOT NULL THEN 'msisdn'
                                 ELSE NULL END;

DECLARE @ColSim sysname = CASE WHEN COL_LENGTH(@Table,'sim_card_number') IS NOT NULL THEN 'sim_card_number'
                              WHEN COL_LENGTH(@Table,'sim_number') IS NOT NULL THEN 'sim_number'
                              WHEN COL_LENGTH(@Table,'sim') IS NOT NULL THEN 'sim'
                              ELSE NULL END;

DECLARE @HasNote bit = CASE WHEN COL_LENGTH(@Table,'note') IS NOT NULL THEN 1 ELSE 0 END;

-- FK do słowników
DECLARE @HasTypeId bit = CASE WHEN COL_LENGTH(@Table,'sim_card_type_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasOpId   bit = CASE WHEN COL_LENGTH(@Table,'sim_card_operator_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasPlanId bit = CASE WHEN COL_LENGTH(@Table,'sim_card_plan_id') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @HasStId   bit = CASE WHEN COL_LENGTH(@Table,'sim_card_status_id') IS NOT NULL THEN 1 ELSE 0 END;

-- Tabele słownikowe (różne nazwy operatora obsłużone)
DECLARE @TblTypes sysname = CASE WHEN OBJECT_ID(N'dbo.sim_card_types','U') IS NOT NULL THEN N'dbo.sim_card_types' ELSE NULL END;
DECLARE @TblPlans sysname = CASE WHEN OBJECT_ID(N'dbo.sim_card_plans','U') IS NOT NULL THEN N'dbo.sim_card_plans' ELSE NULL END;
DECLARE @TblStatuses sysname = CASE WHEN OBJECT_ID(N'dbo.sim_card_statuses','U') IS NOT NULL THEN N'dbo.sim_card_statuses' ELSE NULL END;

DECLARE @TblOperators sysname = CASE
    WHEN OBJECT_ID(N'dbo.sim_card_operator','U') IS NOT NULL THEN N'dbo.sim_card_operator'
    WHEN OBJECT_ID(N'dbo.sim_card_operators','U') IS NOT NULL THEN N'dbo.sim_card_operators'
    ELSE NULL
END;

-- Kolumny nazw w słownikach
DECLARE @ColTypeName sysname = CASE
    WHEN @TblTypes IS NOT NULL AND COL_LENGTH(@TblTypes, 'sim_card_type_name') IS NOT NULL THEN 'sim_card_type_name'
    WHEN @TblTypes IS NOT NULL AND COL_LENGTH(@TblTypes, 'name') IS NOT NULL THEN 'name'
    WHEN @TblTypes IS NOT NULL AND COL_LENGTH(@TblTypes, 'title') IS NOT NULL THEN 'title'
    ELSE NULL
END;

DECLARE @ColPlanName sysname = CASE
    WHEN @TblPlans IS NOT NULL AND COL_LENGTH(@TblPlans, 'sim_card_plan_name') IS NOT NULL THEN 'sim_card_plan_name'
    WHEN @TblPlans IS NOT NULL AND COL_LENGTH(@TblPlans, 'name') IS NOT NULL THEN 'name'
    WHEN @TblPlans IS NOT NULL AND COL_LENGTH(@TblPlans, 'title') IS NOT NULL THEN 'title'
    ELSE NULL
END;

DECLARE @ColStatusName sysname = CASE
    WHEN @TblStatuses IS NOT NULL AND COL_LENGTH(@TblStatuses, 'sim_card_status_name') IS NOT NULL THEN 'sim_card_status_name'
    WHEN @TblStatuses IS NOT NULL AND COL_LENGTH(@TblStatuses, 'name') IS NOT NULL THEN 'name'
    WHEN @TblStatuses IS NOT NULL AND COL_LENGTH(@TblStatuses, 'title') IS NOT NULL THEN 'title'
    ELSE NULL
END;

DECLARE @ColOperatorName sysname = CASE
    WHEN @TblOperators IS NOT NULL AND COL_LENGTH(@TblOperators, 'sim_card_operator_name') IS NOT NULL THEN 'sim_card_operator_name'
    WHEN @TblOperators IS NOT NULL AND COL_LENGTH(@TblOperators, 'name') IS NOT NULL THEN 'name'
    WHEN @TblOperators IS NOT NULL AND COL_LENGTH(@TblOperators, 'title') IS NOT NULL THEN 'title'
    ELSE NULL
END;

DECLARE @Top nvarchar(20) = CASE WHEN @Top1 = 1 THEN N'TOP (1) ' ELSE N'' END;
DECLARE @W nvarchar(max) = NULLIF(LTRIM(RTRIM(@WhereClause)), N'');

-- Priorytet: tekstowa kolumna w sim_cards, a jeśli jej nie ma to słownik przez FK
DECLARE @ExprType nvarchar(800) =
    CASE
        WHEN @ColTypeText IS NOT NULL THEN N'ISNULL(CAST(s.' + QUOTENAME(@ColTypeText) + N' AS nvarchar(255)), N'''')'
        WHEN @HasTypeId = 1 AND @TblTypes IS NOT NULL AND @ColTypeName IS NOT NULL THEN N'ISNULL(CAST(t.' + QUOTENAME(@ColTypeName) + N' AS nvarchar(255)), N'''')'
        ELSE N'CAST(N'''' AS nvarchar(255))'
    END;

DECLARE @ExprOperator nvarchar(800) =
    CASE
        WHEN @ColOperatorText IS NOT NULL THEN N'ISNULL(CAST(s.' + QUOTENAME(@ColOperatorText) + N' AS nvarchar(255)), N'''')'
        WHEN @HasOpId = 1 AND @TblOperators IS NOT NULL AND @ColOperatorName IS NOT NULL THEN N'ISNULL(CAST(o.' + QUOTENAME(@ColOperatorName) + N' AS nvarchar(255)), N'''')'
        ELSE N'CAST(N'''' AS nvarchar(255))'
    END;

DECLARE @ExprPlan nvarchar(800) =
    CASE
        WHEN @ColPlanText IS NOT NULL THEN N'ISNULL(CAST(s.' + QUOTENAME(@ColPlanText) + N' AS nvarchar(255)), N'''')'
        WHEN @HasPlanId = 1 AND @TblPlans IS NOT NULL AND @ColPlanName IS NOT NULL THEN N'ISNULL(CAST(p.' + QUOTENAME(@ColPlanName) + N' AS nvarchar(255)), N'''')'
        ELSE N'CAST(N'''' AS nvarchar(255))'
    END;

DECLARE @ExprStatus nvarchar(800) =
    CASE
        WHEN @ColStatusText IS NOT NULL THEN N'ISNULL(CAST(s.' + QUOTENAME(@ColStatusText) + N' AS nvarchar(255)), N'''')'
        WHEN @HasStId = 1 AND @TblStatuses IS NOT NULL AND @ColStatusName IS NOT NULL THEN N'ISNULL(CAST(st.' + QUOTENAME(@ColStatusName) + N' AS nvarchar(255)), N'''')'
        ELSE N'CAST(N'''' AS nvarchar(255))'
    END;

DECLARE @ExprUser nvarchar(800) =
    CASE
        WHEN @ColUserText IS NOT NULL THEN N'ISNULL(CAST(s.' + QUOTENAME(@ColUserText) + N' AS nvarchar(255)), N'''')'
        ELSE N'CAST(N'''' AS nvarchar(255))'
    END;

DECLARE @ExprMobile nvarchar(800) =
    CASE WHEN @ColMobile IS NULL THEN N'CAST(N'''' AS nvarchar(255))'
         ELSE N'ISNULL(CAST(s.' + QUOTENAME(@ColMobile) + N' AS nvarchar(255)), N'''')' END;

DECLARE @ExprSim nvarchar(800) =
    CASE WHEN @ColSim IS NULL THEN N'CAST(N'''' AS nvarchar(255))'
         ELSE N'ISNULL(CAST(s.' + QUOTENAME(@ColSim) + N' AS nvarchar(255)), N'''')' END;

DECLARE @ExprNote nvarchar(800) =
    CASE WHEN @HasNote = 1 THEN N'ISNULL(CAST(s.[note] AS nvarchar(max)), N'''')'
         ELSE N'CAST(N'''' AS nvarchar(max))' END;

DECLARE @JoinTypes nvarchar(max) =
    CASE WHEN @HasTypeId = 1 AND @TblTypes IS NOT NULL AND @ColTypeName IS NOT NULL
         THEN N' LEFT JOIN dbo.sim_card_types t ON t.id = s.sim_card_type_id ' ELSE N'' END;

DECLARE @JoinPlans nvarchar(max) =
    CASE WHEN @HasPlanId = 1 AND @TblPlans IS NOT NULL AND @ColPlanName IS NOT NULL
         THEN N' LEFT JOIN dbo.sim_card_plans p ON p.id = s.sim_card_plan_id ' ELSE N'' END;

DECLARE @JoinStatuses nvarchar(max) =
    CASE WHEN @HasStId = 1 AND @TblStatuses IS NOT NULL AND @ColStatusName IS NOT NULL
         THEN N' LEFT JOIN dbo.sim_card_statuses st ON st.id = s.sim_card_status_id ' ELSE N'' END;

DECLARE @JoinOperators nvarchar(max) =
    CASE WHEN @HasOpId = 1 AND @TblOperators IS NOT NULL AND @ColOperatorName IS NOT NULL
         THEN N' LEFT JOIN ' + @TblOperators + N' o ON o.id = s.sim_card_operator_id ' ELSE N'' END;

DECLARE @Sql nvarchar(max) = N'
SELECT ' + @Top + N'
  Id = s.id,
  [Type]        = ' + @ExprType + N',
  [Operator]    = ' + @ExprOperator + N',
  [Plan]        = ' + @ExprPlan + N',
  [Status]      = ' + @ExprStatus + N',
  [User]        = ' + @ExprUser + N',
  MobileNumber  = ' + @ExprMobile + N',
  SimCardNumber = ' + @ExprSim + N',
  Note          = ' + @ExprNote + N'
FROM dbo.sim_cards s
' + @JoinTypes + @JoinOperators + @JoinPlans + @JoinStatuses + N'
' + CASE WHEN @W IS NULL THEN N'' ELSE N'WHERE ' + @W END + N'
ORDER BY s.id DESC;';

EXEC sp_executesql
  @Sql,
  N'@Id int, @UserId int',
  @Id = @Id,
  @UserId = @UserId;
";
        }

        private static string BuildLookupsSql()
        {
            return @"
-- Lookups powinny pochodzić ze słowników (dbo.sim_card_* + dbo.users), a nie z dbo.sim_cards,
-- bo inaczej lista jest ograniczona do wartości występujących już w danych SIM.

DECLARE @TblOperators sysname = CASE
    WHEN OBJECT_ID(N'dbo.sim_card_operator', 'U') IS NOT NULL THEN N'dbo.sim_card_operator'
    WHEN OBJECT_ID(N'dbo.sim_card_operators', 'U') IS NOT NULL THEN N'dbo.sim_card_operators'
    ELSE NULL
END;

-- Kolumny nazw w słownikach (obsługa różnych schematów)
DECLARE @ColTypeName sysname = CASE
    WHEN OBJECT_ID(N'dbo.sim_card_types', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_types', 'sim_card_type_name') IS NOT NULL THEN N'sim_card_type_name'
    WHEN OBJECT_ID(N'dbo.sim_card_types', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_types', 'name') IS NOT NULL THEN N'name'
    WHEN OBJECT_ID(N'dbo.sim_card_types', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_types', 'title') IS NOT NULL THEN N'title'
    ELSE NULL
END;

DECLARE @ColPlanName sysname = CASE
    WHEN OBJECT_ID(N'dbo.sim_card_plans', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_plans', 'sim_card_plan_name') IS NOT NULL THEN N'sim_card_plan_name'
    WHEN OBJECT_ID(N'dbo.sim_card_plans', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_plans', 'name') IS NOT NULL THEN N'name'
    WHEN OBJECT_ID(N'dbo.sim_card_plans', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_plans', 'title') IS NOT NULL THEN N'title'
    ELSE NULL
END;

DECLARE @ColStatusName sysname = CASE
    WHEN OBJECT_ID(N'dbo.sim_card_statuses', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_statuses', 'sim_card_status_name') IS NOT NULL THEN N'sim_card_status_name'
    WHEN OBJECT_ID(N'dbo.sim_card_statuses', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_statuses', 'name') IS NOT NULL THEN N'name'
    WHEN OBJECT_ID(N'dbo.sim_card_statuses', 'U') IS NOT NULL AND COL_LENGTH(N'dbo.sim_card_statuses', 'title') IS NOT NULL THEN N'title'
    ELSE NULL
END;

DECLARE @ColOperatorName sysname = CASE
    WHEN @TblOperators IS NOT NULL AND COL_LENGTH(@TblOperators, 'sim_card_operator_name') IS NOT NULL THEN N'sim_card_operator_name'
    WHEN @TblOperators IS NOT NULL AND COL_LENGTH(@TblOperators, 'name') IS NOT NULL THEN N'name'
    WHEN @TblOperators IS NOT NULL AND COL_LENGTH(@TblOperators, 'title') IS NOT NULL THEN N'title'
    ELSE NULL
END;

-- Users: wybór najlepszej kolumny prezentacyjnej
DECLARE @UserDisplayExpr nvarchar(400) = NULL;

IF OBJECT_ID(N'dbo.users', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.users', 'DisplayName') IS NOT NULL
        SET @UserDisplayExpr = N'LTRIM(RTRIM(CAST(u.DisplayName AS nvarchar(255))))';
    ELSE IF COL_LENGTH(N'dbo.users', 'display_name') IS NOT NULL
        SET @UserDisplayExpr = N'LTRIM(RTRIM(CAST(u.display_name AS nvarchar(255))))';
    ELSE IF COL_LENGTH(N'dbo.users', 'FullName') IS NOT NULL
        SET @UserDisplayExpr = N'LTRIM(RTRIM(CAST(u.FullName AS nvarchar(255))))';
    ELSE IF COL_LENGTH(N'dbo.users', 'SamAccountName') IS NOT NULL
        SET @UserDisplayExpr = N'LTRIM(RTRIM(CAST(u.SamAccountName AS nvarchar(255))))';
    ELSE IF COL_LENGTH(N'dbo.users', 'samaccountname') IS NOT NULL
        SET @UserDisplayExpr = N'LTRIM(RTRIM(CAST(u.samaccountname AS nvarchar(255))))';
    ELSE IF COL_LENGTH(N'dbo.users', 'Email') IS NOT NULL
        SET @UserDisplayExpr = N'LTRIM(RTRIM(CAST(u.Email AS nvarchar(255))))';
END;

DECLARE @Sql nvarchar(max) = N'';

-- TYPES
IF OBJECT_ID(N'dbo.sim_card_types', 'U') IS NOT NULL AND @ColTypeName IS NOT NULL
BEGIN
    SET @Sql += N'
SELECT Kind = N''types'',
       Val  = LTRIM(RTRIM(CAST(t.' + QUOTENAME(@ColTypeName) + N' AS nvarchar(255))))
FROM dbo.sim_card_types t
WHERE NULLIF(LTRIM(RTRIM(CAST(t.' + QUOTENAME(@ColTypeName) + N' AS nvarchar(255)))), N'''') IS NOT NULL
';
END
ELSE
BEGIN
    SET @Sql += N'
SELECT Kind = N''types'',
       Val  = CAST(N'''' AS nvarchar(255))
WHERE 1 = 0
';
END;

-- OPERATORS
IF @TblOperators IS NOT NULL AND @ColOperatorName IS NOT NULL
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''operators'',
       Val  = LTRIM(RTRIM(CAST(o.' + QUOTENAME(@ColOperatorName) + N' AS nvarchar(255))))
FROM ' + @TblOperators + N' o
WHERE NULLIF(LTRIM(RTRIM(CAST(o.' + QUOTENAME(@ColOperatorName) + N' AS nvarchar(255)))), N'''') IS NOT NULL
';
END
ELSE
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''operators'',
       Val  = CAST(N'''' AS nvarchar(255))
WHERE 1 = 0
';
END;

-- PLANS
IF OBJECT_ID(N'dbo.sim_card_plans', 'U') IS NOT NULL AND @ColPlanName IS NOT NULL
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''plans'',
       Val  = LTRIM(RTRIM(CAST(p.' + QUOTENAME(@ColPlanName) + N' AS nvarchar(255))))
FROM dbo.sim_card_plans p
WHERE NULLIF(LTRIM(RTRIM(CAST(p.' + QUOTENAME(@ColPlanName) + N' AS nvarchar(255)))), N'''') IS NOT NULL
';
END
ELSE
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''plans'',
       Val  = CAST(N'''' AS nvarchar(255))
WHERE 1 = 0
';
END;

-- STATUSES
IF OBJECT_ID(N'dbo.sim_card_statuses', 'U') IS NOT NULL AND @ColStatusName IS NOT NULL
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''statuses'',
       Val  = LTRIM(RTRIM(CAST(st.' + QUOTENAME(@ColStatusName) + N' AS nvarchar(255))))
FROM dbo.sim_card_statuses st
WHERE NULLIF(LTRIM(RTRIM(CAST(st.' + QUOTENAME(@ColStatusName) + N' AS nvarchar(255)))), N'''') IS NOT NULL
';
END
ELSE
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''statuses'',
       Val  = CAST(N'''' AS nvarchar(255))
WHERE 1 = 0
';
END;

-- USERS
IF @UserDisplayExpr IS NOT NULL
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''users'',
       Val  = ' + @UserDisplayExpr + N'
FROM dbo.users u
WHERE NULLIF(' + @UserDisplayExpr + N', N'''') IS NOT NULL
';
END
ELSE
BEGIN
    SET @Sql += N'
UNION ALL
SELECT Kind = N''users'',
       Val  = CAST(N'''' AS nvarchar(255))
WHERE 1 = 0
';
END;

-- DISTINCT + sort
DECLARE @Final nvarchar(max) = N'
SELECT Kind, Val
FROM (
' + @Sql + N'
) x
WHERE NULLIF(LTRIM(RTRIM(Val)), N'''') IS NOT NULL
GROUP BY Kind, Val
ORDER BY Kind, Val;
';

EXEC sp_executesql @Final;
";
        }

        // =========================
        // Ownership helpers (anti-enumeration)
        // =========================

        private static async Task<string> BuildUserOwnershipWhereClauseAsync(SqlConnection con, int userId)
        {
            var whereBuilderSql = @"
DECLARE @UserIdCol sysname = CASE
    WHEN COL_LENGTH(N'dbo.sim_cards', 'user_id') IS NOT NULL THEN 'user_id'
    WHEN COL_LENGTH(N'dbo.sim_cards', 'assigned_to_user_id') IS NOT NULL THEN 'assigned_to_user_id'
    WHEN COL_LENGTH(N'dbo.sim_cards', 'assigned_user_id') IS NOT NULL THEN 'assigned_user_id'
    WHEN COL_LENGTH(N'dbo.sim_cards', 'userId') IS NOT NULL THEN 'userId'
    ELSE NULL
END;

DECLARE @UserNameCol sysname = CASE
    WHEN COL_LENGTH(N'dbo.sim_cards', 'user_name') IS NOT NULL THEN 'user_name'
    WHEN COL_LENGTH(N'dbo.sim_cards', 'assigned_to') IS NOT NULL THEN 'assigned_to'
    WHEN COL_LENGTH(N'dbo.sim_cards', 'user') IS NOT NULL THEN 'user'
    ELSE NULL
END;

DECLARE @Where nvarchar(max) = N'';

IF @UserIdCol IS NOT NULL
BEGIN
    SET @Where = N's.' + QUOTENAME(@UserIdCol) + N' = @UserId';
END
ELSE IF @UserNameCol IS NOT NULL
     AND OBJECT_ID(N'dbo.users', 'U') IS NOT NULL
     AND (COL_LENGTH(N'dbo.users', 'DisplayName') IS NOT NULL OR COL_LENGTH(N'dbo.users', 'display_name') IS NOT NULL)
BEGIN
    DECLARE @UserDisplayExpr nvarchar(300) =
        CASE
            WHEN COL_LENGTH(N'dbo.users', 'DisplayName') IS NOT NULL THEN N'LTRIM(RTRIM(CAST(u.DisplayName AS nvarchar(255))))'
            ELSE N'LTRIM(RTRIM(CAST(u.display_name AS nvarchar(255))))'
        END;

    SET @Where = N'EXISTS (
        SELECT 1
        FROM dbo.users u
        WHERE u.id = @UserId
          AND ' + @UserDisplayExpr + N' =
              LTRIM(RTRIM(CAST(s.' + QUOTENAME(@UserNameCol) + N' AS nvarchar(255))))
    )';
END
ELSE
BEGIN
    SET @Where = N'1 = 0';
END;

SELECT @Where AS WhereSql;
";

            using var cmdWhere = new SqlCommand(whereBuilderSql, con) { CommandType = CommandType.Text };
            cmdWhere.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });

            var obj = await cmdWhere.ExecuteScalarAsync().ConfigureAwait(false);
            var whereSql = Convert.ToString(obj) ?? "1 = 0";

            whereSql = (whereSql ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(whereSql) ? "1 = 0" : whereSql;
        }

        private static async Task<string> TryLoadUserDisplayNameAsync(SqlConnection con, int userId)
        {
            if (userId <= 0)
                return string.Empty;

            // Minimalny, bezpieczny odczyt nazwy bieżącego usera do filtrów w self-scope
            if (con == null)
                return string.Empty;

            const string sql = @"
IF OBJECT_ID(N'dbo.users', 'U') IS NULL
BEGIN
    SELECT CAST(N'' AS nvarchar(255));
    RETURN;
END

DECLARE @Expr nvarchar(400) = NULL;

IF COL_LENGTH(N'dbo.users', 'DisplayName') IS NOT NULL
    SET @Expr = N'LTRIM(RTRIM(CAST(u.DisplayName AS nvarchar(255))))';
ELSE IF COL_LENGTH(N'dbo.users', 'display_name') IS NOT NULL
    SET @Expr = N'LTRIM(RTRIM(CAST(u.display_name AS nvarchar(255))))';
ELSE IF COL_LENGTH(N'dbo.users', 'FullName') IS NOT NULL
    SET @Expr = N'LTRIM(RTRIM(CAST(u.FullName AS nvarchar(255))))';
ELSE IF COL_LENGTH(N'dbo.users', 'SamAccountName') IS NOT NULL
    SET @Expr = N'LTRIM(RTRIM(CAST(u.SamAccountName AS nvarchar(255))))';
ELSE IF COL_LENGTH(N'dbo.users', 'samaccountname') IS NOT NULL
    SET @Expr = N'LTRIM(RTRIM(CAST(u.samaccountname AS nvarchar(255))))';
ELSE IF COL_LENGTH(N'dbo.users', 'Email') IS NOT NULL
    SET @Expr = N'LTRIM(RTRIM(CAST(u.Email AS nvarchar(255))))';

IF @Expr IS NULL
BEGIN
    SELECT CAST(N'' AS nvarchar(255));
    RETURN;
END

DECLARE @Sql nvarchar(max) = N'SELECT ' + @Expr + N' FROM dbo.users u WHERE u.id = @Id;';
EXEC sp_executesql @Sql, N'@Id int', @Id = @Id;
";

            using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };
            cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = userId });

            var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return (Convert.ToString(obj) ?? string.Empty).Trim();
        }
    }
}
