// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-03.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Permalinks;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Stores the existing resolution API's short-lived credentials in the normal
/// application database. This compatibility state contains no file identities.
/// </summary>
internal sealed class MediaPermalinkLeaseRepository(IDbContextFactory<JellyfinDbContext> contexts)
{
    internal static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    internal static string Digest(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    internal async Task InsertAsync(MediaPermalinkLease lease, CancellationToken token)
    {
        var context = await contexts.CreateDbContextAsync(token).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            context.MediaPermalinkLeases.Add(lease);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    internal async Task<MediaPermalinkLease?> ReadAsync(string handle, CancellationToken token)
    {
        var context = await contexts.CreateDbContextAsync(token).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.MediaPermalinkLeases.AsNoTracking()
                .SingleOrDefaultAsync(lease => lease.Handle == handle, token).ConfigureAwait(false);
        }
    }

    /// <summary>Returns a bounded page of leases that may be eligible for reclamation.</summary>
    /// <param name="now">The current UTC time.</param>
    /// <param name="offset">The number of candidates to skip.</param>
    /// <param name="maximum">The maximum page size.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>The candidate leases in stable order.</returns>
    internal async Task<List<MediaPermalinkLease>> ReclamationCandidatesAsync(DateTime now, int offset, int maximum, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.MediaPermalinkLeases.AsNoTracking()
            .Where(lease => (lease.ConsumedAtUtc == null && lease.ExpiresAtUtc <= now) || lease.ConsumedAtUtc != null)
            .OrderBy(lease => lease.ExpiresAtUtc > now && lease.CompletedAtUtc == null)
            .ThenBy(lease => lease.UpdatedAtUtc).ThenBy(lease => lease.Handle)
            .Skip(offset).Take(maximum).ToListAsync(token).ConfigureAwait(false);
    }

    /// <summary>Deletes a lease only if its revision is unchanged.</summary>
    /// <param name="lease">The lease selected for reclamation.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>A task representing the delete operation.</returns>
    internal async Task DeleteIfUnchangedAsync(MediaPermalinkLease lease, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token).ConfigureAwait(false);
        _ = await context.MediaPermalinkLeases
            .Where(value => value.Handle == lease.Handle && value.Revision == lease.Revision)
            .ExecuteDeleteAsync(token).ConfigureAwait(false);
    }

    /// <summary>Atomically consumes or advances a lease and optionally issues its successor.</summary>
    /// <param name="lease">The lease being consumed or advanced.</param>
    /// <param name="successor">An optional successor lease to insert in the same save.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    internal async Task SaveAsync(MediaPermalinkLease lease, MediaPermalinkLease? successor, CancellationToken token)
    {
        var context = await contexts.CreateDbContextAsync(token).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            context.MediaPermalinkLeases.Attach(lease);
            context.Entry(lease).State = EntityState.Modified;
            context.Entry(lease).Property(value => value.Revision).OriginalValue = lease.Revision;
            lease.Revision = checked(lease.Revision + 1);
            if (successor is not null)
            {
                context.MediaPermalinkLeases.Add(successor);
            }

            try
            {
                await context.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "lease-consumed",
                    "Another request already consumed or advanced this permalink lease.",
                    exception);
            }
        }
    }
}
