# 简单查盘器

**移动硬盘、U 盘弹不出来？**

查看占用它的软件，选中后尝试关闭。

[下载 Windows 安装包](https://github.com/yangyiyu789-web/disk-release/releases/download/v1.1.3/SimpleDiskChecker-Setup-1.1.3.exe)

适用 Windows 10 / 11（x64），需要 .NET Framework 4.8 或更新版本。

## 怎么用

1. 选择磁盘，点击“重新检查”。
2. 保存工作，勾选软件，点击“关闭所选”。
3. 回到 Windows 安全弹出，再拔出硬盘。

![简单查盘器界面](docs/images/overview.png)

*截图为演示数据。*

关闭前先保存工作。强制结束可能丢失未保存内容；本工具不执行磁盘弹出。

<details>
<summary>更多用法与注意事项</summary>

- 新接入的磁盘：在“更多”中刷新磁盘。
- 查看完整路径与进程信息：点击软件行末的详情。
- 无法正常关闭：使用“强制结束…”并核对名单；混合选择时先尝试正常关闭。
- 关闭相关文件夹：在“更多”中操作。多标签窗口的其他标签可能一起关闭，工具不会结束整个资源管理器。
- 检查结果受权限和系统状态影响，空列表不保证没有占用。可按需使用管理员权限重新检查；内部磁盘仅供检查，系统关键进程受保护。
- 请安装到本机磁盘。仅为当前用户安装，可选桌面快捷方式，不自动启动。
- 本地运行，不联网、不上传扫描结果，不添加开机启动、驱动或后台服务。
- 程序尚未数字签名，Windows 可能显示“未知发布者”。

</details>

<details>
<summary>开发资料</summary>

## 从源码构建

在 Windows x64 上使用 PowerShell。应用使用系统自带的 .NET Framework C# 编译器，不需要 Visual Studio；构建前需安装 .NET Framework 4.8 或更新版本。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

程序输出到 `bin\DiskRelease.exe`。制作安装包还需从 [Inno Setup 官网](https://jrsoftware.org/isdl.php)获取编译器，本项目使用 6.7.3。编译器不随源码分发；将下例路径替换为本机的 `ISCC.exe` 路径：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Installer -InnoSetupCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

安装包输出到 `dist\简单查盘器-安装包-1.1.3.exe`。构建过程不自动下载依赖或启动应用。

## 测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

默认运行三组测试，检查界面布局与操作限制、资源管理器列表投影、主按钮的正常关闭与强制结束状态。它们使用测试数据，不结束真实用户软件。

可额外运行扫描辅助进程的无效磁盘身份协议检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -IncludeWorkerProtocol
```

这些检查不能代替在真实磁盘上验证扫描结果或 Windows 安全弹出。

## 目录

```text
src/                    应用源码、图标与清单
tests/                  测试源码与脚本
docs/images/            使用示例截图（演示数据）
installer-assets/       安装器简体中文语言文件及来源说明
licenses/               第三方许可文本
build.ps1               构建应用与安装包
installer.iss           Inno Setup 安装脚本
中文使用说明.txt          随程序提供的使用说明
安装说明.txt              安装前提示
```

编译产物与本机编译器不纳入源码仓库。版本变化见 [CHANGELOG.md](CHANGELOG.md)。

## 许可

本项目原创代码采用 [MIT License](LICENSE)。安装器语言文件及 Inno Setup 生成的安装引擎保留其各自的许可与声明，详见 [第三方声明](THIRD-PARTY-NOTICES.md)；本项目的 MIT 许可不覆盖第三方材料。

</details>
