// File: Models/Projects/ProjectListItemDto.cs
// Description: DTO listy projektów (Projects list).
// Version: 1.02
// Created: 2026-01-25
// Updated: 2026-01-26 - dodanie scoringu + PriorityScore.
// Change history:
// - 1.00: Initial.
// - 1.01: Scoring fields.
// - 1.02 (2026-01-27): Hybrid progress: WorkPointsDone/WorkPointsTotal + BusinessStage.

using System;

namespace ITManager.Models.Projects
{
    public sealed class ProjectListItemDto
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int ProjectStatusId { get; set; }
        public string ProjectStatusName { get; set; } = string.Empty;

        // Canonical business stage resolved from ProjectStatusName.
        public ProjectBusinessStage BusinessStage { get; set; }
        public string BusinessStageName { get; set; } = string.Empty;

        public int ProjectPriorityId { get; set; }
        public string ProjectPriorityName { get; set; } = string.Empty;

        public int OwnerUserId { get; set; }
        public string OwnerDisplayName { get; set; } = string.Empty;

        public DateTime? PlannedStartDate { get; set; }
        public DateTime? DeadlineDate { get; set; }

        // Legacy percent stored in DB (kept for backward compatibility, not shown as primary indicator).
        public int ProgressPercent { get; set; }

        // Hybrid progress based on ticket "points" (sum of ticket_priority_value).
        public int WorkPointsDone { get; set; }
        public int WorkPointsTotal { get; set; }

        public int TicketsDoneCount { get; set; }
        public int TicketsTotalCount { get; set; }

        public int OpenTicketsCount { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        public bool IsActive { get; set; }

        public int Impact { get; set; }

        public int Urgency { get; set; }

        public int Scope { get; set; }

        public int CostOfDelay { get; set; }

        public int Effort { get; set; }

        public int PriorityScore { get; set; }
    }
}
