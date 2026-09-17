using System.Globalization;

using Microsoft.FluentUI.AspNetCore.Components;

using Serilog;

using Stampd.UI.Components;
using Stampd.UI.Services;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();
builder.Host.UseSerilog();

// Razor Components with both Static SSR and Interactive Server. Pages opt into
// InteractiveServer via @rendermode InteractiveServer; everything else stays static.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddFluentUIComponents();

// HttpClient pointed at the Stampd WebApi. The recipient signing endpoints are anonymous
// (per-token auth in the URL), so StampdApiClient carries no Bearer token by default.
// DesignerApiClient talks to authenticated endpoints (/api/templates etc.) — in
// Development we auto-mint a JWT via DevTokenProvider and inject it through a
// DelegatingHandler so the UI works with zero copy-paste. In any non-Development
// environment the designer pages keep their auth bar and the user supplies a real token.
var apiBaseUrl = builder.Configuration["Stampd:Api:BaseUrl"] ?? "http://localhost:5090";
builder.Services.AddHttpClient<StampdApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));

// v2.0 — CurrentUserService is the single source of "who's signed in + what roles
// they have" for Blazor pages. Lives outside the Dev-only block so non-Dev pages get
// a working "Not signed in" surface that returns false for every IsInRole check.
builder.Services.AddScoped<CurrentUserService>();

if (builder.Environment.IsDevelopment())
{
    // v2.0 — Stampd:DevAuth:Roles is a comma-separated list (e.g. "Sender" or
    // "Admin,Sender"). Default is Sender so the existing demo flow is unchanged; flip
    // to "Admin" in appsettings.Development.json to test the new admin dashboard
    // without restarting with code changes.
    var rolesRaw = builder.Configuration["Stampd:DevAuth:Roles"] ?? "Sender";
    var roles = rolesRaw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    builder.Services.AddSingleton(new DevTokenOptions
    {
        ApiBaseUrl = apiBaseUrl,
        Subject = builder.Configuration["Stampd:DevAuth:Subject"] ?? "designer-user",
        TenantId = builder.Configuration["Stampd:DevAuth:TenantId"],
        Roles = roles,
    });
    // DevTokenProvider needs its own bare HttpClient to mint the initial token, otherwise
    // the DelegatingHandler below would call back into it recursively before the cache
    // is populated.
    builder.Services.AddHttpClient(nameof(DevTokenProvider));
    builder.Services.AddSingleton<DevTokenProvider>();
    builder.Services.AddTransient<DevAuthHttpHandler>();

    builder.Services
        .AddHttpClient<DesignerApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
        .AddHttpMessageHandler<DevAuthHttpHandler>();
}
else
{
    builder.Services.AddHttpClient<DesignerApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
}

var app = builder.Build();

app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
