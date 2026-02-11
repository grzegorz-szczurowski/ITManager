// File: Models/Projects/LookupItemDto.cs
// Description: Prosty element słownika (Id + Name) do dropdownów.
// Version: 1.00
// Created: 2026-01-26
// Change history:
// - 1.00: Initial.

namespace ITManager.Models.Projects
{
    public sealed class LookupItemDto
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
