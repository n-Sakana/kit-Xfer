using System;
using System.Collections.Generic;
using System.IO;

namespace Xfer
{
    internal sealed class AppState
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, SelectionState> _selections =
            new Dictionary<string, SelectionState>(StringComparer.Ordinal);

        public AppState(string initialPath)
        {
            var initial = ResolveInitialSelection(initialPath);
            _selections.Add("optical", SelectionState.FromSnapshot(initial));
        }

        public string FolderPath(string mode)
        {
            lock (_gate)
            {
                return RequireSelection(mode).DirectoryPath;
            }
        }

        public FolderSnapshot ReadFolder(string mode)
        {
            lock (_gate)
            {
                return RequireSelection(mode).ReadSnapshot();
            }
        }

        public FolderSnapshot SelectFolder(string mode, string path)
        {
            var snapshot = FolderCatalog.Inspect(path);
            SetSelection(mode, snapshot);
            return snapshot;
        }

        public FolderSnapshot SelectFiles(string mode, IList<string> paths)
        {
            var snapshot = FolderCatalog.InspectFiles(paths);
            SetSelection(mode, snapshot);
            return snapshot;
        }

        public FolderSnapshot SelectPath(string mode, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("No file or folder was specified.", "path");
            }

            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                return SelectFolder(mode, fullPath);
            }
            if (File.Exists(fullPath))
            {
                return SelectFiles(mode, new[] { fullPath });
            }

            throw new FileNotFoundException(
                string.Format("Path not found: {0}", fullPath),
                fullPath);
        }

        public void ClearSelection(string mode)
        {
            lock (_gate)
            {
                RequireSelection(mode).Clear();
            }
        }

        private void SetSelection(string mode, FolderSnapshot snapshot)
        {
            lock (_gate)
            {
                _selections[RequireMode(mode)] = SelectionState.FromSnapshot(snapshot);
            }
        }

        private SelectionState RequireSelection(string mode)
        {
            return _selections[RequireMode(mode)];
        }

        private static string RequireMode(string mode)
        {
            if (mode != "optical")
            {
                throw new ArgumentException("Invalid selection mode.", "mode");
            }
            return mode;
        }

        private static FolderSnapshot ResolveInitialSelection(string requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                var requested = Path.GetFullPath(requestedPath);
                if (Directory.Exists(requested))
                {
                    return FolderCatalog.Inspect(requested);
                }
                if (File.Exists(requested))
                {
                    return FolderCatalog.InspectFiles(new[] { requested });
                }

                throw new FileNotFoundException(string.Format("Path not found: {0}", requested));
            }

            var defaultDirectory = DefaultPickerDirectory();
            return new FolderSnapshot(
                string.Empty,
                defaultDirectory,
                "none",
                new List<FolderFile>());
        }

        private static string DefaultPickerDirectory()
        {
            if (PlatformInfo.IsWindows)
            {
                var windowsRoot = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrWhiteSpace(windowsRoot) && Directory.Exists(windowsRoot))
                {
                    return Path.GetFullPath(windowsRoot);
                }
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return !string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents)
                ? Path.GetFullPath(documents)
                : Path.GetFullPath(Directory.GetCurrentDirectory());
        }

        private sealed class SelectionState
        {
            private string _sourceKind;
            private string _sourcePath;
            private List<string> _filePaths;

            private SelectionState()
            {
            }

            public string DirectoryPath { get; private set; }

            public static SelectionState FromSnapshot(FolderSnapshot snapshot)
            {
                if (snapshot == null)
                {
                    throw new ArgumentNullException("snapshot");
                }

                var selection = new SelectionState();
                selection.DirectoryPath = snapshot.DirectoryPath;
                if (snapshot.SourceKind == "none")
                {
                    return selection;
                }

                selection._sourceKind = snapshot.SourceKind;
                selection._sourcePath = snapshot.SourceKind == "folder" ? snapshot.Path : null;
                if (snapshot.SourceKind == "files")
                {
                    selection._filePaths = new List<string>();
                    foreach (var file in snapshot.Files)
                    {
                        selection._filePaths.Add(file.FullPath);
                    }
                }
                return selection;
            }

            public FolderSnapshot ReadSnapshot()
            {
                if (_sourceKind == null)
                {
                    return new FolderSnapshot(
                        string.Empty,
                        DirectoryPath,
                        "none",
                        new List<FolderFile>());
                }

                return _sourceKind == "files"
                    ? FolderCatalog.InspectFiles(new List<string>(_filePaths))
                    : FolderCatalog.Inspect(_sourcePath);
            }

            public void Clear()
            {
                _sourceKind = null;
                _sourcePath = null;
                _filePaths = null;
            }
        }
    }
}
