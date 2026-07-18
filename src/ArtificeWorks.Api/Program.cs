using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Api.Configuration;
using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Handlers;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RabbitMqConfiguration>(builder.Configuration.GetSection(nameof(RabbitMqConfiguration)));
builder.Services.Configure<RedisConfiguration>(builder.Configuration.GetSection(nameof(RedisConfiguration)));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// RFC 7807 ProblemDetails everywhere: enables framework-generated errors (routing
// 404s, unhandled exceptions via UseExceptionHandler) to render as ProblemDetails.
// The customizer stamps a `code` on details built through IProblemDetailsService
// (the exception-handler path) so even unexpected 500s carry a reason code.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            context.ProblemDetails.Extensions["code"] = ProblemCodes.InternalError;
        }
    };
});

// Model-validation failures ([ApiController] auto-400) must carry the same
// contract as our hand-written errors: a ProblemDetails with a `code` extension.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var factory = context.HttpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, context.ModelState);
        problem.Extensions["code"] = ProblemCodes.ValidationFailed;
        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    };
});

var connectionString = builder.Configuration.GetConnectionString("ArtificeWorksDatabase")
    ?? throw new InvalidOperationException("Connection string 'ArtificeWorksDatabase' was not found.");

builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();

builder.Services.AddScoped<ProductHandler>();
builder.Services.AddScoped<WorkOrderHandler>();

var app = builder.Build();

// Turns unhandled exceptions into a ProblemDetails 500 (code `internal_error` is
// added by the handler below) instead of leaking a stack trace.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
