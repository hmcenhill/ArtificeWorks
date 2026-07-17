using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

public class DatabaseFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; private set; } = null!;
    public ArtificeWorksDbContext Context { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Container = new PostgreSqlBuilder("postgres:15.1").Build();
        await Container.StartAsync();

        Context = await GetNewArtificeWorksDbContext();
        await Context.Database.MigrateAsync();
    }

    public async Task<ArtificeWorksDbContext> GetNewArtificeWorksDbContext()
    {
        var options = new DbContextOptionsBuilder<ArtificeWorksDbContext>()
            .UseNpgsql(Container.GetConnectionString())
            .Options;

        return new ArtificeWorksDbContext(options);
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
        await Container.DisposeAsync();
    }
}