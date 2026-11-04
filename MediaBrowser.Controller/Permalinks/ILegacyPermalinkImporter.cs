// SlopTank modification notice: added or changed by SlopTank on 2026-11-05.
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Imports previously issued legacy permalink aliases into the current alias table.</summary>
public interface ILegacyPermalinkImporter
{
    /// <summary>Runs the legacy permalink import against the current library.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing the import operation.</returns>
    Task RunAsync(CancellationToken cancellationToken);
}
