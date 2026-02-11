// File: Services/UserPreferencesService.cs
// Description: Preferencje użytkownika (dbo.users). Aktualnie obsługuje PreferredTimeZoneId.
// Version: 1.00
// Created: 2026-01-24

using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using ITManager.Services.Auth;

namespace ITManager.Services
{
    public sealed class UserPreferencesService
    {
        private readonly string _cs;
        private readonly CurrentUserContextService _current;

        public UserPreferencesService(IConfiguration cfg, CurrentUserContextService current)
        {
            _cs = cfg.GetConnectionString("ITManagerConnection")
                ?? throw new InvalidOperationException("Missing connection string: ConnectionStrings:ITManagerConnection");

            _current = current;
        }

        public async Task<string?> GetPreferredTimeZoneIdAsync()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId <= 0)
                return null;

            using var con = new SqlConnection(_cs);
            using var cmd = new SqlCommand(@"
SELECT PreferredTimeZoneId
FROM dbo.users
WHERE id = @id;", con);

            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = userId });

            await con.OpenAsync().ConfigureAwait(false);

            var val = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (val == null || val == DBNull.Value)
                return null;

            return val.ToString();
        }

        public async Task SetPreferredTimeZoneIdAsync(string timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId))
                return;

            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId <= 0)
                return;

            using var con = new SqlConnection(_cs);
            using var cmd = new SqlCommand(@"
UPDATE dbo.users
SET PreferredTimeZoneId = @tz
WHERE id = @id;", con);

            cmd.Parameters.Add(new SqlParameter("@tz", SqlDbType.NVarChar, 64) { Value = timeZoneId.Trim() });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = userId });

            await con.OpenAsync().ConfigureAwait(false);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task<int> GetCurrentUserIdAsync()
        {
            await _current.EnsureInitializedAsync().ConfigureAwait(false);

            if (!_current.HasAccess)
                return 0;

            return _current.CurrentUser.UserId ?? 0;
        }
    }
}
