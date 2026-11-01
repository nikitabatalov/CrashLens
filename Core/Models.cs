using System.Text.Json.Serialization;

namespace CrashLens.Core;

public sealed class CrashEvent
{
    public string Key { get; set; } = "";
    public DateTimeOffset TimeUtc { get; set; }
    public string Channel { get; set; } = "";
    public long RecordId { get; set; }
    public int EventId { get; set; }
    public string Provider { get; set; } = "";
    public string Severity { get; set; } = "Error";
    public string Category { get; set; } = "Windows";
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Message { get; set; } = "";
    public string RawXml { get; set; } = "";
    public string DumpPath { get; set; } = "";

    [JsonIgnore] public DateTime LocalTime => TimeUtc.LocalDateTime;
    [JsonIgnore] public string TimeLabel => LocalTime.ToString("dd.MM.yyyy HH:mm:ss");
    [JsonIgnore] public string CodeLabel => string.IsNullOrWhiteSpace(Code) ? $"Event {EventId}" : Code;
    [JsonIgnore] public string SeverityLabel => Severity switch
    {
        "Critical" => "Критично", "Warning" => "Предупр.", _ => "Ошибка"
    };
    [JsonIgnore] public string SeverityBackground => Severity switch
    {
        "Critical" => "#533344", "Warning" => "#4A422B", _ => "#293D56"
    };
    [JsonIgnore] public string SeverityForeground => Severity switch
    {
        "Critical" => "#FF8DAB", "Warning" => "#FFD77A", _ => "#8DCBFF"
    };
}

public sealed class AppSettings
{
    public bool AutoStart { get; set; } = true;
    public bool ShowCriticalNotifications { get; set; } = true;
    public DateTimeOffset? LastScanUtc { get; set; }
}
