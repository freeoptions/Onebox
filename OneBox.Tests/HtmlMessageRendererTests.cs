using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.WebView2.WinForms;
using OneBox.App;
using Forms = System.Windows.Forms;

namespace OneBox.Tests;

[TestClass]
public sealed class HtmlMessageRendererTests
{
    [TestMethod]
    public void SwitchingHtmlMailReusesDocumentAndKeepsMailScriptsDisabledWithoutShowingWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"OneBox-web-test-{Guid.NewGuid():N}");
            using var context = new Forms.ApplicationContext();
            using var form = new Forms.Form();
            using var view = new WebView2();
            form.Controls.Add(view);
            _ = form.Handle;
            _ = view.Handle;
            async Task CheckAsync()
            {
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: directory);
                await view.EnsureCoreWebView2Async(environment);
                var web = view.CoreWebView2;
                web.Settings.IsScriptEnabled = false;
                web.Settings.IsWebMessageEnabled = false;
                var navigationCount = 0;
                web.NavigationStarting += (_, _) => navigationCount++;
                var renderer = new HtmlMessageRenderer(web);
                Assert.IsTrue(await renderer.ShowAsync("<p>第一封正文</p><script>document.title='mail-script-ran'</script>", false));
                Assert.AreEqual("第一封正文", JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.body.innerText")));
                Assert.AreEqual("\"\"", await web.ExecuteScriptAsync("document.title"));
                Assert.IsTrue(await renderer.ShowAsync("<html><head><style>p{color:red}</style></head><body><p>第二封正文</p></body></html>", false));
                StringAssert.Contains(await web.ExecuteScriptAsync("document.body.innerText"), "第二封正文");
                Assert.IsFalse((await web.ExecuteScriptAsync("document.body.innerText")).Contains("第一封正文", StringComparison.Ordinal));
                Assert.AreEqual(1, navigationCount, "切换HTML邮件不应重新导航页面");
                using var cancellation = new CancellationTokenSource();
                var canceled = renderer.ShowAsync("<p>旧选择</p>", false, cancellation.Token);
                cancellation.Cancel();
                Assert.IsTrue(await renderer.ShowAsync("<p>当前选择</p>", false));
                await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
                StringAssert.Contains(await web.ExecuteScriptAsync("document.body.innerText"), "当前选择");
                Assert.AreEqual(1, navigationCount);
                Assert.IsTrue(await renderer.ShowAsync("<p>允许远程图片</p>", true));
                Assert.AreEqual(2, navigationCount, "切换图片策略应重建文档以更新内容安全策略");
                StringAssert.Contains(await web.ExecuteScriptAsync("document.querySelector('meta[http-equiv]').content"), "https: http:");
                Assert.IsFalse(web.Settings.IsScriptEnabled);
                var openedUriList = new List<Uri>();
                var links = new MailLinkNavigation(openedUriList.Add);
                web.NavigationStarting += links.OnNavigationStarting;
                web.NewWindowRequested += links.OnNewWindowRequested;
                await renderer.ShowAsync("""
                    <a id="normal" href="https://example.invalid/normal">普通链接</a>
                    <a id="button" href="https://example.invalid/button" target="_blank">链接式按钮</a>
                    """, true);
                var normalOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web.NavigationStarting += (_, e) => { if (e.Uri == "https://example.invalid/normal") normalOpened.TrySetResult(); };
                await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """{"expression":"document.getElementById('normal').click()","userGesture":true}""");
                await normalOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(new Uri("https://example.invalid/normal"), openedUriList.Single());
                var buttonOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web.NewWindowRequested += (_, e) => { if (e.Uri == "https://example.invalid/button") buttonOpened.TrySetResult(); };
                await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """{"expression":"document.getElementById('button').click()","userGesture":true}""");
                await buttonOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(2, openedUriList.Count);
                Assert.AreEqual(new Uri("https://example.invalid/button"), openedUriList[1]);
                await renderer.ShowAsync("""<meta http-equiv="refresh" content="0;url=https://example.invalid/automatic">""", true);
                Assert.AreEqual("0", await web.ExecuteScriptAsync("document.querySelectorAll('meta[http-equiv=refresh]').length"));
                Assert.AreEqual(2, openedUriList.Count, "自动跳转不应拉起外部浏览器");
                var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web.NavigationStarting += (_, e) => { if (e.Uri.StartsWith("file:", StringComparison.Ordinal)) blocked.TrySetResult(); };
                web.Navigate("file:///C:/Windows/notepad.exe");
                await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(2, openedUriList.Count, "不支持的协议不应交给系统执行");
                Assert.AreEqual("about:blank", web.Source, "打开外部链接后应保留邮件阅读页面");
                Assert.IsFalse(form.Visible, "测试不应打开窗口");
            }
            view.BeginInvoke(new Action(async () =>
            {
                try { await CheckAsync(); }
                catch (Exception error) { failure = error; }
                finally
                {
                    view.Dispose();
                    context.ExitThread();
                }
            }));
            Forms.Application.Run(context);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(45)), "HTML正文切换检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
