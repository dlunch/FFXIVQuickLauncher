using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Korea;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Tests.Game.Korea;

[TestClass]
public sealed class KoreanGameLauncherTests
{
    [TestMethod]
    public void LaunchGamePassesUnencryptedProductionArgumentsToRunner()
    {
        using var gamePath = TemporaryGamePath.Create();
        var runner = new RecordingGameRunner();

        new KoreanGameLauncher().LaunchGame(
            runner,
            "game-token",
            string.Empty,
            gamePath.Directory,
            DpiAwareness.Unaware);

        Assert.AreEqual(gamePath.Executable.FullName, runner.Path);
        Assert.AreEqual(gamePath.Executable.DirectoryName, runner.WorkingDirectory);
        StringAssert.Contains(runner.Arguments, " DEV.TestSID=game-token");
        StringAssert.Contains(runner.Arguments, " DEV.LobbyHost01=nlobbyf-live.ff14.co.kr");
        Assert.AreEqual(0, runner.Environment?.Count);
        Assert.AreEqual(DpiAwareness.Unaware, runner.DpiAwareness);
    }

    [TestMethod]
    public void AdditionalArgumentsCannotOverrideGameTokenOrServerSettings()
    {
        using var gamePath = TemporaryGamePath.Create();
        var runner = new RecordingGameRunner();

        new KoreanGameLauncher().LaunchGame(
            runner,
            "official-token",
            "DEV.TestSID=attacker DEV.LobbyHost01=attacker.example custom=value with spaces",
            gamePath.Directory,
            DpiAwareness.Aware);

        Assert.IsFalse(runner.Arguments!.Contains("attacker", StringComparison.Ordinal));
        StringAssert.Contains(runner.Arguments, " DEV.TestSID=official-token");
    }

    [TestMethod]
    public void LaunchGameBuildsUnencryptedArguments()
    {
        using var gamePath = TemporaryGamePath.Create();
        var runner = new RecordingGameRunner();

        new KoreanGameLauncher().LaunchGame(
            runner,
            "game-token",
            string.Empty,
            gamePath.Directory,
            DpiAwareness.Unaware);

        Assert.IsFalse(runner.Arguments!.StartsWith("//**sqex", StringComparison.Ordinal));
        StringAssert.Contains(runner.Arguments, " DEV.TestSID=game-token");
        StringAssert.Contains(runner.Arguments, " DEV.LobbyHost01=nlobbyf-live.ff14.co.kr");
    }

    [TestMethod]
    public void LaunchGameRejectsMissingExecutableBeforeCallingRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xivlauncher-korea-launch-{Guid.NewGuid():N}");
        var gamePath = new DirectoryInfo(path);
        var runner = new RecordingGameRunner();

        try
        {
            Assert.ThrowsException<BinaryNotPresentException>(() =>
                new KoreanGameLauncher().LaunchGame(
                    runner,
                    "game-token",
                    string.Empty,
                    gamePath,
                    DpiAwareness.Unaware));
            Assert.IsNull(runner.Path);
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private sealed class RecordingGameRunner : IGameRunner
    {
        public string? Path { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public string? Arguments { get; private set; }

        public IDictionary<string, string>? Environment { get; private set; }

        public DpiAwareness DpiAwareness { get; private set; }

        public Process? Start(
            string path,
            string workingDirectory,
            string arguments,
            IDictionary<string, string> environment,
            DpiAwareness dpiAwareness)
        {
            this.Path = path;
            this.WorkingDirectory = workingDirectory;
            this.Arguments = arguments;
            this.Environment = environment;
            this.DpiAwareness = dpiAwareness;
            return null;
        }
    }

    private sealed class TemporaryGamePath : IDisposable
    {
        private TemporaryGamePath(DirectoryInfo directory, FileInfo executable)
        {
            this.Directory = directory;
            this.Executable = executable;
        }

        public DirectoryInfo Directory { get; }

        public FileInfo Executable { get; }

        public static TemporaryGamePath Create()
        {
            var path = Path.Combine(Path.GetTempPath(), $"xivlauncher-korea-launch-{Guid.NewGuid():N}");
            var gameDirectory = System.IO.Directory.CreateDirectory(Path.Combine(path, "game"));
            var executable = new FileInfo(Path.Combine(gameDirectory.FullName, "ffxiv_dx11.exe"));
            using (executable.Create())
            {
            }

            return new TemporaryGamePath(new DirectoryInfo(path), executable);
        }

        public void Dispose()
        {
            if (this.Directory.Exists)
                this.Directory.Delete(true);
        }
    }
}
