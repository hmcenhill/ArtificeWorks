using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Workflow;
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

// Persistence: the worker touches orders through the same repositories the API uses.
builder.Services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();

// The material-picking workflow (Epic 5) — the worker's real work.
builder.Services.AddScoped<MaterialPickingService>();

// Production + inspection (Epic 6): the middle of the pipeline, including the rework cycle.
builder.Services.AddProductionAndInspection(builder.Configuration);

// Full messaging: the worker now publishes too (MaterialsReserved hands the pipeline to
// production), so it needs the publisher and a correlation context, not just the connection.
// The handler sets the correlation id per message from the inbound envelope.
builder.Services.AddRabbitMqMessaging(builder.Configuration);

// Consumption plumbing + handlers. Registering a handler is the ONLY change needed to
// consume a new event type — the consumer and dispatcher stay untouched.
builder.Services.AddEventConsumer();
builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();

// Epic 6. Note the cycle: rework-required is published by this same process and consumed by
// it, so the rebuild loop really does go out over the broker and come back.
builder.Services.AddEventHandler<MaterialsReserved, MaterialsReservedHandler>();
builder.Services.AddEventHandler<ProductionCompleted, ProductionCompletedHandler>();
builder.Services.AddEventHandler<ReworkRequired, ReworkRequiredHandler>();

var host = builder.Build();
host.Run();
