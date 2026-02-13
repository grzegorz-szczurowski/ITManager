// File: Program.cs
// Description: Konfiguracja aplikacji ITManager.(.NET 8, Blazor Server, MudBlazor) z lokalizacj¹ PL/EN.
// Created: 2025-12-05
// Updated: 2025-12-11 - serwis PDF, endpoint wydruku, HttpContextAccessor do Windows Auth, serwis Tickets.
// Updated: 2025-12-13 - dodanie wielojêcznoœci (PL/EN), RequestLocalization, endpoint zmiany jêzyka (cookie).
// Updated: 2025-12-15 - dodanie pipeline uwierzytelniania i autoryzacji (Windows Auth pod IIS) + serwisy auth (ITManager roles).
// Updated: 2025-12-19 - wersja 1.01 - resx obok komponentów (bez ResourcesPath), PL jako domyœlny, walidacja /set-culture i bezpieczniejsze cookie.
// Updated: 2025-12-23 - RBAC: IAppAuthorizationService -> RbacAuthorizationService.
// Updated: 2025-12-29 - wersja 1.03 - pe³ne nazwy kultur: pl-PL i en-US (RequestLocalization + /set-culture).
// Updated: 2026-01-08 - wersja 1.04 - IIS AutomaticAuthentication (Windows user auto) + endpoint /whoami.
// Updated: 2026-01-08 - wersja 1.05 - RBAC policies Perm:<CODE> (User + Guest) + endpoint /whoami-rbac (diagnostyka).
// Updated: 2026-01-09 - wersja 1.06 - Windows only: FallbackPolicy wymaga zalogowania (bez Anonymous).
// Updated: 2026-01-09 - wersza 1.07 - Dynamiczny PolicyProvider dla "Perm:<CODE>" + handler scoped (fix DI lifetime).
// Updated: 2026-01-25 - wersja 1.08 - DetailedErrors tylko w Development + DeveloperExceptionPage w Development.
// Updated: 2026-01-30 - wersja 1.09 - FIX: ustawiony domyœlny scheme Negotiate dla Challenge (dzia³a lokalnie i pod IIS).
// Updated: 2026-02-07 - wersja 1.10 - Tickets: rejestracja TicketsQueryService, TicketsCommandService oraz TicketEventsRepository (migracja CQRS + eventy).
// Updated: 2026-02-11 - wersja 1.11 - Notifications: rejestracja NotificationsRepository i NotificationsService (in-app).
// Updated: 2026-02-11 - wersja 1.12 - FIX: TicketEventsRepository nie przyjmuje connection stringa, poprawione komunikaty o CS.
// Updated: 2026-02-11 - wersja 1.13 - Reports: rejestracja GlovesReportService (fix zawieszania strony /reports/gloves).
// Version: 1.13

using ITManager.Components;
using ITManager.Models;
using ITManager.Models.Auth;
using ITManager.Services;
using ITManager.Services.Auth;
using ITManager.Services.Notifications;
using ITManager.Services.Reports;
using ITManager.Services.Scheduling;
using ITManager.Services.Security;
using ITManager.Services.Tickets;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using QuestPDF.Infrastructure;
using System;
using System.Globalization;
using System.Linq;
using TristoneHub.Services.Reports;

var builder = WebApplication.CreateBuilder(args);

//
// IIS Windows Auth: automatycznie wype³nia HttpContext.User.
// W trybie Windows only, Anonymous jest wy³¹czone w IIS.
//
builder.Services.Configure<IISOptions>(options =>
{
    options.AutomaticAuthentication = true;
});

builder.Services.Configure<IISServerOptions>(options =>
{
    options.AutomaticAuthentication = true;
});

//
// Lokalizacja
//
builder.Services.AddLocalization();

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[]
    {
        new CultureInfo("pl-PL"),
        new CultureInfo("en-US"),
    };

    options.DefaultRequestCulture = new RequestCulture("pl-PL");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;

    options.RequestCultureProviders =
    [
        new QueryStringRequestCultureProvider(),
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
});

// Razor Components (Blazor Server)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

//
// Uwierzytelnianie i autoryzacja
// FIX: ustawiamy domyœlny scheme na Negotiate, ¿eby Challenge dzia³a³ tak¿e lokalnie (Kestrel/VS Debug).
//
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

// Windows only: wszystko domyœlnie wymaga zalogowania
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

//
// RBAC: dynamiczne policies "Perm:<CODE>" + handler
//
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// DetailedErrors: tylko Development (PROD ma byæ "cichy")
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(o => o.DetailedErrors = builder.Environment.IsDevelopment());

// MudBlazor
builder.Services.AddMudServices();

// Serwisy aplikacyjne
builder.Services.AddScoped<ContactsService>();
builder.Services.AddScoped<ContactUpdateService>();
builder.Services.AddScoped<IContactsPdfService, ContactsPdfService>();

// Tickets: migracja CQRS + eventy (bez ruszania Razorów)
// UWAGA: TicketsService zostaje, bo Razory go u¿ywaj¹. Nowe serwisy s¹ wstrzykiwane do TicketsService.

// TicketEventsRepository NIE ma connection stringa w konstruktorze. To repo dzia³a na istniej¹cym SqlConnection/SqlTransaction.
builder.Services.AddScoped<TicketEventsRepository>();

builder.Services.AddScoped<TicketsQueryService>(sp =>
{
    var cs = builder.Configuration.GetConnectionString("ITManagerConnection");
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException("Brak connection stringa: ConnectionStrings:ITManagerConnection");

    // Odporne na ró¿ne podpisy konstruktorów (np. (string, ctx), (string, ctx, logger), itd.)
    return ActivatorUtilities.CreateInstance<TicketsQueryService>(sp, cs);
});

builder.Services.AddScoped<TicketsCommandService>(sp =>
{
    var cs = builder.Configuration.GetConnectionString("ITManagerConnection");
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException("Brak connection stringa: ConnectionStrings:ITManagerConnection");

    // Odporne na ró¿ne podpisy konstruktorów (u Ciebie jest (string, CurrentUserContextService, TicketEventsRepository))
    return ActivatorUtilities.CreateInstance<TicketsCommandService>(sp, cs);
});

builder.Services.AddScoped<TicketsService>();

// Notifications: in-app
builder.Services.AddScoped<NotificationsRepository>();
builder.Services.AddScoped<NotificationsService>();

builder.Services.AddScoped<DevicesService>();
builder.Services.AddScoped<LicensesService>();
builder.Services.AddScoped<SimCardsService>();
builder.Services.AddScoped<SystemsService>();
builder.Services.AddScoped<UsersService>();
builder.Services.AddScoped<AgreementsService>();
builder.Services.AddScoped<DictionariesService>();
builder.Services.AddScoped<PermissionsService>();
builder.Services.AddScoped<UserRolesAdminService>();
builder.Services.AddScoped<TicketSchedulerService>();
builder.Services.AddSingleton<TicketSchedulingRepository>();
builder.Services.AddHostedService<TicketSchedulerHostedService>();
builder.Services.AddScoped<ScheduledTicketDefinitionsService>();
builder.Services.AddScoped<ITManager.Services.Wms.WmsPartsRepository>();

// Reports
builder.Services.AddScoped<PrintsReportService>();
builder.Services.AddScoped<GlovesReportService>();

builder.Services.AddScoped<UserPreferencesService>();
builder.Services.AddScoped<TimeZoneService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ITManager.Services.Projects.ProjectsRepository>();
builder.Services.AddScoped<ITManager.Services.Projects.ProjectsService>();
builder.Services.AddScoped<BackupStatusService>();

// HttpContext
builder.Services.AddHttpContextAccessor();

// Auth + RBAC (Twoje istniej¹ce serwisy)
builder.Services.AddScoped<CurrentUserContext>();
builder.Services.AddScoped<IActiveDirectoryObjectGuidResolver, ActiveDirectoryObjectGuidResolver>();
builder.Services.AddScoped<IAppAuthorizationService, RbacAuthorizationService>();
builder.Services.AddScoped<CurrentUserContextService>();

// QuestPDF
QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

// Error handling
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

//
// Localization
//
var locOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>();
app.UseRequestLocalization(locOptions.Value);

app.UseAntiforgery();

//
// Change culture
//
app.MapGet("/set-culture", (HttpContext http, string? culture, string? redirectUri) =>
{
    var allowedCultures = new[] { "pl-PL", "en-US" };

    var resolvedCulture = allowedCultures.Contains(culture ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        ? allowedCultures.First(x => string.Equals(x, culture, StringComparison.OrdinalIgnoreCase))
        : "pl-PL";

    http.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(resolvedCulture)),
        new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps
        });

    var target = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri;
    return Results.LocalRedirect(target);
});

//
// Contacts PDF
//
app.MapGet("/api/contacts/print",
    async (string? search,
           string? department,
           ContactsService contactsService,
           IContactsPdfService pdfService) =>
    {
        try
        {
            var filter = search ?? string.Empty;
            var contacts = await contactsService.GetContactsAsync(filter);

            if (!string.IsNullOrWhiteSpace(department))
            {
                contacts = contacts.Where(c => c.Dzial == department).ToList();
            }

            var pdfBytes = pdfService.GenerateContactsPdf(contacts);
            var fileName = $"Kontakty_{DateTime.Now:yyyyMMdd}.pdf";

            return Results.File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ContactsPrint] B³¹d generowania PDF: {ex}");
            return Results.Problem("Nie uda³o siê wygenerowaæ dokumentu PDF z kontaktami.");
        }
    });

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
