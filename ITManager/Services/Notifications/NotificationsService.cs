// File: Services/Notifications/NotificationsService.cs
// Description: Serwis aplikacyjny dla in-app notifications.
//              Wykonuje RBAC i deleguje ADO.NET do NotificationsRepository.
// Version: 1.11
// Updated: 2026-02-12
// Change log:
//   - 1.11 (2026-02-12) FIX: kompatybilność z repozytorium opartym o EventType tinyint (bez zmian API publicznego).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ITManager.Models.Notifications;
using ITManager.Services.Auth;
using Microsoft.Extensions.Logging;

namespace ITManager.Services.Notifications;

public sealed class NotificationsService
{
    private const string PermView = "Notifications.View";
    private const string PermMarkRead = "Notifications.MarkRead";

    private readonly NotificationsRepository _repo;
    private readonly CurrentUserContextService _currentUserContextService;
    private readonly ILogger<NotificationsService> _logger;

    public NotificationsService(
        NotificationsRepository repo,
        CurrentUserContextService currentUserContextService,
        ILogger<NotificationsService> logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _currentUserContextService = currentUserContextService ?? throw new ArgumentNullException(nameof(currentUserContextService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> GetUnreadCountForCurrentUserAsync(CancellationToken ct = default)
    {
        await GuardCanViewAsync(ct);

        var userId = GetCurrentUserIdOrThrow();

        try
        {
            return await _repo.GetUnreadCountAsync(userId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationsService.GetUnreadCountForCurrentUserAsync failed for userId={UserId}", userId);
            throw;
        }
    }

    public async Task<IReadOnlyList<NotificationRow>> GetLatestForCurrentUserAsync(int top, CancellationToken ct = default)
    {
        await GuardCanViewAsync(ct);

        var userId = GetCurrentUserIdOrThrow();

        if (top < 1) top = 1;
        if (top > 200) top = 200;

        try
        {
            return await _repo.GetLatestAsync(userId, top, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationsService.GetLatestForCurrentUserAsync failed for userId={UserId} top={Top}", userId, top);
            throw;
        }
    }

    public async Task<int> MarkAsReadForCurrentUserAsync(long notificationId, CancellationToken ct = default)
    {
        await GuardCanMarkReadAsync(ct);

        var userId = GetCurrentUserIdOrThrow();

        try
        {
            return await _repo.MarkAsReadAsync(notificationId, userId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationsService.MarkAsReadForCurrentUserAsync failed for userId={UserId} notificationId={NotificationId}", userId, notificationId);
            throw;
        }
    }

    public async Task<int> MarkAllAsReadForCurrentUserAsync(CancellationToken ct = default)
    {
        await GuardCanMarkReadAsync(ct);

        var userId = GetCurrentUserIdOrThrow();

        try
        {
            return await _repo.MarkAllAsReadAsync(userId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationsService.MarkAllAsReadForCurrentUserAsync failed for userId={UserId}", userId);
            throw;
        }
    }

    private async Task GuardCanViewAsync(CancellationToken ct)
    {
        _ = ct;
        var ctx = _currentUserContextService.CurrentUser;

        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (!ctx.Has(PermView))
            throw new UnauthorizedAccessException("Brak uprawnień do przeglądania powiadomień.");
    }

    private async Task GuardCanMarkReadAsync(CancellationToken ct)
    {
        _ = ct;
        var ctx = _currentUserContextService.CurrentUser;

        if (!ctx.IsAuthenticated)
            throw new UnauthorizedAccessException("Użytkownik niezalogowany.");

        if (!ctx.Has(PermMarkRead))
            throw new UnauthorizedAccessException("Brak uprawnień do oznaczania powiadomień jako przeczytane.");
    }

    private int GetCurrentUserIdOrThrow()
    {
        var ctx = _currentUserContextService.CurrentUser;
        if (ctx.UserId is null)
            throw new UnauthorizedAccessException("Brak UserId w kontekście użytkownika.");
        return ctx.UserId.Value;
    }
}
