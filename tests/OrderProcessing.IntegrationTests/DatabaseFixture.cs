using Microsoft.EntityFrameworkCore;

using OrderProcessing.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace OrderProcessing.IntegrationTests;

public class DatabaseFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; private set; } = null!;
    public WorkOrderProcessingDbContext Context { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Container = new PostgreSqlBuilder("postgres:15.1").Build();
        await Container.StartAsync();

        Context = await GetNewWorkOrderProcessingDbContext();
        await Context.Database.MigrateAsync();
    }

    public async Task<WorkOrderProcessingDbContext> GetNewWorkOrderProcessingDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkOrderProcessingDbContext>()
            .UseNpgsql(Container.GetConnectionString())
            .Options;

        return new WorkOrderProcessingDbContext(options);
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
        await Container.DisposeAsync();
    }
}