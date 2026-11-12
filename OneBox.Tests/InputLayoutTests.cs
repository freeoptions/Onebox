using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OneBox.Tests;

[TestClass]
public sealed class InputLayoutTests
{
    [TestMethod]
    public void DialogSingleLineInputsHaveEnoughViewportForTextWithoutOpeningWindows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var application = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "App.xaml")).Root!;
                foreach (var fileName in new[] { "AccountDialog.xaml", "ComposeDialog.xaml" })
                {
                    var window = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", fileName)).Root!;
                    var content = new XElement(window.Elements().Single());
                    foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(item => item.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                        attribute.Remove();
                    var resources = new XElement(application.Element(presentation + "Application.Resources")!.Elements().Single());
                    foreach (var element in resources.Descendants().Where(item => item.Name.NamespaceName == "clr-namespace:OneBox.App"))
                        element.Name = XName.Get(element.Name.LocalName, "clr-namespace:OneBox.App;assembly=OneBox");
                    var wrapper = new XElement(presentation + "Grid",
                        window.Attributes().Where(item => item.IsNamespaceDeclaration).Select(item => new XAttribute(item)),
                        new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:OneBox.App;assembly=OneBox"),
                        new XElement(presentation + "Grid.Resources", resources), content);
                    var grid = (Grid)XamlReader.Parse(wrapper.ToString());
                    TextElement.SetFontFamily(grid, new FontFamily("Segoe UI, Microsoft YaHei UI"));
                    foreach (var mode in fileName == "AccountDialog.xaml" ? new[] { "standard", "gmail-imap", "gmail" } : new[] { "compose" })
                    {
                        if (fileName == "AccountDialog.xaml")
                        {
                            ((StackPanel)grid.FindName("StandardFieldsPanel")).Visibility = mode != "gmail" ? Visibility.Visible : Visibility.Collapsed;
                            ((StackPanel)grid.FindName("GmailPanel")).Visibility = mode == "gmail" ? Visibility.Visible : Visibility.Collapsed;
                            ((StackPanel)grid.FindName("GoogleSetupPanel")).Visibility = Visibility.Visible;
                            ((Button)grid.FindName("AppPasswordButton")).Visibility = mode == "gmail-imap" ? Visibility.Visible : Visibility.Collapsed;
                            ((TextBlock)grid.FindName("SecretLabel")).Text = mode == "gmail-imap" ? "应用专用密码" : "客户端授权码";
                        }
                        foreach (var fontSize in new[] { 13d, 16d, 19.5d })
                        {
                            TextElement.SetFontSize(grid, fontSize);
                            foreach (var size in new[] { 10, 11, 12, 13, 14, 19, 20, 23, 25, 27 })
                                grid.Resources[$"FontSize{size}"] = size * fontSize / 13;
                            grid.Resources["HelpLineHeight"] = 22 * fontSize / 13;
                            grid.Measure(new Size(540, 760));
                            grid.Arrange(new Rect(0, 0, 540, 760));
                            grid.UpdateLayout();
                            var textBoxList = FindChildren<TextBox>(grid).Where(item => !item.AcceptsReturn && item.ActualHeight > 0).ToList();
                            Assert.IsGreaterThanOrEqualTo(2, textBoxList.Count, "没有找到待检查的输入框");
                            foreach (var textBox in textBoxList)
                            {
                                textBox.Text = "测试邮箱example@163.com";
                                grid.UpdateLayout();
                                Assert.IsLessThanOrEqualTo(textBox.ViewportHeight + 0.5, textBox.ExtentHeight,
                                    $"{fileName}/{textBox.Name}, FontSize={fontSize}: 文字区域高度{textBox.ExtentHeight}超过可见高度{textBox.ViewportHeight}");
                            }
                            foreach (var passwordBox in FindChildren<PasswordBox>(grid).Where(item => item.ActualHeight > 0))
                            {
                                passwordBox.Password = "layout-test";
                                grid.UpdateLayout();
                                var contentHost = passwordBox.Template.FindName("PART_ContentHost", passwordBox) as ScrollViewer;
                                Assert.IsNotNull(contentHost, "没有找到密码框文字区域");
                                Assert.IsLessThanOrEqualTo(contentHost.ViewportHeight + 0.5, contentHost.ExtentHeight,
                                    $"{fileName}/{passwordBox.Name}: 密码字符被裁切");
                            }
                        }
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "输入框检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T matching) yield return matching;
            foreach (var descendant in FindChildren<T>(child)) yield return descendant;
        }
    }
}
