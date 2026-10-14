using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.App.Helpers;

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
        File.SetAttributes(parent, File.GetAttributes(parent) | FileAttributes.ReadOnly);
        var directoryAttributes = File.GetAttributes(parent);
        var originalAttributes = File.GetAttributes(source);

        var result = ProjectFilePreparation.ClearReadOnly(fixture.Project);

        Assert.AreEqual(3, result.FilesChanged);
        Assert.AreEqual(3, result.FilesChecked);
        Assert.AreEqual(originalAttributes & ~FileAttributes.ReadOnly, File.GetAttributes(source));
        Assert.IsFalse(File.GetAttributes(output).HasFlag(FileAttributes.ReadOnly));
        Assert.IsFalse(File.GetAttributes(projectFile).HasFlag(FileAttributes.ReadOnly));
        Assert.AreEqual(directoryAttributes, File.GetAttributes(parent));
        Assert.AreEqual("test payload", File.ReadAllText(source));
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
        fixture.File("project/source/test.bin", FileAttributes.ReadOnly);
        Assert.AreEqual(1, ProjectFilePreparation.ClearReadOnly(fixture.Project).FilesChanged);
        Assert.AreEqual(original, File.GetAttributes(path));
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
        var original = fileSystem.GetAttributes(path);

        Assert.AreEqual(1, ProjectFilePreparation.ClearReadOnly(fixture.Project, fileSystem).FilesChanged);
        Assert.AreEqual(original & ~FileAttributes.ReadOnly, fileSystem.LastWrittenAttributes);
        Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
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
        var link = fixture.Junction(rootIsJunction ? "linked-project" : "project/link", Path.GetDirectoryName(external)!);
        var root = rootIsJunction ? link : fixture.Project;
        var error = Assert.ThrowsException<IOException>(() => ProjectFilePreparation.ClearReadOnly(root));
        StringAssert.Contains(error.Message, link);
        Assert.AreEqual(original, File.GetAttributes(external));
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
        public Dictionary<string, uint> Tags { get; } = [];
        public Dictionary<string, FileAttributes> ExtraAttributes { get; } = [];
        public FileAttributes LastWrittenAttributes { get; private set; }
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
            return base.GetEntries(directory);
        }

        public override void SetAttributes(string path, FileAttributes attributes)
        {
            Fail(path, "write");
            WrittenPaths.Add(path);
            LastWrittenAttributes = attributes;
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
        Directory.Delete(root, true);
    }
}
