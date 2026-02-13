// File: Services/Raports/GlovesReportService.cs
// Description: Serwis raportu pobrań rękawic (TEMREX) - KPI, trend, ranking działów, top users, typy bez rozmiarów.
// Version: 1.10
// Created: 2026-02-11
// Updated: 2026-02-12
// Change log:
//   - 1.00 (2026-02-11) Initial version.
//   - 1.01 (2026-02-11) ByUser: zwracamy Username + CardNumber + Department (UG01) + Qty + Transactions.
//   - 1.02 (2026-02-12) FIX: dopasowanie do aktualnej struktury TEMREX.
//   - 1.10 (2026-02-12) MANAGER VIEW: Department filter + ranking działów + agregacja typów bez rozmiarów (ByItemType).

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ITManager.Models.Reports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ITManager.Services.Reports;

public sealed class GlovesReportService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<GlovesReportService> _logger;

    // Normalizacja typu rękawic: usuwamy rozmiary.
    // Cel: "typ bez rozmiaru" ma być stabilny i użyteczny dla managerów.
    private static readonly Regex RxSizeWords = new(
        @"\b(rozmiar|rozm\.|size)\b\s*[:\-]?\s*([0-9]{1,2}([.,][0-9])?|x{0,2}s|s|m|l|xl|xxl|xxxl)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RxParenSize = new(
        @"\(([^)]*?\b(rozmiar|rozm\.|size)\b[^)]*?)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RxBareSizes = new(
        @"\b(xx?xl|xxxl|xl|l|m|s|xs)\b|\b(6\.?5|7\.?0?|7\.?5|8\.?0?|8\.?5|9\.?0?|9\.?5|10\.?0?|10\.?5|11\.?0?|12\.?0?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RxMultiSpaces = new(
        @"\s{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public GlovesReportService(IConfiguration cfg, ILogger<GlovesReportService> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    private string GetTemrexConnectionString()
    {
        // wspieramy oba warianty klucza, bo w appsettings często bywa "Temrex" vs "TEMREX"
        var cs = _cfg.GetConnectionString("TEMREX");
        if (string.IsNullOrWhiteSpace(cs))
            cs = _cfg.GetConnectionString("Temrex");

        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Brak connection stringa: ConnectionStrings:TEMREX (lub Temrex)");

        return cs;
    }

    public async Task<List<GlovesItemOption>> GetAvailableItemsAsync(CancellationToken ct = default)
    {
        var cs = GetTemrexConnectionString();

        const string sql = @"
SELECT TOP 500
    LTRIM(RTRIM(tl.ITEMNUMBER)) AS ItemNumber,
    ISNULL(NULLIF(LTRIM(RTRIM(i.DESCR)), ''), '') AS ItemDescr
FROM dbo.TransactionLog tl
LEFT JOIN dbo.Items i
    ON i.ITEMNUMBER = tl.ITEMNUMBER
WHERE tl.TRANSCODE = 'WN'
  AND tl.ITEMNUMBER IS NOT NULL
  AND LTRIM(RTRIM(tl.ITEMNUMBER)) <> ''
GROUP BY tl.ITEMNUMBER, i.DESCR
ORDER BY ItemNumber;";

        var result = new List<GlovesItemOption>();

        await using var con = new SqlConnection(cs);
        await con.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new GlovesItemOption
            {
                ItemNumber = rd.IsDBNull(0) ? "" : rd.GetString(0),
                ItemDescr = rd.IsDBNull(1) ? "" : rd.GetString(1)
            });
        }

        return result;
    }

    public async Task<List<GlovesVmOption>> GetAvailableVendingMachinesAsync(CancellationToken ct = default)
    {
        var cs = GetTemrexConnectionString();

        const string sql = @"
SELECT TOP 200
    ISNULL(tl.VMID, 0) AS VmId,
    ISNULL(NULLIF(LTRIM(RTRIM(vm.DESCR)), ''), '') AS VmDescr
FROM dbo.TransactionLog tl
LEFT JOIN dbo.VendingMachines vm
    ON vm.VMID = tl.VMID
WHERE tl.TRANSCODE = 'WN'
  AND tl.VMID IS NOT NULL
GROUP BY tl.VMID, vm.DESCR
ORDER BY VmId;";

        var result = new List<GlovesVmOption>();

        await using var con = new SqlConnection(cs);
        await con.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new GlovesVmOption
            {
                VmId = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture),
                VmDescr = rd.IsDBNull(1) ? "" : rd.GetString(1)
            });
        }

        return result;
    }

    public async Task<List<GlovesDepartmentOption>> GetAvailableDepartmentsAsync(CancellationToken ct = default)
    {
        var cs = GetTemrexConnectionString();

        const string sql = @"
SELECT TOP 200
    ISNULL(NULLIF(LTRIM(RTRIM(tl.USERGROUP01)), ''), '') AS Department
FROM dbo.TransactionLog tl
WHERE tl.TRANSCODE = 'WN'
  AND tl.USERGROUP01 IS NOT NULL
GROUP BY tl.USERGROUP01
ORDER BY Department;";

        var result = new List<GlovesDepartmentOption>();

        await using var con = new SqlConnection(cs);
        await con.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, con) { CommandType = CommandType.Text };

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var dept = rd.IsDBNull(0) ? "" : rd.GetString(0);
            if (!string.IsNullOrWhiteSpace(dept))
                result.Add(new GlovesDepartmentOption { Department = dept });
        }

        return result;
    }

    public async Task<GlovesReportResult> GetReportAsync(GlovesReportRequest req, CancellationToken ct = default)
    {
        if (req is null)
            throw new ArgumentNullException(nameof(req));

        var cs = GetTemrexConnectionString();

        var dateFrom = req.DateFrom.Date;
        var dateTo = req.DateTo.Date;

        if (dateTo < dateFrom)
            (dateFrom, dateTo) = (dateTo, dateFrom);

        var toExclusive = dateTo.AddDays(1);

        var result = new GlovesReportResult();

        await using var con = new SqlConnection(cs);
        await con.OpenAsync(ct);

        // KPI
        try
        {
            const string sqlKpi = @"
SELECT
    COUNT(*) AS TransactionsCount,
    COUNT(DISTINCT LTRIM(RTRIM(tl.USERNUMBER))) AS UniqueUsersCount,
    SUM(ABS(ISNULL(tl.QTY,0))) AS TotalQty
FROM dbo.TransactionLog tl
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/;";

            await using var cmd = new SqlCommand(ApplyFilters(sqlKpi, req), con);

            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = dateFrom;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;
            AddFilterParameters(cmd, req);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                result.Kpis.TransactionsCount = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture);
                result.Kpis.UniqueUsersCount = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture);
                result.Kpis.TotalQty = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture);

                var days = Math.Max(1, (dateTo - dateFrom).Days + 1);
                result.Kpis.AvgQtyPerDay = Math.Round((decimal)result.Kpis.TotalQty / days, 2);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Reports.Gloves] KPI query failed. From={From} To={To}", dateFrom, toExclusive);
            throw;
        }

        // Compare KPI
        if (req.CompareWithPreviousPeriod)
        {
            var days = Math.Max(1, (dateTo - dateFrom).Days + 1);
            var prevTo = dateFrom.AddDays(-1);
            var prevFrom = prevTo.AddDays(-(days - 1));
            var prevToExclusive = prevTo.AddDays(1);

            const string sqlPrev = @"
SELECT
    COUNT(*) AS TransactionsCount,
    COUNT(DISTINCT LTRIM(RTRIM(tl.USERNUMBER))) AS UniqueUsersCount,
    SUM(ABS(ISNULL(tl.QTY,0))) AS TotalQty
FROM dbo.TransactionLog tl
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/;";

            await using var cmd = new SqlCommand(ApplyFilters(sqlPrev, req), con);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = prevFrom;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = prevToExclusive;
            AddFilterParameters(cmd, req);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                result.Kpis.PrevTransactionsCount = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture);
                result.Kpis.PrevUniqueUsersCount = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture);
                result.Kpis.PrevTotalQty = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture);
                result.Kpis.PrevAvgQtyPerDay = Math.Round((decimal)result.Kpis.PrevTotalQty / days, 2);
            }
        }

        // Trend
        result.Trend = await LoadTrendAsync(con, req, dateFrom, toExclusive, ct);

        // Ranking działów (must have)
        {
            const string sql = @"
SELECT TOP 50
    ISNULL(NULLIF(LTRIM(RTRIM(tl.USERGROUP01)), ''), '(unknown)') AS Department,
    SUM(ABS(ISNULL(tl.QTY,0))) AS Qty,
    COUNT(*) AS Transactions,
    COUNT(DISTINCT LTRIM(RTRIM(tl.USERNUMBER))) AS UniqueUsers
FROM dbo.TransactionLog tl
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/
GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(tl.USERGROUP01)), ''), '(unknown)')
ORDER BY Qty DESC, Transactions DESC;";

            await using var cmd = new SqlCommand(ApplyFilters(sql, req), con);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = dateFrom;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;
            AddFilterParameters(cmd, req);

            var rows = new List<GlovesByDepartmentRow>();

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                rows.Add(new GlovesByDepartmentRow
                {
                    Department = rd.IsDBNull(0) ? "" : rd.GetString(0),
                    Qty = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture),
                    Transactions = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture),
                    UniqueUsers = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture),
                });
            }

            result.ByDepartment = rows;
        }

        // Top ByUser
        {
            const string sql = @"
SELECT TOP 50
    ISNULL(NULLIF(LTRIM(RTRIM(u.DESCR)), ''), '') AS UserName,
    ISNULL(NULLIF(LTRIM(RTRIM(tl.USERNUMBER)), ''), '(unknown)') AS CardNumber,
    ISNULL(MAX(NULLIF(LTRIM(RTRIM(tl.USERGROUP01)), '')), '') AS Department,
    SUM(ABS(ISNULL(tl.QTY,0))) AS Qty,
    COUNT(*) AS Transactions
FROM dbo.TransactionLog tl
LEFT JOIN dbo.Users u
    ON u.USERNUMBER = tl.USERNUMBER
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/
GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(tl.USERNUMBER)), ''), '(unknown)'), u.DESCR
ORDER BY Qty DESC, Transactions DESC;";

            await using var cmd = new SqlCommand(ApplyFilters(sql, req), con);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = dateFrom;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;
            AddFilterParameters(cmd, req);

            var rows = new List<GlovesByUserRow>();

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                rows.Add(new GlovesByUserRow
                {
                    UserName = rd.IsDBNull(0) ? "" : rd.GetString(0),
                    CardNumber = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Department = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    Qty = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture),
                    Transactions = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture),
                });
            }

            result.ByUser = rows;
        }

        // ByItem (SKU)
        {
            const string sql = @"
SELECT TOP 200
    LTRIM(RTRIM(tl.ITEMNUMBER)) AS ItemNumber,
    ISNULL(NULLIF(LTRIM(RTRIM(i.DESCR)), ''), '') AS ItemDescr,
    SUM(ABS(ISNULL(tl.QTY,0))) AS Qty,
    COUNT(*) AS Transactions
FROM dbo.TransactionLog tl
LEFT JOIN dbo.Items i
    ON i.ITEMNUMBER = tl.ITEMNUMBER
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/
GROUP BY tl.ITEMNUMBER, i.DESCR
ORDER BY Qty DESC, Transactions DESC;";

            await using var cmd = new SqlCommand(ApplyFilters(sql, req), con);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = dateFrom;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;
            AddFilterParameters(cmd, req);

            var rows = new List<GlovesByItemRow>();

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                rows.Add(new GlovesByItemRow
                {
                    ItemNumber = rd.IsDBNull(0) ? "" : rd.GetString(0),
                    ItemDescr = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Qty = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture),
                    Transactions = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture),
                });
            }

            result.ByItem = rows;
        }

        // Agregacja typu bez rozmiarów
        result.ByItemType = result.ByItem
            .Select(x => new
            {
                Type = NormalizeGloveType(x.ItemDescr),
                x.Qty,
                x.Transactions
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Type))
            .GroupBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GlovesByItemTypeRow
            {
                GloveType = g.Key,
                Qty = g.Sum(x => x.Qty),
                Transactions = g.Sum(x => x.Transactions)
            })
            .OrderByDescending(x => x.Qty)
            .ThenByDescending(x => x.Transactions)
            .Take(50)
            .ToList();

        // Top ByVm
        {
            const string sql = @"
SELECT TOP 50
    ISNULL(tl.VMID, 0) AS VmId,
    ISNULL(NULLIF(LTRIM(RTRIM(vm.DESCR)), ''), '') AS VmDescr,
    SUM(ABS(ISNULL(tl.QTY,0))) AS Qty,
    COUNT(*) AS Transactions
FROM dbo.TransactionLog tl
LEFT JOIN dbo.VendingMachines vm
    ON vm.VMID = tl.VMID
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/
GROUP BY tl.VMID, vm.DESCR
ORDER BY Qty DESC, Transactions DESC;";

            await using var cmd = new SqlCommand(ApplyFilters(sql, req), con);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = dateFrom;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;
            AddFilterParameters(cmd, req);

            var rows = new List<GlovesByVmRow>();

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                rows.Add(new GlovesByVmRow
                {
                    VmId = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture),
                    VmDescr = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Qty = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture),
                    Transactions = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture),
                });
            }

            result.ByVm = rows;
        }

        return result;
    }

    private async Task<List<GlovesTrendPoint>> LoadTrendAsync(
        SqlConnection con,
        GlovesReportRequest req,
        DateTime from,
        DateTime toExclusive,
        CancellationToken ct)
    {
        var isWeek = req.TrendGranularity == GlovesTrendGranularity.Week;

        var sql = isWeek
            ? @"
SELECT
    DATEPART(YEAR, tl.TRANSTARTDATETIME) AS Y,
    DATEPART(ISO_WEEK, tl.TRANSTARTDATETIME) AS W,
    SUM(ABS(ISNULL(tl.QTY,0))) AS Qty,
    COUNT(*) AS Transactions
FROM dbo.TransactionLog tl
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/
GROUP BY DATEPART(YEAR, tl.TRANSTARTDATETIME), DATEPART(ISO_WEEK, tl.TRANSTARTDATETIME)
ORDER BY Y, W;"
            : @"
SELECT
    DATEPART(YEAR, tl.TRANSTARTDATETIME) AS Y,
    DATEPART(MONTH, tl.TRANSTARTDATETIME) AS M,
    SUM(ABS(ISNULL(tl.QTY,0))) AS Qty,
    COUNT(*) AS Transactions
FROM dbo.TransactionLog tl
WHERE tl.TRANSCODE = 'WN'
  AND tl.TRANSTARTDATETIME >= @from
  AND tl.TRANSTARTDATETIME <  @to
  /**dept**/
  /**items**/
  /**vms**/
GROUP BY DATEPART(YEAR, tl.TRANSTARTDATETIME), DATEPART(MONTH, tl.TRANSTARTDATETIME)
ORDER BY Y, M;";

        await using var cmd = new SqlCommand(ApplyFilters(sql, req), con);
        cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = from;
        cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;
        AddFilterParameters(cmd, req);

        var rows = new List<GlovesTrendPoint>();

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var y = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture);
            var p = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture);
            var qty = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture);
            var tr = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture);

            rows.Add(new GlovesTrendPoint
            {
                Label = isWeek ? $"{y}-W{p:00}" : $"{y}-{p:00}",
                SortKey = y * 100 + p,
                Qty = qty,
                Transactions = tr
            });
        }

        return rows;
    }

    private static string ApplyFilters(string sql, GlovesReportRequest req)
    {
        var deptFilter = !string.IsNullOrWhiteSpace(req.Department)
            ? " AND LTRIM(RTRIM(tl.USERGROUP01)) = @dept "
            : "";

        var itemsFilter = (req.ItemNumbers is { Count: > 0 })
            ? " AND LTRIM(RTRIM(tl.ITEMNUMBER)) IN (" + string.Join(",", req.ItemNumbers.Select((_, i) => $"@it{i}")) + ") "
            : "";

        var vmsFilter = (req.VmIds is { Count: > 0 })
            ? " AND tl.VMID IN (" + string.Join(",", req.VmIds.Select((_, i) => $"@vm{i}")) + ") "
            : "";

        return sql
            .Replace("/**dept**/", deptFilter)
            .Replace("/**items**/", itemsFilter)
            .Replace("/**vms**/", vmsFilter);
    }

    private static void AddFilterParameters(SqlCommand cmd, GlovesReportRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Department))
        {
            var p = cmd.Parameters.Add("@dept", SqlDbType.VarChar, 50);
            p.Value = req.Department.Trim();
        }

        if (req.ItemNumbers is { Count: > 0 })
        {
            for (var i = 0; i < req.ItemNumbers.Count; i++)
            {
                var p = cmd.Parameters.Add($"@it{i}", SqlDbType.VarChar, 50);
                p.Value = (req.ItemNumbers[i] ?? "").Trim();
            }
        }

        if (req.VmIds is { Count: > 0 })
        {
            for (var i = 0; i < req.VmIds.Count; i++)
            {
                var p = cmd.Parameters.Add($"@vm{i}", SqlDbType.Int);
                p.Value = req.VmIds[i];
            }
        }
    }

    private static string NormalizeGloveType(string descr)
    {
        if (string.IsNullOrWhiteSpace(descr))
            return "";

        var s = descr.Trim();

        // Usuń fragmenty o rozmiarze w nawiasach, np. "(Size 9)" albo "(rozmiar 10)"
        s = RxParenSize.Replace(s, "");

        // Usuń "size/rozmiar: X"
        s = RxSizeWords.Replace(s, "");

        // Usuń same tokeny rozmiarów i wartości liczbowe, jeśli występują jako osobne wyrazy
        s = RxBareSizes.Replace(s, "");

        // Sprzątanie separatorów, wielokrotnych spacji
        s = s.Replace("  ", " ");
        s = RxMultiSpaces.Replace(s, " ").Trim();

        s = s.Trim(' ', '-', '/', '|', ',', ';', ':');

        return s;
    }
}
