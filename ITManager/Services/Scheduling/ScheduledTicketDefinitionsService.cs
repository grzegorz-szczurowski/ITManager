// Path: Services/Scheduling/ScheduledTicketDefinitionsService.cs
// File: ScheduledTicketDefinitionsService.cs
// Description: Warstwa serwisowa dla UI do obsługi ScheduledTicketDefinitions (CRUD + enable).
// Created: 2026-01-03
// Version: 1.00

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ITManager.Services.Scheduling;

public sealed class ScheduledTicketDefinitionsService
{
    private readonly TicketSchedulingRepository _repo;

    public ScheduledTicketDefinitionsService(TicketSchedulingRepository repo)
    {
        _repo = repo;
    }

    public Task<List<ScheduledTicketDefinitionDto>> GetDefinitionsAsync(CancellationToken ct = default)
        => _repo.GetDefinitionsAsync(ct);

    public Task<ScheduledTicketDefinitionDto?> GetDefinitionAsync(int definitionId, CancellationToken ct = default)
        => _repo.GetDefinitionAsync(definitionId, ct);

    public Task<int> UpsertAsync(ScheduledTicketDefinitionDto model, int byUserId, CancellationToken ct = default)
        => _repo.UpsertDefinitionAsync(model, byUserId, ct);

    public Task SetEnabledAsync(int definitionId, bool enabled, int byUserId, CancellationToken ct = default)
        => _repo.SetDefinitionEnabledAsync(definitionId, enabled, byUserId, ct);
}
