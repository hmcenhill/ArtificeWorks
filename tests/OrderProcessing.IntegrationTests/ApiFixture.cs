using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OrderProcessing.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace OrderProcessing.IntegrationTests;

public class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private WebApplicationFactory<Program> _factory;
    public HttpClient Client { get; private set; } = null!;

    public ApiFixture()
    {
        _container = new PostgreSqlBuilder("postgres:15.1").Build();
        _factory = new WebApplicationFactory<Program>();

    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = _factory.WithWebHostBuilder(host =>
            {
                host.ConfigureServices(services =>
                {
                    var existingRegistration = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkOrderProcessingDbContext>));
                    if (existingRegistration is not null)
                    {
                        services.Remove(existingRegistration);
                    }

                    services.AddDbContext<WorkOrderProcessingDbContext>(options => options.UseNpgsql(_container.GetConnectionString()));
                });
            });

        Client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkOrderProcessingDbContext>();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        await _factory.DisposeAsync();
    }
}
