// File: Services/Wms/WmsPartsService.cs
// Description: Warstwa RBAC nad WmsPartsRepository.
//              View = Operations.Parts.View, Manage = Operations.Parts.Manage.
//              Kazda metoda publiczna sprawdza uprawnienia przed delegacja do repo.
// Version: 1.03
// Created: 2026-03-25
// Change log:
//   - 1.00 (2026-03-25) Initial version — RBAC guards for all WMS Parts operations.
//   - 1.01 (2026-03-28) ImportPartsAsync — bulk import with validation.
//   - 1.02 (2026-04-01) DeletePartAsync — usuwanie części (Manage).
//   - 1.03 (2026-04-01) SearchPartLocationsAsync — RBAC proxy for PartsFinder search.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ITManager.Models.Wms;
using ITManager.Services.Auth;

namespace ITManager.Services.Wms;

public sealed class WmsPartsService
{
    private const string PermView = "Operations.Parts.View";
    private const string PermManage = "Operations.Parts.Manage";

    private readonly WmsPartsRepository _repo;
    private readonly WmsPartFinderRepository _finderRepo;
    private readonly CurrentUserContextService _ctx;
    private readonly ILogger<WmsPartsService> _logger;

    public WmsPartsService(
        WmsPartsRepository repo,
        WmsPartFinderRepository finderRepo,
        CurrentUserContextService ctx,
        ILogger<WmsPartsService> logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _finderRepo = finderRepo ?? throw new ArgumentNullException(nameof(finderRepo));
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Guards ──

    private void GuardView()
    {
        var user = _ctx.CurrentUser;
        if (user == null || !user.IsAuthenticated || user.IsActive != true || !user.Has(PermView))
            throw new UnauthorizedAccessException("Brak uprawnienia " + PermView + ".");
    }

    private void GuardManage()
    {
        var user = _ctx.CurrentUser;
        if (user == null || !user.IsAuthenticated || user.IsActive != true || !user.Has(PermManage))
            throw new UnauthorizedAccessException("Brak uprawnienia " + PermManage + ".");
    }

    // ── KPI (View) ──

    public async Task<WmsPartKpiDto> GetPartKpiAsync(CancellationToken ct)
    {
        GuardView();
        return await _repo.GetPartKpiAsync(ct);
    }

    // ── Read (View) ──

    public async Task<(List<WmsAssemblyPartListItemDto> Items, int TotalCount)> SearchPartsAsync(
        string? search, int pageIndex, int pageSize, string? sortColumn, bool sortDesc, CancellationToken ct)
    {
        GuardView();
        return await _repo.SearchPartsAsync(search, pageIndex, pageSize, sortColumn, sortDesc, ct);
    }

    public async Task<WmsAssemblyPartEditDto?> GetPartByIdAsync(int id, CancellationToken ct)
    {
        GuardView();
        return await _repo.GetPartByIdAsync(id, ct);
    }

    public async Task<List<WmsUnitOfMeasureDto>> GetUnitsAsync(CancellationToken ct)
    {
        GuardView();
        return await _repo.GetUnitsAsync(ct);
    }

    public async Task<List<WmsPartLocationDto>> GetContainersByPartIdAsync(int partId, CancellationToken ct)
    {
        GuardView();
        return await _repo.GetContainersByPartIdAsync(partId, ct);
    }

    /// <summary>
    /// Wyszukiwanie kontenerow po numerze czesci (PartsFinder).
    /// </summary>
    public async Task<List<WmsPartFinderResultDto>> SearchPartLocationsAsync(string search, CancellationToken ct)
    {
        GuardView();
        return await _finderRepo.SearchByPartNumberAsync(search, ct);
    }

    // ── Write (Manage) ──

    public async Task<(bool Success, string? ErrorMessage)> UpsertPartAsync(WmsAssemblyPartEditDto dto, CancellationToken ct)
    {
        GuardManage();
        return await _repo.UpsertPartAsync(dto, ct);
    }

    // ── Delete (Manage) ──

    public async Task<(bool Ok, string? Error)> DeletePartAsync(int id, CancellationToken ct)
    {
        GuardManage();
        return await _repo.DeletePartAsync(id, ct);
    }

    // ── Import ──

    /// <summary>
    /// Validates and imports parsed rows. Returns validated rows (with errors marked).
    /// If all valid and commit=true, inserts into DB.
    /// </summary>
    public async Task<(List<WmsPartImportRowDto> Rows, int ErrorCount, string? GlobalError)> ValidateImportAsync(
        List<WmsPartImportRowDto> rows, CancellationToken ct)
    {
        GuardManage();

        if (rows.Count == 0)
            return (rows, 0, "File contains no data rows.");

        if (rows.Count > 1000)
            return (rows, rows.Count, "Maximum 1000 rows per import. File has " + rows.Count + " rows.");

        var units = await _repo.GetUnitsAsync(ct);
        var unitLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in units)
            unitLookup[u.Name] = u.Id;

        // Detect duplicates within file
        var fileNumbers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var sn = row.SystemNumber.Trim();
            if (!string.IsNullOrWhiteSpace(sn))
            {
                if (!fileNumbers.ContainsKey(sn))
                    fileNumbers[sn] = new List<int>();
                fileNumbers[sn].Add(row.RowNumber);
            }
        }

        var fileDuplicates = new HashSet<string>(
            fileNumbers.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        // Check DB duplicates
        var allNumbers = rows.Where(r => !string.IsNullOrWhiteSpace(r.SystemNumber))
            .Select(r => r.SystemNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dbExisting = await _repo.GetExistingSystemNumbersAsync(allNumbers, ct);

        int errorCount = 0;

        foreach (var row in rows)
        {
            var errors = new List<string>();

            // Required fields
            if (string.IsNullOrWhiteSpace(row.SystemNumber))
                errors.Add("SystemNumber is required.");
            else
            {
                row.SystemNumber = row.SystemNumber.Trim();

                if (row.SystemNumber.Length < 9)
                    errors.Add("SystemNumber must be at least 9 characters.");

                if (fileDuplicates.Contains(row.SystemNumber))
                    errors.Add("Duplicate SystemNumber in file (rows: " +
                        string.Join(", ", fileNumbers[row.SystemNumber]) + ").");

                if (dbExisting.Contains(row.SystemNumber))
                    errors.Add("SystemNumber already exists in database.");

                // Calculate short number
                if (row.SystemNumber.Length >= 9)
                    row.ShortSystemNumber = row.SystemNumber.Substring(3, row.SystemNumber.Length - 6);
            }

            if (string.IsNullOrWhiteSpace(row.PartName))
                errors.Add("PartName is required.");
            else
                row.PartName = row.PartName.Trim();

            // Unit of measure
            if (!string.IsNullOrWhiteSpace(row.UnitOfMeasureName))
            {
                var unitName = row.UnitOfMeasureName.Trim();
                if (unitLookup.TryGetValue(unitName, out var unitId))
                    row.UnitOfMeasureId = unitId;
                else
                    errors.Add($"Unit of measure '{unitName}' not found in dictionary.");
            }

            // Weight validation
            if (row.PartWeight.HasValue && row.PartWeight.Value < 0)
                errors.Add("Weight cannot be negative.");

            // MaxQty validation
            if (row.MaxQuantityPerContainer.HasValue && row.MaxQuantityPerContainer.Value < 0)
                errors.Add("MaxQuantityPerContainer cannot be negative.");

            if (errors.Count > 0)
            {
                row.IsValid = false;
                row.Error = string.Join(" | ", errors);
                errorCount++;
            }
            else
            {
                row.IsValid = true;
                row.Error = null;
            }
        }

        return (rows, errorCount, null);
    }

    public async Task<(bool Ok, string? Error)> CommitImportAsync(List<WmsPartImportRowDto> validatedRows, CancellationToken ct)
    {
        GuardManage();

        var toInsert = validatedRows.Where(r => r.IsValid).ToList();
        if (toInsert.Count == 0)
            return (false, "No valid rows to import.");

        if (toInsert.Count != validatedRows.Count)
            return (false, "Cannot import — there are rows with errors.");

        return await _repo.BulkInsertPartsAsync(toInsert, ct);
    }
}
