// File: Models/Raports/GlovesReportModels.cs
// Description: Modele DTO dla raportu pobrań rękawic (TEMREX).
// Version: 1.10
// Created: 2026-02-11
// Updated: 2026-02-12
// Change log:
//   - 1.00 (2026-02-11) Initial version.
//   - 1.01 (2026-02-11) ByUser: dodane Username + Department oraz jawna kolumna CardNumber.
//   - 1.10 (2026-02-12) MANAGER VIEW: filtr Department + ranking działów + agregacja typów bez rozmiarów (ByItemType).

using System;
using System.Collections.Generic;

namespace ITManager.Models.Reports;

public sealed class GlovesReportRequest
{
    public DateTime DateFrom { get; set; }
    public DateTime DateTo { get; set; }

    // 0 elementów oznacza brak filtra
    public List<string> ItemNumbers { get; set; } = new();

    // 0 elementów oznacza brak filtra
    public List<int> VmIds { get; set; } = new();

    // Filtr działu (po USERGROUP01). null lub pusty oznacza brak filtra.
    public string? Department { get; set; }

    // Trend na wykresie
    public GlovesTrendGranularity TrendGranularity { get; set; } = GlovesTrendGranularity.Week;

    // Porównanie do poprzedniego okresu jak w PrintsReport
    public bool CompareWithPreviousPeriod { get; set; }
}

public enum GlovesTrendGranularity
{
    Week = 1,
    Month = 2
}

public sealed class GlovesReportResult
{
    public GlovesReportKpis Kpis { get; set; } = new();

    public List<GlovesTrendPoint> Trend { get; set; } = new();

    // Manager: ranking działów (must have)
    public List<GlovesByDepartmentRow> ByDepartment { get; set; } = new();

    // Manager: top users (z kontekstem)
    public List<GlovesByUserRow> ByUser { get; set; } = new();

    // Dane surowe per SKU (zostawiamy)
    public List<GlovesByItemRow> ByItem { get; set; } = new();

    // Agregacja typu rękawic bez rozmiarów (mega ważne)
    public List<GlovesByItemTypeRow> ByItemType { get; set; } = new();

    public List<GlovesByVmRow> ByVm { get; set; } = new();
}

public sealed class GlovesReportKpis
{
    public int TransactionsCount { get; set; }
    public int UniqueUsersCount { get; set; }

    // Sum(ABS(QTY)) dla bezpieczeństwa
    public int TotalQty { get; set; }

    public decimal AvgQtyPerDay { get; set; }

    // Compare
    public int PrevTransactionsCount { get; set; }
    public int PrevUniqueUsersCount { get; set; }
    public int PrevTotalQty { get; set; }
    public decimal PrevAvgQtyPerDay { get; set; }
}

public sealed class GlovesTrendPoint
{
    // np. 2026-W06 albo 2026-02
    public string Label { get; set; } = "";

    // klucz sortowania
    public int SortKey { get; set; }

    public int Qty { get; set; }
    public int Transactions { get; set; }
}

public sealed class GlovesByDepartmentRow
{
    public string Department { get; set; } = "";
    public int Qty { get; set; }
    public int Transactions { get; set; }
    public int UniqueUsers { get; set; }
}

public sealed class GlovesByUserRow
{
    // USERNAME (np. "Bielicki Andrzej")
    public string UserName { get; set; } = "";

    // USERNUMBER (numer karty)
    public string CardNumber { get; set; } = "";

    // UG01 (wydział)
    public string Department { get; set; } = "";

    public int Qty { get; set; }
    public int Transactions { get; set; }
}

public sealed class GlovesByItemRow
{
    public string ItemNumber { get; set; } = "";
    public string ItemDescr { get; set; } = "";
    public int Qty { get; set; }
    public int Transactions { get; set; }
}

public sealed class GlovesByItemTypeRow
{
    // Nazwa typu bez rozmiaru (wyliczana z opisu)
    public string GloveType { get; set; } = "";

    public int Qty { get; set; }
    public int Transactions { get; set; }
}

public sealed class GlovesByVmRow
{
    public int VmId { get; set; }
    public string VmDescr { get; set; } = "";
    public int Qty { get; set; }
    public int Transactions { get; set; }
}

public sealed class GlovesItemOption
{
    public string ItemNumber { get; set; } = "";
    public string ItemDescr { get; set; } = "";
}

public sealed class GlovesVmOption
{
    public int VmId { get; set; }
    public string VmDescr { get; set; } = "";
}

public sealed class GlovesDepartmentOption
{
    public string Department { get; set; } = "";
}
