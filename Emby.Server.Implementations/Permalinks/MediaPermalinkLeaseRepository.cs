// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-03.
using System;
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

    /// <summary>Atomically consumes or advances a lease and optionally issues its successor.</summary>
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
                throw new PermalinkException(PermalinkErrorKind.Conflict, "lease-consumed",
                    "Another request already consumed or advanced this permalink lease.", exception);
            }
        }
    }
}
