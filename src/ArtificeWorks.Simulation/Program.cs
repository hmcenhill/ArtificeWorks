using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Scheduling;
using ArtificeWorks.Infrastructure.Simulation;
using ArtificeWorks.Simulation.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ---------------------------------------------------------------------------------------------
// The third host (10.1).
//
// It PUBLISHES AND SCHEDULES; IT CONSUMES NOTHING. No RabbitMQ connection, no consumer loop, no
// outbox dispatcher — it creates orders over HTTP through the API's front door and runs the world
// sweep against the database. That is the whole surface.
//
// A separate process rather than two more hosted services in Workers, decided at grooming: the
// simulation must be stoppable without stopping the factory, and it must not be able to slow the
// consumers down. It costs a compose service, a health signal and a deployment unit in M7, and
// that was accepted deliberately.
// ---------------------------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

// The same call the API and the worker make (9.1) — same resource shape, same exporter, same
// sampler, same console scope rendering. One trace spans all three services only if all three are
// configured identically, which is why this is one extension and not three blocks of setup.
builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.SimulationServiceName);

var connectionString = builder.Configuration.GetConnectionString("ArtificeWorksDatabase")
    ?? throw new InvalidOperationException("Connection string 'ArtificeWorksDatabase' was not found.");

// It reads the database for the settings row and for the world sweep — and 9.2's snapshot task,
// registered by AddArtificeWorksTelemetry above, is what tells the generator how many orders are
// in flight.
builder.Services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();

// The dials (10.2), the cache in front of them, and the task that keeps it fresh. Registered in
// all three hosts — a setting only one process can see is not a setting.
builder.Services.AddSimulationSettings(builder.Configuration);

// The world sweep (10.4). The same service the API's /system/world/reset calls.
builder.Services.AddWorldReset();

// The generator (10.3), over a typed HttpClient against the API's public base address. Not a
// shortcut into the database: see OrderGenerator's remarks.
var apiBaseAddress = builder.Configuration[$"{SimulationConfiguration.SectionName}:ApiBaseAddress"]
    ?? "http://localhost:5000";

builder.Services.AddHttpClient<OrderGenerator>(client =>
{
    client.BaseAddress = new Uri(apiBaseAddress);
    // Short, because a slow API must not hold a scheduled tick open: the next tick will try again,
    // and a piled-up queue of in-flight creates is exactly what MaxInFlight exists to prevent.
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<OrderGenerator>());
builder.Services.AddScheduledTask<WorldResetTask>();
builder.Services.AddPeriodicTaskHost();

// A health signal, over the same deliberately tiny HttpListener the worker uses (9.4). This host
// is the least likely of the three to be noticed when it dies — it has no visitors and no
// queue depth to watch — which is precisely why it gets one.
builder.Services.AddArtificeWorksHealthChecks();
builder.Services.AddHostedService<MinimalHealthEndpoint>();

var host = builder.Build();
host.Run();
