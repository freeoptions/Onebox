using System.Text.Json;

namespace OneBox.Storage;

public sealed record ReaderSettings
{
    public bool AutoLoadRemoteImages { get; init; } = true;
    public string DownloadDirectory { get; init; } = "";
    public int FontScalePercent { get; init; } = 100;

    public static ReaderSettings Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(path)) ?? throw new JsonException("阅读设置内容为空")
        : new ReaderSettings();

    public void Save(string path)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
