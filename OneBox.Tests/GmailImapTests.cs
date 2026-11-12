using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class GmailImapTests
{
    [TestMethod]
    [DataRow("user@gmail.com")]
    [DataRow("user@googlemail.com")]
    [DataRow("user@GMAIL.COM")]
    public void GmailUsesEncryptedImapAndSmtpEndpoints(string address)
    {
        var provider = new ImapSmtpProvider(ProviderKind.GmailImap);
        Assert.AreEqual(ProviderKind.GmailImap, provider.Kind);
        Assert.AreEqual(("imap.gmail.com", 993, "smtp.gmail.com", 465), ImapSmtpProvider.Servers(address));
    }

    [TestMethod]
    public void GmailPasswordFailureExplainsAppPasswordWithoutChangingLegacyOAuthAdvice()
    {
        var error = new MailKit.Security.AuthenticationException("Invalid credentials");
        var failure = SyncFailure.Describe(error, ProviderKind.GmailImap, "读取收件箱");
        Assert.IsTrue(failure.NeedsAuthorization);
        StringAssert.Contains(failure.Advice, "应用专用密码");
        StringAssert.Contains(failure.Advice, "两步验证");
        var legacyFailure = SyncFailure.Describe(error, ProviderKind.Gmail, "读取收件箱");
        StringAssert.Contains(legacyFailure.Advice, "重新授权Google账号");
    }

    [TestMethod]
    public async Task GmailImapAndLegacyOAuthAccountsKeepSeparateProvidersAndCursorsAfterReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var legacy = new Account(Guid.NewGuid(), "user@gmail.com", "旧账号", ProviderKind.Gmail);
        var imap = new Account(Guid.NewGuid(), "user@gmail.com", "新账号", ProviderKind.GmailImap);
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(legacy);
            await store.UpsertAccountAsync(imap);
            var accountList = await store.GetAccountsAsync();
            Assert.AreEqual(ProviderKind.Gmail, accountList.Single(account => account.Id == legacy.Id).Provider);
            Assert.AreEqual(ProviderKind.GmailImap, accountList.Single(account => account.Id == imap.Id).Provider);
            await store.SaveCursorAsync(new SyncCursor(legacy.Id, "INBOX", "12345", null, null, DateTimeOffset.UtcNow));
            await store.SaveCursorAsync(new SyncCursor(imap.Id, "INBOX", null, 100, 12, DateTimeOffset.UtcNow));
            Assert.AreEqual("12345", (await store.GetCursorAsync(legacy.Id, "INBOX"))!.GmailHistoryId);
            Assert.AreEqual(100L, (await store.GetCursorAsync(imap.Id, "INBOX"))!.UidValidity);
            Assert.IsNull((await store.GetCursorAsync(imap.Id, "INBOX"))!.GmailHistoryId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
