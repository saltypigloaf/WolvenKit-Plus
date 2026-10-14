using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.App.Controllers;
using WolvenKit.App.Models;
using WolvenKit.App.Models.ProjectManagement.Project;
using WolvenKit.App.Services;
using WolvenKit.Common.Interfaces;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Services;

namespace WolvenKit.UnitTests;

[TestClass]
public class ProjectPreparationOperationTests
{
    [TestMethod]
    [DataRow("zip")]
    [DataRow("install")]
    [DataRow("launch")]
    [DataRow("profile")]
    [DataRow("hot")]
    public async Task PreparationFailure_AbortsBeforeBackupCleanupPackingOrGameAccess_AndRestoresReady(string action)
    {
        using var fixture = new ControllerFixture();
        var oldOutput = fixture.Directory.File("project/packed/old.archive");
        var emptySourceDirectory = Path.Combine(fixture.Project.ModDirectory, "empty");
        Directory.CreateDirectory(emptySourceDirectory);
        var outside = fixture.Directory.File("outside/target.bin", FileAttributes.ReadOnly);
        var link = fixture.Directory.Junction("project/link", Path.GetDirectoryName(outside)!);

        var result = action == "hot"
            ? await fixture.Controller.InstallProjectHotAsync()
            : await fixture.Controller.LaunchProjectAsync(new LaunchProfile
            {
                CreateZipFile = action == "zip",
                Install = action is "install" or "launch",
                LaunchGame = action == "launch",
                CreateBackup = true,
                CleanAll = action != "profile"
            });

        Assert.IsFalse(result);
        Assert.IsTrue(File.Exists(oldOutput), "Cleanup must not run after failed preparation.");
        Assert.IsTrue(Directory.Exists(emptySourceDirectory), "Packing must not delete empty folders after failed preparation.");
        Assert.AreEqual(0, Directory.GetFiles(fixture.Directory.Project, "*.zip").Length);
        Assert.IsTrue(File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly));
        fixture.ModTools.VerifyNoOtherCalls();
        fixture.Settings.VerifyNoOtherCalls();
        fixture.Notifications.Verify(n => n.Error(It.Is<string>(s => s.Contains(link)), false), Times.Once);
        fixture.Logger.Verify(l => l.Error(It.Is<Exception>(e => e.Message.Contains(link))), Times.Once);
        fixture.AssertReady();
    }

    [TestMethod]
    public async Task Launch_PreparesBeforeBackupAndCleanup_AndReplacesReadOnlyZip()
    {
        using var fixture = new ControllerFixture();
        var source = fixture.Directory.File("project/source/archive/source.bin", FileAttributes.ReadOnly | FileAttributes.Archive);
        var oldOutput = fixture.Directory.File("project/packed/old.archive", FileAttributes.ReadOnly);
        fixture.Directory.File("project/Test_previousBuild.zip", FileAttributes.ReadOnly);
        fixture.Directory.File("project/Test.zip", FileAttributes.ReadOnly);
        var sourceWasWritable = false;
        var cleanupRanBeforePacking = false;
        fixture.ModTools.Setup(m => m.Pack(It.IsAny<DirectoryInfo>(), It.IsAny<DirectoryInfo>(), It.IsAny<string>()))
            .Returns(() =>
            {
                sourceWasWritable = !File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly);
                cleanupRanBeforePacking = !File.Exists(oldOutput);
                fixture.Directory.File("project/packed/new.archive");
                return true;
            });

        Assert.IsTrue(await fixture.Controller.LaunchProjectAsync(new LaunchProfile { CreateBackup = true, CreateZipFile = true }));
        Assert.IsTrue(sourceWasWritable);
        Assert.IsTrue(cleanupRanBeforePacking);
        using var backup = ZipFile.OpenRead(Path.Combine(fixture.Directory.Project, "Test_previousBuild.zip"));
        Assert.IsNotNull(backup.GetEntry("old.archive"));
        using var zip = ZipFile.OpenRead(Path.Combine(fixture.Directory.Project, "Test.zip"));
        Assert.IsNotNull(zip.GetEntry("new.archive"));
        fixture.AssertReady();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Launch_CleanupFailureCannotBeMaskedByLaterSuccessfulDeletes(bool topLevelFile)
    {
        using var fixture = new ControllerFixture();
        var lockedFile = fixture.Directory.File(topLevelFile ? "project/packed/00-blocked.archive" : "project/packed/blocked/old.archive");
        var laterFile = fixture.Directory.File("project/packed/later.txt");
        using var fileLock = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.IsFalse(await fixture.Controller.LaunchProjectAsync(new LaunchProfile { Install = true, LaunchGame = true }));

        Assert.IsTrue(File.Exists(lockedFile));
        Assert.IsFalse(File.Exists(laterFile), "A later successful deletion must not reset the failed result.");
        fixture.ModTools.VerifyNoOtherCalls();
        fixture.Settings.VerifyNoOtherCalls();
        var affectedPath = topLevelFile ? lockedFile : Path.GetDirectoryName(lockedFile)!;
        fixture.Logger.Verify(l => l.Error(It.Is<string>(s => s.Contains(affectedPath))), Times.Once);
        fixture.AssertReady();
    }

    [TestMethod]
    public async Task Launch_PostBuildCleanupFailureAbortsBeforeLaunchingGame()
    {
        using var fixture = new ControllerFixture();
        fixture.Directory.File("project/source/archive/source.bin");
        FileStream? fileLock = null;
        var laterFile = Path.Combine(fixture.Project.PackedRootDirectory, "later.txt");
        fixture.ModTools.Setup(m => m.Pack(It.IsAny<DirectoryInfo>(), It.IsAny<DirectoryInfo>(), It.IsAny<string>()))
            .Returns(() =>
            {
                var lockedFile = fixture.Directory.File("project/packed/blocked/old.archive");
                fixture.Directory.File("project/packed/later.txt");
                fileLock = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return true;
            });
        try
        {
            Assert.IsFalse(await fixture.Controller.LaunchProjectAsync(new LaunchProfile { CleanAllPostBuild = true, LaunchGame = true }));
            Assert.IsFalse(File.Exists(laterFile));
            fixture.Settings.VerifyNoOtherCalls();
            fixture.AssertReady();
        }
        finally
        {
            fileLock?.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HotReload_PreparesBeforePacking_AndPropagatesPostInstallCleanupResult(bool cleanupFails)
    {
        using var fixture = new ControllerFixture();
        var source = fixture.Directory.File("project/source/archive/source.bin", FileAttributes.ReadOnly);
        var oldOutput = fixture.Directory.File("project/packed/blocked/old.archive", FileAttributes.ReadOnly);
        var laterFile = fixture.Directory.File("project/packed/later.txt", FileAttributes.ReadOnly);
        var game = Path.Combine(fixture.Directory.Root, "game");
        var gameFile = fixture.Directory.File("game/untouched.bin", FileAttributes.ReadOnly);
        var writableAtPack = false;
        FileStream? fileLock = null;
        fixture.Settings.Setup(s => s.GetRED4GameRootDir()).Returns(game);
        fixture.ModTools.Setup(m => m.Pack(It.IsAny<DirectoryInfo>(), It.IsAny<DirectoryInfo>(), It.IsAny<string>()))
            .Returns(() =>
            {
                writableAtPack = !File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly) &&
                                 !File.GetAttributes(oldOutput).HasFlag(FileAttributes.ReadOnly);
                return true;
            });
        fixture.ModTools.Setup(m => m.InstallFiles(It.IsAny<DirectoryInfo>(), It.IsAny<DirectoryInfo>(), true))
            .Returns(() =>
            {
                if (cleanupFails)
                {
                    fileLock = new FileStream(oldOutput, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                return true;
            });
        try
        {
            Assert.AreEqual(!cleanupFails, await fixture.Controller.InstallProjectHotAsync());
            Assert.IsTrue(writableAtPack);
            Assert.IsFalse(File.Exists(laterFile));
            Assert.AreEqual(cleanupFails, File.Exists(oldOutput));
            Assert.IsTrue(File.GetAttributes(gameFile).HasFlag(FileAttributes.ReadOnly));
            fixture.Notifications.Verify(n => n.Success(It.IsAny<string>(), false), cleanupFails ? Times.Never() : Times.Once());
            fixture.AssertReady();
        }
        finally
        {
            fileLock?.Dispose();
        }
    }

    [TestMethod]
    public async Task PackingException_RestoresReadyAndReleasesOperationGuard()
    {
        using var fixture = new ControllerFixture();
        var source = fixture.Directory.File("project/source/archive/source.bin");
        fixture.ModTools.SetupSequence(m => m.Pack(It.IsAny<DirectoryInfo>(), It.IsAny<DirectoryInfo>(), It.IsAny<string>()))
            .Throws(new IOException($"Injected packing error: {source}"))
            .Returns(true);

        Assert.IsFalse(await fixture.Controller.LaunchProjectAsync(new LaunchProfile()));
        fixture.AssertReady();
        fixture.Notifications.Verify(n => n.Error(It.Is<string>(s => s.Contains(source)), false), Times.Once);
        Assert.IsTrue(await fixture.Controller.LaunchProjectAsync(new LaunchProfile()));
        fixture.AssertReady();
    }

    [TestMethod]
    public async Task ConcurrentOperation_IsRejectedWithoutResettingRunningOperation()
    {
        using var fixture = new ControllerFixture();
        fixture.Directory.File("project/source/archive/source.bin");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ModTools.Setup(m => m.Pack(It.IsAny<DirectoryInfo>(), It.IsAny<DirectoryInfo>(), It.IsAny<string>()))
            .Returns(() =>
            {
                started.SetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return true;
            });

        var operation = fixture.Controller.LaunchProjectAsync(new LaunchProfile());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(await fixture.Controller.InstallProjectHotAsync());
            Assert.AreEqual(EStatus.Running, fixture.Progress.Status);
            Assert.IsTrue(fixture.Progress.IsIndeterminate);
            fixture.Settings.VerifyNoOtherCalls();
        }
        finally
        {
            release.SetResult();
            await operation;
        }
        Assert.IsTrue(await operation);
        fixture.AssertReady();
    }

    private sealed class ControllerFixture : IDisposable
    {
        public ProjectPreparationDirectory Directory { get; } = new();
        public Cp77Project Project { get; }
        public Mock<IModTools> ModTools { get; } = new(MockBehavior.Strict);
        public Mock<ISettingsManager> Settings { get; } = new(MockBehavior.Strict);
        public Mock<ILoggerService> Logger { get; } = new();
        public Mock<INotificationService> Notifications { get; } = new();
        public ProgressService<double> Progress { get; } = new() { Status = EStatus.Ready };
        public RED4Controller Controller { get; }

        public ControllerFixture()
        {
            Project = new Cp77Project(Directory.File("project/Test.cpmodproj"), "Test", "test");
            Project.CreateDefaultDirectories();
            var manager = new Mock<IProjectManager>();
            manager.SetupGet(m => m.ActiveProject).Returns(Project);
            var plugins = new Mock<IPluginService>();
            plugins.Setup(p => p.IsInstalled(It.IsAny<EPlugin>())).Returns(true);
            Controller = new RED4Controller(Logger.Object, Notifications.Object, manager.Object, Settings.Object,
                Mock.Of<IHashService>(), ModTools.Object, Mock.Of<IAppArchiveManager>(), Progress,
                plugins.Object, Mock.Of<IModifierViewStateService>(), Mock.Of<IProjectEvents>());
        }

        public void AssertReady()
        {
            Assert.IsFalse(Progress.IsIndeterminate);
            Assert.AreEqual(EStatus.Ready, Progress.Status);
        }

        public void Dispose() => Directory.Dispose();
    }
}
