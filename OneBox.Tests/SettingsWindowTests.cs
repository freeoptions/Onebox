using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;
using OneBoxApplication = OneBox.App.App;

namespace OneBox.Tests;

[TestClass]
public sealed class SettingsWindowTests
{
    [TestMethod]
    public void SettingsWindowSavesChoicesAndUpdatesTypographyWithoutShowingWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
            Application? application = null;
            SettingsWindow? window = null;
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "reader-settings.json");
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var markup = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "App.xaml")).Root!;
                var resources = new XElement(markup.Element(presentation + "Application.Resources")!.Elements().Single());
                var appNamespace = (XNamespace)"clr-namespace:OneBox.App;assembly=OneBox";
                foreach (var element in resources.Descendants().Where(item => item.Name.NamespaceName == "clr-namespace:OneBox.App"))
                    element.Name = appNamespace + element.Name.LocalName;
                resources.SetAttributeValue(XNamespace.Xmlns + "local", appNamespace.NamespaceName);
                resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
                window = new SettingsWindow(new ReaderSettings(), path);
                var fontScaleBox = (ComboBox)window.FindName("FontScaleBox");
                var remoteImagesBox = (CheckBox)window.FindName("RemoteImagesBox");
                var changeCount = 0;
                window.SettingsChanged += (_, _) =>
                {
                    changeCount++;
                    OneBoxApplication.ApplyFontScale(window.Settings.FontScalePercent);
                };
                Assert.IsFalse(File.Exists(path), "打开设置不应写入文件");
                Assert.AreEqual(100, fontScaleBox.SelectedItem);
                foreach (var percent in OneBoxApplication.FontScalePercentList)
                {
                    fontScaleBox.SelectedItem = percent;
                    Assert.AreEqual(percent, ReaderSettings.Load(path).FontScalePercent);
                    Assert.AreEqual(13 * percent / 100d, window.FontSize, 0.001);
                    Assert.AreEqual(27 * percent / 100d, (double)application.Resources["FontSize27"], 0.001);
                    Assert.AreEqual(26 * percent / 100d, (double)application.Resources["BodyLineHeight"], 0.001);
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(540, 440));
                    content.Arrange(new Rect(0, 0, 540, 440));
                    content.UpdateLayout();
                    Assert.IsGreaterThan(0d, fontScaleBox.ActualHeight);
                }
                remoteImagesBox.IsChecked = false;
                remoteImagesBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var savedSettings = ReaderSettings.Load(path);
                Assert.IsFalse(savedSettings.AutoLoadRemoteImages);
                Assert.AreEqual(150, savedSettings.FontScalePercent, "修改图片设置不应重置字号");
                Assert.AreEqual(6, changeCount);
                var autoCopyBox = (CheckBox)window.FindName("AutoCopySelectedTextBox");
                Assert.IsTrue(autoCopyBox.IsChecked == true);
                autoCopyBox.IsChecked = false;
                autoCopyBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.IsFalse(ReaderSettings.Load(path).AutoCopySelectedText);
                autoCopyBox.IsChecked = true;
                autoCopyBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.IsTrue(ReaderSettings.Load(path).AutoCopySelectedText);
                var proxyModeBox = (ComboBox)window.FindName("ProxyModeBox");
                var proxyAddressBox = (TextBox)window.FindName("ProxyAddressBox");
                var applyProxyButton = (Button)window.FindName("ApplyProxyButton");
                Assert.AreEqual(0, proxyModeBox.SelectedIndex);
                proxyModeBox.SelectedIndex = 2;
                Assert.AreEqual(ProxyMode.System, ReaderSettings.Load(path).Proxy.Mode, "自定义地址应用前不应切换连接方式");
                proxyAddressBox.Text = "invalid-address";
                applyProxyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual(ProxyMode.System, ReaderSettings.Load(path).Proxy.Mode);
                Assert.IsFalse(string.IsNullOrEmpty(((TextBlock)window.FindName("ProxyErrorText")).Text));
                proxyAddressBox.Text = "http://127.0.0.1:7890";
                applyProxyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual(ProxyMode.Custom, ReaderSettings.Load(path).Proxy.Mode);
                Assert.AreEqual(proxyAddressBox.Text, ReaderSettings.Load(path).Proxy.Address);
                proxyModeBox.SelectedIndex = 1;
                Assert.AreEqual(ProxyMode.Direct, ReaderSettings.Load(path).Proxy.Mode);
                proxyModeBox.SelectedIndex = 0;
                Assert.AreEqual(ProxyMode.System, ReaderSettings.Load(path).Proxy.Mode);
                Assert.IsFalse(ReaderSettings.Load(path).AutoLoadRemoteImages);
                Assert.AreEqual(150, ReaderSettings.Load(path).FontScalePercent);
                var notificationCornerBox = (ComboBox)window.FindName("NotificationCornerBox");
                var notificationDurationBox = (ComboBox)window.FindName("NotificationDurationBox");
                var notificationCountBox = (ComboBox)window.FindName("NotificationCountBox");
                var notificationsBox = (CheckBox)window.FindName("NotificationsBox");
                var notificationContentBox = (CheckBox)window.FindName("NotificationContentBox");
                notificationCornerBox.SelectedIndex = 3;
                notificationDurationBox.SelectedValue = "10";
                notificationCountBox.SelectedValue = "1";
                notificationContentBox.IsChecked = false;
                notificationContentBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                notificationsBox.IsChecked = false;
                notificationsBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var notificationSettings = ReaderSettings.Load(path).Notifications;
                Assert.AreEqual(NotificationCorner.TopLeft, notificationSettings.Corner);
                Assert.AreEqual(10, notificationSettings.DurationSeconds);
                Assert.AreEqual(1, notificationSettings.MaxVisible);
                Assert.IsFalse(notificationSettings.ShowContent);
                Assert.IsFalse(notificationSettings.Enabled);
                var previewCount = 0;
                window.TestNotificationRequested += (_, _) => previewCount++;
                ((Button)window.FindName("TestNotificationButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual(1, previewCount);
                var notificationAccount = new Account(Guid.NewGuid(), "example@qq.com", "测试", ProviderKind.Qq);
                var notificationMessage = new MailMessage(notificationAccount.Id, "1", "INBOX", new string('邮', 200),
                    new string('名', 100), "sender@example.com", "", DateTimeOffset.Now, new string('摘', 200), false, false, false);
                double? withPreviewHeight = null;
                foreach (var showContent in new[] { true, false })
                {
                    var openCount = 0;
                    var closed = false;
                    var notificationWindow = new MailNotificationWindow(notificationAccount, notificationMessage,
                        notificationSettings with { ShowContent = showContent }, () =>
                        {
                            Assert.IsTrue(closed, "打开邮件前应已经移除通知");
                            openCount++;
                        });
                    try
                    {
                        Assert.AreEqual(SizeToContent.Height, notificationWindow.SizeToContent, "通知高度应随内容收紧");
                        Assert.IsFalse(notificationWindow.ShowActivated);
                        Assert.IsFalse(notificationWindow.ShowInTaskbar);
                        Assert.IsTrue(notificationWindow.Topmost);
                        var notificationContent = (FrameworkElement)notificationWindow.Content;
                        notificationContent.Measure(new Size(368, double.PositiveInfinity));
                        var contentHeight = notificationContent.DesiredSize.Height;
                        notificationContent.Arrange(new Rect(0, 0, 368, contentHeight));
                        notificationContent.UpdateLayout();
                        var subjectText = (TextBlock)notificationWindow.FindName("SubjectText");
                        var previewText = (TextBlock)notificationWindow.FindName("PreviewText");
                        Assert.IsTrue(subjectText.ActualWidth > 0);
                        var previewBottom = previewText.TranslatePoint(new Point(0, previewText.ActualHeight), notificationContent).Y;
                        if (showContent)
                        {
                            withPreviewHeight = contentHeight;
                            Assert.IsTrue(previewBottom <= contentHeight - 16, "摘要和底边应保留紧凑内边距");
                        }
                        else Assert.IsTrue(contentHeight < withPreviewHeight, "无摘要的卡片应更矮");
                        if (!showContent)
                        {
                            Assert.AreEqual("点击查看邮件", subjectText.Text);
                            Assert.AreEqual(Visibility.Collapsed, previewText.Visibility, "没有摘要时应收起整行和间距");
                        }
                        Assert.AreEqual(0, openCount);
                        notificationWindow.Closed += (_, _) => closed = true;
                        var dismissButton = (Button)notificationWindow.FindName(showContent ? "DismissButton" : "OpenButton");
                        dismissButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        dismissButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.AreEqual(showContent ? 0 : 1, openCount, "重复点击不能重复打开邮件");
                        Assert.IsTrue(closed, "点击通知或关闭按钮后应立即关闭");
                        Assert.IsFalse(notificationWindow.HasAnimatedProperties, "主动操作不应等待淡出动画");
                        Assert.IsFalse(notificationWindow.IsVisible, "测试不应弹出通知");
                    }
                    finally { notificationWindow.Close(); }
                }
                var autoClosed = false;
                var autoOpenCount = 0;
                var autoWindow = new MailNotificationWindow(notificationAccount, notificationMessage,
                    notificationSettings with { DurationSeconds = 2 }, () =>
                    {
                        Assert.IsTrue(autoClosed, "自动淡出中点击通知也应先立即移除卡片");
                        autoOpenCount++;
                    });
                try
                {
                    autoWindow.Closed += (_, _) => autoClosed = true;
                    autoWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var sawFade = false;
                    timer.Tick += (_, _) =>
                    {
                        sawFade |= clock.Elapsed.TotalSeconds > 2 && autoWindow.Opacity is > 0 and < 0.999;
                        if (sawFade || autoClosed || clock.Elapsed.TotalSeconds > 7) frame.Continue = false;
                    };
                    timer.Start();
                    try { Dispatcher.PushFrame(frame); }
                    finally { timer.Stop(); }
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Assert.IsTrue(sawFade, "自动到期仍应播放慢淡出");
                        Assert.IsFalse(autoClosed, "慢淡出的中间帧不应提前关闭");
                        ((Button)autoWindow.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.IsTrue(autoClosed, "点击应立即打断自动淡出并关闭");
                        Assert.AreEqual(1, autoOpenCount);
                    }
                    else Assert.IsTrue(autoClosed);
                    Assert.IsFalse(autoWindow.IsVisible, "动画检查不应显示通知窗口");
                }
                finally { autoWindow.Close(); }
                var verificationMessage = notificationMessage with
                {
                    Subject = "服务邮箱验证邮件",
                    Preview = ""
                };
                Assert.IsTrue(MailVerificationCode.IsLikelyVerificationMail(verificationMessage));
                var verificationWindow = new MailNotificationWindow(notificationAccount, verificationMessage,
                    notificationSettings with { ShowContent = true, DurationSeconds = 2 }, () => { });
                try
                {
                    var status = (TextBlock)verificationWindow.FindName("VerificationStatusText");
                    var copyButton = (Button)verificationWindow.FindName("CopyCodeButton");
                    var verificationClosed = false;
                    verificationWindow.Closed += (_, _) => verificationClosed = true;
                    verificationWindow.BeginVerificationCheck();
                    verificationWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.AreEqual(Visibility.Visible, status.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, copyButton.Visibility);

                    var loadFrame = new DispatcherFrame();
                    var loadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2300) };
                    loadTimer.Tick += (_, _) => loadFrame.Continue = false;
                    loadTimer.Start();
                    try { Dispatcher.PushFrame(loadFrame); }
                    finally { loadTimer.Stop(); }

                    var fullMessage = verificationMessage with { HtmlBody = "<p>您的验证码为: <b>aB456c</b></p>" };
                    var result = MailVerificationCode.Find(fullMessage);
                    Assert.IsFalse(verificationClosed, "正文读取超过通知设置时长时，弹窗应继续等待");
                    verificationWindow.CompleteVerificationCheck(result.Code, () => Task.CompletedTask);
                    Assert.AreEqual(Visibility.Collapsed, status.Visibility);
                    Assert.AreEqual(Visibility.Visible, copyButton.Visibility);
                    Assert.AreEqual("验证码 aB456c · 复制", copyButton.Content);
                    var readCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var markReadStarted = false;
                    var copyCompletion = verificationWindow.CompleteVerificationCopyAsync(() =>
                    {
                        Assert.IsTrue(verificationClosed, "通知应在回写已读前完成退场");
                        markReadStarted = true;
                        return readCompletion.Task;
                    });
                    Assert.IsTrue(verificationClosed, "复制成功后应立即移除通知");
                    Assert.IsFalse(verificationWindow.HasAnimatedProperties, "复制成功不播放淡出");
                    Assert.IsTrue(markReadStarted);
                    Assert.IsFalse(copyCompletion.IsCompleted);
                    readCompletion.SetResult();
                }
                finally { verificationWindow.Close(); }
                Assert.IsFalse(window.IsVisible, "测试不应打开设置窗口");
                Assert.IsFalse(application.Windows.OfType<MainWindow>().Any(), "测试不应创建主窗口");

                using var refusedProxy = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                refusedProxy.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                var port = ((System.Net.IPEndPoint)refusedProxy.LocalEndPoint!).Port;
                var provider = new ImapSmtpProvider(ProviderKind.GmailImap, new MailProxy
                {
                    Settings = new ProxySettings { Mode = ProxyMode.Custom, Address = $"http://127.0.0.1:{port}" }
                });
                var accountDialog = new AccountDialog { ValidateConnectionAsync = provider.TestConnectionAsync };
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                try
                {
                    ((ComboBox)accountDialog.FindName("ProviderBox")).SelectedIndex = 2;
                    ((TextBox)accountDialog.FindName("AddressBox")).Text = "example@gmail.com";
                    ((PasswordBox)accountDialog.FindName("SecretBox")).Password = "test only pass word";
                    var acceptButton = (Button)accountDialog.FindName("AcceptButton");
                    var progress = (ProgressBar)accountDialog.FindName("ValidationProgress");
                    var validationText = (TextBlock)accountDialog.FindName("ValidationText");
                    acceptButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.IsFalse(acceptButton.IsEnabled, "连接验证期间应禁止重复点击");
                    Assert.AreEqual(Visibility.Visible, progress.Visibility);
                    StringAssert.Contains(validationText.Text, "正在验证");
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    timer.Tick += (_, _) => { if (acceptButton.IsEnabled || DateTime.UtcNow > deadline) frame.Continue = false; };
                    timer.Start();
                    try { Dispatcher.PushFrame(frame); }
                    finally { timer.Stop(); }
                    Assert.IsTrue(acceptButton.IsEnabled, "验证失败后应恢复重试按钮");
                    Assert.AreEqual(Visibility.Collapsed, progress.Visibility);
                    StringAssert.Contains(validationText.Text, "收信：");
                    StringAssert.Contains(validationText.Text, "发信：");
                    Assert.IsNull(accountDialog.ResultAccount, "连接失败不应创建账号");
                    Assert.IsFalse(accountDialog.IsVisible, "测试不应打开账号窗口");
                }
                finally
                {
                    accountDialog.Close();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                window?.Close();
                application?.Shutdown();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(45)), "设置窗口检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
