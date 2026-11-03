"""Generate a static layout preview and the OneBox window/tray icon."""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "design"
ASSETS = ROOT / "OneBox.App" / "Assets"
OUT.mkdir(exist_ok=True)
ASSETS.mkdir(exist_ok=True)

BG = "#F3F9FD"
WHITE = "#FFFFFF"
INK = "#18374B"
MUTED = "#6E899B"
ACCENT = "#168BCB"
LINE = "#DCEAF3"
SELECTED = "#E4F3FC"

font_path = "C:/Windows/Fonts/msyh.ttc"
bold_path = "C:/Windows/Fonts/msyhbd.ttc"


def font(size, bold=False):
    return ImageFont.truetype(bold_path if bold else font_path, size)


def label(draw, xy, value, size=14, fill=INK, bold=False):
    draw.text(xy, value, font=font(size, bold), fill=fill)


icon = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(icon)
d.rounded_rectangle((10, 10, 246, 246), radius=58, fill=ACCENT)
d.rounded_rectangle((48, 73, 208, 183), radius=19, fill=WHITE)
d.line((50, 78, 128, 139, 206, 78), fill=ACCENT, width=15, joint="curve")
d.line((50, 177, 106, 126), fill=ACCENT, width=11)
d.line((206, 177, 150, 126), fill=ACCENT, width=11)
icon.save(ASSETS / "onebox.ico", format="ICO", sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (256, 256)])
icon.save(ASSETS / "onebox.png")

canvas = Image.new("RGB", (1440, 900), BG)
d = ImageDraw.Draw(canvas)
d.rectangle((0, 0, 1440, 68), fill=WHITE)
d.line((0, 68, 1440, 68), fill=LINE)
canvas.paste(icon.resize((32, 32), Image.Resampling.LANCZOS), (25, 18), icon.resize((32, 32), Image.Resampling.LANCZOS))
label(d, (69, 19), "OneBox", 20, INK, True)
d.rounded_rectangle((275, 16, 696, 53), radius=9, fill=BG, outline=LINE)
label(d, (290, 23), "⌕", 21, MUTED)
label(d, (323, 26), "搜索已同步邮件", 12, MUTED)
label(d, (1210, 26), "已同步", 12, MUTED)
d.rounded_rectangle((1306, 16, 1416, 53), radius=8, fill=ACCENT)
label(d, (1334, 25), "写邮件", 13, WHITE, True)

d.rectangle((0, 69, 250, 869), fill=WHITE)
d.rectangle((251, 69, 640, 869), fill=WHITE)
d.line((250, 69, 250, 869), fill=LINE)
d.line((640, 69, 640, 869), fill=LINE)
label(d, (27, 92), "邮箱", 12, MUTED, True)
d.rounded_rectangle((14, 124, 236, 168), radius=8, fill=SELECTED)
label(d, (27, 136), "▣   统一收件箱", 14, INK, True)
label(d, (27, 200), "账号", 12, MUTED, True)
for y, name, address, mark in [
    (239, "个人 QQ 邮箱", "hello@qq.com", "Q"),
    (300, "网易邮箱", "hello@163.com", "N"),
    (361, "Gmail", "hello@gmail.com", "G"),
]:
    d.rounded_rectangle((26, y, 57, y + 31), radius=8, fill=SELECTED)
    label(d, (35, y + 4), mark, 15, ACCENT, True)
    label(d, (68, y - 1), name, 13, INK, True)
    label(d, (68, y + 19), address, 11, MUTED)
    label(d, (68, y + 36), "已连接", 10, ACCENT)
d.line((20, 779, 230, 779), fill=LINE)
label(d, (27, 792), "＋  添加账号", 12, INK)
label(d, (27, 816), "重新授权所选账号", 11, MUTED)
label(d, (27, 840), "移除所选账号", 11, MUTED)

label(d, (273, 91), "统一收件箱", 18, INK, True)
label(d, (273, 119), "仅搜索已同步邮件", 11, MUTED)
label(d, (576, 102), "刷新", 12, ACCENT)
d.line((251, 138, 640, 138), fill=LINE)
messages = [
    ("项目协作", "周五同步纪要与后续事项", "整理了本周的进度，附件中有最新版本…", "10:42", "hello@qq.com"),
    ("网易邮箱团队", "客户端授权码开通提醒", "您已成功开启 IMAP/SMTP 服务…", "昨天", "hello@163.com"),
    ("Google", "Security alert", "A new sign-in to your Google Account…", "09-10", "hello@gmail.com"),
    ("小林", "资料已收到", "收到，谢谢！我会在周末前反馈。", "09-09", "hello@qq.com"),
]
for index, (sender, subject, excerpt, date, account) in enumerate(messages):
    y = 139 + index * 118
    if index == 0:
        d.rectangle((251, y, 640, y + 117), fill=SELECTED)
        d.rectangle((251, y, 254, y + 117), fill=ACCENT)
    label(d, (273, y + 18), sender, 13, INK, True)
    label(d, (582, y + 18), date, 10, MUTED)
    label(d, (273, y + 46), subject, 13, INK, True)
    label(d, (273, y + 70), excerpt, 11, MUTED)
    label(d, (273, y + 93), account, 10, ACCENT)
    d.line((251, y + 117, 640, y + 117), fill=LINE)

label(d, (674, 94), "周五同步纪要与后续事项", 22, INK, True)
label(d, (674, 154), "发件人：项目协作 <team@example.com>", 13, INK, True)
label(d, (674, 182), "收件人：hello@qq.com", 12, MUTED)
for x, text in [(674, "标为已读"), (777, "星标"), (850, "下载附件"), (964, "显示远程图片")]:
    d.rounded_rectangle((x, 215, x + len(text) * 14 + 18, 248), radius=7, outline=LINE)
    label(d, (x + 9, 223), text, 11, INK)
d.line((674, 265, 1408, 265), fill=LINE)
for y, line in enumerate([
    "你好，",
    "整理了本周的进度与后续事项，供你查阅。",
    "收件箱会按账号同步，阅读状态也会回写到邮箱服务端。",
    "附件需要时再下载到本机，不会自动保存。",
    "",
    "谢谢！",
]):
    label(d, (674, 298 + y * 38), line, 14, INK)

d.rectangle((0, 870, 1440, 900), fill=WHITE)
d.line((0, 869, 1440, 869), fill=LINE)
label(d, (18, 877), "4 封已同步邮件", 11, MUTED)
canvas.save(OUT / "onebox-preview.png")
