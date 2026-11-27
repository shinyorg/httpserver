using NuGet.Versioning;

namespace Shiny.Net.HttpServer.NuGet;

/// <summary>
/// Where a feed's packages live. <see cref="DiskNuGetPackageStore"/> is the one most apps want;
/// implement this to keep them in blob storage, a database, or anywhere else.
/// <para>
/// Ids are case-insensitive and versions compare as NuGet compares them - <c>1.0</c> and
/// <c>1.0.0</c> are the same version, and build metadata is ignored. The feed calls in with whatever
/// casing the client used, so a store has to match the same way.
/// </para>
/// </summary>
public interface INuGetPackageStore
{
    /// <summary>Every version of <paramref name="id"/>, listed or not, in any order. Empty when there are none.</summary>
    ValueTask<IReadOnlyList<NuGetPackage>> GetVersionsAsync(string id, CancellationToken cancellationToken);

    /// <summary>Every version of every package, listed or not - what search and autocomplete run over.</summary>
    ValueTask<IReadOnlyList<NuGetPackage>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new version. <paramref name="nupkg"/> is the package file and
    /// <paramref name="manifest"/> its <c>.nuspec</c>, already validated and parsed into
    /// <paramref name="package"/>. Returns false, storing nothing, when that version is already there.
    /// </summary>
    ValueTask<bool> AddAsync(NuGetPackage package, Stream nupkg, ReadOnlyMemory<byte> manifest, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a version over one that is already there, for a feed with
    /// <see cref="NuGetFeedOptions.AllowOverwrite"/> on. Returns false when nothing was stored.
    /// <para>
    /// The default deletes and then adds, which leaves a moment where the version is missing and a
    /// restore running alongside gets a 404. Override it to swap the package in one step, as
    /// <see cref="DiskNuGetPackageStore"/> does.
    /// </para>
    /// </summary>
    async ValueTask<bool> ReplaceAsync(NuGetPackage package, Stream nupkg, ReadOnlyMemory<byte> manifest, CancellationToken cancellationToken)
    {
        await this.DeleteAsync(package.Id, package.Version, cancellationToken).ConfigureAwait(false);
        return await this.AddAsync(package, nupkg, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>.nupkg</c>, or null when there is no such version.</summary>
    ValueTask<Stream?> OpenPackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken);

    /// <summary>The <c>.nuspec</c>, or null when there is no such version.</summary>
    ValueTask<Stream?> OpenManifestAsync(string id, NuGetVersion version, CancellationToken cancellationToken);

    /// <summary>Lists or unlists a version. False when there is no such version.</summary>
    ValueTask<bool> SetListedAsync(string id, NuGetVersion version, bool listed, CancellationToken cancellationToken);

    /// <summary>Removes a version and its files. False when there was nothing to remove.</summary>
    ValueTask<bool> DeleteAsync(string id, NuGetVersion version, CancellationToken cancellationToken);
}
