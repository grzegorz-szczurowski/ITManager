// File: Services/DictionariesService.cs
// Description: CRUD słowników opartych o istniejące tabele, z jednym modelem UI.
//              Naprawa Int64->Int32: bezpieczny odczyt ID z DB (BIGINT) dla UI (int).
// Created: 2025-12-23
// Version: 1.02
// Change log:
// - 1.01 (2025-12-25) Fix: odczyt BIGINT przez Convert.ToInt32, parametry Id/ParentId jako BigInt.
// - 1.02 (2025-12-27) Locations: usunięcie obsługi kolumny code (brak w DB). Odczyt/zapis tylko name + is_active.

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using ITManager.Models;

namespace ITManager.Services
{
    public sealed class DictionariesService
    {
        private const string ConnectionStringName = "ITManagerConnection";
        private readonly IConfiguration _configuration;

        public DictionariesService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        private string GetCs()
        {
            var cs = _configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException($"Brak ConnectionString '{ConnectionStringName}'.");
            return cs;
        }

        // =====================
        // PRODUCERS
        // =====================

        public async Task<List<DictionaryItem>> GetProducersAsync()
        {
            const string sql = @"
SELECT id, name, is_active
FROM dbo.producers
ORDER BY name;";

            var result = new List<DictionaryItem>();

            using var con = new SqlConnection(GetCs());
            using var cmd = new SqlCommand(sql, con);

            await con.OpenAsync().ConfigureAwait(false);
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new DictionaryItem
                {
                    // id bywa BIGINT, więc nie używamy GetInt32
                    Id = ReadInt32(r, 0),
                    Name = ReadString(r, 1),
                    IsActive = ReadBool(r, 2)
                });
            }

            return result;
        }

        public async Task SaveProducerAsync(DictionaryItem item)
        {
            if (item is null) throw new ArgumentNullException(nameof(item));

            var isInsert = item.Id <= 0;

            var sql = isInsert
                ? @"INSERT INTO dbo.producers (name, is_active)
                   VALUES (@Name, @IsActive);"
                : @"UPDATE dbo.producers
                    SET name = @Name,
                        is_active = @IsActive
                    WHERE id = @Id;";

            using var con = new SqlConnection(GetCs());
            using var cmd = new SqlCommand(sql, con);

            if (!isInsert)
                cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = item.Id;

            cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = (item.Name ?? string.Empty).Trim();
            cmd.Parameters.Add("@IsActive", SqlDbType.Bit).Value = item.IsActive;

            await con.OpenAsync().ConfigureAwait(false);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // =====================
        // MODELS
        // =====================

        public async Task<List<DictionaryItem>> GetModelsAsync()
        {
            const string sql = @"
SELECT m.id,
       m.name,
       m.producer_id,
       p.name,
       m.is_active
FROM dbo.models m
LEFT JOIN dbo.producers p ON p.id = m.producer_id
ORDER BY p.name, m.name;";

            var result = new List<DictionaryItem>();

            using var con = new SqlConnection(GetCs());
            using var cmd = new SqlCommand(sql, con);

            await con.OpenAsync().ConfigureAwait(false);
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new DictionaryItem
                {
                    Id = ReadInt32(r, 0),
                    Name = ReadString(r, 1),
                    ParentId = r.IsDBNull(2) ? null : ReadInt32(r, 2),
                    ParentName = r.IsDBNull(3) ? null : ReadString(r, 3),
                    IsActive = ReadBool(r, 4)
                });
            }

            return result;
        }

        public async Task SaveModelAsync(DictionaryItem item)
        {
            if (item is null) throw new ArgumentNullException(nameof(item));

            var isInsert = item.Id <= 0;

            var sql = isInsert
                ? @"INSERT INTO dbo.models (name, producer_id, is_active)
                   VALUES (@Name, @ParentId, @IsActive);"
                : @"UPDATE dbo.models
                    SET name = @Name,
                        producer_id = @ParentId,
                        is_active = @IsActive
                    WHERE id = @Id;";

            using var con = new SqlConnection(GetCs());
            using var cmd = new SqlCommand(sql, con);

            if (!isInsert)
                cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = item.Id;

            cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = (item.Name ?? string.Empty).Trim();

            var parentValue = (object?)item.ParentId ?? DBNull.Value;
            cmd.Parameters.Add("@ParentId", SqlDbType.BigInt).Value = parentValue;

            cmd.Parameters.Add("@IsActive", SqlDbType.Bit).Value = item.IsActive;

            await con.OpenAsync().ConfigureAwait(false);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // =====================
        // LOCATIONS
        // =====================

        public async Task<List<DictionaryItem>> GetLocationsAsync()
        {
            const string sql = @"
SELECT id, name, is_active
FROM dbo.locations
ORDER BY name;";

            var result = new List<DictionaryItem>();

            using var con = new SqlConnection(GetCs());
            using var cmd = new SqlCommand(sql, con);

            await con.OpenAsync().ConfigureAwait(false);
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new DictionaryItem
                {
                    Id = ReadInt32(r, 0),
                    Name = ReadString(r, 1),
                    IsActive = ReadBool(r, 2)
                });
            }

            return result;
        }

        public async Task SaveLocationAsync(DictionaryItem item)
        {
            if (item is null) throw new ArgumentNullException(nameof(item));

            var isInsert = item.Id <= 0;

            var sql = isInsert
                ? @"INSERT INTO dbo.locations (name, is_active)
                   VALUES (@Name, @IsActive);"
                : @"UPDATE dbo.locations
                    SET name = @Name,
                        is_active = @IsActive
                    WHERE id = @Id;";

            using var con = new SqlConnection(GetCs());
            using var cmd = new SqlCommand(sql, con);

            if (!isInsert)
                cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = item.Id;

            cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = (item.Name ?? string.Empty).Trim();
            cmd.Parameters.Add("@IsActive", SqlDbType.Bit).Value = item.IsActive;

            await con.OpenAsync().ConfigureAwait(false);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // =====================
        // Helpers (jak dla siebie za pół roku)
        // =====================

        private static int ReadInt32(SqlDataReader r, int ordinal)
        {
            if (r.IsDBNull(ordinal))
                return 0;

            // id bywa BIGINT, ale UI używa int, więc konwertujemy.
            // Jeśli kiedyś Id przekroczy int.MaxValue, wtedy trzeba będzie zmienić model na long.
            return Convert.ToInt32(r.GetValue(ordinal));
        }

        private static string ReadString(SqlDataReader r, int ordinal)
        {
            if (r.IsDBNull(ordinal))
                return string.Empty;

            return Convert.ToString(r.GetValue(ordinal)) ?? string.Empty;
        }

        private static bool ReadBool(SqlDataReader r, int ordinal)
        {
            if (r.IsDBNull(ordinal))
                return false;

            return Convert.ToBoolean(r.GetValue(ordinal));
        }
    }
}
