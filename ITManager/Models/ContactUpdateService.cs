// File: Models/ContactUpdateService.cs
// Description: Serwis do wysyłania wiadomości e-mail z prośbą o aktualizację danych kontaktowych.
// Created: 2025-12-06
// Updated: 2025-12-08 - HTML body, scalone dane kontaktowe, pogrubienie zmian, informacja o użytkowniku, blokada dla niezalogowanych.
// Updated: 2026-01-28 - CHANGE: usunięto sprawdzanie zalogowanego użytkownika Windows; nadawca identyfikowany na podstawie danych kontaktu.
// Updated: 2026-01-28 - SECURITY: wymagane potwierdzenie, że użytkownik wysyła tylko dla własnego kontaktu (ownershipCheck).
// Updated: 2026-01-28 - CHANGE: "Użytkownik zgłaszający" bazuje na danych sprzed zmiany (currentContact).

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ITManager.Models
{
    /// <summary>
    /// Serwis odpowiedzialny za wysyłanie e-maili z prośbą o aktualizację danych kontaktowych.
    /// </summary>
    public class ContactUpdateService
    {
        private readonly IConfiguration _configuration;

        public ContactUpdateService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Wysyła wiadomość e-mail do administratora z prośbą o aktualizację danych kontaktu.
        /// Dane są scalone, a nowe wartości są pogrubione w treści HTML.
        /// Dodawana jest informacja o użytkowniku wysyłającym zgłoszenie.
        /// SECURITY: ownershipCheck musi potwierdzić, że bieżący użytkownik może wysłać prośbę tylko dla własnego kontaktu.
        /// </summary>
        public async Task SendContactUpdateRequestAsync(
            Contact currentContact,
            Contact updatedContact,
            string? userMessage,
            Func<bool> ownershipCheck)
        {
            if (currentContact == null)
                throw new ArgumentNullException(nameof(currentContact), "Aktualny kontakt nie może być nullem.");

            if (updatedContact == null)
                throw new ArgumentNullException(nameof(updatedContact), "Nowy kontakt nie może być nullem.");

            if (ownershipCheck == null)
                throw new ArgumentNullException(nameof(ownershipCheck), "ownershipCheck jest wymagany, aby wymusić wysyłkę tylko we własnym imieniu.");

            if (!ownershipCheck())
                throw new InvalidOperationException("Wysyłka zablokowana: prośba o aktualizację może być wysłana tylko dla własnego kontaktu.");

            // Użytkownik zgłaszający: dane sprzed zmiany (currentContact).
            // Jeśli currentContact ma braki, awaryjnie użyj updatedContact.
            var reporterDisplay =
                BuildReporterDisplay(currentContact)
                ?? BuildReporterDisplay(updatedContact)
                ?? "nieznany";

            // Odczyt konfiguracji SMTP z appsettings.json
            var smtpSection = _configuration.GetSection("Smtp");

            var host = smtpSection["Host"];
            var portString = smtpSection["Port"];
            var fromString = smtpSection["From"];
            var toString = smtpSection["To"];
            var enableSslString = smtpSection["EnableSsl"];
            var useDefaultCredentialsString = smtpSection["UseDefaultCredentials"];
            var userName = smtpSection["UserName"];
            var password = smtpSection["Password"];

            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Smtp:Host nie jest ustawiony w appsettings.json.");

            if (string.IsNullOrWhiteSpace(fromString))
                throw new InvalidOperationException("Smtp:From nie jest ustawiony w appsettings.json.");

            if (string.IsNullOrWhiteSpace(toString))
                throw new InvalidOperationException("Smtp:To nie jest ustawiony w appsettings.json.");

            host = host.Trim();
            fromString = fromString.Trim();
            toString = toString.Trim();

#if NET8_0_OR_GREATER
            if (!MailAddress.TryCreate(fromString, out var fromAddress))
                throw new InvalidOperationException($"Smtp:From jest nieprawidłowym adresem e-mail: \"{fromString}\".");
#else
            var fromAddress = new MailAddress(fromString);
#endif

            // Obsługa wielu adresów w polu To, rozdzielanych ; lub ,
            var toAddresses = new List<MailAddress>();
            var toParts = toString.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawPart in toParts)
            {
                var part = rawPart.Trim();
                if (string.IsNullOrWhiteSpace(part))
                    continue;

#if NET8_0_OR_GREATER
                if (!MailAddress.TryCreate(part, out var toAddr))
                    throw new InvalidOperationException($"Jeden z adresów w Smtp:To ma nieprawidłowy format: \"{part}\".");
#else
                var toAddr = new MailAddress(part);
#endif
                toAddresses.Add(toAddr);
            }

            if (toAddresses.Count == 0)
                throw new InvalidOperationException("Smtp:To nie zawiera żadnego poprawnego adresu e-mail.");

            // Port SMTP z bezpiecznym parsowaniem
            var port = 25;
            if (!string.IsNullOrWhiteSpace(portString))
            {
                if (!int.TryParse(portString, out port))
                {
                    Console.Error.WriteLine($"[ContactUpdateService] Ostrzeżenie: Smtp:Port ma nieprawidłową wartość \"{portString}\". Używam domyślnego portu 25.");
                    port = 25;
                }
            }

            // SSL z bezpiecznym parsowaniem
            var enableSsl = false;
            if (!string.IsNullOrWhiteSpace(enableSslString))
            {
                if (!bool.TryParse(enableSslString, out enableSsl))
                {
                    Console.Error.WriteLine($"[ContactUpdateService] Ostrzeżenie: Smtp:EnableSsl ma nieprawidłową wartość \"{enableSslString}\". Używam domyślnej wartości false.");
                    enableSsl = false;
                }
            }

            // UseDefaultCredentials z bezpiecznym parsowaniem
            var useDefaultCredentials = false;
            if (!string.IsNullOrWhiteSpace(useDefaultCredentialsString))
            {
                if (!bool.TryParse(useDefaultCredentialsString, out useDefaultCredentials))
                {
                    Console.Error.WriteLine($"[ContactUpdateService] Ostrzeżenie: Smtp:UseDefaultCredentials ma nieprawidłową wartość \"{useDefaultCredentialsString}\". Używam domyślnej wartości false.");
                    useDefaultCredentials = false;
                }
            }

            // Budowa treści maila w formacie HTML
            var bodyBuilder = new StringBuilder();

            bodyBuilder.AppendLine("<html>");
            bodyBuilder.AppendLine("<body style=\"font-family:Segoe UI, Arial, sans-serif; font-size:14px; color:#333333;\">");

            bodyBuilder.AppendLine("<p>Otrzymano prośbę o aktualizację danych kontaktowych z portalu <strong>ITManager</strong>.</p>");

            bodyBuilder.AppendLine("<p>");
            bodyBuilder.AppendLine($"<strong>Użytkownik zgłaszający:</strong> {WebUtility.HtmlEncode(reporterDisplay)}");
            bodyBuilder.AppendLine("</p>");

            bodyBuilder.AppendLine("<p><strong>Dane kontaktowe po zmianach:</strong></p>");
            bodyBuilder.AppendLine("<ul>");

            AppendMergedField(bodyBuilder, "Imię", currentContact.Imie, updatedContact.Imie);
            AppendMergedField(bodyBuilder, "Nazwisko", currentContact.Nazwisko, updatedContact.Nazwisko);
            AppendMergedField(bodyBuilder, "Dział", currentContact.Dzial, updatedContact.Dzial);
            AppendMergedField(bodyBuilder, "Stanowisko", currentContact.Stanowisko, updatedContact.Stanowisko);
            AppendMergedField(bodyBuilder, "Telefon wewnętrzny", currentContact.Wewnetrzny, updatedContact.Wewnetrzny);
            AppendMergedField(bodyBuilder, "Telefon stacjonarny", currentContact.Stacjonarny, updatedContact.Stacjonarny);
            AppendMergedField(bodyBuilder, "Telefon komórkowy", currentContact.Komorkowy, updatedContact.Komorkowy);
            AppendMergedField(bodyBuilder, "E-mail", currentContact.Email, updatedContact.Email);

            bodyBuilder.AppendLine("</ul>");

            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                bodyBuilder.AppendLine("<p><strong>Wiadomość od użytkownika:</strong></p>");
                bodyBuilder.AppendLine("<pre style=\"background-color:#f5f5f5; padding:8px; border-radius:4px; white-space:pre-wrap;\">");
                bodyBuilder.AppendLine(WebUtility.HtmlEncode(userMessage));
                bodyBuilder.AppendLine("</pre>");
            }

            bodyBuilder.AppendLine("<p style=\"margin-top:16px; font-size:12px; color:#777777;\">");
            bodyBuilder.AppendLine("Wiadomość wygenerowana automatycznie przez system ITManager.");
            bodyBuilder.AppendLine("</p>");

            bodyBuilder.AppendLine("</body>");
            bodyBuilder.AppendLine("</html>");

            var subject = $"ITManager: prośba o aktualizację danych kontaktu {updatedContact.Imie} {updatedContact.Nazwisko}";
            var body = bodyBuilder.ToString();

            using var smtpClient = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl
            };

            if (useDefaultCredentials)
            {
                smtpClient.UseDefaultCredentials = true;
            }
            else if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(password))
            {
                smtpClient.UseDefaultCredentials = false;
                smtpClient.Credentials = new NetworkCredential(userName, password);
            }
            else
            {
                smtpClient.UseDefaultCredentials = false;
            }

            using var message = new MailMessage
            {
                From = fromAddress,
                Subject = subject,
                Body = body,
                IsBodyHtml = true,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8
            };

            foreach (var addr in toAddresses)
            {
                message.To.Add(addr);
            }

            await smtpClient.SendMailAsync(message);

            Console.WriteLine("[ContactUpdateService] Wiadomość e-mail z prośbą o aktualizację danych została wysłana.");
        }

        private static string? BuildReporterDisplay(Contact c)
        {
            if (c == null) return null;

            var first = string.IsNullOrWhiteSpace(c.Imie) ? null : c.Imie.Trim();
            var last = string.IsNullOrWhiteSpace(c.Nazwisko) ? null : c.Nazwisko.Trim();

            if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last))
                return $"{first} {last}".Trim();

            var email = string.IsNullOrWhiteSpace(c.Email) ? null : c.Email.Trim();
            if (!string.IsNullOrWhiteSpace(email))
                return email;

            return null;
        }

        private static void AppendMergedField(StringBuilder sb, string label, string? oldValue, string? newValue)
        {
            var oldTrim = string.IsNullOrWhiteSpace(oldValue) ? null : oldValue.Trim();
            var newTrim = string.IsNullOrWhiteSpace(newValue) ? null : newValue.Trim();

            if (string.IsNullOrEmpty(oldTrim) && string.IsNullOrEmpty(newTrim))
            {
                return;
            }

            var labelHtml = WebUtility.HtmlEncode(label);
            var oldHtml = oldTrim != null ? WebUtility.HtmlEncode(oldTrim) : null;
            var newHtml = newTrim != null ? WebUtility.HtmlEncode(newTrim) : null;

            if (string.Equals(oldTrim, newTrim, StringComparison.OrdinalIgnoreCase))
            {
                var valueHtml = oldHtml ?? newHtml ?? string.Empty;
                sb.AppendLine($"<li>{labelHtml}: {valueHtml}</li>");
                return;
            }

            if (string.IsNullOrEmpty(oldHtml) && !string.IsNullOrEmpty(newHtml))
            {
                sb.AppendLine($"<li>{labelHtml}: <b>{newHtml}</b></li>");
            }
            else if (!string.IsNullOrEmpty(oldHtml) && string.IsNullOrEmpty(newHtml))
            {
                sb.AppendLine($"<li>{labelHtml}: (wartość usunięta, wcześniej: {oldHtml})</li>");
            }
            else
            {
                sb.AppendLine($"<li>{labelHtml}: <b>{newHtml}</b> (wcześniej: {oldHtml})</li>");
            }
        }
    }
}
