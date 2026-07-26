using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.Korea;

namespace XIVLauncher.Common.Tests.Game.Korea;

[TestClass]
public class KoreanPatchServiceTests
{
    private const string Boundary = "test-boundary";

    [TestMethod]
    public async Task FiltersEachRepositoryAfterItsInstalledVersion()
    {
        using var gamePath = new TemporaryGamePath();
        Repository.Ffxiv.SetVer(gamePath.Directory, "2026.06.18.0000.0000");
        Repository.Ex1.SetVer(gamePath.Directory, "2024.10.22.0002.0000");
        Repository.Ex2.SetVer(gamePath.Directory, "2026.05.20.0000.0000");
        Repository.Ex3.SetVer(gamePath.Directory, "2026.06.18.0000.0000");
        Repository.Ex4.SetVer(gamePath.Directory, "2026.06.18.0000.0000");
        Repository.Ex5.SetVer(gamePath.Directory, "2026.06.18.0000.0000");

        var handler = new VersionResponseHandler(PatchList(
            Row(99, "2026.06.20.0000.0000", "game/basehash/D2026.06.20.0000.0000.patch"),
            Row(100, "H2024.10.22.0002.0000a", "game/ex1/hash/H2024.10.22.0002.0000a.patch"),
            Row(101, "2024.10.22.0002.0000", "game/ex1/hash/H2024.10.22.0002.0000b.patch"),
            Row(102, "2026.05.15.0000.0000", "game/ex1/hash/D2026.05.15.0000.0000.patch"),
            Row(103, "2026.05.20.0000.0000", "game/ex2/hash/D2026.05.20.0000.0000.patch"),
            Row(104, "2026.05.25.0000.0000", "game/ex2/hash/D2026.05.25.0000.0000.patch")));
        using var client = new HttpClient(handler);

        var plan = await new KoreanPatchService(client).CheckAsync(
            gamePath.Directory,
            CancellationToken.None);

        Assert.IsFalse(plan.IsFreshInstall);
        CollectionAssert.AreEqual(
            new[]
            {
                "2026.06.20.0000.0000",
                "2026.05.15.0000.0000",
                "2026.05.25.0000.0000",
            },
            Array.ConvertAll(plan.PendingPatches.ToArray(), patch => patch.VersionId));
        Assert.AreEqual("zipatch", plan.PendingPatches[0].HashType);
        Assert.AreEqual(
            "http://ngamever-live.ff14.co.kr/http/win32/actoz_release_ko_game/2026.06.18.0000.0000",
            handler.RequestUri?.AbsoluteUri);
    }

    [TestMethod]
    public async Task MissingGameVersionEnablesTheCompleteFreshInstallChain()
    {
        using var gamePath = new TemporaryGamePath();
        var handler = new VersionResponseHandler(PatchList(
            Row(100, "H2024.11.02.0000.0000aa", "game/basehash/H2024.11.02.0000.0000aa.patch"),
            Row(101, "2026.06.18.0000.0000", "game/basehash/D2026.06.18.0000.0000.patch"),
            Row(101, "2026.06.18.0000.0000", "game/ex1/hash/D2026.06.18.0000.0000.patch"),
            Row(101, "2026.06.18.0000.0000", "game/ex2/hash/D2026.06.18.0000.0000.patch"),
            Row(101, "2026.06.18.0000.0000", "game/ex3/hash/D2026.06.18.0000.0000.patch"),
            Row(101, "2026.06.18.0000.0000", "game/ex4/hash/D2026.06.18.0000.0000.patch"),
            Row(102, "2026.06.18.0000.0000", "game/ex5/hash/D2026.06.18.0000.0000.patch")));
        using var client = new HttpClient(handler);

        var plan = await new KoreanPatchService(client).CheckAsync(
            gamePath.Directory,
            CancellationToken.None);

        Assert.IsTrue(plan.IsFreshInstall);
        Assert.AreEqual(7, plan.PendingPatches.Count);
        Assert.IsTrue(plan.PendingPatches.All(patch =>
            patch.Url.StartsWith("https://", StringComparison.Ordinal)));
        Assert.AreEqual(Constants.BASE_GAME_VERSION, plan.InstalledVersions[Repository.Ffxiv]);
        StringAssert.EndsWith(
            handler.RequestUri?.AbsoluteUri,
            $"/{Constants.BASE_GAME_VERSION}");
    }

    [TestMethod]
    public async Task UnknownInstalledRepositoryVersionFailsClosed()
    {
        using var gamePath = new TemporaryGamePath();
        Repository.Ffxiv.SetVer(gamePath.Directory, "2026.06.18.0000.0000");
        Repository.Ex2.SetVer(gamePath.Directory, "2026.05.21.0000.0000");
        var handler = new VersionResponseHandler(PatchList(
            Row(100, "2026.05.20.0000.0000", "game/ex2/hash/D2026.05.20.0000.0000.patch"),
            Row(101, "2026.05.25.0000.0000", "game/ex2/hash/D2026.05.25.0000.0000.patch")));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<KoreanPatchException>(
            () => new KoreanPatchService(client).CheckAsync(
                gamePath.Directory,
                CancellationToken.None));

        Assert.AreEqual(KoreanPatchError.InstalledVersionNotInPatchChain, exception.Error);
        Assert.IsFalse(exception.Message.Contains("2026.05.21", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmptyFreshInstallResponseFailsClosed()
    {
        using var gamePath = new TemporaryGamePath();
        var handler = new VersionResponseHandler(PatchList());
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<KoreanPatchException>(
            () => new KoreanPatchService(client).CheckAsync(
                gamePath.Directory,
                CancellationToken.None));

        Assert.AreEqual(KoreanPatchError.ProtocolChanged, exception.Error);
    }

    [TestMethod]
    public async Task CompleteEmptyResponseIsAcceptedOnlyAtTheReportedLatestVersion()
    {
        using var gamePath = new TemporaryGamePath();
        Repository.Ffxiv.SetVer(gamePath.Directory, "2026.06.18.0000.0000");
        var handler = new VersionResponseHandler(PatchList());
        using var client = new HttpClient(handler);

        var plan = await new KoreanPatchService(client).CheckAsync(
            gamePath.Directory,
            CancellationToken.None);

        Assert.AreEqual(0, plan.PendingPatches.Count);
        Assert.IsFalse(plan.IsFreshInstall);
    }

    [TestMethod]
    public async Task RejectsUnexpectedPatchHostsWithoutRetainingTheResponse()
    {
        using var gamePath = new TemporaryGamePath();
        const string sensitiveMarker = "sensitive-response-marker";
        var body = PatchList(
            $"100\t100\t1\t1\t2026.05.25.0000.0000\thttp://example.invalid/game/ex2/hash/{sensitiveMarker}.patch");
        var handler = new VersionResponseHandler(body);
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<KoreanPatchException>(
            () => new KoreanPatchService(client).CheckAsync(
                gamePath.Directory,
                CancellationToken.None));

        Assert.AreEqual(KoreanPatchError.ProtocolChanged, exception.Error);
        Assert.IsFalse(exception.Message.Contains(sensitiveMarker, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(sensitiveMarker, StringComparison.Ordinal));
    }

    private static string PatchList(params string[] rows)
    {
        return $"--{Boundary}\r\n" +
               "Content-Type: application/octet-stream\r\n" +
               "Content-Location: ffxivpatch/metainfo.http\r\n" +
               "X-Patch-Length: 1\r\n\r\n" +
               string.Join("\r\n", rows) +
               $"\r\n--{Boundary}--\r\n";
    }

    private static string Row(long length, string version, string path)
    {
        return $"{length}\t100\t1\t1\t{version}\thttp://client-patch-live.ff14.co.kr/{path}";
    }

    private sealed class VersionResponseHandler(string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.ASCII, "multipart/mixed"),
            };
            response.Content.Headers.ContentType!.Parameters.Add(
                new("boundary", Boundary));
            response.Headers.Add("X-Repository", "actoz/win32/release_ko/game");
            response.Headers.Add("X-Patch-Module", "ZiPatch");
            response.Headers.Add("X-Latest-Version", "2026.06.18.0000.0000");
            return Task.FromResult(response);
        }
    }

    private sealed class TemporaryGamePath : IDisposable
    {
        public TemporaryGamePath()
        {
            Directory = new DirectoryInfo(Path.Combine(
                Path.GetTempPath(),
                $"xivlauncher-korea-patch-{Guid.NewGuid():N}"));
            Directory.Create();
        }

        public DirectoryInfo Directory { get; }

        public void Dispose()
        {
            if (Directory.Exists)
                Directory.Delete(true);
        }
    }
}
