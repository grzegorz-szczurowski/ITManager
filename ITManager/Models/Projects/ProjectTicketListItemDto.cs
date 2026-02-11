// File: Models/Projects/ProjectTicketListItemDto.cs
// Description: DTO ticketa w kontekście projektu.
// Version: 1.01
// Created: 2026-01-25
// Change history:
// - 1.00 (2026-01-25): Initial.
// - 1.01 (2026-02-04): Added TicketNo + TicketCode (WAL_000000) for display and sorting.

using System;

namespace ITManager.Models.Projects
{
    public sealed class ProjectTicketListItemDto
    {
        public long TicketId { get; set; }

        public int TicketNo { get; set; }

        public string TicketCode { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string StatusName { get; set; } = string.Empty;

        public string PriorityName { get; set; } = string.Empty;

        public string? AssignedToDisplayName { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
