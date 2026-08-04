using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Perianth.Gui;

/// <summary>
/// Change notification, which is all a view model needs from a base class.
/// </summary>
/// <remarks>
/// Twenty lines rather than a dependency. The usual toolkit generates this and
/// a good deal more; nothing here wants the more, and a package taken for one
/// interface is a package to keep in step forever.
/// </remarks>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns and raises, and does neither when the value is unchanged.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
