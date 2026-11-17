using CommunityToolkit.Mvvm.ComponentModel;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

// Rows update in place: a one-second refresh must not recreate them (and restart their meters).
public sealed partial class DriveStatusRowViewModel : ObservableObject
{
    [ObservableProperty] private string label = string.Empty;
    [ObservableProperty] private double usedPercent;
    [ObservableProperty] private string totalText = string.Empty;
    [ObservableProperty] private string freeText = string.Empty;
    [ObservableProperty] private string detail = string.Empty;

    public DriveStatusRowViewModel(DriveStatusSnapshot snapshot)
    {
        Name = snapshot.Name;
        Update(snapshot);
    }

    public string Name { get; }

    public void Update(DriveStatusSnapshot snapshot)
    {
        Label = snapshot.Label;
        UsedPercent = snapshot.UsedPercent;
        TotalText = FormatBytes(snapshot.TotalBytes);
        FreeText = FormatBytes(snapshot.FreeBytes);
        Detail = $"{FreeText} 可用 / {TotalText}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}

public sealed partial class NetworkStatusRowViewModel(
    string name,
    string address,
    string receivedText,
    string sentText) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private string address = address;
    [ObservableProperty] private string receivedText = receivedText;
    [ObservableProperty] private string sentText = sentText;

    public void Update(string currentAddress, string received, string sent)
    {
        Address = currentAddress;
        ReceivedText = received;
        SentText = sent;
    }
}
