using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using OneBox.Core;

namespace OneBox.Providers;

public static class InlineImages
{
    static InlineImages() => MailEncoding.Initialize();
    public const string Host = "onebox-inline.invalid";
    public static string CacheDirectory => Path.Combine(AppContext.BaseDirectory, "InlineImages");
    private static readonly Regex CidReference = new(@"\bcid:([^\s""'<>\)]+)", RegexOptions.IgnoreCase);

    public static bool HasUnresolvedReferences(string? html) => html is not null && CidReference.IsMatch(html);

    public static IReadOnlyList<Uri> GetImageList(string? html)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<Uri>();
        return Regex.Matches(html, $@"https://{Regex.Escape(Host)}/[^\s""'<>\)]+")
            .Select(match => Uri.TryCreate(WebUtility.HtmlDecode(match.Value), UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>().Distinct().ToList();
    }

    public static async Task<string?> ResolveAsync(MimeMessage mime, Guid accountId, string messageId,
        string cacheDirectory, CancellationToken cancellationToken)
    {
        var html = mime.HtmlBody;
        if (string.IsNullOrEmpty(html)) return html;
        html += MailEncoding.CacheMarker;
        if (!HasUnresolvedReferences(html)) return html;
        var cidSet = CidReference.Matches(html).Select(match => NormalizeId(match.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var imageUrlMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var relativeDirectory = $"{accountId:N}/{Hash(messageId)}";
        var directory = Path.Combine(cacheDirectory, accountId.ToString("N"), Hash(messageId));
        foreach (var part in mime.BodyParts.OfType<MimePart>())
        {
            var cid = NormalizeId(part.ContentId ?? "");
            if (!cidSet.Contains(cid) || part.Content is null || !part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            var subtype = part.ContentType.MediaSubtype.ToLowerInvariant();
            if (!Regex.IsMatch(subtype, @"\A[a-z0-9.+-]+\z")) continue;
            Directory.CreateDirectory(directory);
            var fileName = $"{Hash(cid)}.{subtype}";
            var path = Path.Combine(directory, fileName);
            var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await part.Content.DecodeToAsync(output, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            imageUrlMap[cid] = $"https://{Host}/{relativeDirectory}/{fileName}";
        }
        return CidReference.Replace(html, match =>
        {
            var cid = NormalizeId(match.Groups[1].Value);
            return imageUrlMap.GetValueOrDefault(cid, $"https://{Host}/{relativeDirectory}/{Hash(cid)}.missing");
        });
    }

    public static Stream? Open(Uri uri, Guid accountId, string messageId, string cacheDirectory, out string contentType)
    {
        contentType = "application/octet-stream";
        if (uri.Scheme != "https" || uri.Host != Host || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        var prefix = $"/{accountId:N}/{Hash(messageId)}/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var fileName = uri.AbsolutePath[prefix.Length..];
        var match = Regex.Match(fileName, @"\A[a-f0-9]{64}\.([a-z0-9.+-]+)\z");
        if (!match.Success) return null;
        var path = Path.Combine(cacheDirectory, accountId.ToString("N"), Hash(messageId), fileName);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            contentType = "image/" + match.Groups[1].Value;
            return stream;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static async Task<string> SaveAsync(Uri uri, Guid accountId, string messageId, string cacheDirectory,
        string downloadDirectory, CancellationToken cancellationToken)
    {
        using var image = Open(uri, accountId, messageId, cacheDirectory, out var contentType)
            ?? throw new FileNotFoundException("找不到这封邮件的内嵌图片，请重新打开邮件后再试");
        var extension = contentType["image/".Length..] switch
        {
            "jpeg" => "jpg",
            "svg+xml" => "svg",
            "x-icon" or "vnd.microsoft.icon" => "ico",
            var subtype => subtype
        };
        Directory.CreateDirectory(downloadDirectory);
        var fileName = $"图片-{Hash(uri.AbsoluteUri)[..16]}.{extension}";
        var path = Path.Combine(downloadDirectory, fileName);
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            var existingHash = await SHA256.HashDataAsync(existing, cancellationToken);
            var imageHash = await SHA256.HashDataAsync(image, cancellationToken);
            if (CryptographicOperations.FixedTimeEquals(existingHash, imageHash)) return path;
            image.Position = 0;
            path = MailRules.AvailableAttachmentPath(downloadDirectory, fileName);
        }
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        try { await image.CopyToAsync(output, cancellationToken); }
        catch { output.Close(); File.Delete(path); throw; }
        return path;
    }

    private static string NormalizeId(string value) => Uri.UnescapeDataString(WebUtility.HtmlDecode(value)).Trim().Trim('<', '>');

    public static void DeleteAccount(Guid accountId, string cacheDirectory)
    {
        var root = Path.GetFullPath(cacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directory = Path.GetFullPath(Path.Combine(root, accountId.ToString("N")));
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("图片缓存路径越界");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
