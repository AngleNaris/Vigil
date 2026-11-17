using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using SystemAutorun.App.ViewModels;

namespace SystemAutorun.App.Services;

public sealed class DashboardTaskIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        MainWindowViewModel.TaskIcon(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DashboardWindowActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int hours && int.TryParse(parameter?.ToString(), out var candidate) && hours == candidate;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DashboardUptimeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 3 && values[0] is TaskRowViewModel task && values[2] is MainWindowViewModel model
            ? model.GetTaskUptime(task) : "--";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
