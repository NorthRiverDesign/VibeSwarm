using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Read-only client for the <c>github.com/anthropics/skills</c> catalog. Surfaces the list
/// of available skills (cached) and lets the installer download a specific skill's folder.
/// </summary>
public interface IGitHubSkillCatalogClient
{
	/// <summary>
	/// Returns the list of skills exposed by the marketplace repo. Results are cached server-side
	/// so repeated browse-modal opens don't burn the anonymous-rate-limit budget.
	/// </summary>
	Task<IReadOnlyList<MarketplaceSkillSummary>> ListSkillsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Downloads the marketplace repo at <paramref name="gitRef"/> as a gzipped tarball. The
	/// installer extracts only the target skill's subfolder. Caller disposes the stream.
	/// </summary>
	Task<Stream> DownloadTarballAsync(string gitRef, CancellationToken cancellationToken = default);

	/// <summary>
	/// Forces a reload of the cached catalog on the next <see cref="ListSkillsAsync"/> call.
	/// Invoked by the UI's "Refresh" action.
	/// </summary>
	void InvalidateCache();
}
