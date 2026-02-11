// File: Models/Reports/PrintsReportModels.cs
// Description: Modele raportu wydruków (drukarki). Lokalizacje mapowane po location_name.
// Created: 2026-01-14
// Updated: 2026-01-14 - lokalizacje po LocationName (string), nie po LocationId.
// Updated: 2026-01-14 - dodano listę drukarek (PrintsReportPrinterRow).

using System;
using System.Collections.Generic;

namespace TristoneHub.Models.Reports
{
    public sealed class LocationButton
    {
        public string LocationName { get; set; } = "";
        public int PrintersCount { get; set; }
    }

    public sealed class PrintsReportRequest
    {
        public List<string> LocationNames { get; set; } = new();
        public DateTime DateFrom { get; set; }
        public DateTime DateTo { get; set; }
        public bool CompareWithPreviousPeriod { get; set; }
    }

    public sealed class PrintsReportKpis
    {
        public int TotalPages { get; set; }
        public int MonoPages { get; set; }
        public int ColorPages { get; set; }

        public decimal TotalCost { get; set; }
        public decimal MonoCost { get; set; }
        public decimal ColorCost { get; set; }

        public int PrevTotalPages { get; set; }
        public int PrevMonoPages { get; set; }
        public int PrevColorPages { get; set; }

        public decimal PrevTotalCost { get; set; }
        public decimal PrevMonoCost { get; set; }
        public decimal PrevColorCost { get; set; }
    }

    public sealed class PrintsReportDayRow
    {
        public DateTime Day { get; set; }

        public int TotalPages { get; set; }
        public int MonoPages { get; set; }
        public int ColorPages { get; set; }

        public decimal TotalCost { get; set; }
        public decimal MonoCost { get; set; }
        public decimal ColorCost { get; set; }
    }

    public sealed class PrintsReportPrinterRow
    {
        public long DeviceId { get; set; }
        public string Hostname { get; set; } = "";
        public string Model { get; set; } = "";
        public string LocationName { get; set; } = "";

        public int TotalPages { get; set; }
        public int MonoPages { get; set; }
        public int ColorPages { get; set; }

        public decimal TotalCost { get; set; }
        public decimal MonoCost { get; set; }
        public decimal ColorCost { get; set; }
    }

    public sealed class PrintsReportResult
    {
        public PrintsReportKpis Kpis { get; set; } = new();
        public List<PrintsReportDayRow> Daily { get; set; } = new();
        public List<PrintsReportPrinterRow> Printers { get; set; } = new();
    }
}
