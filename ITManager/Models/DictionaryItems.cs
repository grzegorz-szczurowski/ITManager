// File: Models/DictionaryItem.cs
// Description: Wspólny model DTO dla słowników UI (producers, models, locations).
// Created: 2025-12-23
// Version: 1.00
// Change log:
// - 1.00 (2025-12-23) Initial version.

namespace ITManager.Models
{
    public sealed class DictionaryItem
    {
        public int Id { get; set; }

        // Nazwa główna
        public string Name { get; set; } = string.Empty;

        // Kod (tylko dla niektórych słowników, np. locations)
        public string? Code { get; set; }

        // Relacja nadrzędna (np. model -> producent)
        public int? ParentId { get; set; }
        public string? ParentName { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
