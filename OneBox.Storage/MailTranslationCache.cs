using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneBox.Core;

namespace OneBox.Storage;

public sealed class MailTranslationCache(string directory)
{
    public static string GetKey(MailMessage message) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            message.Key, message.FolderId, message.Subject, message.TextBody, message.HtmlBody
        }))));

    public async Task<MailTranslation?> ReadAsync(string key)
    {
        var path = Path.Combine(directory, key + ".json");
        if (!File.Exists(path)) return null;
        var translation = JsonSerializer.Deserialize<MailTranslation>(await File.ReadAllTextAsync(path));
        if (translation?.Subject is null || translation.Body is null)
            throw new InvalidDataException("本机译文缓存内容无效，请检查缓存文件。");
        return translation;
    }

    public async Task SaveAsync(string key, MailTranslation translation)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".json");
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(translation));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
