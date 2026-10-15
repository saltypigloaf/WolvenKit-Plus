using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WolvenKit.App.Helpers;

/// <summary>Prepares only the active project's files and directories, including previous build output.</summary>
internal static class ProjectFilePreparation
{
    internal readonly record struct Result(int FilesChecked, int FilesChanged, int DirectoriesChecked, int DirectoriesChanged);

    internal static Result ClearReadOnly(string projectDirectory) => ClearReadOnly(projectDirectory, new FileSystem());

    internal static Result ClearReadOnly(string projectDirectory, FileSystem fileSystem)
    {
        var currentPath = projectDirectory;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));

            // Validate ancestors without enumerating them: a junction above the project would also
            // redirect every path below it. Cloud directories are safe to enumerate in place.
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                currentPath = directory.FullName;
                if (IsRepositoryMetadata(directory.Name))
                {
                    throw new IOException("The project root is inside repository metadata.");
                }
                var attributes = fileSystem.GetAttributes(currentPath);
                EnsureSafeReparsePoint(currentPath, attributes, fileSystem);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException("The project root must be a directory.");
                }
            }

            var directories = new Stack<string>();
            directories.Push(root);
            var filesChecked = 0;
            var filesChanged = 0;
            var directoriesChecked = 0;
            var directoriesChanged = 0;
            while (directories.TryPop(out var directory))
            {
                currentPath = directory;
                // The root and descendants are writable; ancestors above the root remain validation-only.
                // Revalidate queued directories before changing attributes or enumerating their contents.
                var directoryAttributes = fileSystem.GetAttributes(directory);
                EnsureSafeReparsePoint(directory, directoryAttributes, fileSystem);
                directoriesChecked++;
                if (ClearReadOnlyAttribute(directory, directoryAttributes, fileSystem))
                {
                    directoriesChanged++;
                }

                // Materialize per directory so enumeration errors identify that directory.
                foreach (var entry in fileSystem.GetEntries(directory))
                {
                    currentPath = entry;
                    var relativePath = Path.GetRelativePath(root, entry);
                    if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
                        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    {
                        throw new IOException("The path is outside the active project.");
                    }
                    // Also protects the .git file used by worktrees/submodules, and nested repos.
                    if (IsRepositoryMetadata(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    var attributes = fileSystem.GetAttributes(entry);
                    EnsureSafeReparsePoint(entry, attributes, fileSystem);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }

                    filesChecked++;
                    if (ClearReadOnlyAttribute(entry, attributes, fileSystem))
                    {
                        filesChanged++;
                    }
                }
            }
            return new Result(filesChecked, filesChanged, directoriesChecked, directoriesChanged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or
                                  ArgumentException or NotSupportedException)
        {
            throw new IOException($"Could not prepare project path '{currentPath}': {e.Message}", e);
        }
    }

    private static bool ClearReadOnlyAttribute(string path, FileAttributes attributes, FileSystem fileSystem)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            return false;
        }
        fileSystem.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        return true;
    }

    private static bool IsRepositoryMetadata(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".svn", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafeReparsePoint(string path, FileAttributes attributes, FileSystem fileSystem)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        var tag = fileSystem.GetReparseTag(path);
        // IO_REPARSE_TAG_CLOUD and CLOUD_1..F differ only in the CLOUD_MASK (0xF000).
        // Do not skip these: OneDrive uses them for ordinary project files and directories.
        // All other tags fail closed, including symlinks and mount points (junctions).
        // https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-tags
        if ((tag & ~0x0000F000u) != 0x9000001Au)
        {
            throw new IOException($"Symbolic links, junctions, and unsupported reparse points cannot be prepared (tag 0x{tag:X8}).");
        }
    }

    // A narrow filesystem seam allows deterministic access-failure and cloud-tag regression tests
    // without changing ACLs or requiring a live OneDrive account.
    internal class FileSystem
    {
        public virtual FileAttributes GetAttributes(string path) => File.GetAttributes(path);

        public virtual void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

        public virtual string[] GetEntries(string directory) => Directory.GetFileSystemEntries(directory, "*",
            new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 });

        public virtual uint GetReparseTag(string path)
        {
            const uint openExisting = 3;
            const uint openReparsePoint = 0x00200000;
            const uint backupSemantics = 0x02000000; // Allows opening directories; does not elevate access.
            const int fileAttributeTagInfo = 9;

            // Unlike System.IO, P/Invoke does not automatically add the extended-length prefix.
            var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
                : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
            using var handle = CreateFileW(nativePath, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
                openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, fileAttributeTagInfo,
                    out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return info.ReparseTag;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, uint bufferSize);
}
