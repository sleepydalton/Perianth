using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Perianth.Gui;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private PatchWindow? _patches;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        WireTexturePane();

        // After the window exists, so that reopening the archives can report
        // progress into a pane that is already on screen.
        Opened += async (_, _) => await _model.RestoreAsync().ConfigureAwait(true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Opens the window for applying somebody else's patches.
    /// </summary>
    /// <remarks>
    /// One window, reopened rather than replaced, so patches already read stay
    /// read if it is closed and opened again.
    /// </remarks>
    private void OpenPatchWindow(object? sender, RoutedEventArgs e)
    {
        if (_patches is null)
        {
            _patches = new PatchWindow(_model.Patch);

            // Subscribed only where the window is made. Doing it on every click
            // would add a handler per click to the same window.
            _patches.Closed += (_, _) => _patches = null;
        }

        _patches.Show(this);
        _patches.Activate();
    }

    /// <summary>
    /// Decodes the textures the first time that tab is looked at.
    /// </summary>
    /// <remarks>
    /// Which tab is showing is the window's business, not the view model's, and
    /// the decode is deferred rather than done on selection because eighty
    /// archive reads should not be the cost of clicking a file name.
    /// </remarks>
    private async void AssetTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs)
        {
            return;
        }

        _model.Texture.IsShowing = tabs.SelectedIndex == 1;

        if (_model.Texture.IsShowing)
        {
            await _model.Texture.OpenedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Raises the three dialogs the texture pane needs.
    /// </summary>
    /// <remarks>
    /// The view model asks and the window answers, as with every other picker
    /// here: only a top-level control can raise one, and what comes back is a
    /// path the core takes from there.
    /// </remarks>
    private void WireTexturePane()
    {
        _model.Texture.SaveRequested += async () =>
        {
            string suggested = _model.Texture.SuggestedPngPath();

            try
            {
                IStorageFile? chosen = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save the texture as a PNG",
                    SuggestedFileName = Path.GetFileName(suggested),
                    DefaultExtension = "png",
                    FileTypeChoices = [PngFiles],
                }).ConfigureAwait(true);

                if (chosen?.TryGetLocalPath() is string path)
                {
                    await _model.Texture.SaveAsPngAsync(path).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
        };

        _model.Texture.ReplaceRequested += async () =>
        {
            try
            {
                IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Choose the edited image",
                        AllowMultiple = false,
                        FileTypeFilter = [ImageFiles],
                    }).ConfigureAwait(true);

                if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string path)
                {
                    await _model.Texture.ReplaceFromPngAsync(path).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
        };

        _model.Texture.AddRequested += async () =>
        {
            try
            {
                IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Choose the image to use",
                        AllowMultiple = false,
                        FileTypeFilter = [ImageFiles],
                    }).ConfigureAwait(true);

                if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string path)
                {
                    await _model.Texture.UseNewImageAsync(path).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
        };

        _model.Texture.WriteRequested += async () =>
        {
            string? folder = await AskForFolderAsync("Where to write the mod folder").ConfigureAwait(true);
            if (folder is not null)
            {
                await _model.Texture.WriteModAsync(folder).ConfigureAwait(true);
            }
        };

        _model.Shape.ChooseRequested += async () =>
        {
            try
            {
                IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Choose the reshaped model",
                        AllowMultiple = false,
                        FileTypeFilter = [GlbFiles],
                    }).ConfigureAwait(true);

                if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string path)
                {
                    await _model.Shape.LoadAsync(path).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
        };

        _model.Shape.SaveRequested += async () =>
        {
            string? folder = await AskForFolderAsync("Where to write the mod folder").ConfigureAwait(true);
            if (folder is not null)
            {
                _model.Shape.SaveInto(folder);
            }
        };

        _model.New.ChooseMeshRequested += async () =>
        {
            try
            {
                IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Choose your mesh",
                        AllowMultiple = false,
                        FileTypeFilter = [GlbFiles],
                    }).ConfigureAwait(true);

                if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string path)
                {
                    _model.New.UseMesh(path);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
        };

        _model.New.SaveRequested += async () =>
        {
            string? folder = await AskForFolderAsync("Where to write the mod folder").ConfigureAwait(true);
            if (folder is not null)
            {
                // The mod takes the name of the thing, which is the only name
                // this pane has asked for. A second box for a folder name would
                // be a question with one sensible answer.
                await _model.New.SaveAsync(folder, _model.New.Name).ConfigureAwait(true);
            }
        };

        _model.Texture.PatchRequested += async () =>
        {
            string? folder = await AskForFolderAsync("Where to write the patches").ConfigureAwait(true);
            if (folder is not null)
            {
                await _model.Texture.SavePatchesAsync(folder).ConfigureAwait(true);
            }
        };
    }

    /// <summary>What Blender writes back out.</summary>
    private static FilePickerFileType GlbFiles => new("glTF binary")
    {
        Patterns = ["*.glb"],
        MimeTypes = ["model/gltf-binary"],
    };

    private static FilePickerFileType PngFiles => new("PNG images")
    {
        Patterns = ["*.png"],
        MimeTypes = ["image/png"],
    };

    /// <summary>
    /// What an author may bring back in: a PNG to convert, or a DDS they have
    /// already edited.
    /// </summary>
    private static FilePickerFileType ImageFiles => new("Images (PNG or DDS)")
    {
        Patterns = ["*.png", "*.dds"],
    };

    /// <summary>
    /// Puts one animation at the end of the queue.
    /// </summary>
    /// <remarks>
    /// A click rather than a tick, because the same animation can be queued more
    /// than once and a checkbox has no way to say "again".
    /// </remarks>
    private void AddClipToQueue(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipChoice choice })
        {
            _model.Export.Enqueue(choice);
        }
    }

    /// <summary>Asks which mod folder to export against.</summary>
    private async void ChooseModFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFolder> chosen = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose the mod folder", AllowMultiple = false })
                .ConfigureAwait(true);

            if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string path)
            {
                _model.Export.ModFolder = path;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }

    /// <summary>Asks where the voice decoder is.</summary>
    private async void ChooseVgmstream(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { Title = "Select vgmstream-cli", AllowMultiple = false })
                .ConfigureAwait(true);

            if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string path)
            {
                _model.Export.UseVgmstream(path);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A picker the platform will not raise is not a reason to take the
            // window down with it.
        }
    }

    /// <summary>Asks where to extract and export, and remembers it.</summary>
    private async void ChooseWorkingFolder(object? sender, RoutedEventArgs e)
    {
        string? folder = await AskForFolderAsync("Choose a working folder").ConfigureAwait(true);
        if (folder is not null)
        {
            _model.Export.UseWorkingFolder(folder);
        }
    }

    /// <summary>
    /// Asks for the archive folder and opens it.
    /// </summary>
    /// <remarks>
    /// The dialog lives here rather than in the view model because it belongs to
    /// the window: only a top-level control can raise one. What comes back is a
    /// path, and the view model takes it from there.
    /// </remarks>
    private async void ChooseArchiveFolder(object? sender, RoutedEventArgs e)
    {
        string? folder = await AskForFolderAsync("Select the folder holding sdf.sdftoc").ConfigureAwait(true);
        if (folder is not null)
        {
            await _model.Browse.OpenAsync(folder).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Browses a plain folder of loose files instead of the archives.
    /// </summary>
    /// <remarks>
    /// For an extraction this tool wrote, or a mod folder. Both mirror the
    /// archive's own paths, so everything downstream browses them unchanged.
    /// </remarks>
    private async void ChooseLooseFolder(object? sender, RoutedEventArgs e)
    {
        string? folder = await AskForFolderAsync("Select a folder of extracted or modified files").ConfigureAwait(true);
        if (folder is not null)
        {
            await _model.Browse.OpenFolderAsync(folder).ConfigureAwait(true);
        }
    }

    private async Task<string?> AskForFolderAsync(string title)
    {
        try
        {
            IReadOnlyList<IStorageFolder> chosen = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);

            return chosen.Count == 0 ? null : chosen[0].TryGetLocalPath();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A picker the platform will not raise is not a reason to take the
            // window down with it.
            return null;
        }
    }
}
