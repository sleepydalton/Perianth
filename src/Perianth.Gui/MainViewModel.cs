using Avalonia;
using Avalonia.Styling;

using Perianth.Formats.Diagnostics;

namespace Perianth.Gui;

/// <summary>
/// The window's state.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private bool _dark;
    private Settings _settings = new();

    public MainViewModel()
    {
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        // Choosing on the left describes in the middle. The panes do not know
        // about each other: one raises what happened, the window decides what
        // that means.
        Browse.Chosen += path => _ = Asset.ShowAsync(Browse.Paths, path);
        Browse.Opened += root =>
        {
            Export.UseArchives(root, Browse.Paths);
            Texture.UseArchives(root, Browse.Paths);
            Costume.UseArchives(root, Browse.Paths);
            Patch.UseArchives(root);
            Remember(_settings with { ArchiveRoot = root });
        };

        // A plain folder reaches the export pane and no further. Editing a
        // texture and making a patch both need the game's own file to compare
        // against, which a folder of extracted or modified files is not — so
        // those panes keep whatever archives they were given rather than being
        // pointed at a tree that cannot answer them. The folder is not
        // remembered either: the archive root is a setting because it is the
        // same every time, and a folder is wherever this piece of work is.
        Browse.OpenedFolder += root =>
        {
            Export.UseFolder(root, Browse.Paths);
            Texture.UseFolder(root, Browse.Paths);
        };
        Asset.Resolved += Export.Show;
        Asset.Resolved += Texture.Show;
        Asset.Resolved += Costume.Show;

        // So an edit can be seen in Blender before it is ever loaded in the
        // game. One function, not a reference to the other pane.
        // One overlay, two sources. The export reads an ordinary content root
        // and knows nothing about which pane put what in it.
        Export.StagedChanges = root =>
        {
            Result<int> textures = Texture.OverlayInto(root);
            if (!textures.TryGetValue(out int fromTextures, out Refusal? refusal))
            {
                return refusal;
            }

            Result<int> costume = Costume.OverlayInto(root);
            return costume.TryGetValue(out int fromCostume, out Refusal? costumeRefusal)
                ? Result.Ok(fromTextures + fromCostume)
                : costumeRefusal;
        };

        // What is being worn joins the export as models drawn alongside.
        Export.Equipment = () => Costume.WornModels;
        Costume.WornChanged += Export.CostumeChanged;
        Export.StagedCount = () => Texture.Staged;
        Export.Saved += () =>
        {
            // The texture pane suggests writing beside the user's other work.
            Texture.UseWorkingFolder(Export.WorkingFolder);

            Remember(_settings with
            {
                WorkingFolder = Export.WorkingFolder,
                VgmstreamCli = Export.Vgmstream,
                Locale = Export.Locale,
            });
        };
    }

    /// <summary>Finding a file in the archives.</summary>
    public BrowseViewModel Browse { get; } = new();

    /// <summary>What the chosen model resolves to.</summary>
    public AssetViewModel Asset { get; } = new();

    /// <summary>What its materials are painted with.</summary>
    public TextureViewModel Texture { get; } = new();

    /// <summary>Dressing the main character, when that is what is selected.</summary>
    public CostumeViewModel Costume { get; } = new();

    /// <summary>Applying patches somebody else made.</summary>
    public PatchViewModel Patch { get; } = new();

    /// <summary>Taking it out of the archives, and turning it into a GLB.</summary>
    public ExportViewModel Export { get; } = new();

    /// <summary>
    /// Restores what was chosen last time, and reopens the archives if they are
    /// still there.
    /// </summary>
    /// <remarks>
    /// Nothing here is required: a missing setting, or a folder that has since
    /// moved, leaves the window exactly as it opens for the first time. The
    /// archives are reopened rather than merely remembered because the path
    /// alone is no use — the index has to be walked before anything can be
    /// searched.
    /// </remarks>
    public async System.Threading.Tasks.Task RestoreAsync()
    {
        _settings = Settings.Load();

        if (_settings.Dark != _dark)
        {
            // Flip rather than toggle: restoring must not write back what it
            // has just read.
            Flip();
        }

        Export.Restore(_settings);
        Texture.UseWorkingFolder(_settings.WorkingFolder);

        if (_settings.ArchiveRoot is string root && System.IO.Directory.Exists(root))
        {
            await Browse.OpenAsync(root).ConfigureAwait(true);
        }
    }

    private void Remember(Settings settings)
    {
        _settings = settings with { Dark = _dark };

        // A preference that cannot be saved is not worth interrupting anyone
        // over, and there is nothing they could do about it now.
        _ = _settings.Save();
    }

    /// <summary>Switches between the light and dark palettes.</summary>
    public RelayCommand ToggleThemeCommand { get; }

    /// <summary>Names the theme the button would switch to, not the current one.</summary>
    public string ThemeButtonText => _dark ? "Light mode" : "Dark mode";

    private void ToggleTheme()
    {
        Flip();
        Remember(_settings);
    }

    private void Flip()
    {
        _dark = !_dark;

        if (Application.Current is Application application)
        {
            application.RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        Raise(nameof(ThemeButtonText));
    }
}
