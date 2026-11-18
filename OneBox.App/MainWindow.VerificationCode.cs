using System.Windows;
using OneBox.Core;
using OneBox.Providers;

namespace OneBox.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim _notificationBodyLoadLock = new(2, 2);
    private string? _readerVerificationCode;

    private async Task<string?> FindNotificationCodeAsync(Account account, MailMessage message, CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => MailVerificationCode.Find(message), cancellationToken);
        if (result.Code is not null || result.Ambiguous || message.TextBody is not null || message.HtmlBody is not null)
            return result.Code;
        if (!_providerMap.TryGetValue(account.Provider, out var provider)) return null;

        await _notificationBodyLoadLock.WaitAsync(cancellationToken);
        try
        {
            var row = _viewModel.Messages.FirstOrDefault(item => item.Message.Key == message.Key);
            if (row is { HasCachedBody: true })
            {
                var cachedMessage = row.Message;
                return await Task.Run(() => MailVerificationCode.Find(cachedMessage).Code, cancellationToken);
            }
            var secret = _vault.Read(account.Id);
            if (secret is null) return null;
            var fullMessage = await LoadMessageBodyAsync(account, provider, secret, message, MailBodySource.Notification, cancellationToken);
            return await Task.Run(() => MailVerificationCode.Find(fullMessage).Code, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 正文识别是可选操作，读取失败时保留普通通知。
            return null;
        }
        finally { _notificationBodyLoadLock.Release(); }
    }

    private Task MarkNotificationReadAsync(MailMessage message) =>
        ChangeMessageFlagAsync(new MessageRow(message, ""), readFlag: true, desiredValue: true);

    private async Task UpdateReaderVerificationCodeAsync(MessageRow row, CancellationToken cancellationToken)
    {
        var message = row.Message;
        try
        {
            var result = await Task.Run(() => MailVerificationCode.Find(message), cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(MessageList.SelectedItem, row)) return;
            _readerVerificationCode = result.Code;
            if (result.Code is not null) CopyReaderCodeButton.Content = $"验证码 {result.Code} · 复制";
            CopyReaderCodeButton.Visibility = result.Code is null ? Visibility.Collapsed : Visibility.Visible;
            CopyReaderCodeButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async void OnCopyReaderCode(object sender, RoutedEventArgs e)
    {
        if (_readerVerificationCode is not { } code || MessageList.SelectedItem is not MessageRow row) return;
        CopyReaderCodeButton.IsEnabled = false;
        try
        {
            if (!await SelectionAutoCopy.CopyExplicitAsync(code))
            {
                Notify("验证码复制失败，请重试", "失败");
                return;
            }
            Notify("验证码已复制", "成功");
            await ChangeMessageFlagAsync(row, readFlag: true, desiredValue: true);
        }
        finally
        {
            if (ReferenceEquals(MessageList.SelectedItem, row)) CopyReaderCodeButton.IsEnabled = true;
        }
    }
}
