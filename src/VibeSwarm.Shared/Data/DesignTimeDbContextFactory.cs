using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VibeSwarm.Shared.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VibeSwarmDbContext>
{
    public VibeSwarmDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VibeSwarmDbContext>();
        optionsBuilder.UseSqlite("Data Source=vibeswarm.db");

        return new VibeSwarmDbContext(optionsBuilder.Options);
    }
}
