using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Patch.PatchList;

namespace XIVLauncher.Common.Game.Korea;

public interface IKoreanPatchService
{
    Task<KoreanPatchPlan> CheckAsync(DirectoryInfo gamePath, CancellationToken cancellationToken);
}

public sealed class KoreanPatchService(HttpClient client) : IKoreanPatchService
{
    private static readonly Uri VersionEndpoint =
        new("http://ngamever-live.ff14.co.kr/http/win32/actoz_release_ko_game/");

    private static readonly Repository[] GameRepositories =
    [
        Repository.Ffxiv,
        Repository.Ex1,
        Repository.Ex2,
        Repository.Ex3,
        Repository.Ex4,
        Repository.Ex5,
    ];

    private static readonly Regex VersionPattern =
        new(@"^(?:H)?\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}[a-z]{0,2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<KoreanPatchPlan> CheckAsync(
        DirectoryInfo gamePath,
        CancellationToken cancellationToken)
    {
        var versions = GameRepositories.ToDictionary(
            repository => repository,
            repository => repository.GetVer(gamePath).Trim());
        var ffxivVersionFile = Repository.Ffxiv.GetVerFile(gamePath);
        var isFreshInstall = !ffxivVersionFile.Exists ||
                             string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(
                                 ffxivVersionFile.FullName, cancellationToken).ConfigureAwait(false));

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                new Uri(VersionEndpoint, Uri.EscapeDataString(versions[Repository.Ffxiv])),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new KoreanPatchException(
                KoreanPatchError.NetworkFailure,
                "VersionCheck",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new KoreanPatchException(
                KoreanPatchError.NetworkFailure,
                "VersionCheck",
                exception);
        }

        using (response)
        {
        if (!response.IsSuccessStatusCode)
            throw new KoreanPatchException(KoreanPatchError.NetworkFailure, "VersionCheck");

        var contentType = response.Content.Headers.ContentType;
        if (!string.Equals(contentType?.MediaType, "multipart/mixed",
                StringComparison.OrdinalIgnoreCase) ||
            !response.Headers.TryGetValues("X-Repository", out var repositoryHeaders) ||
            !repositoryHeaders.Contains("actoz/win32/release_ko/game", StringComparer.Ordinal) ||
            !response.Headers.TryGetValues("X-Patch-Module", out var moduleHeaders) ||
            !moduleHeaders.Contains("ZiPatch", StringComparer.Ordinal) ||
            !response.Headers.TryGetValues("X-Latest-Version", out var latestVersionHeaders) ||
            latestVersionHeaders.SingleOrDefault() is not { } latestVersion ||
            !VersionPattern.IsMatch(latestVersion))
        {
            throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "VersionCheck");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var boundary = contentType!.Parameters
            .SingleOrDefault(parameter =>
                string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim('"');
        if (string.IsNullOrWhiteSpace(boundary) ||
            !body.TrimEnd().EndsWith($"--{boundary}--", StringComparison.Ordinal))
        {
            throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
        }

        var allPatches = ParsePatchList(body);
        if (isFreshInstall &&
            GameRepositories.Any(repository => allPatches.All(patch => patch.GetRepo() != repository)))
        {
            throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
        }
        if (allPatches.Length == 0 &&
            (isFreshInstall ||
             !string.Equals(latestVersion, versions[Repository.Ffxiv], StringComparison.Ordinal)))
        {
            throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
        }

        var pending = new HashSet<PatchListEntry>();

        foreach (var repositoryGroup in allPatches.GroupBy(patch => patch.GetRepo()))
        {
            var patches = repositoryGroup.ToArray();
            var installedVersion = versions[repositoryGroup.Key];

            if (repositoryGroup.Key == Repository.Ffxiv ||
                isFreshInstall ||
                installedVersion == Constants.BASE_GAME_VERSION)
            {
                pending.UnionWith(patches);
                continue;
            }

            var installedIndex = Array.FindLastIndex(
                patches,
                patch => string.Equals(patch.VersionId, installedVersion, StringComparison.Ordinal));
            if (installedIndex < 0)
            {
                throw new KoreanPatchException(
                    KoreanPatchError.InstalledVersionNotInPatchChain,
                    "VersionCheck");
            }

            for (var i = installedIndex + 1; i < patches.Length; i++)
                pending.Add(patches[i]);
        }

            return new KoreanPatchPlan(
                new KoreanVersionReport(versions),
                allPatches.Where(pending.Contains).ToArray(),
                isFreshInstall);
        }
    }

    internal static PatchListEntry[] ParsePatchList(string body)
    {
        try
        {
            var patches = new List<PatchListEntry>();
            foreach (var rawLine in body.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
            {
                if (!rawLine.Contains('\t'))
                    continue;

                var fields = rawLine.Split('\t');
                if (fields.Length != 6 ||
                    !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                    length <= 0 ||
                    !VersionPattern.IsMatch(fields[4]) ||
                    !Uri.TryCreate(fields[5], UriKind.Absolute, out var patchUri) ||
                    patchUri.Scheme is not ("http" or "https") ||
                    !string.Equals(patchUri.Host, "client-patch-live.ff14.co.kr",
                        StringComparison.OrdinalIgnoreCase) ||
                    !patchUri.AbsolutePath.StartsWith("/game/", StringComparison.Ordinal) ||
                    patchUri.AbsolutePath.Contains("..", StringComparison.Ordinal))
                {
                    throw new FormatException("Malformed Korean patch row.");
                }

                patches.Add(new PatchListEntry
                {
                    Length = length,
                    VersionId = fields[4],
                    HashType = "zipatch",
                    HashBlockSize = 0,
                    Hashes = [],
                    Url = new UriBuilder(patchUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri,
                });
            }

            return patches.ToArray();
        }
        catch (Exception exception) when (exception is not KoreanPatchException)
        {
            throw new KoreanPatchException(
                KoreanPatchError.ProtocolChanged,
                "PatchList",
                exception);
        }
    }
}

public sealed record KoreanPatchPlan(
    KoreanVersionReport InstalledVersions,
    IReadOnlyList<PatchListEntry> PendingPatches,
    bool IsFreshInstall);

public sealed class KoreanVersionReport
{
    private readonly IReadOnlyDictionary<Repository, string> versions;

    public KoreanVersionReport(IReadOnlyDictionary<Repository, string> versions)
    {
        this.versions = new Dictionary<Repository, string>(versions);
    }

    public string this[Repository repository] => versions[repository];
}

public enum KoreanPatchError
{
    NetworkFailure,
    ProtocolChanged,
    InstalledVersionNotInPatchChain,
}

public sealed class KoreanPatchException(
    KoreanPatchError error,
    string stage,
    Exception? innerException = null)
    : Exception($"Korean patch operation failed during {stage}.", innerException)
{
    public KoreanPatchError Error { get; } = error;

    public string Stage { get; } = stage;
}
