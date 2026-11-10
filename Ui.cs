using Microsoft.UI.Xaml;

namespace Ludoteca;

/// <summary>Funzioni usate da x:Bind nei template XAML.</summary>
public static class Ui
{
    public static Visibility VisibleIf(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;
}
