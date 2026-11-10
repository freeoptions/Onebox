using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class SyncFailureTests
{
    [TestMethod]
    public void AuthenticationFailureRetainsServerReasonAndSuggestsAuthorization()
    {
        var failure = SyncFailure.Describe(new MailKit.Security.AuthenticationException("LOGIN rejected by server"),
            ProviderKind.NetEase, "读取收件箱");
        Assert.IsTrue(failure.NeedsAuthorization);
        StringAssert.Contains(failure.DisplayText, "LOGIN rejected by server");
        StringAssert.Contains(failure.DisplayText, "客户端授权码");
        StringAssert.Contains(failure.DisplayText, "读取收件箱");
    }

    [TestMethod]
    public void NetworkTimeoutSuggestsRetryWithoutRequestingAuthorization()
    {
        var failure = SyncFailure.Describe(new TaskCanceledException("The operation was canceled"), ProviderKind.NetEase, "读取收件箱");
        Assert.IsFalse(failure.NeedsAuthorization);
        Assert.AreEqual("同步请求超时", failure.Summary);
        StringAssert.Contains(failure.Advice, "重试");
    }

    [TestMethod]
    public void NestedServerFailureKeepsDetailsWhileRemovingCredentials()
    {
        const string exampleSecret = "test-only+credential";
        var error = new IOException($"server rejected {exampleSecret}", new InvalidOperationException(
            $"refresh_token=example-token&client_secret={Uri.EscapeDataString(exampleSecret)} Authorization: Bearer example-bearer"));
        var failure = SyncFailure.Describe(error, ProviderKind.Gmail, "读取收件箱", exampleSecret);
        StringAssert.Contains(failure.Details, "server rejected");
        StringAssert.Contains(failure.Details, "InvalidOperationException");
        Assert.IsFalse(failure.Details.Contains(exampleSecret, StringComparison.Ordinal));
        Assert.IsFalse(failure.Details.Contains(Uri.EscapeDataString(exampleSecret), StringComparison.Ordinal));
        Assert.IsFalse(failure.Details.Contains("example-token", StringComparison.Ordinal));
        Assert.IsFalse(failure.Details.Contains("example-bearer", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StoredFailureIsAvailableForDisplayAfterReloadAndClearsOnSuccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var failure = SyncFailure.Describe(new InvalidOperationException("SELECT INBOX rejected"), ProviderKind.NetEase, "读取收件箱");
        var account = new Account(Guid.NewGuid(), "example@163.com", "测试账号", ProviderKind.NetEase,
            ConnectionStatus.Failed, failure.DisplayText);
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            var viewModel = new MainViewModel { ProblemAccount = (await store.GetAccountsAsync()).Single() };
            Assert.IsTrue(viewModel.HasSyncProblem);
            StringAssert.Contains(viewModel.ProblemDetails, "SELECT INBOX rejected");
            Assert.IsFalse(viewModel.NeedsAuthorization);
            await store.UpsertAccountAsync(account with { Status = ConnectionStatus.Online, LastError = null });
            var reloaded = (await store.GetAccountsAsync()).Single();
            Assert.IsNull(reloaded.LastError);
            Assert.AreEqual(ConnectionStatus.Online, reloaded.Status);
            viewModel.ProblemAccount = null;
            Assert.IsFalse(viewModel.HasSyncProblem);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ReaderStartsEmptyAndSyncControlsFollowRunningState()
    {
        var viewModel = new MainViewModel();
        Assert.IsFalse(viewModel.HasSelectedMessage);
        Assert.IsTrue(viewModel.CanSync);
        viewModel.IsSyncing = true;
        Assert.IsFalse(viewModel.CanSync);
        Assert.AreEqual("正在同步…", viewModel.SyncButtonText);
        viewModel.ShowMessage(new MailMessage(Guid.NewGuid(), "1", "INBOX", "测试", "", "sender@example.com", "", DateTimeOffset.UtcNow, "", false, false, false));
        Assert.IsTrue(viewModel.HasSelectedMessage);
        viewModel.ShowMessage(null);
        Assert.IsFalse(viewModel.HasSelectedMessage);
    }

    [TestMethod]
    public void LegacyFailureExplainsMissingDetailsAndAuthorizationStateIsActionable()
    {
        var viewModel = new MainViewModel
        {
            ProblemAccount = new Account(Guid.NewGuid(), "example@163.com", "测试账号", ProviderKind.NetEase,
                ConnectionStatus.Failed, "ImapCommandException")
        };
        StringAssert.Contains(viewModel.ProblemDetails, "旧版本未保存具体失败原因");
        viewModel.ProblemAccount = viewModel.ProblemAccount with { Status = ConnectionStatus.NeedsAuthorization };
        Assert.IsTrue(viewModel.NeedsAuthorization);
        StringAssert.Contains(viewModel.ProblemTitle, "需要更新授权");
    }
}
