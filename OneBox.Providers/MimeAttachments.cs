using MimeKit;

namespace OneBox.Providers;

internal static class MimeAttachments
{
    internal static async Task SaveAsync(MimeEntity part, string outputPath, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        try
        {
            if (part is MimePart { Content: not null } filePart)
                await filePart.Content.DecodeToAsync(output, cancellationToken);
            else if (part is MessagePart { Message: not null } messagePart)
                await messagePart.Message.WriteToAsync(output, cancellationToken);
            else throw new InvalidDataException("附件内容为空或格式不受支持");
        }
        catch { output.Close(); File.Delete(outputPath); throw; }
    }
}
