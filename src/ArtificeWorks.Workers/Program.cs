using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Workers.Consuming;
using ArtificeWorks.Workers.Handlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Render logging scopes so the correlation id the consumer pushes per delivery prefixes
// every log line — grepping one id spans the API and worker sides. See docs/messaging-topology.md.
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);

var connectionString = builder.Configuration.GetConnectionString("ArtificeWorksDatabase")
    ?? throw new InvalidOperationException("Connection string 'ArtificeWorksDatabase' was not found.");

// Persistence: the worker touches orders through the same repository the API uses.
builder.Services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();

// Shared RabbitMQ connection (declares the artifice.events exchange on first use).
builder.Services.AddRabbitMqConnection(builder.Configuration);

// Consumption plumbing + handlers. Registering a handler is the ONLY change needed to
// consume a new event type — the consumer and dispatcher stay untouched.
builder.Services.AddEventConsumer();
builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();

var host = builder.Build();
host.Run();
