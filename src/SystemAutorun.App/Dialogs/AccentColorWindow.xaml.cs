using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SystemAutorun.App.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SystemAutorun.App.Dialogs;

public partial class AccentColorWindow : ToolWindow
{
    private readonly List<(string Id, Button Button)> _presetButtons = [];
    private string _selectedAccent;

    internal AccentColorWindow()
    {
        InitializeComponent();
        _selectedAccent = ThemeService.AccentPreference;
        BuildPresetButtons();
        SyncSelection(_selectedAccent, updateHexText: true);
    }

    internal string SelectedAccent => _selectedAccent;

    private void BuildPresetButtons()
    {
        PresetGrid.Children.Clear();
        _presetButtons.Clear();
        var itemStyle = TryFindResource("DialogButtonStyle") as Style;
        foreach (var preset in ThemeService.AccentPresets)
        {
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.SwatchHex)),
            };
            var label = new TextBlock
            {
                Text = preset.Label.Replace("（默认）", string.Empty, StringComparison.Ordinal),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            stack.Children.Add(swatch);
            stack.Children.Add(label);

            var button = new Button
            {
                Margin = new Thickness(3),
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Style = itemStyle,
                Content = stack,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"预设强调色 {preset.Label}");
            button.Click += (_, _) => SyncSelection(preset.Id, updateHexText: true);
            _presetButtons.Add((preset.Id, button));
            PresetGrid.Children.Add(button);
        }
    }

    private void SyncSelection(string accent, bool updateHexText)
    {
        _selectedAccent = ThemeService.NormalizeAccentInput(accent);
        var color = ThemeService.ResolveSwatchColor(_selectedAccent);
        if (updateHexText)
        {
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (!string.Equals(HexTextBox.Text, hex, StringComparison.OrdinalIgnoreCase))
            {
                HexTextBox.Text = hex;
            }
        }
        PreviewSwatch.Background = new SolidColorBrush(color);
        var preset = ThemeService.AccentPresets.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedAccent, StringComparison.OrdinalIgnoreCase));
        PreviewTitleText.Text = preset is not null ? $"当前选择：{preset.Label}" : $"当前选择：{_selectedAccent}";
        foreach (var (id, button) in _presetButtons)
        {
            var isCurrent = string.Equals(id, _selectedAccent, StringComparison.OrdinalIgnoreCase);
            ColorStateAssist.SetRestingBorderBrush(button, isCurrent
                ? new SolidColorBrush(color)
                : TryFindResource("Semantic.Brush.BorderStrong") as Brush);
        }
        ApplyButton.IsEnabled = true;
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ThemeService.TryNormalizeHex(HexTextBox.Text, out var normalized))
        {
            var matchedPreset = ThemeService.AccentPresets.FirstOrDefault(item =>
                string.Equals(item.SwatchHex, normalized, StringComparison.OrdinalIgnoreCase));
            SyncSelection(matchedPreset?.Id ?? normalized, updateHexText: false);
        }
        else
        {
            ApplyButton.IsEnabled = false;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SyncSelection(ThemeService.DefaultAccentId, updateHexText: true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ThemeService.TryNormalizeHex(HexTextBox.Text, out var normalized))
        {
            var matchedPreset = ThemeService.AccentPresets.FirstOrDefault(item =>
                string.Equals(item.SwatchHex, normalized, StringComparison.OrdinalIgnoreCase));
            _selectedAccent = matchedPreset?.Id ?? normalized;
        }
        else if (!ThemeService.IsPresetAccent(_selectedAccent))
        {
            MessageDialog.ShowError(this, "颜色格式无效", "请输入有效的 6 位十六进制颜色，例如 #3B82F6。");
            return;
        }

        ThemeService.SetAccent(_selectedAccent);
        DialogResult = true;
    }
}
