// Moving the downloaded engines to another folder (Settings → Engines → where engines are stored).
//
// The chosen folder must be empty or already one of Filee's engine folders: the installer replaces folders named after
// the engines ("ffmpeg", "calibre", …), so a busy folder like D:\software, which may hold the user's own FFmpeg in
// D:\software\ffmpeg, would lose it. Moving is all or nothing: when one entry can't be moved (an engine is running),
// the entries already moved go back and the engines stay where they were.

namespace Filee.Engines.Infrastructure;

/// <summary>Whether a folder can receive the engines (<see cref="EngineFolder.Check"/>).</summary>
public enum EngineFolderCheck
{
    /// <summary>Empty, missing or already an engine folder: the engines can move there.</summary>
    Usable,

    /// <summary>The same folder as the current one (also through a junction or symbolic link): nothing to move.</summary>
    Same,

    /// <summary>One folder is inside the other.</summary>
    Nested,

    /// <summary>The folder has files that aren't Filee's engines.</summary>
    NotEmpty,

    /// <summary>Not a full local path.</summary>
    Invalid,
}

public static class EngineFolder
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether the engines in <paramref name="current"/> can move to <paramref name="target"/>.</summary>
    public static EngineFolderCheck Check(string current, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return EngineFolderCheck.Invalid;
        var from = Physical(current);
        var to = Physical(target);
        if (string.Equals(from, to, PathComparison))
            return EngineFolderCheck.Same;
        if (IsInside(to, from) || IsInside(from, to))
            return EngineFolderCheck.Nested;
        return IsEmptyOrEngineFolder(target) ? EngineFolderCheck.Usable : EngineFolderCheck.NotEmpty;
    }

    /// <summary>
    /// Moves everything in <paramref name="from"/> into <paramref name="to"/> (which <see cref="Check"/> found usable)
    /// and removes <paramref name="from"/> when it is left empty. On the same drive the folders are renamed; otherwise
    /// they are copied, then deleted. When an entry fails, the moved ones go back and the exception is rethrown.
    /// </summary>
    /// <param name="progress">Fraction of the bytes moved, 0 to 1.</param>
    public static async Task MoveAsync(string from, string to, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from))
        {
            progress?.Report(1);
            return;
        }
        // Through a junction a folder can be on another drive than its path says: move between the real folders.
        var source = Physical(from);
        var target = Physical(to);
        var sameDrive = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), PathComparison);
        var entries = new DirectoryInfo(source).GetFileSystemInfos();
        var total = Math.Max(1, entries.Sum(Size));
        long done = 0;
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(target, entry.Name);
                var size = Size(entry);
                // An engine the target folder already has (it was an engine folder before) keeps its copy there.
                if (!Path.Exists(destination))
                {
                    var start = done;
                    await Task.Run(() => MoveEntry(entry, destination, sameDrive,
                        bytes => progress?.Report(Math.Min(1, (double)(start + bytes) / total)), cancellationToken), cancellationToken);
                    moved.Add((entry.FullName, destination));
                }
                done += size;
                progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        catch
        {
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try { MoveEntry(Info(moved[i].To), moved[i].From, sameDrive, null, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        // The old folder goes when it is left empty; a junction or symbolic link goes as a link, then its target.
        DeleteIfEmpty(from);
        if (!string.Equals(source, Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)), PathComparison))
            DeleteIfEmpty(source);
        progress?.Report(1);
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True when <paramref name="folder"/> is missing, empty, or holds Filee's engines.</summary>
    internal static bool IsEmptyOrEngineFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return true;
        var entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        return entries.Count == 0
               || Directory.Exists(Path.Combine(folder, ".downloads"))
               || entries.Any(e => File.Exists(Path.Combine(e, ".filee-component")));
    }

    /// <summary>The full path with a junction or symbolic link at its end resolved, without a trailing separator.</summary>
    private static string Physical(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            if (Directory.Exists(full) && Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
                full = target.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    private static FileSystemInfo Info(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

    private static long Size(FileSystemInfo entry) => entry switch
    {
        FileInfo file => file.Length,
        DirectoryInfo dir when dir.LinkTarget is null => dir.EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }).Sum(f => f.Length),
        _ => 0,
    };

    private static void MoveEntry(FileSystemInfo entry, string destination, bool sameDrive, Action<long>? copied,
        CancellationToken cancellationToken)
    {
        if (sameDrive)
        {
            if (entry is DirectoryInfo)
                Directory.Move(entry.FullName, destination);
            else
                File.Move(entry.FullName, destination);
            return;
        }
        long bytes = 0;
        try
        {
            if (entry is FileInfo file)
            {
                file.CopyTo(destination);
                copied?.Invoke(file.Length);
                file.Delete();
                return;
            }
            CopyFolder((DirectoryInfo)entry, destination, length => copied?.Invoke(bytes += length), cancellationToken);
            Directory.Delete(entry.FullName, recursive: true);
        }
        catch
        {
            // Leave the source whole and the target without a half copy. A source that was partly deleted (a locked
            // file stopped the deletion) is completed again from the copy before the copy goes.
            if (Directory.Exists(destination) && Directory.Exists(entry.FullName))
                RestoreFrom(destination, entry.FullName);
            TryDelete(destination);
            throw;
        }
    }

    private static void CopyFolder(DirectoryInfo source, string destination, Action<long> copied, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in source.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.CopyTo(Path.Combine(destination, file.Name));
            copied(file.Length);
        }
        foreach (var dir in source.EnumerateDirectories())
            CopyFolder(dir, Path.Combine(destination, dir.Name), copied, cancellationToken);
    }

    private static void RestoreFrom(string copy, string source)
    {
        foreach (var file in Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories))
        {
            var original = Path.Combine(source, Path.GetRelativePath(copy, file));
            if (File.Exists(original))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            try { File.Copy(file, original); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
