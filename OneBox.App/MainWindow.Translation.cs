using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.App;

public partial class MainWindow
{
    private readonly Dictionary<string, Task<MailTranslation>> _translationTaskMap = [];
    private MailTranslation? _displayedTranslation;
    private bool _needsTranslationStyleUpdate;
    private int _translationViewRevision;

    private async Task UpdateTranslationAvailabilityAsync(MessageRow row, CancellationToken cancellationToken)
    {
        var message = row.Message;
        try
        {
            var canTranslate = await Task.Run(() => DeepSeekTranslator.CanTranslate(message), cancellationToken);
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(MessageList.SelectedItem, row))
                TranslateButton.IsEnabled = canTranslate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(MessageList.SelectedItem, row))
                Notify("暂时无法判断邮件语言：" + error.GetType().Name, "告警");
        }
    }

    private async Task<MailTranslation> LoadTranslationAsync(MailMessage message, string key, bool updateStyle)
    {
        var cache = new MailTranslationCache(Path.Combine(_dataDirectory, "translations"));
        var cached = await Task.Run(() => cache.ReadAsync(key));
        if (cached is not null && !updateStyle) return cached;
        var apiKey = _vault.ReadDeepSeekApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请先在设置的“邮件翻译”中填写DeepSeek API Key。");
        IReadOnlyList<string>? htmlTextList = null;
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            if (_htmlRenderer is null) throw new InvalidOperationException("HTML阅读组件不可用，请重新打开邮件后再翻译。");
            htmlTextList = await _htmlRenderer.ReadTranslationTextAsync(message.HtmlBody);
        }
        var translation = await _translator.TranslateAsync(message.Subject,
            htmlTextList is null ? DeepSeekTranslator.GetBody(message) : "", apiKey, htmlTextList);
        try { await Task.Run(() => cache.SaveAsync(key, translation)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify("译文未能保存到本机，仅本次运行可复用；请检查目录写入权限。", "告警");
        }
        return translation;
    }

    private async void OnTranslateMail(object sender, RoutedEventArgs e)
    {
        if (!TranslateButton.IsEnabled || MessageList.SelectedItem is not MessageRow row || _viewModel.IsBodyLoading) return;
        var revision = ++_translationViewRevision;
        TranslateButton.IsEnabled = false;
        try
        {
            if (_displayedTranslation is not null)
            {
                _displayedTranslation = null;
                _viewModel.SelectedSubject = row.Message.Subject;
                _viewModel.SelectedBody = row.Message.TextBody ?? "";
                TranslateButton.Content = "翻译";
                await ShowBodyAsync(row.Message);
                return;
            }
            TranslateButton.Content = "正在翻译…";
            var message = row.Message;
            var key = await Task.Run(() => MailTranslationCache.GetKey(message));
            if (revision != _translationViewRevision || !ReferenceEquals(MessageList.SelectedItem, row)) return;
            if (_needsTranslationStyleUpdate) _translationTaskMap.Remove(key);
            if (!_translationTaskMap.TryGetValue(key, out var translationTask))
            {
                translationTask = LoadTranslationAsync(message, key, _needsTranslationStyleUpdate);
                _translationTaskMap.Add(key, translationTask);
            }
            MailTranslation translation;
            try { translation = await translationTask; }
            catch { _translationTaskMap.Remove(key); throw; }
            // 切换邮件后仍完成翻译并保存，重新打开时复用同一个请求或已有译文。
            if (revision != _translationViewRevision || !ReferenceEquals(MessageList.SelectedItem, row)) return;
            if (!string.IsNullOrWhiteSpace(row.Message.HtmlBody) && translation.HtmlTextList is null)
            {
                _needsTranslationStyleUpdate = true;
                Notify("此邮件缓存为旧版纯文本译文。点击“更新译文样式”会重新翻译一次并保存，产生一次API费用。", "告警");
                return;
            }
            _needsTranslationStyleUpdate = false;
            _displayedTranslation = translation;
            _viewModel.SelectedSubject = translation.Subject;
            _viewModel.SelectedBody = translation.Body;
            await ShowBodyAsync(row.Message, translation: translation);
            if (revision != _translationViewRevision || !ReferenceEquals(MessageList.SelectedItem, row)) return;
            TranslateButton.Content = "显示原文";
        }
        catch (Exception error)
        {
            if (revision != _translationViewRevision || !ReferenceEquals(MessageList.SelectedItem, row)) return;
            var message = error switch
            {
                OperationCanceledException => "翻译请求超时，请稍后重试。",
                JsonException or KeyNotFoundException => "DeepSeek返回格式无效，原文已保留。",
                HttpRequestException or InvalidOperationException or InvalidDataException => error.Message,
                _ => "无法读取或保存译文，请检查网络和本机文件权限。"
            };
            Notify(message, "失败");
        }
        finally
        {
            if (revision == _translationViewRevision && ReferenceEquals(MessageList.SelectedItem, row))
            {
                TranslateButton.Content = _displayedTranslation is not null ? "显示原文" : _needsTranslationStyleUpdate ? "更新译文样式" : "翻译";
                TranslateButton.IsEnabled = true;
            }
        }
    }
}
