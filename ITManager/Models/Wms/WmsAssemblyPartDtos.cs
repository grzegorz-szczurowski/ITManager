// File: Models/Wms/WmsAssemblyPartDtos.cs
// Description: DTO dla modułu WMS Parts (lista, edycja, lookup).
// Version: 1.00
// Created: 2026-01-05
// Change log:
//   - 1.00: Pierwsza wersja DTO.

namespace ITManager.Models.Wms;

public sealed class WmsAssemblyPartListItemDto
{
    public int Id { get; set; }

    public string SystemNumber { get; set; } = string.Empty;
    public string PartName { get; set; } = string.Empty;

    public string? ShortSystemNumber { get; set; }

    public decimal? PartWeight { get; set; }
    public int? MaxQuantityPerContainer { get; set; }

    public int? UnitOfMeasureId { get; set; }
    public string? UnitName { get; set; }
    public string? UnitSymbol { get; set; }

    public string? Note { get; set; }
}

public sealed class WmsAssemblyPartEditDto
{
    public int Id { get; set; }

    public string SystemNumber { get; set; } = string.Empty;
    public string PartName { get; set; } = string.Empty;

    public string? ShortSystemNumber { get; set; }

    public decimal? PartWeight { get; set; }
    public int? MaxQuantityPerContainer { get; set; }

    public int? UnitOfMeasureId { get; set; }

    public string? Note { get; set; }
}

public sealed class WmsUnitOfMeasureDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;

    public override string ToString() => $"{Name} ({Symbol})";
}
