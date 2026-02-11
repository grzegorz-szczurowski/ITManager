// File: Services/AgreementsService.cs
// Description: Serwis do odczytu agreements (IT Resources) z bazy ITManager.
// Created: 2025-12-19
// Version: 1.00
// Change history:
// 1.00 (2025-12-19) - Initial version.

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using ITManager.Models;

namespace ITManager.Services
{
    public sealed class AgreementsService
    {
        private readonly string _connectionString;

        public AgreementsService(IConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            _connectionString = configuration.GetConnectionString("ITManagerConnection")
                ?? throw new InvalidOperationException("Missing connection string: ITManagerConnection");
        }

        public async Task<GetAgreementsResult> GetAgreementsAsync()
        {
            // Ważne: poniższy SQL jest wzorcem. Nazwę widoku/tabeli i kolumny dopasuj
            // do tego co masz w bazie. Na start daję sensowne domyślne nazwy.
            const string sql = @"
SELECT
    AgreementName,
    AgreementType,
    Supplier,
    Owner,
    ValidFrom,
    ValidTo,
    Status
FROM dbo.vw_agreements
ORDER BY AgreementName;";

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                using (var command = new SqlCommand(sql, connection))
                {
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = 30;

                    await connection.OpenAsync();

                    var rows = new List<AgreementListRow>();

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var row = new AgreementListRow
                            {
                                AgreementName = ReadString(reader, "AgreementName"),
                                AgreementType = ReadString(reader, "AgreementType"),
                                Supplier = ReadString(reader, "Supplier"),
                                Owner = ReadString(reader, "Owner"),
                                ValidFrom = ReadString(reader, "ValidFrom"),
                                ValidTo = ReadString(reader, "ValidTo"),
                                Status = ReadString(reader, "Status"),
                            };

                            rows.Add(row);
                        }
                    }

                    return GetAgreementsResult.Success(rows);
                }
            }
            catch (SqlException ex)
            {
                // Typowy błąd na tym etapie: widok dbo.vw_agreements nie istnieje.
                // Wtedy zobaczysz w UI błąd i od razu wiesz co poprawić w zapytaniu.
                Console.Error.WriteLine($"[AgreementsService] SQL ERROR: {ex}");
                return GetAgreementsResult.Fail("Błąd SQL podczas ładowania agreements. Sprawdź widok dbo.vw_agreements i nazwy kolumn.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AgreementsService] ERROR: {ex}");
                return GetAgreementsResult.Fail("Błąd podczas ładowania agreements.");
            }
        }

        private static string ReadString(SqlDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal))
                return string.Empty;

            return Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? string.Empty;
        }
    }

    public sealed class GetAgreementsResult
    {
        public bool Ok { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;
        public List<AgreementListRow>? Rows { get; private set; }

        private GetAgreementsResult() { }

        public static GetAgreementsResult Success(List<AgreementListRow> rows)
            => new GetAgreementsResult { Ok = true, Rows = rows ?? new List<AgreementListRow>() };

        public static GetAgreementsResult Fail(string errorMessage)
            => new GetAgreementsResult { Ok = false, ErrorMessage = errorMessage ?? "Unknown error" };
    }
}
