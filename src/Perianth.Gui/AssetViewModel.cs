using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Gui;

/// <summary>One resolved file, as the pane shows it.</summary>
/// <param name="Label">Which part of the set this is.</param>
/// <param name="Path">The archive path it resolved to.</param>
/// <param name="Note">How it was found, when that is worth saying.</param>
public sealed record AssetRow(string Label, string Path, string Note);

/// <summary>
/// What one model resolves to, and what it does not.
/// </summary>
/// <remarks>
/// The conventions that name a character's files are observed rather than
/// proven, and hold to different degrees — the setup ANIM is found directly for
/// 65% of characters and through the rig family for another 32%. So this pane
/// shows which rule matched, not merely the answer: a variant posed through its
/// family can leave a few parts unplaced where a direct match leaves none, and
/// the person exporting it is the one who needs to know.
/// </remarks>
public sealed class AssetViewModel : ViewModelBase
{
    private CancellationTokenSource? _pending;
    private string _status = "Choose a file on the left.";
    private string _title = string.Empty;
    private bool _busy;

    /// <summary>The resolved set, in the order the pane lists it.</summary>
    public ObservableCollection<AssetRow> Rows { get; } = [];

    /// <summary>What the conventions did not account for, in prose.</summary>
    public ObservableCollection<string> Unresolved { get; } = [];

    /// <summary>Raised when a model resolves, so the export pane can follow.</summary>
    public event Action<CharacterAssets>? Resolved;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The model this pane is describing.</summary>
    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public bool HasRows => Rows.Count > 0;

    public bool HasUnresolved => Unresolved.Count > 0;

    /// <summary>
    /// Resolves <paramref name="modelPath"/> and describes what it found.
    /// </summary>
    public async Task ShowAsync(ImmutableArray<SdfPathEntry> paths, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);

        _pending?.Cancel();
        CancellationTokenSource mine = new();
        _pending = mine;

        Rows.Clear();
        Unresolved.Clear();
        Title = modelPath;
        Busy = true;
        Status = "Resolving…";

        Result<CharacterAssets> resolved = await Task.Run(
            () => CharacterResolver.Resolve(paths, modelPath), mine.Token).ConfigureAwait(true);

        if (mine.IsCancellationRequested)
        {
            return;
        }

        Busy = false;
        Raise(nameof(HasRows));
        Raise(nameof(HasUnresolved));

        if (!resolved.TryGetValue(out CharacterAssets? assets, out Refusal? refusal))
        {
            // Selecting a texture or an animation is an ordinary thing to do,
            // and the refusal already explains that a set is assembled around a
            // model. Saying it plainly beats an empty pane.
            Status = refusal.Message;
            return;
        }

        Add("model", assets.Model, AssetMatch.Exact);
        Add("cameldata", assets.Cameldata);
        Add("editordata", assets.Editordata);
        Add("setup", assets.Setup);
        Add("mouth", assets.Mouth);
        Add("eyes", assets.Eyes);
        Add("pupils", assets.Pupils);
        Add("eyebrows", assets.Eyebrows);
        Add("lip-sync", assets.LipsyncDatabase);

        if (assets.Clips.Length > 0)
        {
            string note = assets.Clips[0].Match == AssetMatch.VariantBase ? "via the rig family" : string.Empty;
            Rows.Add(new AssetRow(
                "clips",
                string.Create(CultureInfo.InvariantCulture, $"{assets.Clips.Length} animation clips"),
                note));
        }

        foreach (string note in assets.Unresolved)
        {
            Unresolved.Add(note);
        }

        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"Resolved as '{assets.Name}' — {assets.Paths().Length} files.");

        Raise(nameof(HasRows));
        Raise(nameof(HasUnresolved));
        Resolved?.Invoke(assets);
    }

    private void Add(string label, ResolvedAsset? asset)
    {
        if (asset is not null)
        {
            Add(label, asset.VirtualPath, asset.Match);
        }
    }

    private void Add(string label, string? path, AssetMatch match = AssetMatch.Exact)
    {
        if (path is not null)
        {
            Rows.Add(new AssetRow(
                label,
                path,
                match == AssetMatch.VariantBase ? "via the rig family" : string.Empty));
        }
    }
}
