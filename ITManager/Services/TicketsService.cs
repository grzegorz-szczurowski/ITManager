// File: Services/TicketsService.cs
// Description: Fasada dla TicketsQueryService + TicketsCommandService.
//              UI (Razor) korzysta wyłącznie z TicketsService, a logika jest rozdzielona:
//              - TicketsQueryService: odczyty (read-only)
//              - TicketsCommandService: komendy (create/update)
//              - TicketEventsRepository: outbox zdarzeń (atomicznie w transakcjach komend)
// Notes:
//   - Temat maili pomijamy w całości.
// Version: 2.25
// Updated: 2026-02-11
// Change log:
//   - 2.20 (2026-02-08) MIGRATION: stabilne DTO w TicketsService (CreateTicketRequest, Impact/Urgency, TicketActionRow)
//                                 + spięcie Query/Command end-to-end (bez zależności Razor -> wewnętrzne typy serwisów).
//   - 2.21 (2026-02-08) FIX: TicketActionRow alias CreatedAt dla kompatybilności z TicketEdit.razor
//                            + TicketEventsRepository tworzony bez connection stringa (zgodnie z projektem).
//   - 2.22 (2026-02-08) NO-OP: porządek i zgodność sygnatur (bez zmian funkcjonalnych).
//   - 2.23 (2026-02-09) FEATURE: GetTicketByCodeAsync dla linków /tickets/{TicketCode} (np. WAL000001).
//   - 2.24 (2026-02-11) REFACTOR: konstruktor DI-friendly (można wstrzyknąć gotowe serwisy Query/Command),
//                                 lepsza walidacja i stała nazwa connection stringa.
//   - 2.25 (2026-02-11) FIX: usunięty konstruktor legacy (IConfiguration, CurrentUserContextService) aby DI miało jednoznaczną ścieżkę tworzenia.

using ITManager.Models;
using ITManager.Services.Tickets;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ITManager.Services
{
    public sealed class TicketsService
    {
        /* =========================
           DTOs używane przez UI
        ========================= */

        public sealed class CreateTicketRequest
        {
            public string Title { get; set; } = string.Empty;
            public string FirstActionText { get; set; } = string.Empty;
            public string CategoryName { get; set; } = string.Empty;
            public string ImpactName { get; set; } = string.Empty;
            public string UrgencyName { get; set; } = string.Empty;

            // Jeśli null -> requester = aktualny user.
            public string? RequesterDisplayName { get; set; } = null;
        }

        public sealed class ImpactUrgencyItem
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;

            // Wagi są przydatne do diagnozy i ewentualnego liczenia priorytetu po stronie backendu.
            public int Weight { get; set; }
        }

        public sealed class ImpactUrgencyDictionaryResult
        {
            public List<ImpactUrgencyItem> Impacts { get; set; } = new();
            public List<ImpactUrgencyItem> Urgencies { get; set; } = new();
        }

        public sealed class TicketActionRow
        {
            public long Id { get; set; }

            // Backend trzyma UTC, UI potrzebuje CreatedAt.
            public DateTime CreatedAtUtc { get; set; }

            // Alias pod TicketEdit.razor (Property="x => x.CreatedAt")
            public DateTime CreatedAt
            {
                get => CreatedAtUtc;
                set => CreatedAtUtc = value;
            }

            public string CreatedByDisplayName { get; set; } = string.Empty;

            public string ActionText { get; set; } = string.Empty;
            public bool IsPublic { get; set; }

            // Optionalne, w zależności od danych w DB.
            public string? StatusFromName { get; set; }
            public string? StatusToName { get; set; }
            public string? WaitingReasonCode { get; set; }
        }

        private readonly TicketsQueryService _query;
        private readonly TicketsCommandService _command;

        /// <summary>
        /// Jedyny konstruktor. TicketsService jest czystą fasadą, bez tworzenia zależności ręcznie.
        /// Connection string i repo eventów są rozwiązywane przez DI w Program.cs.
        /// </summary>
        public TicketsService(
            TicketsQueryService query,
            TicketsCommandService command)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _command = command ?? throw new ArgumentNullException(nameof(command));
        }

        /* =========================
           Public API dla UI
        ========================= */

        // Dictionaries
        public Task<ImpactUrgencyDictionaryResult> GetImpactAndUrgencyDictionaryAsync()
            => _query.GetImpactAndUrgencyDictionaryAsync();

        // CREATE
        public Task<long> CreateTicketAsync(CreateTicketRequest req)
            => _command.CreateTicketAsync(req);

        // Assign
        public Task AssignTicketToCurrentUserAsync(long ticketId)
            => _command.AssignTicketToCurrentUserAsync(ticketId);

        // Read lists
        public Task<List<Ticket>> GetTicketsAsync()
            => _query.GetTicketsAsync();

        public Task<List<string>> GetTicketStatusesAsync()
            => _query.GetTicketStatusesAsync();

        public Task<List<Ticket>> GetTicketsQueueAsync()
            => _query.GetTicketsQueueAsync();

        public Task<int> GetAssignedToMeOpenTicketsCountAsync()
            => _query.GetAssignedToMeOpenTicketsCountAsync();

        public Task<int> GetAllOpenTicketsCountAsync()
            => _query.GetAllOpenTicketsCountAsync();

        public Task<int> GetTicketsQueueCountAsync()
            => _query.GetTicketsQueueCountAsync();

        public Task<List<Ticket>> GetTicketsAssignedToCurrentUserAsync()
            => _query.GetTicketsAssignedToCurrentUserAsync();

        public Task<List<Ticket>> GetTicketsOwnAsync()
            => _query.GetTicketsOwnAsync();

        public Task<List<Ticket>> GetTicketsMyTeamAsync()
            => _query.GetTicketsMyTeamAsync();

        public Task<Ticket?> GetTicketByIdAsync(int id)
            => _query.GetTicketByIdAsync(id);

        public Task<Ticket?> GetTicketByCodeAsync(string ticketCode)
            => _query.GetTicketByCodeAsync(ticketCode);

        // UPDATE ticket content (strict)
        public Task UpdateTicketAsync(
            long id,
            string title,
            string problemDescription,
            string problemSolution,
            string categoryName,
            string statusName,
            string requesterName,
            string operatorName,
            string impactName,
            string urgencyName,
            DateTime? requestDate,
            DateTime? closingDate,
            int weightPriority)
            => _command.UpdateTicketAsync(
                id, title, problemDescription, problemSolution, categoryName,
                statusName, requesterName, operatorName, impactName, urgencyName,
                requestDate, closingDate, weightPriority);

        // Actions
        public Task<bool> CanCurrentUserWritePrivateTicketActionsAsync()
            => _query.CanCurrentUserWritePrivateTicketActionsAsync();

        public Task<List<TicketActionRow>> GetTicketActionsAsync(long ticketId)
            => _query.GetTicketActionsAsync(ticketId);

        public Task<TicketActionRow?> GetTicketActionByIdAsync(long actionId)
            => _query.GetTicketActionByIdAsync(actionId);

        public Task<long> AddTicketActionAsync(
            long ticketId,
            string actionText,
            bool isPublic,
            string? targetStatusName,
            string? waitingReasonCode)
            => _command.AddTicketActionAsync(ticketId, actionText, isPublic, targetStatusName, waitingReasonCode);

        public Task<long> UpdateTicketActionAsync(
            long ticketId,
            long actionId,
            string actionText,
            bool isPublic,
            string? targetStatusName,
            string? waitingReasonCode)
            => _command.UpdateTicketActionAsync(ticketId, actionId, actionText, isPublic, targetStatusName, waitingReasonCode);
    }
}
