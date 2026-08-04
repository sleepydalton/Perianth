using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Perianth.Gui;

/// <summary>
/// The window for applying somebody else's patches.
/// </summary>
/// <remarks>
/// Its own window rather than a fourth tab, because nothing here depends on
/// which model is selected or on anything else the main window is showing. The
/// dialogs live here for the same reason they live in the main window: only a
/// top-level control can raise one.
/// </remarks>
public sealed partial class PatchWindow : Window
{
    private readonly PatchViewModel _model;

    public PatchWindow()
        : this(new PatchViewModel())
    {
    }

    public PatchWindow(PatchViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        InitializeComponent();
        DataContext = model;

        model.OpenRequested += async () =>
        {
            string? folder = await AskForFolderAsync(
                "Choose the folder holding the patches").ConfigureAwait(true);

            if (folder is not null)
            {
                await _model.OpenFolderAsync(folder).ConfigureAwait(true);
            }
        };

        model.OpenFilesRequested += async () => await OpenAsync().ConfigureAwait(true);
        model.MakeRequested += async () => await MakeAsync().ConfigureAwait(true);
        model.WriteRequested += async () => await WriteAsync().ConfigureAwait(true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task OpenAsync()
    {
        try
        {
            IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Choose the patches to apply",
                    AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType("Perianth patches")
                    {
                        Patterns = ["*.perianthpatch"],
                    }],
                }).ConfigureAwait(true);

            List<string> files = [];
            foreach (IStorageFile file in chosen)
            {
                if (file.TryGetLocalPath() is string path)
                {
                    files.Add(path);
                }
            }

            await _model.OpenAsync(files).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A picker the platform will not raise is not a reason to take the
            // window down with it.
        }
    }

    /// <summary>Asks for the mod folder, then where the patches should go.</summary>
    private async Task MakeAsync()
    {
        string? mod = await AskForFolderAsync("Choose the mod folder to make patches from").ConfigureAwait(true);
        if (mod is null)
        {
            return;
        }

        string? destination = await AskForFolderAsync("Where to write the patches").ConfigureAwait(true);
        if (destination is not null)
        {
            await _model.MakeFromFolderAsync(mod, destination).ConfigureAwait(true);
        }
    }

    private async Task<string?> AskForFolderAsync(string title)
    {
        try
        {
            IReadOnlyList<IStorageFolder> chosen = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = title, AllowMultiple = false })
                .ConfigureAwait(true);

            return chosen.Count == 0 ? null : chosen[0].TryGetLocalPath();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> chosen = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Where to write the mod folder",
                    AllowMultiple = false,
                }).ConfigureAwait(true);

            if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is string folder)
            {
                await _model.WriteModAsync(folder).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }
}
