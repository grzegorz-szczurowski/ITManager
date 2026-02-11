// File: Models/Projects/ProjectFormLookupsDto.cs
// Description: Lookupi do formularza projektu (statusy, priorytety, ownerzy + scoring).
// Version: 1.10
// Created: 2026-01-25
// Updated: 2026-01-26 - dodanie list scoringu: Impacts/Urgencies/Scopes/DelayCosts/Efforts.

using System.Collections.Generic;

namespace ITManager.Models.Projects
{
    public sealed class ProjectFormLookupsDto
    {
        public List<LookupItemDto> Statuses { get; set; } = new();
        public List<LookupItemDto> Priorities { get; set; } = new();
        public List<LookupItemDto> Owners { get; set; } = new();

        // Scoring lookups
        public List<LookupItemDto> Impacts { get; set; } = new();
        public List<LookupItemDto> Urgencies { get; set; } = new();
        public List<LookupItemDto> Scopes { get; set; } = new();
        public List<LookupItemDto> DelayCosts { get; set; } = new();
        public List<LookupItemDto> Efforts { get; set; } = new();
    }
}
