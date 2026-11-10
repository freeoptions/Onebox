using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
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
                Assert.IsFalse(window.IsVisible, "测试不应打开设置窗口");
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
