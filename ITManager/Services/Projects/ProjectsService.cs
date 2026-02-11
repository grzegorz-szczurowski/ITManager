// File: Services/Projects/ProjectsService.cs
// Description: Warstwa logiki i RBAC dla modułu Projects (dopasowana do CurrentUserContextService).
// Version: 1.07
// Created: 2026-01-25
// Updated:
// - 2026-01-28 - Tickets: pobieranie kandydatów do powiązania oraz powiązanie ticketów z projektem (RBAC Projects.Edit + Tickets.View).
// - 2026-01-28 - FIX: ticketIds jako long (dbo.tickets.id bigint).

using ITManager.Models.Projects;
using ITManager.Services.Auth;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ITManager.Services.Projects
{
    public sealed class ProjectsService
    {
        private const string PermProjectsView = "Projects.View";
        private const string PermProjectsViewAll = "Projects.View.All";
        private const string PermProjectsCreate = "Projects.Create";
        private const string PermProjectsEdit = "Projects.Edit";

        private const string PermTicketsView = "Tickets.View";

        private readonly ProjectsRepository _repo;
        private readonly CurrentUserContextService _currentUserContextService;

        public ProjectsService(ProjectsRepository repo, CurrentUserContextService currentUserContextService)
        {
            _repo = repo;
            _currentUserContextService = currentUserContextService;
        }

        public async Task<ProjectFormLookupsDto> GetProjectFormLookupsAsync()
        {
            EnsureAnyPermission(PermProjectsView, PermProjectsCreate, PermProjectsEdit);
            return await _repo.GetProjectFormLookupsAsync().ConfigureAwait(false);
        }

        public async Task<List<ProjectListItemDto>> GetProjectsAsync()
        {
            EnsurePermission(PermProjectsView);

            var userId = GetCurrentUserIdOrThrow();
            var canViewAll = _currentUserContextService.Can(PermProjectsViewAll);

            var items = await _repo.GetProjectsForUserAsync(userId, canViewAll).ConfigureAwait(false);
            ApplyBusinessStages(items);
            return items;
        }

        public async Task<ProjectDetailsDto?> GetProjectDetailsAsync(int projectId)
        {
            EnsurePermission(PermProjectsView);

            var userId = GetCurrentUserIdOrThrow();
            var canViewAll = _currentUserContextService.Can(PermProjectsViewAll);

            var dto = await _repo.GetProjectDetailsAsync(projectId, userId, canViewAll).ConfigureAwait(false);
            if (dto != null)
                ApplyBusinessStage(dto);
            return dto;
        }

        public async Task<List<ProjectLookupItemDto>> GetProjectsLookupAsync()
        {
            EnsurePermission(PermProjectsView);

            var userId = GetCurrentUserIdOrThrow();
            var canViewAll = _currentUserContextService.Can(PermProjectsViewAll);

            return await _repo.GetProjectsLookupForUserAsync(userId, canViewAll).ConfigureAwait(false);
        }

        public async Task<int> CreateProjectAsync(ProjectUpsertRequest request)
        {
            EnsurePermission(PermProjectsCreate);

            var actorUserId = GetCurrentUserIdOrThrow();
            EnsureOwnerOrDefaultToActor(request, actorUserId);

            var actor = GetCurrentUserDisplayNameOrFallback();

            await ApplyStageSideEffectsAsync(request, isCreate: true).ConfigureAwait(false);
            await ValidateUpsertAsync(request).ConfigureAwait(false);

            return await _repo.CreateProjectAsync(request, createdByUserId: actorUserId, createdBy: actor)
                .ConfigureAwait(false);
        }

        public async Task UpdateProjectAsync(ProjectUpsertRequest request)
        {
            EnsurePermission(PermProjectsEdit);

            var actorUserId = GetCurrentUserIdOrThrow();
            EnsureOwnerOrDefaultToActor(request, actorUserId);

            var actor = GetCurrentUserDisplayNameOrFallback();

            await ApplyStageSideEffectsAsync(request, isCreate: false).ConfigureAwait(false);
            await ValidateUpsertAsync(request).ConfigureAwait(false);

            await _repo.UpdateProjectAsync(request, updatedBy: actor).ConfigureAwait(false);
        }

        public async Task<List<ProjectLinkableTicketDto>> GetTicketsLinkCandidatesAsync(int projectId, string search)
        {
            EnsurePermission(PermProjectsEdit);
            EnsurePermission(PermTicketsView);

            if (projectId <= 0)
                throw new InvalidOperationException("Nieprawidłowy projekt.");

            var userId = GetCurrentUserIdOrThrow();
            var canViewAllProjects = _currentUserContextService.Can(PermProjectsViewAll);

            var safeSearch = (search ?? string.Empty).Trim();
            if (safeSearch.Length > 200)
                safeSearch = safeSearch.Substring(0, 200);

            return await _repo.GetTicketsLinkCandidatesAsync(projectId, userId, canViewAllProjects, safeSearch)
                .ConfigureAwait(false);
        }

        public async Task LinkTicketsToProjectAsync(int projectId, List<long> ticketIds)
        {
            EnsurePermission(PermProjectsEdit);

            if (projectId <= 0)
                throw new InvalidOperationException("Nieprawidłowy projekt.");

            if (ticketIds == null || ticketIds.Count == 0)
                throw new InvalidOperationException("Nie wybrano ticketów do powiązania.");

            var ids = ticketIds.Where(x => x > 0).Distinct().ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException("Nie wybrano poprawnych ticketów.");

            var actorUserId = GetCurrentUserIdOrThrow();
            var actor = GetCurrentUserDisplayNameOrFallback();
            var canViewAllProjects = _currentUserContextService.Can(PermProjectsViewAll);

            await _repo.LinkTicketsToProjectAsync(projectId, ids, actorUserId, actor, canViewAllProjects)
                .ConfigureAwait(false);
        }

        private static void EnsureOwnerOrDefaultToActor(ProjectUpsertRequest request, int actorUserId)
        {
            if (request.OwnerUserId <= 0)
                request.OwnerUserId = actorUserId;
        }

        private void EnsurePermission(string permissionCode)
        {
            if (!_currentUserContextService.HasAccess)
                throw new InvalidOperationException("Brak dostępu aplikacyjnego.");

            if (!_currentUserContextService.Can(permissionCode))
                throw new InvalidOperationException("Brak uprawnień.");
        }

        private void EnsureAnyPermission(params string[] permissionCodes)
        {
            if (!_currentUserContextService.HasAccess)
                throw new InvalidOperationException("Brak dostępu aplikacyjnego.");

            foreach (var p in permissionCodes)
            {
                if (_currentUserContextService.Can(p))
                    return;
            }

            throw new InvalidOperationException("Brak uprawnień.");
        }

        private int GetCurrentUserIdOrThrow()
        {
            var ctx = _currentUserContextService.CurrentUser;

            if (ctx == null || !ctx.IsAuthenticated || !_currentUserContextService.HasAccess)
                throw new InvalidOperationException("Brak dostępu aplikacyjnego.");

            var id = TryGetIntProperty(ctx, "UserId")
                ?? TryGetIntProperty(ctx, "Id")
                ?? TryGetIntProperty(ctx, "user_id")
                ?? TryGetIntProperty(ctx, "userId")
                ?? TryGetIntProperty(ctx, "id");

            if (id.HasValue && id.Value > 0)
                return id.Value;

            throw new InvalidOperationException("Nie można ustalić identyfikatora użytkownika (CurrentUser.*Id).");
        }

        private string? GetCurrentUserDisplayNameOrFallback()
        {
            var ctx = _currentUserContextService.CurrentUser;

            if (ctx == null)
                return null;

            var name =
                TryGetStringProperty(ctx, "DisplayName")
                ?? TryGetStringProperty(ctx, "FullName")
                ?? TryGetStringProperty(ctx, "Name")
                ?? TryGetStringProperty(ctx, "Login");

            name = (name ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static int? TryGetIntProperty(object obj, string propertyName)
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (prop == null)
                return null;

            var val = prop.GetValue(obj);
            if (val == null)
                return null;

            if (val is int i)
                return i;

            if (val is long l && l <= int.MaxValue && l >= int.MinValue)
                return (int)l;

            if (val is short s)
                return s;

            return null;
        }

        private static string? TryGetStringProperty(object obj, string propertyName)
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (prop == null)
                return null;

            var val = prop.GetValue(obj);
            return val as string;
        }

        private async Task ApplyStageSideEffectsAsync(ProjectUpsertRequest request, bool isCreate)
        {
            var statusName = await _repo.GetProjectStatusNameByIdAsync(request.ProjectStatusId).ConfigureAwait(false);
            var stage = ProjectBusinessStageResolver.Resolve(statusName);

            request.ProgressPercent = stage == ProjectBusinessStage.Done ? 100 : 0;

            if (stage == ProjectBusinessStage.Validation || stage == ProjectBusinessStage.OnHold)
            {
                _ = isCreate;
            }
        }

        private async Task ValidateUpsertAsync(ProjectUpsertRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new InvalidOperationException("Project name is required.");

            var statusName = await _repo.GetProjectStatusNameByIdAsync(request.ProjectStatusId).ConfigureAwait(false);
            var stage = ProjectBusinessStageResolver.Resolve(statusName);

            if (stage == ProjectBusinessStage.OnHold || stage == ProjectBusinessStage.Validation)
            {
                if (string.IsNullOrWhiteSpace(request.LastUpdateNote))
                    throw new InvalidOperationException("Last update note is required for this stage.");
            }

            if (request.PlannedStartDate.HasValue && request.DeadlineDate.HasValue
                && request.DeadlineDate.Value.Date < request.PlannedStartDate.Value.Date)
            {
                throw new InvalidOperationException("Deadline must be greater or equal to planned start.");
            }

            if (request.Impact < 1 || request.Impact > 5)
                throw new InvalidOperationException("Impact must be between 1 and 5.");

            if (request.Urgency < 1 || request.Urgency > 5)
                throw new InvalidOperationException("Urgency must be between 1 and 5.");

            if (request.Scope < 1 || request.Scope > 3)
                throw new InvalidOperationException("Scope must be between 1 and 3.");

            if (request.CostOfDelay < 1 || request.CostOfDelay > 3)
                throw new InvalidOperationException("Cost of delay must be between 1 and 3.");

            if (request.Effort < 1 || request.Effort > 3)
                throw new InvalidOperationException("Effort must be between 1 and 3.");
        }

        private static void ApplyBusinessStages(List<ProjectListItemDto> items)
        {
            foreach (var it in items)
            {
                it.BusinessStage = ProjectBusinessStageResolver.Resolve(it.ProjectStatusName);
                it.BusinessStageName = ProjectBusinessStageResolver.ToDisplayName(it.BusinessStage);
            }
        }

        private static void ApplyBusinessStage(ProjectDetailsDto dto)
        {
            dto.BusinessStage = ProjectBusinessStageResolver.Resolve(dto.ProjectStatusName);
            dto.BusinessStageName = ProjectBusinessStageResolver.ToDisplayName(dto.BusinessStage);
        }
    }
}
