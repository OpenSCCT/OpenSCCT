using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenScct.Launcher.Content;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Launcher.App;

internal sealed partial class MainForm
{
    private void ChooseSteamSource()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the Chaos Theory installation from your Steam library",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = _steamSource.Value ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (GodotSteamContent.FindSource(picker.SelectedPath) is not { } source)
            {
                SelectPage(LibraryPage.Games);
                SetStatus("This folder does not contain the Steam edition of Chaos Theory Versus. Choose its installed game folder.", Theme.Warning);
                return;
            }
            var settings = _settings.Copy();
            settings.GodotSteamFolder = source;
            settings.Save(_layout.Settings);
            _settings = settings;
            _steamSource = new Lazy<string?>(() => source);
            SetStatus("Steam installation selected.", Theme.Accent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SelectPage(LibraryPage.Games);
            SetStatus("Could not select the Steam installation. " + ex.Message, Theme.Error);
        }
        finally
        {
            _detailsKey = null;
            RefreshLocal();
        }
    }

    private bool NeedsSteamSource => _steamContentRoot is null && _steamSource.Value is null;

    private async Task EnsureSteamContentAsync(CancellationToken cancel, SemVersion? requiredGame = null)
    {
        var minimum = requiredGame > GodotSteamContent.MinimumGameVersion ? requiredGame : GodotSteamContent.MinimumGameVersion;
        if (_store.Current() is not { } current || current < minimum)
        {
            await CheckAsync(cancel);
            if (_lastCheck?.Latest is not { } latest || latest.Version < minimum)
                throw new InvalidOperationException("The Steam content builder is unavailable. Check for updates again.");
            await UpdateGameBinaryAsync(cancel);
            if (_store.Current() is not { } installed || installed < minimum)
                throw new InvalidOperationException("Close the running game to finish installing the Steam content builder.");
        }
        if (_steamContent.ActiveRoot() is null) await BuildSteamContentAsync(cancel);
    }

    private async Task BuildSteamContentAsync(CancellationToken cancel)
    {
        if (_steamContent.ActiveRoot() is not null && (_games.Running().Count > 0 || _unreal.IsRunning || EditorUsesSharedContent()))
            throw new InvalidOperationException("Close the game versions and editor before rebuilding their game files.");
        string? folder = _steamSource.Value;
        if (folder is null || GodotSteamContent.FindSource(folder) is not { } source)
        {
            _steamSource = new Lazy<string?>(() => null);
            throw new InvalidOperationException("Choose your Steam installation in Settings to build the game files.");
        }
        var version = _store.Current() ?? throw new InvalidOperationException("Install the Godot client before building its game files.");
        if (!GodotSteamContent.SupportsClient(version))
            throw new InvalidOperationException("Update the Godot client before building its Steam game files.");
        SetOperation("Building Steam game files");
        SetStatus("Reading the Steam installation.", Theme.Muted);
        _progressText.Visible = true;
        // The characters step alone runs for minutes after every other unit is done. Show how long the running unit has
        // been going, ticking each second, so the last unit does not look frozen.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var unitStarted = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        ContentProgress? last = null;
        bool finishing = false;
        void Show()
        {
            if (IsDisposed || _work is null || cancel.IsCancellationRequested || last is not { } p) return;
            string unit = p.Running.FirstOrDefault() ?? p.Line;
            string clock = Clock(unitStarted.TryGetValue(unit, out var since) ? started.Elapsed - since : started.Elapsed);
            string more = p.Running.Count > 1 ? $" and {p.Running.Count - 1} more" : string.Empty;
            _progressText.Text = p.Total > 0 ? $"{p.Done} of {p.Total}  ·  {unit}{more}  ·  {clock}" : $"{unit}  ·  {clock}";
            bool lastUnit = p.Total > 0 && p.Done == p.Total - 1 && p.Running.Count == 1;
            if (lastUnit != finishing)
            {
                finishing = lastUnit;
                SetStatus(lastUnit ? $"Finishing {unit}. This last part can take several minutes." : "Converting the Steam game files.", Theme.Muted);
            }
        }
        var progress = new Progress<ContentProgress>(p =>
        {
            if (IsDisposed || _work is null || cancel.IsCancellationRequested) return;
            if (last is null) SetStatus("Converting the Steam game files.", Theme.Muted);
            foreach (string name in p.Running) unitStarted.TryAdd(name, started.Elapsed);
            foreach (string name in unitStarted.Keys.Except(p.Running).ToList()) unitStarted.Remove(name);
            last = p;
            _progress.Visible = p.Total > 0;
            _progress.Fraction = p.Total > 0 ? (double)p.Done / p.Total : 0;
            Show();
        });
        using var tick = new System.Windows.Forms.Timer { Interval = 1000 };
        tick.Tick += (_, _) => Show();
        tick.Start();
        await _steamContent.BuildAsync(_layout.VersionDir(version.ToString()), source, progress, cancel);
        tick.Stop();
        SetStatus("Steam game files are ready.", Theme.Accent);
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
