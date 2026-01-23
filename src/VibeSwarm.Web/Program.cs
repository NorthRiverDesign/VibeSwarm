using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Services;
using VibeSwarm.Worker;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=vibeswarm.db";

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();
builder.Services.AddWorkerServices();
builder.Services.AddVibeSwarmData(connectionString);

// Register SignalR job update service
builder.Services.AddSingleton<IJobUpdateService, SignalRJobUpdateService>();

var app = builder.Build();

// Apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapHub<JobHub>("/jobhub");
app.MapFallbackToPage("/_Host");

app.Run();
