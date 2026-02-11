// File: Models/Projects/ProjectLinkableTicketDto.cs
// Description: DTO ticketa możliwego do powiązania z projektem.
// Version: 1.02
// Created: 2026-01-28
// Updated:
// 1.01 (2026-01-28) TicketId: int -> long (dbo.tickets.id to bigint).
// 1.02 (2026-02-04) Added TicketNo + TicketCode (WAL_000000) for display and sorting.

namespace ITManager.Models.Projects
{
    public sealed class ProjectLinkableTicketDto
    {
        public long TicketId { get; set; }

        public int TicketNo { get; set; }

        public string TicketCode { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string StatusName { get; set; } = string.Empty;

        public string PriorityName { get; set; } = string.Empty;

        public string AssignedToDisplayName { get; set; } = string.Empty;
    }
}
