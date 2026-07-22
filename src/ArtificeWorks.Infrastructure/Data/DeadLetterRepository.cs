using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Messaging.DeadLetters;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// Queries over <c>dead_letters</c>, and the one write that marks a record replayed.
/// <para>
/// Ordinary EF, which is exactly the argument for a table rather than a queue browser: paging,
/// filtering by work order, and "show me the ones nobody has dealt with yet" are all just
/// queries here, and none of them are expressible against AMQP without consuming messages you
/// then have to put back.
/// </para>
/// </summary>
public class DeadLetterRepository : IDeadLetterRepository
{
    private readonly ArtificeWorksDbContext _context;

    public DeadLetterRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<DeadLetterPageDto> GetPage(
        int page,
        int pageSize,
        Guid? workOrderId,
        bool? replayed,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _context.DeadLetters.AsNoTracking();

        if (workOrderId is not null)
        {
            query = query.Where(letter => letter.WorkOrderId == workOrderId);
        }

        if (replayed is true)
        {
            query = query.Where(letter => letter.ReplayedUtc != null);
        }
        else if (replayed is false)
        {
            query = query.Where(letter => letter.ReplayedUtc == null);
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(letter => letter.ParkedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(letter => new DeadLetterSummaryDto(
                letter.Id,
                letter.EventType,
                letter.CorrelationId,
                letter.WorkOrderId,
                letter.Attempts,
                letter.LastError,
                letter.ParkedUtc,
                letter.ReplayedUtc,
                letter.ReplayCount))
            .ToListAsync(cancellationToken);

        // The list row carries the first line of the error only; the full text is on the detail.
        var trimmed = items
            .Select(item => item with { Error = FirstLine(item.Error) })
            .ToList();

        return new DeadLetterPageDto(trimmed, page, pageSize, total);
    }

    public async Task<DeadLetterDetailDto?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var letter = await _context.DeadLetters.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);

        return letter is null
            ? null
            : new DeadLetterDetailDto(
                letter.Id, letter.EventType, letter.CorrelationId, letter.WorkOrderId,
                letter.Attempts, letter.LastError, letter.ParkedUtc, letter.ReplayedUtc,
                letter.ReplayCount, letter.Payload);
    }

    public async Task<DeadLetterReplayInfo?> MarkReplayed(Guid id, CancellationToken cancellationToken = default)
    {
        var letter = await _context.DeadLetters.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (letter is null)
        {
            return null;
        }

        letter.MarkReplayed(DateTime.UtcNow);

        // This save also flushes the outbox row the caller staged — the re-send and the record of
        // it are one transaction.
        await _context.SaveChangesAsync(cancellationToken);

        return new DeadLetterReplayInfo(letter.EventType, letter.Payload, letter.CorrelationId, letter.ReplayCount);
    }

    private static string FirstLine(string error)
    {
        var newline = error.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? error : error[..newline];
        return line.Length <= 200 ? line : line[..197] + "...";
    }

    /// <summary>
    /// Writes a parked message down. Used by the worker's <c>ParkedQueueDrain</c>, which is
    /// deliberately the most defensive code in the system: it handles messages already known to
    /// be broken, and a drain that throws on one re-creates the exact wedge 8.2 just fixed.
    /// </summary>
    public async Task Add(DeadLetter letter, CancellationToken cancellationToken = default)
    {
        _context.DeadLetters.Add(letter);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
