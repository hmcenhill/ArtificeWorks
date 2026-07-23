using ArtificeWorks.Application.Simulation;

namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// Reads and writes the single settings row (10.2). Narrow on purpose — there is one row, so
/// there is nothing to query.
/// </summary>
public interface ISimulationSettingsRepository
{
    /// <summary>The stored settings, or null when no row has ever been written.</summary>
    Task<SimulationSettings?> Get(CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the row and returns what is now stored.</summary>
    Task<SimulationSettings> Save(SimulationSettings settings, string updatedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="defaults"/> only if no row exists, and returns what is in force.
    /// <para>
    /// <strong><c>CatalogSeeder</c>'s exact contract, for the same reason</strong>: a re-run must
    /// never quietly stomp a value someone set live. Boot the API twice with a failure rate of 0.9
    /// dialled in and it is still 0.9.
    /// </para>
    /// </summary>
    Task<SimulationSettings> SeedIfMissing(SimulationSettings defaults, CancellationToken cancellationToken = default);
}
