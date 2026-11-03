# OneBox Windows

OneBox 是本机多邮箱客户端。当前源码实现了 WPF 三栏界面、Gmail OAuth/Gmail API、QQ/网易 IMAP/SMTP、本地 SQLite 缓存和 Windows 凭据管理器。目标框架为 .NET 8，SDK 锁定为 8.0.417。当前机器上的 .NET 10 运行时在程序启动前发生内部错误，因此交付版本使用 .NET 8 自包含发布。

## 开发环境

- Windows 10/11、.NET 8 SDK 8.0.417。
- 邮件正文的 HTML 阅读需要 WebView2 Runtime；纯文本可直接显示。
- Gmail 账号首次使用需要在自己的 Google Cloud 项目[启用 Gmail API](https://console.cloud.google.com/apis/library/gmail.googleapis.com)，并在 [Google Auth Platform](https://console.cloud.google.com/auth/clients) 创建 **桌面应用** OAuth 客户端。添加账号时可导入 Google 下载的桌面客户端 JSON，也可填写其中对应的客户端 ID 与密钥；此后点击“在浏览器中登录”，由 Google 网页完成账号选择和授权，OneBox 自动读取授权邮箱。
- QQ 和网易邮箱需要先在网页端开启 IMAP/SMTP，添加时输入客户端授权码，而不是登录密码。网易首批支持 `163.com`、`126.com`、`yeah.net`。

账号令牌、授权码和 Google 客户端密钥保存到 Windows 凭据管理器。账号、邮件、同步游标和待同步操作保存到 EXE 同目录的 `mail.db`。Gmail 客户端 ID 是非秘密元数据，保存到同目录的 `google-client-id.txt`；导入的 JSON 不会被复制。WebView2 用户数据也放在 EXE 同目录的 `WebView2` 文件夹。数据库升级时先在同目录生成带时间戳的备份。仓库不存放实际账号凭据。

Gmail 授权会申请离线访问，刷新令牌在 Windows 凭据管理器中跨重启保留，短期访问令牌到期后自动续期。Google 仍可在用户撤销授权、客户端设置改变等情况下使其失效。若 OAuth 同意屏幕保持 **External / Testing**，Gmail 权限对应的测试授权和刷新令牌约 7 天后会过期；长期自用应核对项目发布状态，不能承诺令牌永远有效。

## 当前功能

- 统一收件箱、按账号收件箱、最近 90 天邮件索引、正文按需获取、本地搜索（中文、完整拼音、首字母）、手动刷新和运行期间轮询。
- Gmail 刷新令牌续期与 `historyId` 增量读取；IMAP 按 `UIDVALIDITY` 检测重建，增量获取新邮件并核对最近邮件的 FLAGS。
- 纯文本及受限 WebView2 HTML 阅读。脚本和远程资源默认阻止，远程图片仅在用户点击后显示。
- 撰写、发信账号确认、附件发送和手动下载；下载使用安全文件名且不覆盖现有文件。
- 已读/未读和星标操作先写本地待执行队列，连接恢复后回写服务端；单账号失败不会阻断其他账号。
- 账号独立移除及重新授权。移除只清除本机数据与凭据，不删除服务器邮件。
- 最大化启动；关闭隐藏托盘、最小化留在任务栏，托盘左键切换窗口，右键仅显示与退出。

## 尚待验收与完善

真实 Gmail、QQ、网易账号尚未接入验证；程序未启动做界面交互验收。当前文件夹导航只覆盖收件箱，旧邮件按需加载、附件缓存上限、SMTP 不确定发送结果的服务端核对、复杂 MIME 字符集和同步限流仍需继续完善。静态预览见 [`design/onebox-preview.png`](design/onebox-preview.png)。

测试项目为 `OneBox.Tests`。在安装指定 SDK 后可运行 `dotnet test OneBox.Tests/OneBox.Tests.csproj --configuration Release`。最终 Release 产物应在界面验收后按明确构建指令生成。
