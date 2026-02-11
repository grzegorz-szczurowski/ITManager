// File: Models/Projects/ProjectUpsertRequest.cs
// Description: Model wejściowy do tworzenia i aktualizacji projektu.
// Version: 1.02
// Created: 2026-01-25
// Updated: 2026-01-26 - dodanie pól scoringu (Impact, Urgency, Scope, CostOfDelay, Effort)
// Change history:
// 1.00 (2026-01-25) - Initial.
// 1.01 (2026-01-26) - Scoring fields added.
// 1.02 (2026-01-27) - ProgressPercent: legacy (computed server-side for compatibility).

using System;

namespace ITManager.Models.Projects
{
    public sealed class ProjectUpsertRequest
    {
        public int? Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public int ProjectStatusId { get; set; }

        public int ProjectPriorityId { get; set; }

        public int OwnerUserId { get; set; }

        public DateTime? PlannedStartDate { get; set; }

        public DateTime? DeadlineDate { get; set; }

        // Legacy percent stored in dbo.projects.progress_percent.
        // W nowym podejściu (hybryda) nie edytujemy tego ręcznie.
        // Serwis może to przeliczyć na podstawie punktów z ticketów.
        public int ProgressPercent { get; set; }

        public string? LastUpdateNote { get; set; }

        public bool IsActive { get; set; } = true;

        // Scoring (zgodnie z modelem):
        // Impact (1-5), Urgency (1-5), Scope (1-3), CostOfDelay (1-3), Effort (1-3)
        // PriorityScore liczymy po stronie bazy: (I * U) + S + D - E

        public int Impact { get; set; } = 3;

        public int Urgency { get; set; } = 3;

        public int Scope { get; set; } = 2;

        public int CostOfDelay { get; set; } = 2;

        public int Effort { get; set; } = 2;
    }
}
