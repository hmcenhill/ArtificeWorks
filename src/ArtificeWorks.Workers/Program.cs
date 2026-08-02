using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Chaos;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Simulation;
using ArtificeWorks.Infrastructure.Workflow;
using ArtificeWorks.Workers.Consuming;
using ArtificeWorks.Workers.Handlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// The same call the API makes (9.1) — same resource shape, same exporter, same sampler, same
// console scope rendering. One trace spans both services only if both are configured identically,
// which is why this is one extension and not two blocks of setup. See docs/observability.md.
builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.WorkerServiceName);

var connectionString = builder.Configuration.GetConnectionString("ArtificeWorksDatabase")
    ?? throw new InvalidOperationException("Connection string 'ArtificeWorksDatabase' was not found.");

// Persistence: the worker touches orders through the same repositories the API uses.
builder.Services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();

// The material-picking workflow (Epic 5) — the worker's real work.
builder.Services.AddScoped<MaterialPickingService>();

// 13.3's sub-assembly loop: spawn a child order for a short manufactured component, put a finished
// child's units away as stock, release the parent. Registered before picking runs because the pick
// is where the spawn happens.
builder.Services.AddSubAssemblies();

// The factory's live dials (10.2). Registered here and not only in the API for the reason the
// story exists: a PUT handled by the API changes nothing about what the worker does, and the
// worker is where inspections actually fail. It also brings 10.1's pace policy, which the
// worker's own outbox dispatcher consults.
builder.Services.AddSimulationSettings(builder.Configuration);

// Failure injection registry (Epic 12). The worker is where faults FIRE: its inspection service
// consults an armed FailInspection fault and routes the order to rework/Fault. Registered before the
// workflow below so the inspection service resolves the registry.
builder.Services.AddChaos();

// Production + inspection (Epic 6): the middle of the pipeline, including the rework cycle.
builder.Services.AddProductionAndInspection(builder.Configuration);

// Shipping + dispatch (Epic 7): the end of it.
builder.Services.AddShipping(builder.Configuration);

// Full messaging: the worker now publishes too (MaterialsReserved hands the pipeline to
// production), so it needs the publisher and a correlation context, not just the connection.
// The handler sets the correlation id per message from the inbound envelope.
builder.Services.AddRabbitMqMessaging(builder.Configuration);

// The outbox dispatcher (8.1). Every handler now stages its next-stage event in the same
// transaction as the work; this is the loop that puts those rows on the wire.
builder.Services.AddOutboxDispatcher();

// Consumption plumbing + handlers. Registering a handler is the ONLY change needed to
// consume a new event type — the consumer and dispatcher stay untouched.
builder.Services.AddEventConsumer();
builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();

// Recovery (8.3): drain artifice.parked into dead_letters. A separate consumer from the main
// loop on purpose — the pipeline's handlers must never run for a message they already failed.
builder.Services.AddDeadLetters();
builder.Services.AddHostedService<ParkedQueueDrain>();

// Epic 6. Note the cycle: rework-required is published by this same process and consumed by
// it, so the rebuild loop really does go out over the broker and come back.
builder.Services.AddEventHandler<MaterialsReserved, MaterialsReservedHandler>();
builder.Services.AddEventHandler<ProductionCompleted, ProductionCompletedHandler>();
builder.Services.AddEventHandler<ReworkRequired, ReworkRequiredHandler>();

// Epic 7. inspection-passed → book a carrier (or, since 13.3, put a sub-assembly order's units away
// as stock); shipment-scheduled → dispatch and complete.
builder.Services.AddEventHandler<InspectionPassed, InspectionPassedHandler>();
builder.Services.AddEventHandler<ShipmentScheduled, ShipmentScheduledHandler>();

// 13.3. work-order.completed gets its first pipeline subscriber — it was the terminal announcement
// binding to nobody through Epics 7–8, and read only by the dashboard relay since 11.2. A child
// order completing is what releases the parent that was waiting for it, which makes the gate
// event-driven instead of a poll. Registering this handler is also what adds the queue binding.
builder.Services.AddEventHandler<WorkOrderCompleted, WorkOrderCompletedHandler>();

// Health (9.4). The worker had no health signal at all, and it is the half more likely to be
// wedged — a consumer that has quietly stopped consuming is silent, which is the failure mode this
// milestone exists to remove. Same checks as the API, over a deliberately tiny HTTP listener.
builder.Services.AddArtificeWorksHealthChecks();
builder.Services.AddHostedService<MinimalHealthEndpoint>();

var host = builder.Build();
host.Run();
