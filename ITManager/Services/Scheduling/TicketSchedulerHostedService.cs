// Path: Services/Scheduling/TicketSchedulerHostedService.cs
// File: TicketSchedulerHostedService.cs
// Description: HostedService wykonujący harmonogram i tworzący tickety z ScheduledTicketDefinitions.
//              Zabezpieczenie single runner: sp_getapplock (na tym samym SqlConnection).
// Created: 2026-01-02
// Updated: 2026-01-03 - Migracja do ScheduledTicketDefinitions + deduplikacja przez UNIQUE index (always on).
// Updated: 2026-01-03 - FIX: wszystkie operacje runnera lecą na tym samym SqlConnection (applock działa).
// Updated: 2026-01-04 - Drobne poprawki: cancel w pętli, bezpieczne logowanie, porządek w flow.
// Version: 1.13

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ITManager.Services.Scheduling;

public sealed class TicketSchedulerHostedService : BackgroundService
{
    private readonly TicketSchedulingRepository _repo;
    private readonly ILogger<TicketSchedulerHostedService> _logger;

    private const int PollSeconds = 30;
    private const int MaxBatch = 20;

    public TicketSchedulerHostedService(TicketSchedulingRepository repo, ILogger<TicketSchedulerHostedService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[TicketScheduler] Started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TicketScheduler] Loop error.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignore
            }
        }

        _logger.LogInformation("[TicketScheduler] Stopped.");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nowUtc = DateTime.UtcNow;

        await using var conn = await _repo.OpenConnectionAsync(ct);

        var hasLock = await _repo.TryAcquireRunnerLockAsync(conn, ct);
        if (!hasLock)
            return;

        try
        {
            // Wszystkie wywołania poniżej idą na tym samym conn,
            // bo applock ma LockOwner=Session (żyje w sesji SQL).
            var due = await _repo.GetDueDefinitionsAsync(conn, nowUtc, MaxBatch, ct);
            if (due.Count == 0)
                return;

            foreach (var d in due)
            {
                ct.ThrowIfCancellationRequested();

                var sw = Stopwatch.StartNew();
                var occurrenceUtc = d.ScheduleNextRunAtUtc; // klucz wystąpienia (idempotencja)

                try
                {
                    // Render tokenów w lokalnym czasie (UI-friendly), ale stan i klucze w UTC
                    var nowLocal = _repo.ToPlantLocalTime(nowUtc);

                    var titleNoPrefix = RenderTemplate(d.TitleTemplate, nowLocal);
                    var title = "[OKRESOWE] " + titleNoPrefix;

                    var desc = string.IsNullOrWhiteSpace(d.DescriptionTemplate)
                        ? null
                        : RenderTemplate(d.DescriptionTemplate!, nowLocal);

                    var create = await _repo.TryCreateTicketFromDefinitionAsync(
                        conn,
                        d,
                        title,
                        desc,
                        occurrenceUtc,
                        nowUtc,
                        ct);

                    await _repo.InsertRunLogAsync(
                        conn,
                        definitionId: d.DefinitionId,
                        occurrenceUtc: occurrenceUtc,
                        resultCode: create.Created ? (byte)0 : (byte)1,
                        createdTicketId: create.TicketId,
                        message: create.Created ? "Created." : "Skipped (duplicate occurrence).",
                        durationMs: (int)sw.ElapsedMilliseconds,
                        ct: ct);

                    // Następne wystąpienie liczymy od occurrenceUtc (stabilniejsze)
                    var nextUtc = d.ComputeNextOccurrenceUtc(occurrenceUtc.AddSeconds(1));

                    await _repo.UpdateDefinitionAfterRunAsync(
                        conn,
                        d.DefinitionId,
                        lastRunAtUtc: nowUtc,
                        nextRunAtUtc: nextUtc,
                        ct: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TicketScheduler] Error definitionId={DefinitionId}", d.DefinitionId);

                    await _repo.InsertRunLogAsync(
                        conn,
                        definitionId: d.DefinitionId,
                        occurrenceUtc: occurrenceUtc,
                        resultCode: 4,
                        createdTicketId: null,
                        message: ex.Message,
                        durationMs: (int)sw.ElapsedMilliseconds,
                        ct: ct);

                    // Fail-safe: przesuń NextRun żeby nie mielić błędu co PollSeconds
                    var safeNext = nowUtc.AddMinutes(10);
                    try
                    {
                        await _repo.UpdateDefinitionAfterRunAsync(
                            conn,
                            d.DefinitionId,
                            lastRunAtUtc: nowUtc,
                            nextRunAtUtc: safeNext,
                            ct: ct);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
        finally
        {
            try { await _repo.ReleaseRunnerLockAsync(conn, ct); } catch { /* ignore */ }
        }
    }

    private static string RenderTemplate(string input, DateTime nowLocal)
    {
        var now = nowLocal;
        return (input ?? string.Empty)
            .Replace("{Now}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{Date}", now.ToString("yyyy-MM-dd"))
            .Replace("{Year}", now.Year.ToString())
            .Replace("{Month}", now.Month.ToString("00"))
            .Replace("{Day}", now.Day.ToString("00"));
    }
}
