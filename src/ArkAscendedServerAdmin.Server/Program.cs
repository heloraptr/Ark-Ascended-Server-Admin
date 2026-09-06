using ArkAscendedServerAdmin.Server;
using ArkAscendedServerAdmin.Server.Components;
using ArkAscendedServerAdmin.Components.Layout;
using Microsoft.Extensions.Hosting.WindowsServices;
using Radzen;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts with System32 as its working directory; anchor the content root to the
    // executable so appsettings.json and wwwroot resolve. Left alone under `dotnet run` so static web
    // assets keep working from the project directory.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

builder.Services.AddWindowsService(options => options.ServiceName = "ArkAscendedServerAdmin");

builder.Services.AddCurseForgeApi(builder.Configuration);

builder.Services.AddRadzenComponents();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// No HTTPS redirection/HSTS here: TLS is terminated by the reverse proxy (see PLAN.md step 8). Phase 1
// adds forwarded-header handling and the HTTPS guard middleware.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(MainLayout).Assembly);

app.Run();
