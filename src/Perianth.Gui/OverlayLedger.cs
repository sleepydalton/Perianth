using System;
using System.Collections.Generic;
using System.IO;
using Perianth.Formats.Diagnostics;

namespace Perianth.Gui;

/// <summary>
/// What a pane put into the shared preview folder, so it can take it back out.
/// </summary>
/// <remarks>
/// <para>
/// The preview folder is a content root an export reads, and it outlives the
/// session that filled it. So anything a pane leaves there keeps applying —
/// silently, to every later export of that model, with nothing on screen saying
/// why. A texture edit from the morning was still repainting a character in the
/// afternoon, which is the failure this exists to make impossible.
/// </para>
/// <para>
/// Recorded rather than inferred. The folder is shared between the panes and
/// with whatever the author put there by hand, so "delete the editordata" is not
/// a safe rule: only the files a pane wrote are its to remove, and the only way
/// to know which those are is to have written them down.
/// </para>
/// <para>
/// One ledger file per pane, named by the caller. Three panes needed the same
/// mechanism and a third copy of it would have been the scope failure this
/// project exists to avoid.
/// </para>
/// </remarks>
internal static class OverlayLedger
{
    /// <summary>Removes the files named in <paramref name="ledger"/>, and the ledger.</summary>
    internal static Result<int> Withdraw(string root, string ledger)
    {
        string path = Path.Combine(root, ledger);
        if (!File.Exists(path))
        {
            return Result.Ok(0);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{path}' could not be read.");
        }

        int removed = 0;
        foreach (string line in lines)
        {
            string entry = line.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            string file = Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refusal.Resource($"'{file}' could not be removed.");
            }
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{path}' could not be removed.");
        }

        return Result.Ok(removed);
    }

    /// <summary>Writes down what a pane has just laid down.</summary>
    /// <remarks>
    /// Nothing written means no ledger, so an empty overlay leaves the folder as
    /// it found it rather than dropping an empty file into it.
    /// </remarks>
    internal static Result<int> Record(string root, string ledger, IReadOnlyList<string> ours)
    {
        if (ours.Count == 0)
        {
            return Result.Ok(0);
        }

        try
        {
            File.WriteAllLines(Path.Combine(root, ledger), ours);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{Path.Combine(root, ledger)}' could not be written.");
        }

        return Result.Ok(ours.Count);
    }
}
