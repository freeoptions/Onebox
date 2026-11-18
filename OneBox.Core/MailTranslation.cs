namespace OneBox.Core;

public sealed record MailTranslation(string Subject, string Body, IReadOnlyList<string>? HtmlTextList = null);
