using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;

namespace OneBox.Tests;

[TestClass]
public sealed class LogTextLayoutTests
{
    [TestMethod]
    public void ActualLogPopupTextFitsFontSizesAndDisplayScalesWithoutOpeningWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "App.xaml")).Root!;
                var main = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "MainWindow.xaml")).Root!;
                var resources = new XElement(app.Element(p + "Application.Resources")!.Elements().Single());
                foreach (var element in resources.Descendants().Where(item => item.Name.NamespaceName == "clr-namespace:OneBox.App"))
                    element.Name = XName.Get(element.Name.LocalName, "clr-namespace:OneBox.App;assembly=OneBox");
                var content = new XElement(main.Descendants(p + "Border").Single(item => (string?)item.Attribute(x + "Name") == "NoticeHistoryContent"));
                content.SetAttributeValue("Width", "360");
                foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(item => item.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                    attribute.Remove();
                var markup = new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:OneBox.App;assembly=OneBox"),
                    new XElement(p + "Grid.Resources", resources), content);
                var grid = (Grid)XamlReader.Parse(markup.ToString());
                grid.Width = 360;
                var model = new MainViewModel();
                model.Notify("示例账号 · 收件箱实时监听已连接（30秒主动补查兜底）", "状态");
                model.Notify("示例账号 · 自动收信检查暂时失败，15秒后重试", "告警", "隔离测试诊断详情");
                grid.DataContext = model;
                foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
                foreach (var percent in OneBox.App.App.FontScalePercentList)
                {
                    VisualTreeHelper.SetRootDpi(grid, new DpiScale(dpi, dpi));
                    foreach (var size in new[] { 10, 11, 12, 13, 14, 19, 20, 23, 25, 27 })
                        grid.Resources[$"FontSize{size}"] = size * percent / 100d;
                    grid.Measure(new Size(360, double.PositiveInfinity));
                    grid.Arrange(new Rect(0, 0, 360, grid.DesiredSize.Height));
                    grid.UpdateLayout();
                    var logTextList = Children<TextBlock>(grid).Where(item => model.RecentNoticeList.Any(entry => entry.Text == item.Text)).ToArray();
                    Assert.AreEqual(2, logTextList.Length);
                    foreach (var text in logTextList)
                    {
                        Assert.IsTrue(text.ActualWidth > 150, "日志应留有足够的换行宽度");
                        Assert.IsTrue(text.ActualHeight + 0.5 >= text.DesiredSize.Height, "字体放大后日志不能被裁切");
                        Assert.AreEqual(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(text));
                        Assert.AreEqual(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(text));
                        var location = text.TranslatePoint(new Point(), grid);
                        Assert.IsTrue(location.X >= 0 && location.X + text.ActualWidth <= grid.ActualWidth + 0.5);
                    }
                    var output = Environment.GetEnvironmentVariable("ONEBOX_LOG_PREVIEW");
                    if (dpi == 1.25 && percent == 115 && !string.IsNullOrEmpty(output))
                    {
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(grid.ActualWidth * dpi),
                            (int)Math.Ceiling(grid.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                        bitmap.Render(grid);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(output);
                        encoder.Save(file);
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "日志字体静态布局检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }

    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T matching) yield return matching;
            foreach (var item in Children<T>(child)) yield return item;
        }
    }
}
