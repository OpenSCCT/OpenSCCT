using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OpenScct.Launcher.Ps2;
using OpenScct.Launcher.Retail;
using OpenScct.Project;

namespace OpenScct.Launcher.Linux;

internal sealed partial class MainWindow
{
    private static readonly (string Id, string Title, string Group)[] Editions =
    [
        ("Game", "OpenSCCT", "Game versions"), ("Unreal", "OpenSCCT Unreal 5.8.4", "Game versions"),
    ];

    private readonly StackPanel _godotPage;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _feedback = Ui.Text(string.Empty, 14);
    private readonly StackPanel _sidebar = new() { Spacing = 4, Width = 282 };
    private readonly ComboBox _compactEditions = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, Button> _editionButtons = new();
    private readonly Button _games = Ui.Button("Games");
    private readonly Button _mods = Ui.Button("Mods");
    private string _section = "Games";
    private string _edition = "Game";
    private string? _lastJoin;
    private Bitmap? _artwork;

    private void InitializeLibrary()
    {
        if (File.Exists(_model.Layout.Settings) || _model.Store.Current() is not null)
        {
            string saved = _model.Settings.SelectedProduct;
            _edition = Editions.Any(e => e.Id == saved) ? saved : "Game";
        }
        _model.Settings.SelectedProduct = _edition;
        var title = Ui.Text("OpenSCCT", 25);
        title.FontWeight = FontWeight.Bold;
        title.VerticalAlignment = VerticalAlignment.Center;
        var mark = new Image { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        using (Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenScct.Launcher.icon.png"))
        {
            if (stream is not null)
            {
                var logo = new Bitmap(stream);
                mark.Source = logo;
                Closed += (_, _) => logo.Dispose();
            }
        }
        var brand = Ui.Row(8, mark, title, new Border
        {
            Child = Ui.Text(_model.LauncherVersion?.ToString() ?? "dev", 11, Ui.Accent),
            Background = Ui.Surface, Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var nav = Ui.Row(6, Nav(_games, "Games", "games"), Nav(_mods, "Mods", "mods"), Nav(_settings, "Settings", "settings"), _launcher);
        var web = IconButton(Link("Website", ProjectLinks.Website), "web", "OpenSCCT website");
        var utilities = Ui.Row(5, web);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 22, Margin = new Thickness(20, 9) };
        header.Children.Add(brand);
        Grid.SetColumn(nav, 1); header.Children.Add(nav);
        Grid.SetColumn(utilities, 2); header.Children.Add(utilities);
        _games.Click += (_, _) => ShowPage("Games", _edition);
        _mods.Click += (_, _) => ShowPage("Mods", _edition);
        _settings.Click += (_, _) => ShowPage("Settings", _edition);

        string? previousGroup = null;
        foreach ((string id, string label, string group) in Editions)
        {
            if (group != previousGroup)
            {
                var heading = Caption(group);
                heading.Margin = new Thickness(9, previousGroup is null ? 2 : 17, 0, 5);
                _sidebar.Children.Add(heading);
                previousGroup = group;
            }
            var button = EditionRow(id, label);
            _editionButtons.Add(id, button);
            _sidebar.Children.Add(button);
        }
        _compactEditions.ItemsSource = Editions.Select(e => e.Group + " · " + e.Title).ToArray();
        _compactEditions.SelectionChanged += (_, _) =>
        {
            int index = _compactEditions.SelectedIndex;
            if (index >= 0 && Editions[index].Id != _edition) SelectEdition(Editions[index].Id);
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(13, 19, 18, 0) };
        body.Children.Add(_sidebar);
        var pageStack = Ui.Stack(_compactEditions, _feedback, _page);
        pageStack.Spacing = 13;
        pageStack.Margin = new Thickness(0, 0, 0, 20);
        _libraryScroll.Content = pageStack;
        _libraryScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        Grid.SetColumn(_libraryScroll, 1);
        body.Children.Add(_libraryScroll);
        void ResizeLibrary(double width)
        {
            bool compact = width < 860;
            _sidebar.IsVisible = !compact;
            _compactEditions.IsVisible = compact && _section != "Settings";
            body.ColumnSpacing = compact ? 0 : 18;
            header.ColumnSpacing = width < 650 ? 8 : 22;
            utilities.IsVisible = width >= 650;
            brand.IsVisible = width >= 780;
            header.Margin = new Thickness(width < 650 ? 12 : 20, 9);
        }
        SizeChanged += (_, e) => ResizeLibrary(e.NewSize.Width);
        ResizeLibrary(Width);
        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        shell.Children.Add(new Border { Child = header, BorderBrush = Ui.Edge, BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Shade("#20291F", "#161C17") });
        Grid.SetRow(body, 1); shell.Children.Add(body);
        Content = shell;
        ShowPage("Games", _edition);
    }

    private Control UnrealPage(bool mods) => Ui.Stack(
        GameHero("Unreal", "Game versions · Unreal Engine", mods ? "Unreal community maps" : "OpenSCCT Unreal 5.8.4",
            mods ? "Manage maps using the Windows launcher." : "The Unreal modernisation is available for Windows x64.",
            Link("Unreal releases", "https://github.com/OpenSCCT/OpenSCCT-Unreal/releases"),
            Ui.Text("Windows x64", 13, Ui.Muted)),
        Card("Native Linux edition", "OpenSCCT runs on Linux and builds the original Versus content from your Steam installation.",
            ActionButton("Open Godot edition", () => SelectEdition("Game"))));

    private async Task<string?> PickFile(string title, string? pattern = null)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (pattern is not null) options.FileTypeFilter = [new FilePickerFileType("ISO disc image") { Patterns = [pattern, "*.ISO"] }];
        var picked = await StorageProvider.OpenFilePickerAsync(options);
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath() ?? throw new IOException("Choose a local file.");
    }

    private async Task<string?> PickFolder(string title)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.");
    }

    private Button ActionButton(string label, Action action, bool primary = false) =>
        ActionButton(label, () => { action(); return Task.CompletedTask; }, primary);

    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = Ui.Button(label, primary);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or Win32Exception or NotSupportedException or System.Text.Json.JsonException)
            { SetFeedback(ex.Message, true); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Button Link(string label, string url) => ActionButton(label, () => OpenLink(url));

    private static Control Card(string title, string description, params Control[] actions)
    {
        var stack = Ui.Stack(Ui.Text(title, 20), Ui.Text(description, 15, Ui.Muted));
        foreach (Control action in actions) stack.Children.Add(action);
        return Ui.Panel(stack);
    }

    private static WrapPanel ButtonRow(params Button[] buttons)
    {
        var row = new WrapPanel();
        foreach (Button button in buttons) { button.Margin = new Thickness(0, 2, 6, 2); row.Children.Add(button); }
        return row;
    }

    private void Highlight(Button button, bool selected)
    {
        button.Background = selected ? Ui.Accent : Ui.Surface;
        button.BorderBrush = selected ? Ui.Accent : Ui.Edge;
        _navLabels[button].Foreground = selected ? Ui.Background : Ui.Primary;
        _navIcons[button].Stroke = selected ? Ui.Background : Ui.Accent;
    }

    private void SetFeedback(string text, bool error = false)
    {
        _feedback.Text = text;
        _feedback.Foreground = error ? Ui.Error : Ui.Accent;
        _feedback.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void RequirePage(string section, string edition)
    {
        if (_section != section || _edition != edition || _page.Content is not Control)
            throw new InvalidOperationException($"The {edition} {section} page did not open.");
    }

    private string GodotStatus() => _model.Busy ? "Working..."
        : _model.ClientUpdateRequired ? "Update Godot"
        : _model.Playable ? "Ready to play"
        : _model.SteamSource is null ? "Steam folder needed"
        : _model.Store.Current() is null ? "Ready to install" : "Build Steam content";

    private void SelectEdition(string id)
    {
        ShowPage(_section == "Mods" ? "Mods" : "Games", id);
        try
        {
            _model.SaveSettings(settings => settings.SelectedProduct = id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetFeedback(ex.Message, true); }
    }

    private void ShowPage(string section, string edition)
    {
        _section = section;
        _edition = edition;
        _compactEditions.SelectedIndex = Array.FindIndex(Editions, e => e.Id == edition);
        foreach ((string id, Button button) in _editionButtons)
        {
            bool selected = id == edition && section != "Settings";
            button.Background = selected ? SelectedRow : Brushes.Transparent;
            button.BorderBrush = selected ? SelectedEdge : Brushes.Transparent;
            _editionLabels[id].Foreground = selected ? Ui.Accent : Ui.Primary;
            var state = _editionStates[id];
            state.Foreground = Ui.Muted;
            _editionLights[id].Background = id switch
            {
                "Game" => _model.Playable ? Ui.Accent : Pending,
                _ => Ui.Muted,
            };
            state.Text = id switch
            {
                "Unreal" => "Windows x64",
                _ => GodotStatus(),
            };
        }
        Highlight(_games, section == "Games");
        Highlight(_mods, section == "Mods");
        Highlight(_settings, section == "Settings");
        SetFeedback(string.Empty);
        _compactEditions.IsVisible = !_sidebar.IsVisible && section != "Settings";
        _libraryScroll.Offset = default;
        _page.Content = section == "Settings" ? SettingsPage() : edition switch
        {
            "Unreal" => UnrealPage(section == "Mods"),
            _ => section == "Games" ? _godotPage : Ui.Stack(
                GameHero("Game", "OpenSCCT · Mods", "Community maps", "Browse and install additional Versus maps.",
                    ActionButton("Manage maps", ShowMapsAsync, true), Ui.Text("Native Linux", 13, Ui.Muted)),
                Card("Original maps", "The original Versus maps are built from the installed Steam edition.",
                    ActionButton("Open Godot setup", () => ShowPage("Games", "Game")))),
        };
    }
}
