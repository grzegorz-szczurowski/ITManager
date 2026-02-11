// File: Models/SimCardsModels.cs
// Description: Modele edycji SIM (new + edit) oraz lookups do comboboxów.

using System.Collections.Generic;

namespace ITManager.Models
{
    public sealed class SimCardEditModel
    {
        public int Id { get; set; }

        public string Type { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        public string User { get; set; } = string.Empty;

        public string MobileNumber { get; set; } = string.Empty;
        public string SimCardNumber { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;
    }

    public sealed class SimCardsFilterLookups
    {
        public List<string> Types { get; set; } = new();
        public List<string> Operators { get; set; } = new();
        public List<string> Plans { get; set; } = new();
        public List<string> Statuses { get; set; } = new();
        public List<string> Users { get; set; } = new();
    }
}
