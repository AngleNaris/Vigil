using System.Globalization;
using System.Windows.Data;
using SystemAutorun.App.ViewModels;

namespace SystemAutorun.App.Services;

/// <summary>
/// Returns true when the active status filter matches the status passed as the
/// converter parameter, used to highlight the corresponding metric cell.
/// </summary>
public sealed class StatusFilterActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var activeValue = (value as FilterOption)?.Value ?? string.Empty;
        var target = parameter as string ?? string.Empty;
        return string.Equals(activeValue, target, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
