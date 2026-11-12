using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
            OneBoxApplication? application = null;
            SettingsWindow? window = null;
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "reader-settings.json");
                application = new OneBoxApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.InitializeComponent();
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
                Assert.IsFalse(window.IsVisible, "测试不应打开设置窗口");

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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "设置窗口检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
