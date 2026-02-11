// File: Models/AgreementListRow.cs
// Description: Model wiersza listy agreements dla MudDataGrid (IT Resources).
// Created: 2025-12-19
// Version: 1.00
// Change history:
// 1.00 (2025-12-19) - Initial version.

namespace ITManager.Models
{
    public sealed class AgreementListRow
    {
        // Uwaga: typy pól są string, aby uprościć integrację z istniejącymi widokami
        // i uniknąć problemów z formatowaniem dat po stronie UI. Jak będziesz chciał,
        // możemy to później zamienić na DateTime? i dodać formatowanie.

        public string AgreementName { get; set; } = string.Empty;
        public string AgreementType { get; set; } = string.Empty;
        public string Supplier { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string ValidFrom { get; set; } = string.Empty;
        public string ValidTo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
