// File: Models/Projects/ProjectLookupItemDto.cs
// Description: Lekki DTO do list rozwijanych (lookup).
// Version: 1.00
// Created: 2026-01-25
// Change history:
// - 1.00: Initial.

namespace ITManager.Models.Projects
{
    public sealed class ProjectLookupItemDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int ProjectStatusId { get; set; }
        public string ProjectStatusName { get; set; } = string.Empty;
    }
}
