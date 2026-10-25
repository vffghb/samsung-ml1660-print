# 打印助手 · 多格式直通打印（SamsungPrinter）

> 绕开系统打印队列（spooler），把文档直接写入打印机 USB 的 Windows 桌面工具。
> 专为"打印机驱动装不上 / 队列提交成功但不出纸"的 Windows（尤其精简版系统）打造，
> 正常系统同样可用。

![支持格式](https://img.shields.io/badge/格式-PDF·图片·文本·Office-2563EB)
![平台](https://img.shields.io/badge/平台-Windows%20x64-0ea5e9)
![协议](https://img.shields.io/badge/协议-MIT-green)

---

## 背景：解决了什么问题

某些 Windows 环境（精简版系统、驱动签名/安全机制拦截等）下，**打印机驱动 DLL 无法被
系统打印服务（spooler）加载**，表现为：

- 驱动安装正常、设备管理器里打印机"工作正常"
- 打印任务"提交成功、无任何报错"——但**就是不出纸**
- 打印机自检页正常 → 硬件没问题，是系统软件链路坏了

常见的修复思路（重装驱动、重建队列、改注册表、关安全设置）治标不治本。
本工具采用**完全绕开打印队列**的方案，直接与打印机硬件通信：

```
文档 ──→ 页面图像 ──→ 打印机驱动渲染(GDI PrintToFile) ──→ 打印机语言(PDL)
                                                              │
打印机 ◂────────────── USB 设备路径直接写入 (CreateFile+WriteFile) ◂┘
```

三个环节全部不经过 spooler，因此**与系统打印服务是否损坏无关**。

## 功能特性

- **多格式打印**：
  - PDF（PyMuPDF 渲染，清晰度可调 100–300 DPI）
  - 图片：PNG / JPG / BMP / TIF / GIF / WEBP（自动适配页面）
  - 文本：TXT / MD / LOG / CSV（自动换行、自动分页、自动识别编码 GBK/UTF-8）
  - Office：Word(.doc/.docx) / Excel(.xls/.xlsx)（需本机安装 Microsoft Office 或 WPS，自动调用 COM 转 PDF 后打印）
- **USB 设备自动检测**：SetupAPI 枚举打印机 USB 接口，自动识别；检测不到时回退内置路径
- **界面美观**：现代化渐变 UI、拖拽文件、多文件批量打印、实时日志
- **命令行模式**：支持脚本化调用（`-print`）

## 使用方法

### 图形界面（推荐）

1. 双击 `SamsungPrinter.exe`
2. 点击"添加文件"或直接把文件拖进窗口（支持多选）
3. 按需调整 DPI（默认 150，字密可调 200）
4. 点"开始打印"，稍候取纸

### 命令行

```powershell
# 打印单个文件
SamsungPrinter.exe -print "C:\文档.pdf"

# 打印多个文件，指定清晰度
SamsungPrinter.exe -print "a.pdf" "b.png" "c.txt" -dpi 200

# 保留中间文件（调试用）
SamsungPrinter.exe -print "a.pdf" -keep
```

命令行日志输出到 `%TEMP%\samsung-print-cli.log`。

## 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 7 SP1 / 10 / 11（x64） |
| 运行时 | .NET Framework 4.8（Windows 自带） |
| PDF 渲染 | Python 3 + PyMuPDF（`pip install pymupdf`） |
| Office 打印 | 可选：Microsoft Office 或 WPS（仅打印 Word/Excel 时需要） |

## 从源码构建

无需 Visual Studio，用系统自带的 C# 编译器即可：

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe `
    /out:SamsungPrinter.exe `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:Microsoft.CSharp.dll `
    /reference:System.Core.dll `
    SamsungPrinter.cs
```

## 常见问题

**Q: 提示"无法打开打印机 USB 设备"？**
A: 确认打印机已开机、USB 线已连接；首次运行会自动检测设备路径，若自动检测失败会使用
内置路径。可在 `PrintCore.FallbackDevicePath` 处填写你自己的设备路径
（查看方法：设备管理器 → 打印机 → 详细信息 → 设备实例路径）。

**Q: 为什么不用系统打印队列？**
A: 本工具的定位就是解决 spooler 无法正常工作的场景。队列正常时系统打印即可，本工具
作为补充/备用方案，对正常系统也完全兼容。

**Q: 打印 Word/Excel 提示需要 Office？**
A: 是。Office 文件需要 Word/Excel/WPS 的 COM 组件来转 PDF，未安装时请先另存为 PDF
再打印（或在有 Office 的机器上使用）。

## 开源协议

[MIT License](LICENSE)

## 贡献

欢迎提交 Issue 与 Pull Request：
- 支持更多格式（PPT、RTF、EPUB…）
- 更多语言的界面
- 网络打印（TCP/IP 直连）支持
