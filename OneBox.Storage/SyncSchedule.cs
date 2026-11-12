using System.Text.Json;

namespace OneBox.Storage;

public sealed record SyncSchedule(DateTimeOffset LastAttempt, int Failures = 0, DateTimeOffset? RetryAt = null)
{
    public bool IsDue(DateTimeOffset now) => now >= (RetryAt ?? LastAttempt.AddHours(5));

    public SyncSchedule Start(DateTimeOffset now) => new(now, RetryAt is not null ? Failures : 0);

    public SyncSchedule Failed(DateTimeOffset now, bool needsAuthorization)
    {
        var failures = Failures + 1;
        return new(now, failures, !needsAuthorization && failures <= 3 ? now.AddMinutes(5) : null);
    }
}

public sealed class SyncScheduleSettings
{
    public Dictionary<Guid, SyncSchedule> AccountMap { get; init; } = [];

    public static SyncScheduleSettings Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<SyncScheduleSettings>(File.ReadAllText(path)) ?? throw new JsonException("同步调度内容为空")
        : new SyncScheduleSettings();

    public void Save(string path)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
