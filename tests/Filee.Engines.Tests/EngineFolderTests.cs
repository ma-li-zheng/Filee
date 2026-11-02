// Moving the downloaded engines to a folder the user chose (Settings → Engines → where engines are stored).

using System.Diagnostics;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class EngineFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-engine-folder-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Folder(string name) => Path.Combine(_root, name);

    /// <summary>An engine folder as the installer leaves it: the marker file and a program.</summary>
    private void MakeEngine(string engines, string id, string content = "program")
    {
        Directory.CreateDirectory(Path.Combine(engines, id, "bin"));
        File.WriteAllText(Path.Combine(engines, id, ".filee-component"), "sha256");
        File.WriteAllText(Path.Combine(engines, id, "bin", id + ".exe"), content);
    }

    [Fact]
    public void An_empty_or_missing_folder_can_take_the_engines()
    {
        var current = Folder("current");
        MakeEngine(current, "ffmpeg");
        Directory.CreateDirectory(Folder("empty"));

        Assert.Equal(EngineFolderCheck.Usable, EngineFolder.Check(current, Folder("empty")));
        Assert.Equal(EngineFolderCheck.Usable, EngineFolder.Check(current, Folder("missing")));
    }

    [Fact]
    public void A_folder_with_other_files_is_refused_so_its_own_ffmpeg_folder_is_never_replaced()
    {
        var current = Folder("current");
        MakeEngine(current, "ffmpeg");
        var software = Folder("software");
        Directory.CreateDirectory(Path.Combine(software, "ffmpeg", "bin"));
        File.WriteAllText(Path.Combine(software, "ffmpeg", "bin", "ffmpeg.exe"), "the user's own");

        Assert.Equal(EngineFolderCheck.NotEmpty, EngineFolder.Check(current, software));
    }

    [Fact]
    public void A_folder_that_already_holds_engines_can_take_them()
    {
        var current = Folder("current");
        var other = Folder("other");
        MakeEngine(other, "pandoc");

        Assert.Equal(EngineFolderCheck.Usable, EngineFolder.Check(current, other));
    }

    [Fact]
    public void The_same_folder_nested_folders_and_relative_paths_are_told_apart()
    {
        var current = Folder("current");
        Directory.CreateDirectory(current);

        Assert.Equal(EngineFolderCheck.Same, EngineFolder.Check(current, current + Path.DirectorySeparatorChar));
        Assert.Equal(EngineFolderCheck.Nested, EngineFolder.Check(current, Path.Combine(current, "inner")));
        Assert.Equal(EngineFolderCheck.Nested, EngineFolder.Check(current, _root));
        Assert.Equal(EngineFolderCheck.Invalid, EngineFolder.Check(current, "engines"));
    }

    [Fact]
    public async Task Moving_takes_every_engine_and_removes_the_old_folder()
    {
        var current = Folder("current");
        MakeEngine(current, "ffmpeg");
        MakeEngine(current, "pandoc");
        Directory.CreateDirectory(Path.Combine(current, ".downloads", "locks"));
        var target = Folder("target");
        var reports = new List<double>();

        await EngineFolder.MoveAsync(current, target, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(target, "ffmpeg", "bin", "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(target, "pandoc", ".filee-component")));
        Assert.True(Directory.Exists(Path.Combine(target, ".downloads", "locks")));
        Assert.False(Directory.Exists(current));
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task An_engine_the_new_folder_already_has_keeps_its_copy_and_the_old_one_stays()
    {
        var current = Folder("current");
        MakeEngine(current, "ffmpeg", "old");
        MakeEngine(current, "pandoc");
        var target = Folder("target");
        MakeEngine(target, "ffmpeg", "new");

        await EngineFolder.MoveAsync(current, target, null, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "ffmpeg", "bin", "ffmpeg.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(current, "ffmpeg", "bin", "ffmpeg.exe")));
        Assert.True(Directory.Exists(Path.Combine(target, "pandoc")));
    }

    [Fact]
    public async Task An_engine_in_use_stops_the_move_and_the_moved_ones_go_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a folder with an open file");
        var current = Folder("current");
        MakeEngine(current, "calibre");
        MakeEngine(current, "ffmpeg");
        MakeEngine(current, "pandoc");
        var target = Folder("target");

        using (File.Open(Path.Combine(current, "ffmpeg", "bin", "ffmpeg.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => EngineFolder.MoveAsync(current, target, null, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        foreach (var id in new[] { "calibre", "ffmpeg", "pandoc" })
        {
            Assert.True(File.Exists(Path.Combine(current, id, "bin", id + ".exe")), id);
            Assert.False(Directory.Exists(Path.Combine(target, id)), id);
        }
    }

    [Fact]
    public async Task A_junction_to_the_chosen_folder_is_the_same_folder_and_goes_when_engines_move_elsewhere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature");
        var real = Folder("real");
        MakeEngine(real, "ffmpeg");
        var link = Folder("link");
        Junction(link, real);

        Assert.Equal(EngineFolderCheck.Same, EngineFolder.Check(link, real));
        Assert.Equal(EngineFolderCheck.Same, EngineFolder.Check(real, link));

        var elsewhere = Folder("elsewhere");
        await EngineFolder.MoveAsync(link, elsewhere, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(elsewhere, "ffmpeg", "bin", "ffmpeg.exe")));
        Assert.False(Directory.Exists(link));
        Assert.False(Directory.Exists(real));
    }

    [Fact]
    public void The_chosen_folder_replaces_the_default_and_none_keeps_it()
    {
        Assert.Equal(EngineEnvironment.DefaultDownloadRoot(Filee.Core.Settings.UserDataStore.OverrideDirectory),
            EngineEnvironment.ResolveDownloadRoot(null));
        Assert.Equal(EngineEnvironment.ResolveDownloadRoot(null), EngineEnvironment.ResolveDownloadRoot("  "));
        Assert.Equal(Folder("chosen"), EngineEnvironment.ResolveDownloadRoot(Folder("chosen")));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    /// <summary>Reports at once (Progress&lt;T&gt; posts to the thread pool, so the last report could come late).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
