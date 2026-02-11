// File: Models/LicensesDtos.cs
// Description: DTO dla modułu Licenses używany przez LicensesService oraz UI (dialogi/strony).
// Created: 2026-01-08
// Notes:
// - NIE duplikujemy LookupItem (jest już w Models/LookupItem.cs).
// - Te nazwy pól są spójne z LicensesService.cs który wkleiłeś: TypeId/Type, StatusId/Status, ProducerId/Producer, ModelId/Model, UserId/User.

using System;
using System.Collections.Generic;

namespace ITManager.Models
{
    /// <summary>
    /// Wiersz listy licencji (do DataGrid oraz do odczytu szczegółów przez GetLicenseByIdAsync).
    /// Pola dict są w formie: (Id + Name) analogicznie jak w Devices.
    /// </summary>
    public sealed class LicenseListRow
    {
        public int Id { get; set; }

        // Dict: Id + Name
        public int? TypeId { get; set; }
        public string Type { get; set; } = string.Empty;

        public int? StatusId { get; set; }
        public string Status { get; set; } = string.Empty;

        public int? ProducerId { get; set; }
        public string Producer { get; set; } = string.Empty;

        public int? ModelId { get; set; }
        public string Model { get; set; } = string.Empty;

        // Dane licencji
        public string Name { get; set; } = string.Empty;
        public string InventoryNo { get; set; } = string.Empty;

        // Użytkownik (w bazie: user_id + user_name lub join do dbo.users)
        public int? UserId { get; set; }
        public string User { get; set; } = string.Empty;

        public DateTime? SubscriptionStarts { get; set; }
        public DateTime? SubscriptionEnds { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// Model do create/update (z Id oraz polami *_Id do zapisu).
    /// </summary>
    public sealed class LicenseUpdateModel
    {
        public int Id { get; set; } // 0 = nowa licencja

        public int? TypeId { get; set; }
        public int? StatusId { get; set; }
        public int? ProducerId { get; set; }
        public int? ModelId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string InventoryNo { get; set; } = string.Empty;

        public int? UserId { get; set; }

        // Jeśli masz w tabeli licenses pole user_name, to service zapisuje też ten tekst
        public string UserName { get; set; } = string.Empty;

        public DateTime? SubscriptionStarts { get; set; }
        public DateTime? SubscriptionEnds { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lookups do filtrów i pól w UI (Autocomplete/Select).
    /// </summary>
    public sealed class LicenseFilterLookups
    {
        public List<LookupItem> Types { get; set; } = new();
        public List<LookupItem> Statuses { get; set; } = new();
        public List<LookupItem> Producers { get; set; } = new();
        public List<LookupItem> Models { get; set; } = new();

        public List<LookupItem> Users { get; set; } = new();
    }
}
