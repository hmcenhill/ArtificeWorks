using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>The one row, read and written (10.2).</summary>
public class SimulationSettingsRepository : ISimulationSettingsRepository
{
    private const string UniqueViolation = "23505";

    private readonly ArtificeWorksDbContext _context;

    public SimulationSettingsRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<SimulationSettings?> Get(CancellationToken cancellationToken = default)
    {
        var row = await _context.SimulationSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        return row?.ToSettings();
    }

    public async Task<SimulationSettings> Save(
        SimulationSettings settings, string updatedBy, CancellationToken cancellationToken = default)
    {
        var row = await _context.SimulationSettings.FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            row = SimulationSettingsRow.From(settings, updatedBy);
            _context.SimulationSettings.Add(row);
        }
        else
        {
            row.Apply(settings, updatedBy);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return row.ToSettings();
    }

    public async Task<SimulationSettings> SeedIfMissing(
        SimulationSettings defaults, CancellationToken cancellationToken = default)
    {
        var existing = await Get(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            return await Save(defaults, "configuration", cancellationToken);
        }
        catch (DbUpdateException e) when (IsDuplicate(e))
        {
            // Three hosts boot at once against a fresh database and all three find no row. The
            // primary key is the arbiter — the same shape as 5.4's reservation and 8.4's key — and
            // the losers simply read the winner's row rather than treating a race as an error.
            _context.ChangeTracker.Clear();
            return await Get(cancellationToken) ?? defaults;
        }
    }

    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
