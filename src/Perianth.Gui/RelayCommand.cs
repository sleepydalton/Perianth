using System;
using System.Windows.Input;

namespace Perianth.Gui;

/// <summary>
/// A button's action, and whether it can run.
/// </summary>
/// <remarks>
/// The other half of what an MVVM package would have supplied. Kept here for
/// the same reason as <see cref="ViewModelBase"/>: it is small, it is stable,
/// and owning it costs less than tracking someone else's version of it.
/// </remarks>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Tells the button to ask again.</summary>
    public void Reconsider() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
