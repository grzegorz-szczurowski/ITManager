// File: Models/ContactsPdfService.cs
// Description: Generowanie dokumentu PDF A4 (poziomo) z listą kontaktów do druku.
// Created: 2025-12-08
// Updated: 2025-12-08 - dopracowany wygląd PDF, poprawione łańcuchowanie metod (QuestPDF).

using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ITManager.Models
{
    public interface IContactsPdfService
    {
        byte[] GenerateContactsPdf(IEnumerable<Contact> contacts);
    }

    public class ContactsPdfService : IContactsPdfService
    {
        public byte[] GenerateContactsPdf(IEnumerable<Contact> contacts)
        {
            if (contacts == null)
            {
                contacts = Enumerable.Empty<Contact>();
            }

            try
            {
                var orderedContacts = contacts
                    .OrderBy(c => c.Nazwisko)
                    .ThenBy(c => c.Imie)
                    .ToList();

                var printDate = DateTime.Now;

                var document = Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4.Landscape());
                        page.Margin(15);
                        page.PageColor(Colors.White);

                        // Domyślny styl – mała, czytelna czcionka
                        page.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Black));

                        // Nagłówek strony
                        page.Header().Element(header =>
                        {
                            header.Row(row =>
                            {
                                // Lewa część: firma + tytuł
                                row.RelativeColumn().Stack(stack =>
                                {
                                    stack.Item()
                                        .Text("Tristone Flowtech Poland Sp. z o.o.")
                                        .SemiBold()
                                        .FontSize(9);

                                    stack.Item()
                                        .Text("Lista kontaktów")
                                        .SemiBold()
                                        .FontSize(11);
                                });

                                // Prawa część: data wydruku (bez godziny)
                                row.ConstantItem(140).AlignRight().Text(text =>
                                {
                                    text.Span("Data wydruku: ").FontSize(9);
                                    text.Span(printDate.ToString("yyyy-MM-dd")).FontSize(9);
                                });
                            });
                        });

                        // Tabela z danymi kontaktowymi
                        page.Content().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(2);  // Nazwisko
                                columns.RelativeColumn(2);  // Imię
                                columns.RelativeColumn(3);  // Dział
                                columns.RelativeColumn(4);  // Stanowisko
                                columns.RelativeColumn(2);  // Wewnętrzny
                                columns.RelativeColumn(2);  // Stacjonarny
                                columns.RelativeColumn(2);  // Komórkowy
                            });

                            // Nagłówki kolumn
                            table.Header(header =>
                            {
                                header.Cell().Element(HeaderCellStyle).Text("Nazwisko");
                                header.Cell().Element(HeaderCellStyle).Text("Imię");
                                header.Cell().Element(HeaderCellStyle).Text("Dział");
                                header.Cell().Element(HeaderCellStyle).Text("Stanowisko");
                                header.Cell().Element(HeaderCellStyle).Text("Wewnętrzny");
                                header.Cell().Element(HeaderCellStyle).Text("Stacjonarny");
                                header.Cell().Element(HeaderCellStyle).Text("Komórkowy");
                            });

                            // Wiersze danych
                            foreach (var contact in orderedContacts)
                            {
                                table.Cell().Element(CellStyle).Text(contact.Nazwisko ?? string.Empty);
                                table.Cell().Element(CellStyle).Text(contact.Imie ?? string.Empty);
                                table.Cell().Element(CellStyle).Text(contact.Dzial ?? string.Empty);
                                table.Cell().Element(CellStyle).Text(contact.Stanowisko ?? string.Empty);

                                table.Cell().Element(CellStyle)
                                    .AlignRight()
                                    .Text(contact.Wewnetrzny ?? string.Empty);

                                table.Cell().Element(CellStyle)
                                    .AlignRight()
                                    .Text(contact.Stacjonarny ?? string.Empty);

                                table.Cell().Element(CellStyle)
                                    .AlignRight()
                                    .Text(contact.Komorkowy ?? string.Empty);
                            }

                            // Styl nagłówków tabeli – delikatna szarość, cienka linia
                            static IContainer HeaderCellStyle(IContainer container)
                            {
                                return container
                                    .PaddingVertical(3)
                                    .PaddingHorizontal(2)
                                    .BorderBottom(0.75f)
                                    .BorderColor(Colors.Grey.Medium)
                                    .Background(Colors.Grey.Lighten3)
                                    .DefaultTextStyle(x => x.SemiBold());
                            }

                            // Styl komórek – cienkie linie, małe odstępy
                            static IContainer CellStyle(IContainer container)
                            {
                                return container
                                    .PaddingVertical(2)
                                    .PaddingHorizontal(2)
                                    .BorderBottom(0.25f)
                                    .BorderColor(Colors.Grey.Lighten3);
                            }
                        });

                        // Stopka z numerem strony
                        page.Footer().Element(footer =>
                        {
                            footer.AlignRight().Text(text =>
                            {
                                text.Span("Strona ").FontSize(7);
                                text.CurrentPageNumber();
                                text.Span(" z ").FontSize(7);
                                text.TotalPages();
                            });
                        });
                    });
                });

                return document.GeneratePdf();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ContactsPdfService] Błąd generowania PDF: {ex}");
                return Array.Empty<byte>();
            }
        }
    }
}
