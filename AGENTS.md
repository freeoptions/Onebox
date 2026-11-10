# 构建交付

- 用户要求构建时，生成Windows x64自包含Release版本；发布产物为OneBox.App/bin/Release/publish-win-x64/OneBox.exe。
- 构建成功后，将OneBox.exe复制到D:\@Softwares\OneBox\OneBox.exe，允许直接覆盖同名exe；保留目标目录中的其他文件和本机数据。
- 复制后核对源文件与目标文件的SHA256，交付时提供目标文件链接，不自动启动程序。
