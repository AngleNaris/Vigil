using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public sealed class ExceptionLogRowViewModel
{
    public ExceptionLogRowViewModel(ExceptionLogEntry entry) => Entry = entry;

    public ExceptionLogEntry Entry { get; }

    public string TimestampText => Entry.Timestamp.ToString("MM-dd HH:mm:ss");

    public string Level => Entry.Level.ToString().ToUpperInvariant();

    public string LevelLabel => Entry.Level switch
    {
        ExceptionLogLevel.Critical => "严重",
        ExceptionLogLevel.Error => "错误",
        ExceptionLogLevel.Warning => "警告",
        _ => Entry.Level.ToString(),
    };

    public string Source => Entry.Source;

    public string Message => Entry.Message;

    public string FileName => Entry.FileName;
}
