using ArtificeWorks.Application.Simulation;

namespace ArtificeWorks.Application.Shipping;

/// <summary>
/// Picks a carrier off the configured list, invents a tracking number, and adds
/// <see cref="ShippingConfiguration.TransitDays"/> for an ETA — unless a weighted coin flip says
/// the carrier has no capacity. That is the whole of it, on purpose.
/// <para>
/// Registered as a singleton so a configured <see cref="ShippingConfiguration.Seed"/> yields one
/// reproducible sequence for the process rather than restarting per message; the generator is
/// therefore locked, since bookings can arrive from more than one thread. Same shape, same
/// reasoning as <see cref="Inspection.RandomVerdictSource"/>.
/// </para>
/// </summary>
public sealed class ConfiguredCarrierBooking : ICarrierBooking
{
    private readonly ShippingConfiguration _config;
    private readonly SimulationSettingsCache? _settings;
    private readonly IReadOnlyList<string> _carriers;
    private readonly Random _random;
    private readonly Lock _gate = new();

    /// <param name="settings">
    /// 10.2's live dials. The refusal rate comes from here so it can be turned up on a running
    /// factory; the carrier list, the transit days and the seed stay in configuration — they are
    /// the world's shape, not a demo button. Null (a unit test) falls back to configuration.
    /// </param>
    public ConfiguredCarrierBooking(ShippingConfiguration config, SimulationSettingsCache? settings = null)
    {
        _config = config;
        _settings = settings;
        _carriers = config.Carriers.Count > 0 ? config.Carriers : ShippingConfiguration.DefaultCarriers;
        _random = config.Seed is int seed ? new Random(seed) : new Random();
    }

    private double RefusalRate => _settings?.Current.RefusalRate ?? _config.RefusalRate;

    public CarrierBookingResult Book(CarrierBookingRequest request)
    {
        string carrier;
        if (request.RequestedCarrier is { Length: > 0 } requested)
        {
            // Case-insensitive so a visitor typing a carrier's name into Swagger isn't punished
            // for it; the configured spelling is what gets recorded.
            var match = _carriers.FirstOrDefault(
                known => string.Equals(known, requested, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return CarrierBookingResult.Unknown(requested,
                    $"'{requested}' is not a carrier this factory works with. Known carriers: {string.Join(", ", _carriers)}.");
            }
            carrier = match;
        }
        else
        {
            carrier = Choose();
        }

        if (Refuses())
        {
            return CarrierBookingResult.Refused(carrier, _config.RefusalReason);
        }

        return CarrierBookingResult.Accepted(
            carrier,
            TrackingNumber(carrier),
            DateTime.UtcNow.AddDays(_config.TransitDays));
    }

    private string Choose()
    {
        lock (_gate)
        {
            return _carriers[_random.Next(_carriers.Count)];
        }
    }

    private bool Refuses()
    {
        var refusalRate = RefusalRate;

        if (refusalRate <= 0)
        {
            // The default. Short-circuited so an unattended factory doesn't burn entropy, and so
            // "RefusalRate 0 means every booking is accepted" is true by construction rather
            // than by the generator never happening to return exactly 0.
            return false;
        }

        lock (_gate)
        {
            return _random.NextDouble() < refusalRate;
        }
    }

    /// <summary>A carrier-flavoured tracking number: initials plus a short random suffix.</summary>
    private string TrackingNumber(string carrier)
    {
        var initials = new string(carrier
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]))
            .Where(char.IsLetter)
            .Take(3)
            .ToArray());

        return $"{(initials.Length > 0 ? initials : "AW")}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
    }
}
