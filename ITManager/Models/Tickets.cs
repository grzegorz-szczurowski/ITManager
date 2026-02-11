// File: Models/Ticket.cs
// Description: Model danych pojedynczego zgłoszenia (ticket) zgodny z widokiem vw_tickets.
// Created: 2025-12-11
// Updated: 2025-12-16 - dodano Impact/Urgency (odczyt z vw_tickets)
// Updated: 2026-01-16 - dodano TicketLastActionAt (ostatnia akcja z dbo.ticket_actions)
// Updated: 2026-02-04 - dodano TicketCode (wymagane dla kolumny ID w TicketsIndex, źródło: vw_tickets.ticket_code)

using System;

namespace ITManager.Models
{
    public class Ticket
    {
        public int Id { get; set; }

        // Natywne źródło: dbo.vw_tickets.ticket_code
        public string TicketCode { get; set; } = string.Empty;

        public DateTime? TicketRequestDate { get; set; }
        public DateTime? TicketLastActionAt { get; set; }
        public DateTime? TicketClosingDate { get; set; }

        public string TicketRequesterName { get; set; } = string.Empty;
        public string TicketCategoryName { get; set; } = string.Empty;
        public string TicketTitle { get; set; } = string.Empty;
        public string TicketOperatorName { get; set; } = string.Empty;

        public int? TicketRequesterId { get; set; }
        public int? TicketKeeperId { get; set; }

        public int WeightPriority { get; set; }

        public string TicketStatusesName { get; set; } = string.Empty;
        public string ProblemDescription { get; set; } = string.Empty;
        public string ProblemSolution { get; set; } = string.Empty;

        public string TicketImpactName { get; set; } = string.Empty;
        public string TicketUrgencyName { get; set; } = string.Empty;

        public string PriorityTag
        {
            get
            {
                var s = WeightPriority;
                if (s >= 80) return "Emergency";
                if (s >= 50) return "Very High";
                if (s >= 30) return "High";
                if (s >= 15) return "Medium";
                if (s >= 1) return "Low";
                return "None";
            }
        }

        public string Summary =>
            $"{TicketTitle} [{PriorityTag}] - {TicketStatusesName}";
    }
}
