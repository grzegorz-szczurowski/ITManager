// File: Services/TimeZoneService.cs
// Description: Konwersje czasu UTC -> strefa użytkownika na podstawie dbo.users.PreferredTimeZoneId.
// Version: 1.02
// Created: 2026-01-24
// Updated: 2026-02-12 - UX: dodane FormatUtcForUser (bez sync call do DB) na potrzeby gridów.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ITManager.Services
{
    public sealed class TimeZoneService
    {
        private readonly UserPreferencesService _prefs;
        private TimeZoneInfo? _cachedTz;

        private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
        {
            // Minimum dla Twojego case: Poland
            { "Europe/Warsaw", "Central European Standard Time" },

            // Kilka popularnych, żeby było praktycznie
            { "Etc/UTC", "UTC" },
            { "Europe/Prague", "Central Europe Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" },
            { "Europe/Paris", "Romance Standard Time" },
            { "Europe/London", "GMT Standard Time" }
        };

        public TimeZoneService(UserPreferencesService prefs)
        {
            _prefs = prefs;
        }

        public async Task<TimeZoneInfo> GetUserTimeZoneAsync()
        {
            if (_cachedTz != null)
                return _cachedTz;

            var id = await _prefs.GetPreferredTimeZoneIdAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id))
            {
                _cachedTz = TimeZoneInfo.Utc;
                return _cachedTz;
            }

            var resolvedId = ResolveTimeZoneId(id);

            try
            {
                _cachedTz = TimeZoneInfo.FindSystemTimeZoneById(resolvedId);
            }
            catch
            {
                _cachedTz = TimeZoneInfo.Utc;
            }

            return _cachedTz;
        }

        public async Task<DateTime> FromUtcAsync(DateTime utc)
        {
            var tz = await GetUserTimeZoneAsync().ConfigureAwait(false);

            var safeUtc = utc;
            if (safeUtc.Kind != DateTimeKind.Utc)
                safeUtc = DateTime.SpecifyKind(safeUtc, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(safeUtc, tz);
        }

        public async Task<DateTime?> FromUtcAsync(DateTime? utc)
        {
            if (!utc.HasValue)
                return null;

            return await FromUtcAsync(utc.Value).ConfigureAwait(false);
        }

        public void InvalidateCache()
        {
            _cachedTz = null;
        }

        public bool HasCachedTimeZone => _cachedTz != null;

        public string FormatUtcForUser(DateTime utc, string format = "yyyy-MM-dd HH:mm")
        {
            // Celowo bez blokowania async, formatowanie działa po wcześniejszym załadowaniu cache.
            // Gdy cache nie jest dostępny, pokazujemy UTC, aby uniknąć sync-IO.

            var safeUtc = utc;
            if (safeUtc.Kind != DateTimeKind.Utc)
                safeUtc = DateTime.SpecifyKind(safeUtc, DateTimeKind.Utc);

            var tz = _cachedTz;
            if (tz == null)
                return safeUtc.ToString(format);

            var local = TimeZoneInfo.ConvertTimeFromUtc(safeUtc, tz);
            return local.ToString(format);
        }

        private static string ResolveTimeZoneId(string id)
        {
            var trimmed = id.Trim();

            if (IanaToWindows.TryGetValue(trimmed, out var windowsId))
                return windowsId;

            return trimmed;
        }
    }
}
