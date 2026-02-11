// File: Services/Reports/PrintsReportService.cs
// Description: Raport wydruków (drukarki) na bazie dbo.vw_printer_prints_report.
//              Lokalizacje mapowane po location_name (dbo.locations.name).
// Created: 2026-01-14
// Updated: 2026-01-14 - filtr lokalizacji po LocationName (string).
// Updated: 2026-01-14 - dodano listę drukarek (agregacja per device).
// Updated: 2026-01-15 - Sort: stabilne sortowanie drukarek (koszt DESC + hostname ASC).

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using TristoneHub.Models.Reports;

namespace TristoneHub.Services.Reports
{
    public sealed class PrintsReportService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;

        public PrintsReportService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private string GetConnStr()
            => _configuration.GetConnectionString(ConnectionStringName)
               ?? throw new InvalidOperationException("Missing connection string ITManagerConnection");

        public async Task<List<LocationButton>> GetAvailableLocationsAsync()
        {
            var result = new List<LocationButton>();

            using var conn = new SqlConnection(GetConnStr());
            await conn.OpenAsync();

            var sql = @"
SELECT
    location_name,
    COUNT(DISTINCT device_id) AS printers_count
FROM dbo.vw_printer_prints_report
GROUP BY location_name
ORDER BY location_name;";

            using var cmd = new SqlCommand(sql, conn);

            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                result.Add(new LocationButton
                {
                    LocationName = Convert.ToString(rd["location_name"]) ?? "",
                    PrintersCount = Convert.ToInt32(rd["printers_count"])
                });
            }

            return result;
        }

        public async Task<PrintsReportResult> GetReportAsync(PrintsReportRequest request)
        {
            var res = new PrintsReportResult();

            var dateFrom = request.DateFrom.Date;
            var dateTo = request.DateTo.Date;

            if (dateTo < dateFrom)
                (dateFrom, dateTo) = (dateTo, dateFrom);

            var toExclusive = dateTo.AddDays(1);

            var daysCount = (toExclusive - dateFrom).Days;
            var prevToExclusive = dateFrom;
            var prevFrom = prevToExclusive.AddDays(-daysCount);

            var locNames = request.LocationNames?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            using var conn = new SqlConnection(GetConnStr());
            await conn.OpenAsync();

            res.Kpis = await LoadKpisAsync(conn, dateFrom, toExclusive, locNames);
            res.Daily = await LoadDailyAsync(conn, dateFrom, toExclusive, locNames);
            res.Printers = await LoadPrintersAsync(conn, dateFrom, toExclusive, locNames);

            if (request.CompareWithPreviousPeriod)
            {
                var prevKpis = await LoadKpisAsync(conn, prevFrom, prevToExclusive, locNames);

                res.Kpis.PrevTotalPages = prevKpis.TotalPages;
                res.Kpis.PrevMonoPages = prevKpis.MonoPages;
                res.Kpis.PrevColorPages = prevKpis.ColorPages;

                res.Kpis.PrevTotalCost = prevKpis.TotalCost;
                res.Kpis.PrevMonoCost = prevKpis.MonoCost;
                res.Kpis.PrevColorCost = prevKpis.ColorCost;
            }

            return res;
        }

        private static string BuildLocationFilterSql(IReadOnlyList<string> locationNames)
        {
            if (locationNames == null || locationNames.Count == 0)
                return "";

            var p = new List<string>();
            for (int i = 0; i < locationNames.Count; i++)
                p.Add($"@loc{i}");

            return $" AND location_name IN ({string.Join(",", p)})";
        }

        private static void AddLocationParameters(SqlCommand cmd, IReadOnlyList<string> locationNames)
        {
            if (locationNames == null) return;

            for (int i = 0; i < locationNames.Count; i++)
                cmd.Parameters.Add($"@loc{i}", SqlDbType.NVarChar, 255).Value = locationNames[i];
        }

        private async Task<PrintsReportKpis> LoadKpisAsync(SqlConnection conn, DateTime fromInclusive, DateTime toExclusive, List<string> locationNames)
        {
            var kpis = new PrintsReportKpis();
            var locFilter = BuildLocationFilterSql(locationNames);

            var sql = $@"
SELECT
    SUM(total_pages_diff) AS total_pages,
    SUM(mono_pages_diff)  AS mono_pages,
    SUM(color_pages_diff) AS color_pages,

    SUM(CAST(mono_pages_diff AS decimal(18,4))  * ISNULL(cost_mono, 0))  AS mono_cost,
    SUM(CAST(color_pages_diff AS decimal(18,4)) * ISNULL(cost_color, 0)) AS color_cost
FROM dbo.vw_printer_prints_report
WHERE read_time >= @from AND read_time < @to
{locFilter};";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = fromInclusive;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;

            AddLocationParameters(cmd, locationNames);

            using var rd = await cmd.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                var total = rd["total_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["total_pages"]);
                var mono = rd["mono_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["mono_pages"]);
                var color = rd["color_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["color_pages"]);

                var monoCost = rd["mono_cost"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["mono_cost"]);
                var colorCost = rd["color_cost"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["color_cost"]);

                kpis.TotalPages = total;
                kpis.MonoPages = mono;
                kpis.ColorPages = color;

                kpis.MonoCost = Decimal.Round(monoCost, 2);
                kpis.ColorCost = Decimal.Round(colorCost, 2);
                kpis.TotalCost = Decimal.Round(monoCost + colorCost, 2);
            }

            return kpis;
        }

        private async Task<List<PrintsReportDayRow>> LoadDailyAsync(SqlConnection conn, DateTime fromInclusive, DateTime toExclusive, List<string> locationNames)
        {
            var rows = new List<PrintsReportDayRow>();
            var locFilter = BuildLocationFilterSql(locationNames);

            var sql = $@"
SELECT
    CAST(read_time AS date) AS [day],
    SUM(total_pages_diff) AS total_pages,
    SUM(mono_pages_diff)  AS mono_pages,
    SUM(color_pages_diff) AS color_pages,

    SUM(CAST(mono_pages_diff AS decimal(18,4))  * ISNULL(cost_mono, 0))  AS mono_cost,
    SUM(CAST(color_pages_diff AS decimal(18,4)) * ISNULL(cost_color, 0)) AS color_cost
FROM dbo.vw_printer_prints_report
WHERE read_time >= @from AND read_time < @to
{locFilter}
GROUP BY CAST(read_time AS date)
ORDER BY [day];";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = fromInclusive;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;

            AddLocationParameters(cmd, locationNames);

            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var monoCost = rd["mono_cost"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["mono_cost"]);
                var colorCost = rd["color_cost"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["color_cost"]);

                rows.Add(new PrintsReportDayRow
                {
                    Day = Convert.ToDateTime(rd["day"]),
                    TotalPages = rd["total_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["total_pages"]),
                    MonoPages = rd["mono_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["mono_pages"]),
                    ColorPages = rd["color_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["color_pages"]),
                    MonoCost = Decimal.Round(monoCost, 2),
                    ColorCost = Decimal.Round(colorCost, 2),
                    TotalCost = Decimal.Round(monoCost + colorCost, 2),
                });
            }

            return rows;
        }

        private async Task<List<PrintsReportPrinterRow>> LoadPrintersAsync(SqlConnection conn, DateTime fromInclusive, DateTime toExclusive, List<string> locationNames)
        {
            var rows = new List<PrintsReportPrinterRow>();
            var locFilter = BuildLocationFilterSql(locationNames);

            var sql = $@"
SELECT
    device_id,
    hostname,
    model,
    location_name,

    SUM(total_pages_diff) AS total_pages,
    SUM(mono_pages_diff)  AS mono_pages,
    SUM(color_pages_diff) AS color_pages,

    SUM(CAST(mono_pages_diff AS decimal(18,4))  * ISNULL(cost_mono, 0))  AS mono_cost,
    SUM(CAST(color_pages_diff AS decimal(18,4)) * ISNULL(cost_color, 0)) AS color_cost
FROM dbo.vw_printer_prints_report
WHERE read_time >= @from AND read_time < @to
{locFilter}
GROUP BY device_id, hostname, model, location_name

-- Sortowanie dla datagrida: koszt malejąco, a potem hostname rosnąco (stabilnie i przewidywalnie)
ORDER BY
    (SUM(CAST(mono_pages_diff AS decimal(18,4))  * ISNULL(cost_mono, 0)) +
     SUM(CAST(color_pages_diff AS decimal(18,4)) * ISNULL(cost_color, 0))) DESC,
    hostname ASC;";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = fromInclusive;
            cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = toExclusive;

            AddLocationParameters(cmd, locationNames);

            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var monoCost = rd["mono_cost"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["mono_cost"]);
                var colorCost = rd["color_cost"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["color_cost"]);

                rows.Add(new PrintsReportPrinterRow
                {
                    DeviceId = Convert.ToInt64(rd["device_id"]),
                    Hostname = Convert.ToString(rd["hostname"]) ?? "",
                    Model = Convert.ToString(rd["model"]) ?? "",
                    LocationName = Convert.ToString(rd["location_name"]) ?? "",

                    TotalPages = rd["total_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["total_pages"]),
                    MonoPages = rd["mono_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["mono_pages"]),
                    ColorPages = rd["color_pages"] == DBNull.Value ? 0 : Convert.ToInt32(rd["color_pages"]),

                    MonoCost = Decimal.Round(monoCost, 2),
                    ColorCost = Decimal.Round(colorCost, 2),
                    TotalCost = Decimal.Round(monoCost + colorCost, 2),
                });
            }

            return rows;
        }
    }
}
