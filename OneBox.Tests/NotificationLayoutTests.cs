using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OneBox.Tests;

[TestClass]
public sealed class NotificationLayoutTests
{
    [TestMethod]
    public void VerificationRowCanGrowAndShrinkInActualNotificationMarkupWithoutOpeningWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "App.xaml")).Root!;
                var window = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "MailNotificationWindow.xaml")).Root!;
                var resources = new XElement(app.Element(p + "Application.Resources")!.Elements().Single());
                foreach (var element in resources.Descendants().Where(item => item.Name.NamespaceName == "clr-namespace:OneBox.App"))
                    element.Name = XName.Get(element.Name.LocalName, "clr-namespace:OneBox.App;assembly=OneBox");
                var content = new XElement(window.Element(p + "Grid")!);
                foreach (var image in content.Descendants(p + "Image"))
                    image.SetAttributeValue("Source", "pack://application:,,,/OneBox;component/Assets/onebox.png");
                foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(item => item.Name.LocalName == "Click").ToArray())
                    attribute.Remove();
                var wrapper = new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:OneBox.App;assembly=OneBox"),
                    new XElement(p + "Grid.Resources", resources), content);
                var grid = (Grid)XamlReader.Parse(wrapper.ToString());
                grid.Width = 368;
                TextElement.SetFontSize(grid, 13);
                ((Run)grid.FindName("AccountAddressRun")).Text = "example@gmail.com";
                ((TextBlock)grid.FindName("ReceivedAtText")).Text = "2026-11-18 09:10:11";
                ((TextBlock)grid.FindName("SenderText")).Text = "邮箱验证";
                ((TextBlock)grid.FindName("SubjectText")).Text = "你的邮箱验证码";
                ((TextBlock)grid.FindName("PreviewText")).Text = "请使用下面的验证码完成验证。";
                var code = (Button)grid.FindName("CopyCodeButton");
                code.Content = "验证码 123456 · 复制";
                double Layout()
                {
                    grid.Measure(new Size(368, double.PositiveInfinity));
                    grid.Arrange(new Rect(0, 0, 368, grid.DesiredSize.Height));
                    grid.UpdateLayout();
                    return grid.ActualHeight;
                }
                var previewHeight = Layout();
                code.Visibility = Visibility.Visible;
                var codeHeight = Layout();
                Assert.IsTrue(((TextBlock)grid.FindName("SubjectText")).ActualWidth > 150, "通知文字应使用卡片可用宽度");
                Assert.IsTrue(codeHeight > previewHeight + 20, "验证码会使真实通知增高，定位必须使用新布局尺寸");
                var bottom = code.TranslatePoint(new Point(0, code.ActualHeight), grid).Y;
                Assert.IsTrue(bottom <= codeHeight - 12, "验证码按钮应完整保留在卡片内");
                var output = Environment.GetEnvironmentVariable("ONEBOX_NOTIFICATION_PREVIEW");
                if (!string.IsNullOrEmpty(output))
                {
                    var bitmap = new RenderTargetBitmap(368, (int)Math.Ceiling(codeHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(grid);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(output);
                    encoder.Save(file);
                }
                code.Visibility = Visibility.Collapsed;
                Assert.AreEqual(previewHeight, Layout(), 0.5, "动态内容关闭后应恢复原来的通知高度");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "通知静态布局检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
