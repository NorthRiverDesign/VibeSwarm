using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using VibeSwarm.Client;
using VibeSwarm.Client.Auth;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register the CookieHandler for browser fetch credential inclusion
builder.Services.AddTransient<CookieHandler>();

// Configure HttpClient with the CookieHandler for cookie authentication
// This ensures credentials (cookies) are included with all requests,
// which is critical for iOS Safari's stricter cookie policies
builder.Services.AddHttpClient("VibeSwarm", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
}).AddHttpMessageHandler<CookieHandler>();

// Register the default HttpClient as the named client for DI
builder.Services.AddScoped(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return factory.CreateClient("VibeSwarm");
});

// HTTP service implementations
builder.Services.AddScoped<IJobService, HttpJobService>();
builder.Services.AddScoped<IProjectService, HttpProjectService>();
builder.Services.AddScoped<HttpProviderService>();
builder.Services.AddScoped<IProviderService>(sp => sp.GetRequiredService<HttpProviderService>());
builder.Services.AddScoped<ISkillService, HttpSkillService>();
builder.Services.AddScoped<ISettingsService, HttpSettingsService>();
builder.Services.AddScoped<IIdeaService, HttpIdeaService>();
builder.Services.AddScoped<IUserService, HttpUserService>();
builder.Services.AddScoped<IFileSystemService, HttpFileSystemService>();
builder.Services.AddScoped<IVersionControlService, HttpVersionControlService>();

// UI services
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ChangePasswordModalService>();

// Auth
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>();

await builder.Build().RunAsync();
