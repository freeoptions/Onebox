using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Shell;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;

namespace OneBox.Tests;

[TestClass]
public sealed class LayoutTests
{
    [TestMethod]
    public void ActualMainWindowMarkupFitsDesktopWidthsAndFailureStatesWithoutOpeningWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var application = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "App.xaml")).Root!;
                var window = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "MainWindow.xaml")).Root!;
                Assert.IsTrue(window.Descendants(presentation + "Button").Any(item => (string?)item.Attribute("Click") == "OnRowToggleStar"), "列表星标没有点击入口");
                Assert.IsTrue(window.Descendants(presentation + "Button").Any(item => (string?)item.Attribute("Click") == "OnFavorites"), "侧栏没有收藏夹入口");
                var root = new XElement(window.Element(presentation + "Grid")!);
                foreach (var attribute in window.Attributes().Where(item => item.IsNamespaceDeclaration))
                    root.SetAttributeValue(attribute.Name, attribute.Value);
                root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:OneBox.App;assembly=OneBox");
                foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(item => item.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                    attribute.Remove();
                var resources = new XElement(application.Element(presentation + "Application.Resources")!.Elements().Single());
                foreach (var element in resources.Descendants().Where(item => item.Name.NamespaceName == "clr-namespace:OneBox.App"))
                    element.Name = XName.Get(element.Name.LocalName, "clr-namespace:OneBox.App;assembly=OneBox");
                foreach (var image in root.Descendants(presentation + "Image").Where(item => (string?)item.Attribute("Source") == "Assets/onebox.png"))
                    image.SetAttributeValue("Source", "pack://application:,,,/OneBox;component/Assets/onebox.png");
                var wrapper = new XElement(presentation + "Grid",
                    root.Attributes().Where(item => item.IsNamespaceDeclaration).Select(item => new XAttribute(item)),
                    new XElement(presentation + "Grid.Resources", resources), root);
                var grid = (Grid)XamlReader.Parse(wrapper.ToString());
                var viewModel = new MainViewModel();
                grid.DataContext = viewModel;
                foreach (var width in new[] { 1000d, 1420d, 1920d })
                {
                    foreach (var state in new[] { "empty", "failure", "expanded", "inbox", "reading" })
                    {
                        var hasFailure = state is "failure" or "expanded";
                        viewModel.ProblemAccount = hasFailure ? new Account(Guid.NewGuid(), "example@163.com", "测试账号",
                            ProviderKind.NetEase, ConnectionStatus.Failed, "服务器拒绝请求\n请检查原因后重试。") : null;
                        viewModel.Accounts.Clear();
                        viewModel.Accounts.Add(new Account(Guid.NewGuid(), "example@163.com", "测试账号", ProviderKind.NetEase));
                        var message = new MailMessage(viewModel.Accounts[0].Id, "1", "INBOX", "这是一封用于核对阅读布局的邮件", "测试发件人",
                            "sender@example.com", "example@163.com", DateTimeOffset.UtcNow, "邮件摘要", false, true, true, "邮件正文");
                        var readMessage = message with { ServerId = "2", FromName = "已读发件人", IsRead = true, Preview = "", TextBody = null };
                        viewModel.ShowMessages(state is "reading" or "inbox" ? new[] { message, readMessage } : Array.Empty<MailMessage>());
                        viewModel.ShowMessage(state == "reading" ? message : null);
                        foreach (var expander in FindChildren<Expander>(grid)) expander.IsExpanded = state == "expanded";
                        grid.Measure(new Size(width, 640));
                        grid.Arrange(new Rect(0, 0, width, 640));
                        grid.UpdateLayout();
                        viewModel.Notify(new string('测', 200), "失败");
                        grid.UpdateLayout();
                        foreach (var name in new[] { "MinimizeCaption", "MaximizeCaption", "CloseCaption" })
                        {
                            var captionButton = (Button)grid.FindName(name);
                            var captionPosition = captionButton.TranslatePoint(new Point(), grid);
                            Assert.AreEqual(Visibility.Visible, captionButton.Visibility, name);
                            Assert.IsTrue(captionButton.ActualWidth >= 40 && captionButton.ActualHeight >= 32, name);
                            Assert.IsTrue(captionPosition.X >= 0 && captionPosition.X + captionButton.ActualWidth <= width, name);
                            Assert.IsTrue(WindowChrome.GetIsHitTestVisibleInChrome(captionButton), name);
                        }
                        Assert.IsLessThanOrEqualTo(width, grid.DesiredSize.Width, $"布局超出窗口：{width}");
                        var searchBox = (TextBox)grid.FindName("SearchBox");
                        Assert.IsGreaterThan(100d, searchBox.ActualWidth, "搜索框宽度不足");
                        var noticePanel = (Border)grid.FindName("NoticePanel");
                        Assert.IsGreaterThan(200d, noticePanel.ActualWidth, "消息区宽度不足");
                        searchBox.Text = "测试搜索";
                        grid.UpdateLayout();
                        Assert.IsLessThanOrEqualTo(searchBox.ViewportHeight + 0.5, searchBox.ExtentHeight, "搜索框文字被裁切");
                        var messageList = (ListBox)grid.FindName("MessageList");
                        var inboxPage = (Grid)grid.FindName("InboxPage");
                        var readerPage = (Grid)grid.FindName("ReaderPage");
                        Assert.AreEqual(Visibility.Visible, inboxPage.Visibility);
                        Assert.IsGreaterThanOrEqualTo(247d, messageList.ActualWidth, "邮件列表列过窄");
                        Assert.IsGreaterThan(180d, messageList.ActualHeight, "失败提示挤占了邮件列表");
                        if (state == "reading")
                        {
                            Assert.AreEqual(Visibility.Visible, readerPage.Visibility);
                            Assert.IsGreaterThanOrEqualTo(439d, readerPage.ActualWidth, "阅读区过窄");
                            var listRight = inboxPage.TranslatePoint(new Point(inboxPage.ActualWidth, 0), grid).X;
                            var readerLeft = readerPage.TranslatePoint(new Point(), grid).X;
                            Assert.IsTrue(listRight <= readerLeft, "邮件列表与阅读区需要并排显示");
                            Assert.IsGreaterThan(180d, readerPage.ActualHeight, "阅读页高度不足");
                            var plainBody = (TextBox)grid.FindName("PlainBody");
                            var bodyPosition = plainBody.TranslatePoint(new Point(), readerPage);
                            var bodyHeight = plainBody.ActualHeight;
                            viewModel.IsBodyLoading = true;
                            grid.UpdateLayout();
                            Assert.AreEqual(bodyPosition, plainBody.TranslatePoint(new Point(), readerPage), "加载提示不应推动正文位置");
                            Assert.AreEqual(bodyHeight, plainBody.ActualHeight, "加载提示不应改变正文区域高度");
                            viewModel.IsBodyLoading = false;
                            var attachmentButton = (Button)grid.FindName("AttachmentButton");
                            viewModel.IsDownloadingAttachments = true;
                            grid.UpdateLayout();
                            Assert.IsFalse(attachmentButton.IsEnabled, "下载中仍允许重复触发");
                            Assert.AreEqual("正在下载…", attachmentButton.Content);
                            viewModel.IsDownloadingAttachments = false;
                            grid.UpdateLayout();
                            Assert.IsTrue(attachmentButton.IsEnabled);
                            if (width == 1420)
                            {
                                var splitter = FindChildren<GridSplitter>(grid).Single(item => Grid.GetColumn(item) == 1);
                                var listWidth = inboxPage.ActualWidth;
                                var readerWidth = readerPage.ActualWidth;
                                splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                                splitter.RaiseEvent(new DragDeltaEventArgs(32, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                                splitter.RaiseEvent(new DragCompletedEventArgs(32, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                                grid.UpdateLayout();
                                Assert.AreEqual(listWidth + 32, inboxPage.ActualWidth, 1, "拖动后邮件列表应变宽");
                                Assert.AreEqual(readerWidth - 32, readerPage.ActualWidth, 1, "拖动后阅读区应变窄");
                            }
                        }
                        else
                        {
                            Assert.AreEqual(Visibility.Collapsed, readerPage.Visibility);
                            if (state == "inbox")
                            {
                                var unreadSender = FindChildren<TextBlock>(messageList).Single(item => item.Text == "测试发件人");
                                var readSender = FindChildren<TextBlock>(messageList).Single(item => item.Text == "已读发件人");
                                var row = viewModel.Messages[0];
                                var container = (ListBoxItem)messageList.ItemContainerGenerator.ContainerFromItem(row);
                                var subject = FindChildren<TextBlock>(container).Single(item => item.Text == message.Subject);
                                Assert.AreEqual(FontWeights.SemiBold, unreadSender.FontWeight, "未读发件人未加粗");
                                Assert.AreEqual(FontWeights.SemiBold, subject.FontWeight, "未读主题未加粗");
                                Assert.AreSame(grid.Resources["InkBrush"], subject.Foreground);
                                Assert.AreEqual(FontWeights.Normal, readSender.FontWeight, "已读发件人仍然加粗");
                                row.UpdateFlags(true, false);
                                grid.UpdateLayout();
                                Assert.AreEqual(FontWeights.Normal, unreadSender.FontWeight, "已读样式没有即时更新");
                                Assert.AreEqual(FontWeights.Normal, subject.FontWeight, "已读主题仍然加粗");
                                Assert.AreSame(grid.Resources["MutedBrush"], unreadSender.Foreground);
                                Assert.AreSame(grid.Resources["MutedBrush"], subject.Foreground);
                                var starButton = FindChildren<Button>(messageList).First(item => item.DataContext == viewModel.Messages[0]);
                                var starImage = (Image)starButton.Content;
                                Assert.AreSame(grid.Resources["UnfavoriteIcon"], starImage.Source);
                                viewModel.Messages[0].UpdateFlags(true, true);
                                grid.UpdateLayout();
                                Assert.AreSame(grid.Resources["FavoriteIcon"], starImage.Source, "星标没有即时更新");
                                var compactRow = (ListBoxItem)messageList.ItemContainerGenerator.ContainerFromItem(viewModel.Messages[1]);
                                Assert.AreEqual(compactRow.ActualHeight, container.ActualHeight, 0.5, "列表行高不应受摘要影响");
                                Assert.IsFalse(FindChildren<TextBlock>(container).Any(item => item.Text == message.Preview), "列表不应显示摘要");
                                var rowHeight = container.ActualHeight;
                                var multilineBody = string.Join("\r\n", Enumerable.Repeat("邮件正文与详情", 20));
                                row.CacheBody(row.Message with { TextBody = multilineBody, Preview = multilineBody.Replace('\n', ' ') });
                                grid.Measure(new Size(width, 640));
                                grid.Arrange(new Rect(0, 0, width, 640));
                                grid.UpdateLayout();
                                Assert.AreEqual(rowHeight, container.ActualHeight, 0.5, "读取正文后列表行高应保持不变");
                                Assert.IsFalse(FindChildren<TextBlock>(container).Any(item => item.Text.Contains("邮件正文与详情", StringComparison.Ordinal)),
                                    "读取正文后列表不应显示正文片段");
                                Assert.AreEqual(multilineBody, row.Message.TextBody, "列表显示处理不应修改完整正文");
                            }
                        }
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "布局检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T matching) yield return matching;
            foreach (var descendant in FindChildren<T>(child)) yield return descendant;
        }
    }
}
