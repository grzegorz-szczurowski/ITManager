// File: Services/Tickets/TicketWorkflow.cs
// Description: Prosta maszyna stanów dla ticketów (v3 - uproszczona, permissions-based).
//              Operator (keeper) może ustawić dowolny status poza New i Closed.
//              Admin może również ustawić Closed.
//              New jest statusem tylko dla tworzenia i nie może być ustawiany z UI.
//              Cancelled traktujemy jako status końcowy (bez przejść).
// Notes:
//   - RBAC: mapowanie roli workflow wyłącznie na podstawie NOWYCH permissionów (Tickets.*.*).
//   - Brak legacy i brak fallbacków kompatybilności: nieznane statusy rzucają wyjątek.
// Created: 2025-12-16
// Updated: 2026-01-15 - v2: uproszczenie workflow (operator: wszystko poza New/Closed).
// Updated: 2026-02-02 - v3: migracja do nowej architektury uprawnień (Tickets.View.* / Tickets.Edit.* / Tickets.Assign.Team).
// Version: 3.00

using System;
using System.Collections.Generic;
using System.Linq;

namespace ITManager.Services.Tickets
{
    public enum TicketStatus
    {
        New = 1,

        // Uwaga: w DB jest "Assigned". Enum zostaje jako Queue dla czytelności,
        // ale mapujemy go na "Assigned".
        Queue = 2,

        InProgress = 3,
        Waiting = 4,
        Resolved = 5,
        Closed = 6,
        Cancelled = 7
    }

    public enum TicketRole
    {
        Requester = 1,
        Operator = 2,
        Admin = 3
    }

    public enum TicketField
    {
        Reason = 1,
        Comment = 2
    }

    public sealed class TransitionDecision
    {
        public bool Allowed { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<TicketField> MissingFields { get; set; } = new();
    }

    public static class TicketWorkflow
    {
        // =========================
        // RBAC (NOWA architektura)
        // =========================

        private const string PermEditAll = "Tickets.Edit.All";
        private const string PermEditTeam = "Tickets.Edit.Team";
        private const string PermEditAssigned = "Tickets.Edit.Assigned";
        private const string PermAssignTeam = "Tickets.Assign.Team";

        // =========================
        // Allowed targets (workflow)
        // =========================

        private static readonly TicketStatus[] OperatorAllowedTargets = new[]
        {
            TicketStatus.Queue,
            TicketStatus.InProgress,
            TicketStatus.Waiting,
            TicketStatus.Resolved,
            TicketStatus.Cancelled
        };

        private static readonly TicketStatus[] AdminAllowedTargets = new[]
        {
            TicketStatus.Queue,
            TicketStatus.InProgress,
            TicketStatus.Waiting,
            TicketStatus.Resolved,
            TicketStatus.Closed,
            TicketStatus.Cancelled
        };

        // =========================
        // Permissions -> workflow role
        // =========================

        public static TicketRole MapFromPermissions(IReadOnlyCollection<string>? permissionCodes)
        {
            if (permissionCodes == null || permissionCodes.Count == 0)
                return TicketRole.Requester;

            static bool Has(IReadOnlyCollection<string> set, string code)
                => set.Contains(code, StringComparer.OrdinalIgnoreCase);

            if (Has(permissionCodes, PermEditAll))
                return TicketRole.Admin;

            if (Has(permissionCodes, PermAssignTeam) || Has(permissionCodes, PermEditTeam) || Has(permissionCodes, PermEditAssigned))
                return TicketRole.Operator;

            return TicketRole.Requester;
        }

        // =========================
        // Status mapping (NO legacy)
        // =========================

        public static TicketStatus MapFromDbStatusName(string? dbStatusName)
        {
            var s = (dbStatusName ?? string.Empty).Trim();

            return s switch
            {
                "New" => TicketStatus.New,
                "Assigned" => TicketStatus.Queue,
                "In progress" => TicketStatus.InProgress,
                "Waiting" => TicketStatus.Waiting,
                "Resolved" => TicketStatus.Resolved,
                "Closed" => TicketStatus.Closed,
                "Cancelled" => TicketStatus.Cancelled,
                _ => throw new InvalidOperationException($"Nieznany status w DB: '{dbStatusName}'.")
            };
        }

        public static string ToDbStatusName(TicketStatus s)
        {
            return s switch
            {
                TicketStatus.New => "New",
                TicketStatus.Queue => "Assigned",
                TicketStatus.InProgress => "In progress",
                TicketStatus.Waiting => "Waiting",
                TicketStatus.Resolved => "Resolved",
                TicketStatus.Closed => "Closed",
                TicketStatus.Cancelled => "Cancelled",
                _ => throw new InvalidOperationException($"Nieznany TicketStatus enum: '{s}'.")
            };
        }

        // =========================
        // Public API
        // =========================

        public static List<TicketStatus> GetAllowedTargets(
            TicketStatus currentStatus,
            TicketRole role,
            DateTime now,
            DateTime? closedOrResolvedAtUtcOrLocal)
        {
            _ = now;
            _ = closedOrResolvedAtUtcOrLocal;

            // Cancelled jest końcowy
            if (currentStatus == TicketStatus.Cancelled)
                return new List<TicketStatus>();

            TicketStatus[] pool = role switch
            {
                TicketRole.Admin => AdminAllowedTargets,
                TicketRole.Operator => OperatorAllowedTargets,
                _ => Array.Empty<TicketStatus>()
            };

            // New nigdy nie jest targetem z UI, nie zwracamy też bieżącego statusu
            return pool
                .Where(x => x != TicketStatus.New && x != currentStatus)
                .Distinct()
                .ToList();
        }

        public static TransitionDecision CanTransition(
            TicketStatus from,
            TicketStatus to,
            TicketRole role,
            DateTime now,
            DateTime? closedOrResolvedAtUtcOrLocal,
            Func<TicketField, bool> hasField)
        {
            _ = now;
            _ = closedOrResolvedAtUtcOrLocal;

            var decision = new TransitionDecision();

            if (hasField == null)
            {
                decision.Allowed = false;
                decision.Error = "Brak walidatora pól (hasField).";
                return decision;
            }

            // Cancelled jest końcowy
            if (from == TicketStatus.Cancelled)
            {
                decision.Allowed = false;
                decision.Error = "Status jest końcowy (Cancelled). Brak dozwolonych zmian.";
                return decision;
            }

            // Requester nie zmienia statusów
            if (role == TicketRole.Requester)
            {
                decision.Allowed = false;
                decision.Error = "Brak uprawnień do zmiany statusu.";
                return decision;
            }

            // New nie ustawiamy z UI nigdy
            if (to == TicketStatus.New)
            {
                decision.Allowed = false;
                decision.Error = "Nie można ustawić statusu New ręcznie.";
                return decision;
            }

            // Operator nie może ustawić Closed
            if (role == TicketRole.Operator && to == TicketStatus.Closed)
            {
                decision.Allowed = false;
                decision.Error = "Operator nie może ustawić statusu Closed.";
                return decision;
            }

            // Target musi być w dozwolonych
            var allowedTargets = GetAllowedTargets(from, role, now, closedOrResolvedAtUtcOrLocal);
            if (!allowedTargets.Contains(to))
            {
                decision.Allowed = false;
                decision.Error = "Przejście do wskazanego statusu nie jest dozwolone.";
                return decision;
            }

            // Waiting wymaga Reason + Comment
            if (to == TicketStatus.Waiting)
            {
                if (!hasField(TicketField.Reason))
                    decision.MissingFields.Add(TicketField.Reason);

                if (!hasField(TicketField.Comment))
                    decision.MissingFields.Add(TicketField.Comment);

                if (decision.MissingFields.Count > 0)
                {
                    decision.Allowed = false;
                    decision.Error = "Brak wymaganych danych do zmiany statusu.";
                    return decision;
                }
            }

            decision.Allowed = true;
            return decision;
        }
    }
}
