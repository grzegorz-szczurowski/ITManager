// File: Services/Wms/WmsPartsRepository.cs
// Description: Repozytorium SQL dla modułu WMS Parts.
//              Obsługuje listę z wyszukiwaniem (server paging), odczyt po ID,
//              zapis (insert/update) oraz walidację unikalności system_number.
// Notes:
// - SQL Server 2017
// - System.Data.SqlClient
// - Connection string: ConnectionStrings:ITManagerConnection
// Version: 1.00
// Created: 2026-01-05
// Change log:
//   - 1.00: Pierwsza wersja repozytorium.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ITManager.Models.Wms;

namespace ITManager.Services.Wms;

public sealed class WmsPartsRepository
{
    private readonly string _connectionString;
    private readonly ILogger<WmsPartsRepository> _logger;

    public WmsPartsRepository(IConfiguration configuration, ILogger<WmsPartsRepository> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("ITManagerConnection")
            ?? throw new InvalidOperationException("Brak connection string: ConnectionStrings:ITManagerConnection");
    }

    public async Task<(List<WmsAssemblyPartListItemDto> Items, int TotalCount)> SearchPartsAsync(
        string? search,
        int pageIndex,
        int pageSize,
        string? sortColumn,
        bool sortDesc,
        CancellationToken ct)
    {
        var items = new List<WmsAssemblyPartListItemDto>();

        var like = BuildLike(search);
        var orderBy = BuildOrderBy(sortColumn, sortDesc);

        var sql = $@"
SELECT
    id,
    system_number,
    part_name,
    short_system_number,
    part_weight,
    max_quantity_per_container,
    unit_of_measure_id,
    unit_name,
    unit_symbol,
    note
FROM dbo.vw_wms_assembly_parts
WHERE
    (@search IS NULL OR @search = N'' OR
     system_number LIKE @like OR
     short_system_number LIKE @like OR
     part_name LIKE @like)
{orderBy}
OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;

SELECT COUNT(1)
FROM dbo.vw_wms_assembly_parts
WHERE
    (@search IS NULL OR @search = N'' OR
     system_number LIKE @like OR
     short_system_number LIKE @like OR
     part_name LIKE @like);
";

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await con.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, con);
            cmd.CommandType = CommandType.Text;

            cmd.Parameters.AddWithValue("@search", (object?)search ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@like", (object?)like ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@offset", pageIndex * pageSize);
            cmd.Parameters.AddWithValue("@pageSize", pageSize);

            int totalCount = 0;

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                items.Add(new WmsAssemblyPartListItemDto
                {
                    Id = reader.GetInt32(0),
                    SystemNumber = reader.GetString(1),
                    PartName = reader.GetString(2),
                    ShortSystemNumber = reader.IsDBNull(3) ? null : reader.GetString(3),
                    PartWeight = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    MaxQuantityPerContainer = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    UnitOfMeasureId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    UnitName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    UnitSymbol = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Note = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }

            if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
            {
                totalCount = reader.GetInt32(0);
            }

            return (items, totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WMS Parts: błąd podczas SearchPartsAsync");
            return (new List<WmsAssemblyPartListItemDto>(), 0);
        }
    }

    public async Task<WmsAssemblyPartEditDto?> GetPartByIdAsync(int id, CancellationToken ct)
    {
        const string sql = @"
SELECT
    id,
    system_number,
    part_name,
    short_system_number,
    part_weight,
    max_quantity_per_container,
    unit_of_measure_id,
    note
FROM dbo.WMS_assembly_parts
WHERE id = @id;
";

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await con.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", id);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new WmsAssemblyPartEditDto
            {
                Id = reader.GetInt32(0),
                SystemNumber = reader.GetString(1),
                PartName = reader.GetString(2),
                ShortSystemNumber = reader.IsDBNull(3) ? null : reader.GetString(3),
                PartWeight = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                MaxQuantityPerContainer = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                UnitOfMeasureId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Note = reader.IsDBNull(7) ? null : reader.GetString(7),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WMS Parts: błąd podczas GetPartByIdAsync, id={Id}", id);
            return null;
        }
    }

    public async Task<List<WmsUnitOfMeasureDto>> GetUnitsAsync(CancellationToken ct)
    {
        var list = new List<WmsUnitOfMeasureDto>();

        const string sql = @"
SELECT id, name, symbol
FROM dbo.WMS_unit_of_measure
ORDER BY name;
";

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await con.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, con);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                list.Add(new WmsUnitOfMeasureDto
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Symbol = reader.GetString(2),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WMS Parts: błąd podczas GetUnitsAsync");
        }

        return list;
    }

    public async Task<(bool Success, string? ErrorMessage)> UpsertPartAsync(WmsAssemblyPartEditDto dto, CancellationToken ct)
    {
        if (dto is null)
            return (false, "Brak danych do zapisu.");

        dto.SystemNumber = (dto.SystemNumber ?? string.Empty).Trim();
        dto.PartName = (dto.PartName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(dto.SystemNumber))
            return (false, "Pole System number jest wymagane.");
        if (string.IsNullOrWhiteSpace(dto.PartName))
            return (false, "Pole Part name jest wymagane.");

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await con.OpenAsync(ct);

            await using var tx = con.BeginTransaction();

            var duplicate = await ExistsSystemNumberAsync(con, tx, dto.SystemNumber, dto.Id, ct);
            if (duplicate)
            {
                tx.Rollback();
                return (false, "Podany system number już istnieje.");
            }

            if (dto.Id <= 0)
            {
                const string insertSql = @"
INSERT INTO dbo.WMS_assembly_parts
(
    system_number,
    part_name,
    short_system_number,
    part_weight,
    max_quantity_per_container,
    unit_of_measure_id,
    note
)
VALUES
(
    @system_number,
    @part_name,
    @short_system_number,
    @part_weight,
    @max_quantity_per_container,
    @unit_of_measure_id,
    @note
);

SELECT CAST(SCOPE_IDENTITY() AS int);
";
                await using var cmd = new SqlCommand(insertSql, con, tx);
                FillPartParameters(cmd, dto);

                var newIdObj = await cmd.ExecuteScalarAsync(ct);
                dto.Id = (newIdObj is int newId) ? newId : dto.Id;

                tx.Commit();
                return (true, null);
            }
            else
            {
                const string updateSql = @"
UPDATE dbo.WMS_assembly_parts
SET
    system_number = @system_number,
    part_name = @part_name,
    short_system_number = @short_system_number,
    part_weight = @part_weight,
    max_quantity_per_container = @max_quantity_per_container,
    unit_of_measure_id = @unit_of_measure_id,
    note = @note
WHERE id = @id;
";
                await using var cmd = new SqlCommand(updateSql, con, tx);
                cmd.Parameters.AddWithValue("@id", dto.Id);
                FillPartParameters(cmd, dto);

                await cmd.ExecuteNonQueryAsync(ct);

                tx.Commit();
                return (true, null);
            }
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            _logger.LogWarning(ex, "WMS Parts: konflikt unikalności system_number");
            return (false, "Podany system number już istnieje.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WMS Parts: błąd podczas UpsertPartAsync");
            return (false, "Błąd zapisu do bazy danych.");
        }
    }

    private static void FillPartParameters(SqlCommand cmd, WmsAssemblyPartEditDto dto)
    {
        cmd.Parameters.AddWithValue("@system_number", dto.SystemNumber);
        cmd.Parameters.AddWithValue("@part_name", dto.PartName);

        var shortSys = string.IsNullOrWhiteSpace(dto.ShortSystemNumber) ? (object)DBNull.Value : dto.ShortSystemNumber.Trim();
        cmd.Parameters.AddWithValue("@short_system_number", shortSys);

        cmd.Parameters.AddWithValue("@part_weight", (object?)dto.PartWeight ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@max_quantity_per_container", (object?)dto.MaxQuantityPerContainer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@unit_of_measure_id", (object?)dto.UnitOfMeasureId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)dto.Note ?? DBNull.Value);
    }

    private static async Task<bool> ExistsSystemNumberAsync(
        SqlConnection con,
        SqlTransaction tx,
        string systemNumber,
        int currentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP 1 1
FROM dbo.WMS_assembly_parts
WHERE system_number = @system_number
  AND (@id <= 0 OR id <> @id);
";
        await using var cmd = new SqlCommand(sql, con, tx);
        cmd.Parameters.AddWithValue("@system_number", systemNumber);
        cmd.Parameters.AddWithValue("@id", currentId);

        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is not null && obj != DBNull.Value;
    }

    private static string? BuildLike(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;

        var s = search.Trim();
        return $"%{s}%";
    }

    private static string BuildOrderBy(string? sortColumn, bool sortDesc)
    {
        var col = (sortColumn ?? string.Empty).Trim();

        // Whitelist kolumn z widoku, żeby nie zrobić SQL injection przez ORDER BY
        col = col switch
        {
            "SystemNumber" => "system_number",
            "PartName" => "part_name",
            "ShortSystemNumber" => "short_system_number",
            "PartWeight" => "part_weight",
            "MaxQuantityPerContainer" => "max_quantity_per_container",
            _ => "system_number"
        };

        var dir = sortDesc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}";
    }
}
