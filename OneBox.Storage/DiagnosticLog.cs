using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OneBox.Storage;

public sealed class DiagnosticLog(string directory)
{
    private readonly object _gate = new();
    private DateOnly? _cleanedDate;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public bool TryAppend(string kind, string text, string details)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var now = DateTimeOffset.Now;
                var today = DateOnly.FromDateTime(now.LocalDateTime);
                if (_cleanedDate != today)
                {
                    foreach (var path in Directory.EnumerateFiles(directory, "imap-idle-*.log"))
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (DateOnly.TryParseExact(name["imap-idle-".Length..], "yyyy-MM-dd", out var date) && date < today.AddDays(-6))
                            File.Delete(path);
                    }
                    _cleanedDate = today;
                }
                var entry = JsonSerializer.Serialize(new { Time = now, Kind = kind, Text = text, Details = details }, JsonOptions);
                File.AppendAllText(Path.Combine(directory, $"imap-idle-{today:yyyy-MM-dd}.log"), entry + Environment.NewLine, Encoding.UTF8);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
