using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.App.Helpers;
using WolvenKit.App.Models.ProjectManagement.Project;
using WolvenKit.Core.Interfaces;

namespace WolvenKit.UnitTests;

[TestClass]
public class ProjectFilePreparationTests
{
    [TestMethod]
    public void ClearReadOnly_IncludesNestedSourceAndBuildOutput_PreservesOtherAttributes()
    {
        using var fixture = new ProjectPreparationDirectory();
        var source = fixture.File("project/source/hidden/nested.bin", FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        var output = fixture.File("project/packed/archive/old.archive", FileAttributes.ReadOnly | FileAttributes.Archive);
        var projectFile = fixture.File("project/test.cpmodproj", FileAttributes.ReadOnly);
        var parent = Path.GetDirectoryName(source)!;
        Directory.SetLastWriteTimeUtc(parent, new DateTime(2020, 1, 1));
        File.SetAttributes(parent, File.GetAttributes(parent) | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        var directoryAttributes = File.GetAttributes(parent);
        var originalAttributes = File.GetAttributes(source);

        var result = ProjectFilePreparation.ClearReadOnly(fixture.Project);

        Assert.AreEqual(3, result.FilesChanged);
        Assert.AreEqual(3, result.FilesChecked);
        Assert.AreEqual(1, result.DirectoriesChanged);
        Assert.AreEqual(5, result.DirectoriesChecked);
        Assert.AreEqual(originalAttributes & ~FileAttributes.ReadOnly, File.GetAttributes(source));
        Assert.IsFalse(File.GetAttributes(output).HasFlag(FileAttributes.ReadOnly));
        Assert.IsFalse(File.GetAttributes(projectFile).HasFlag(FileAttributes.ReadOnly));
        Assert.AreEqual(directoryAttributes & ~FileAttributes.ReadOnly, File.GetAttributes(parent));
        Assert.AreEqual("test payload", File.ReadAllText(source));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("source/archive/empty")]
    public void ClearReadOnly_EmptyRootAndNestedDirectories_PreservesAttributesAndAncestors(string relativeDirectory)
    {
        using var fixture = new ProjectPreparationDirectory();
        var leaf = Directory.CreateDirectory(Path.Combine(fixture.Project, relativeDirectory)).FullName;
        var originals = new Dictionary<string, FileAttributes>();
        for (var path = leaf; ; path = Path.GetDirectoryName(path)!)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
            originals[path] = File.GetAttributes(path);
            if (path == fixture.Project)
            {
                break;
            }
        }
        // This fixture-owned directory is above the project root and must never be changed by preparation.
        File.SetAttributes(fixture.Root, File.GetAttributes(fixture.Root) | FileAttributes.ReadOnly | FileAttributes.Archive);
        var ancestorAttributes = File.GetAttributes(fixture.Root);
        var fileSystem = new TestFileSystem();

        var result = ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem);

        Assert.AreEqual(0, result.FilesChecked);
        Assert.AreEqual(0, result.FilesChanged);
        Assert.AreEqual(originals.Count, result.DirectoriesChecked);
        Assert.AreEqual(originals.Count, result.DirectoriesChanged);
        foreach (var (path, attributes) in originals)
        {
            Assert.AreEqual(attributes & ~FileAttributes.ReadOnly, File.GetAttributes(path));
        }
        Assert.AreEqual(ancestorAttributes, File.GetAttributes(fixture.Root));
        CollectionAssert.DoesNotContain(fileSystem.WrittenPaths, fixture.Root);

        fileSystem.WrittenPaths.Clear();
        var repeated = ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem);
        Assert.AreEqual(0, repeated.DirectoriesChanged);
        Assert.AreEqual(0, repeated.FilesChanged);
        Assert.AreEqual(0, fileSystem.WrittenPaths.Count, "Writable directories must not have attributes rewritten.");
    }

    [TestMethod]
    public void ClearReadOnly_ThenActualProjectEmptyFolderCleanup_RemovesReadOnlyEmptyBranch()
    {
        using var fixture = new ProjectPreparationDirectory();
        var project = new Cp77Project(fixture.File("project/Test.cpmodproj"), "Test", "test");
        var archiveDirectory = project.ModDirectory;
        var leaf = Directory.CreateDirectory(Path.Combine(archiveDirectory, "2bt", "shirts_the_third", "round_neck")).FullName;
        var retainedFile = fixture.File("project/source/archive/retained/data.bin");
        for (var path = leaf; ; path = Path.GetDirectoryName(path)!)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly | FileAttributes.Archive);
            if (path == project.ProjectDirectory)
            {
                break;
            }
        }

        var result = ProjectFilePreparation.ClearReadOnly(project.ProjectDirectory);
        project.DeleteEmptyFolders(Mock.Of<ILoggerService>());

        Assert.AreEqual(6, result.DirectoriesChanged);
        Assert.IsFalse(Directory.Exists(Path.Combine(archiveDirectory, "2bt")));
        Assert.IsTrue(Directory.Exists(archiveDirectory));
        Assert.IsTrue(Directory.Exists(project.ProjectDirectory));
        Assert.AreEqual("test payload", File.ReadAllText(retainedFile));
    }

    [TestMethod]
    public void ClearReadOnly_OrdinaryFilesAndReruns_DoNotWriteAttributes()
    {
        using var fixture = new ProjectPreparationDirectory();
        var ordinary = fixture.File("project/ordinary.bin", FileAttributes.Hidden | FileAttributes.Archive);
        var readOnly = fixture.File("project/source/readonly.bin", FileAttributes.ReadOnly | FileAttributes.Archive);
        var timestamp = File.GetLastWriteTimeUtc(ordinary);
        var attributes = File.GetAttributes(ordinary);
        var fileSystem = new TestFileSystem();

        ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem);
        CollectionAssert.AreEqual(new[] { readOnly }, fileSystem.WrittenPaths);
        fileSystem.WrittenPaths.Clear();
        Assert.AreEqual(0, ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem).FilesChanged);
        Assert.AreEqual(0, fileSystem.WrittenPaths.Count);
        Assert.AreEqual(attributes, File.GetAttributes(ordinary));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(ordinary));
    }

    [TestMethod]
    [DataRow(".git", false)]
    [DataRow(".GiT", true)]
    [DataRow("nested/.hg", false)]
    [DataRow("nested/.svn", false)]
    public void ClearReadOnly_LeavesRepositoryMetadataUntouched(string metadata, bool isFile)
    {
        using var fixture = new ProjectPreparationDirectory();
        var path = fixture.File($"project/{metadata}{(isFile ? "" : "/config")}", FileAttributes.ReadOnly | FileAttributes.Hidden);
        var original = File.GetAttributes(path);
        var metadataPath = Path.GetFullPath(Path.Combine(fixture.Project, metadata));
        File.SetAttributes(metadataPath, File.GetAttributes(metadataPath) | FileAttributes.ReadOnly | FileAttributes.Hidden);
        var metadataAttributes = File.GetAttributes(metadataPath);
        fixture.File("project/source/test.bin", FileAttributes.ReadOnly);
        Assert.AreEqual(1, ProjectFilePreparation.ClearReadOnly(fixture.Project).FilesChanged);
        Assert.AreEqual(original, File.GetAttributes(path));
        Assert.AreEqual(metadataAttributes, File.GetAttributes(metadataPath));
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)]
    [DataRow(4)] [DataRow(5)] [DataRow(6)] [DataRow(7)]
    [DataRow(8)] [DataRow(9)] [DataRow(10)] [DataRow(11)]
    [DataRow(12)] [DataRow(13)] [DataRow(14)] [DataRow(15)]
    public void ClearReadOnly_TraversesCloudDirectoriesAndFiles(int variant)
    {
        using var fixture = new ProjectPreparationDirectory();
        var path = fixture.File("project/cloud/placeholder.bin", FileAttributes.ReadOnly | FileAttributes.Archive);
        var cloudDirectory = Path.GetDirectoryName(path)!;
        var fileSystem = new TestFileSystem();
        var tag = 0x9000001Au | ((uint)variant << 12);
        fileSystem.Tags[fixture.Project] = tag;
        fileSystem.Tags[cloudDirectory] = tag;
        fileSystem.Tags[path] = tag;
        fileSystem.ExtraAttributes[path] = FileAttributes.Offline | (FileAttributes)0x00080000; // Pinned
        File.SetAttributes(fixture.Project, File.GetAttributes(fixture.Project) | FileAttributes.ReadOnly | FileAttributes.Archive);
        File.SetAttributes(cloudDirectory, File.GetAttributes(cloudDirectory) | FileAttributes.ReadOnly | FileAttributes.Archive);
        fileSystem.ExtraAttributes[cloudDirectory] = (FileAttributes)0x00080000; // Pinned
        var original = fileSystem.GetAttributes(path);
        var originalRoot = fileSystem.GetAttributes(fixture.Project);
        var originalDirectory = fileSystem.GetAttributes(cloudDirectory);

        var result = ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem);

        Assert.AreEqual(1, result.FilesChanged);
        Assert.AreEqual(2, result.DirectoriesChanged);
        Assert.AreEqual(2, result.DirectoriesChecked);
        Assert.AreEqual(original & ~FileAttributes.ReadOnly, fileSystem.WrittenAttributes[path]);
        Assert.AreEqual(originalRoot & ~FileAttributes.ReadOnly, fileSystem.WrittenAttributes[fixture.Project]);
        Assert.AreEqual(originalDirectory & ~FileAttributes.ReadOnly, fileSystem.WrittenAttributes[cloudDirectory]);
        Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.IsFalse(File.GetAttributes(cloudDirectory).HasFlag(FileAttributes.ReadOnly));
        Assert.IsFalse(File.GetAttributes(fixture.Project).HasFlag(FileAttributes.ReadOnly));
    }

    [TestMethod]
    [DataRow(0xA0000003u)] // mount point
    [DataRow(0xA000000Cu)] // symlink
    [DataRow(0x9000001Cu)] // unknown to this helper (ProjFS)
    public void ClearReadOnly_RejectsUnsupportedTags(uint tag)
    {
        using var fixture = new ProjectPreparationDirectory();
        var path = fixture.File("project/unsupported.bin", FileAttributes.ReadOnly);
        var fileSystem = new TestFileSystem();
        fileSystem.Tags[path] = tag;
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem));
        StringAssert.Contains(error.Message, path);
        StringAssert.Contains(error.Message, $"0x{tag:X8}");
        Assert.AreEqual(0, fileSystem.WrittenPaths.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClearReadOnly_RealJunctionCannotModifyExternalTarget(bool rootIsJunction)
    {
        using var fixture = new ProjectPreparationDirectory();
        var external = fixture.File("project-outside/target.bin", FileAttributes.ReadOnly | FileAttributes.Archive);
        var original = File.GetAttributes(external);
        var externalDirectory = Path.GetDirectoryName(external)!;
        File.SetAttributes(externalDirectory, File.GetAttributes(externalDirectory) | FileAttributes.ReadOnly | FileAttributes.Archive);
        var originalDirectory = File.GetAttributes(externalDirectory);
        var link = fixture.Junction(rootIsJunction ? "linked-project" : "project/link", externalDirectory);
        var root = rootIsJunction ? link : fixture.Project;
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(root));
        StringAssert.Contains(error.Message, link);
        Assert.AreEqual(original, File.GetAttributes(external));
        Assert.AreEqual(originalDirectory, File.GetAttributes(externalDirectory));
    }

    [TestMethod]
    public void ClearReadOnly_RejectsJunctionAboveProjectRoot()
    {
        using var fixture = new ProjectPreparationDirectory();
        var external = fixture.File("outside/child/target.bin", FileAttributes.ReadOnly);
        var link = fixture.Junction("ancestor", Path.Combine(fixture.Root, "outside"));
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(Path.Combine(link, "child")));
        StringAssert.Contains(error.Message, link);
        Assert.IsTrue(File.GetAttributes(external).HasFlag(FileAttributes.ReadOnly));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClearReadOnly_RealSymbolicLinksCannotModifyExternalTargets(bool directoryLink)
    {
        using var fixture = new ProjectPreparationDirectory();
        var external = fixture.File("outside/target.bin", FileAttributes.ReadOnly);
        var link = Path.Combine(fixture.Project, "link");
        try
        {
            if (directoryLink)
            {
                Directory.CreateSymbolicLink(link, Path.GetDirectoryName(external)!);
            }
            else
            {
                File.CreateSymbolicLink(link, external);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating symlinks requires Windows Developer Mode or the symlink privilege; no elevation is requested.");
        }
        fixture.Links.Add((link, directoryLink));
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(fixture.Project));
        StringAssert.Contains(error.Message, link);
        Assert.IsTrue(File.GetAttributes(external).HasFlag(FileAttributes.ReadOnly));
    }

    [TestMethod]
    [DataRow("enumerate")]
    [DataRow("read")]
    [DataRow("write")]
    public void ClearReadOnly_AccessFailuresIdentifyPathAndPreserveCause(string stage)
    {
        using var fixture = new ProjectPreparationDirectory();
        var file = fixture.File("project/blocked/file.bin", FileAttributes.ReadOnly);
        var path = stage == "enumerate" ? Path.GetDirectoryName(file)! : file;
        var cause = new UnauthorizedAccessException("Injected access denial");
        var fileSystem = new TestFileSystem { FailurePath = path, FailureStage = stage, Failure = cause };
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem));
        StringAssert.Contains(error.Message, path);
        StringAssert.Contains(error.Message, cause.Message);
        Assert.AreSame(cause, error.InnerException);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("blocked")]
    public void ClearReadOnly_DirectoryAttributeFailure_IdentifiesPathAndStopsTraversal(string relativeDirectory)
    {
        using var fixture = new ProjectPreparationDirectory();
        var path = Directory.CreateDirectory(Path.Combine(fixture.Project, relativeDirectory)).FullName;
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        var cause = new UnauthorizedAccessException("Injected directory attribute denial");
        var fileSystem = new TestFileSystem { FailurePath = path, FailureStage = "write", Failure = cause };

        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem));

        StringAssert.Contains(error.Message, path);
        StringAssert.Contains(error.Message, cause.Message);
        Assert.AreSame(cause, error.InnerException);
        CollectionAssert.DoesNotContain(fileSystem.EnumeratedPaths, path);
        Assert.IsTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [TestMethod]
    public void ClearReadOnly_RejectsMissingRoot()
    {
        using var fixture = new ProjectPreparationDirectory();
        var missing = Path.Combine(fixture.Project, "missing");
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(missing));
        StringAssert.Contains(error.Message, missing);
    }

    private sealed class TestFileSystem : ProjectFilePreparation.FileSystem
    {
        public List<string> WrittenPaths { get; } = [];
        public List<string> EnumeratedPaths { get; } = [];
        public Dictionary<string, uint> Tags { get; } = [];
        public Dictionary<string, FileAttributes> ExtraAttributes { get; } = [];
        public Dictionary<string, FileAttributes> WrittenAttributes { get; } = [];
        public string? FailurePath { get; init; }
        public string? FailureStage { get; init; }
        public Exception? Failure { get; init; }

        private void Fail(string path, string stage)
        {
            if (path == FailurePath && stage == FailureStage && Failure is not null)
            {
                throw Failure;
            }
        }

        public override FileAttributes GetAttributes(string path)
        {
            Fail(path, "read");
            return base.GetAttributes(path) | (Tags.ContainsKey(path) ? FileAttributes.ReparsePoint : 0) |
                   ExtraAttributes.GetValueOrDefault(path);
        }

        public override string[] GetEntries(string directory)
        {
            Fail(directory, "enumerate");
            EnumeratedPaths.Add(directory);
            return base.GetEntries(directory);
        }

        public override void SetAttributes(string path, FileAttributes attributes)
        {
            Fail(path, "write");
            WrittenPaths.Add(path);
            WrittenAttributes[path] = attributes;
            base.SetAttributes(path, attributes & ~(FileAttributes.ReparsePoint | ExtraAttributes.GetValueOrDefault(path)));
        }

        public override uint GetReparseTag(string path) => Tags.TryGetValue(path, out var tag) ? tag : base.GetReparseTag(path);
    }
}

internal sealed class ProjectPreparationDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"WolvenKit.ProjectPreparation.{Guid.NewGuid():N}");
    public string Project => Path.Combine(Root, "project");
    public List<(string Path, bool IsDirectory)> Links { get; } = [];

    public ProjectPreparationDirectory() => Directory.CreateDirectory(Project);

    public string File(string relativePath, FileAttributes attributes = FileAttributes.Normal)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "test payload");
        System.IO.File.SetAttributes(path, attributes);
        return path;
    }

    public string Junction(string relativePath, string target)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", path, target })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        Links.Add((path, true));
        return path;
    }

    public void Dispose()
    {
        // Only delete this fixture's explicitly recorded links, then its GUID-named temp tree.
        // Remove links before recursive enumeration so teardown cannot visit their targets.
        var root = Path.GetFullPath(Root);
        Assert.IsTrue(root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
        foreach (var (path, isDirectory) in Links)
        {
            Assert.IsTrue(Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (isDirectory)
            {
                Directory.Delete(path);
            }
            else
            {
                System.IO.File.Delete(path);
            }
        }
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetAttributes(path, FileAttributes.Normal);
        }
        foreach (var path in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetAttributes(path, FileAttributes.Directory);
        }
        System.IO.File.SetAttributes(root, FileAttributes.Directory);
        Directory.Delete(root, true);
    }
}
