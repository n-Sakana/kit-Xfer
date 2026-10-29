using System;
using System.Collections.Generic;
using System.IO;

namespace Xfer
{
    internal static class FolderCatalog
    {

        public static FolderSnapshot Inspect(string path)
        {
            var fullPath = RequireDirectory(path);
            var directory = new DirectoryInfo(fullPath);
            var files = new List<FolderFile>();
            CollectFiles(directory, directory, files);

            files.Sort(delegate (FolderFile left, FolderFile right)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });
            return new FolderSnapshot(fullPath, fullPath, "folder", files);
        }

        public static FolderSnapshot InspectFiles(IList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                throw new ArgumentException("選ぶファイルが指定されていません。", "paths");
            }

            var files = new List<FolderFile>();
            string directoryPath = null;
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("空のファイル名は選べません。", "paths");
                }

                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        string.Format("File not found: {0}", fullPath),
                        fullPath);
                }

                var file = new FileInfo(fullPath);
                if (directoryPath == null)
                {
                    directoryPath = file.DirectoryName;
                }
                else if (!string.Equals(
                    directoryPath,
                    file.DirectoryName,
                    PlatformInfo.IsWindows
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "一度に選ぶファイルは同じフォルダに置かれている必要があります。",
                        "paths");
                }

                if (!names.Add(file.Name))
                {
                    continue;
                }

                files.Add(Describe(file, file.Name));
            }

            files.Sort(delegate (FolderFile left, FolderFile right)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });

            var displayPath = files.Count == 1 ? files[0].FullPath : directoryPath;
            return new FolderSnapshot(displayPath, directoryPath, "files", files);
        }

        public static string PickerPattern(string mode)
        {
            return "*";
        }

        private static string RequireDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A folder path is required.", "path");
            }

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException(string.Format("Folder not found: {0}", fullPath));
            }

            return fullPath;
        }

        private static bool ShouldShow(FileSystemInfo entry)
        {
            if (!PlatformInfo.IsWindows && entry.Name.StartsWith(".", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                return (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void CollectFiles(
            DirectoryInfo root,
            DirectoryInfo directory,
            List<FolderFile> files)
        {
            FileInfo[] currentFiles;
            try
            {
                currentFiles = directory.GetFiles("*", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var file in currentFiles)
            {
                if (!ShouldShow(file))
                {
                    continue;
                }

                try
                {
                    files.Add(Describe(file, RelativeName(root.FullName, file.FullName), true));
                }
                catch (FileNotFoundException)
                {
                    // A file can disappear while a live folder is being enumerated.
                }
            }

            DirectoryInfo[] childDirectories;
            try
            {
                childDirectories = directory.GetDirectories("*", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in childDirectories)
            {
                if (!ShouldShow(child) || IsReparsePoint(child))
                {
                    continue;
                }
                CollectFiles(root, child, files);
            }
        }

        private static bool IsReparsePoint(FileSystemInfo entry)
        {
            try
            {
                return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string RelativeName(string rootPath, string filePath)
        {
            var prefix = rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = PlatformInfo.IsWindows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!filePath.StartsWith(prefix, comparison))
            {
                return Path.GetFileName(filePath);
            }

            return filePath.Substring(prefix.Length)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static FolderFile Describe(FileInfo file, string name, bool fromFolder = false)
        {
            var extension = file.Extension.ToLowerInvariant();
            return new FolderFile(
                file.FullName,
                name,
                extension,
                BadgeFor(extension),
                KindFor(extension),
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
        }

        private static string KindFor(string extension)
        {
            switch (extension)
            {
                case ".pdf":
                    return "PDF";
                case ".doc":
                case ".docx":
                case ".docm":
                case ".rtf":
                    return "Word";
                case ".xls":
                case ".xlsx":
                case ".xlsm":
                case ".xlsb":
                case ".xla":
                case ".xlam":
                case ".xlt":
                case ".xltx":
                case ".xltm":
                    return "Excel";
                case ".ppt":
                case ".pptx":
                case ".pptm":
                    return "PowerPoint";
                default:
                    return "Other";
            }
        }

        private static string BadgeFor(string extension)
        {
            var badge = extension.TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(badge) ? "FILE" : badge.Substring(0, Math.Min(4, badge.Length));
        }
    }

    internal sealed class FolderSnapshot
    {
        public FolderSnapshot(
            string path,
            string directoryPath,
            string sourceKind,
            List<FolderFile> files)
        {
            Path = path;
            DirectoryPath = directoryPath;
            SourceKind = sourceKind;
            Files = files;
        }

        public string Path { get; private set; }
        public string SourceKind { get; private set; }
        public List<FolderFile> Files { get; private set; }
        internal string DirectoryPath { get; private set; }
    }

    internal sealed class FolderFile
    {
        public FolderFile(
            string fullPath,
            string name,
            string extension,
            string badge,
            string kind,
            long size,
            DateTimeOffset modifiedUtc)
        {
            FullPath = fullPath;
            Name = name;
            Extension = extension;
            Badge = badge;
            Kind = kind;
            Size = size;
            ModifiedUtc = modifiedUtc;
        }

        public string Name { get; private set; }
        public string Extension { get; private set; }
        public string Badge { get; private set; }
        public string Kind { get; private set; }
        public long Size { get; private set; }
        public DateTimeOffset ModifiedUtc { get; private set; }
        internal string FullPath { get; private set; }
    }
}
