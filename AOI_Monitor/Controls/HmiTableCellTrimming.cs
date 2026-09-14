using System.Windows;
using System.Windows.Controls;

namespace AOI_Monitor.Controls;

/// <summary>
/// Opt-in attached behavior for the shared HMI table styles: gives every text cell inside
/// a DataGrid <see cref="TextTrimming.CharacterEllipsis"/> so an over-long value truncates
/// visibly with an ellipsis instead of cropping mid-glyph. Scoped to the table's own
/// resources on purpose - an application-wide implicit TextBlock style would leak into
/// unrelated control templates and let the ellipsis mask genuine clipping defects the HMI
/// layout audit is meant to catch outside tables.
/// </summary>
public static class HmiTableCellTrimming
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(HmiTableCellTrimming),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element)
        => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value)
        => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not true || grid.Resources.Contains(typeof(TextBlock)))
            return;

        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        grid.Resources.Add(typeof(TextBlock), style);
    }
}
