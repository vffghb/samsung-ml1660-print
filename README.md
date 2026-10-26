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

## v2.1 升级内容

- **新增在线文字打印**：不用准备文件，点"＋ 文字"即可在软件里直接输入要打印的内容
  - 可选**字体**（自动列出系统已安装字体，常用中文字体排在最前）
  - 可选**字号**（8–72，可自行输入，最大 200）
  - 可选**粗体 / 斜体**；编辑区"所见即所得"，A4 自动换行、自动分页
- **新增应用图标**：程序、窗口、任务栏均显示蓝色"印"字图标
- 界面增加"＋ 文字"入口，整体版本升级为 v2.1

## v2.0 升级内容

- **修复"Python was not found"报错**：自动定位 Python（PATH → `py` 启动器 → 注册表 → 常见安装目录），
  并自动识别、跳过微软商店的假 Python 占位符（App Execution Alias）
- **PDF 渲染重构**：改用临时脚本文件执行，消除命令行转义与中文路径乱码问题；页路径由程序拼接，不再依赖解析 Python 输出
- **修复子进程输出死锁**：stdout/stderr 改为异步读取 + 超时保护
- **打印参数修正**：固定 A4 纸张并保留边距，避免内容贴边被打印机裁掉
- **USB 写入增强**：打开失败自动重试，错误码转为中文可读信息
- **设备枚举增强**：优先匹配三星设备（VID_04E8），且验证设备可打开
- **GUI 稳定性**：窗口关闭后后台线程安全退出，不再抛异常
- **CLI 增加 `-help`**：命令行用法说明

## 功能特性

- **多格式打印**：
  - PDF（PyMuPDF 渲染，清晰度可调 100–300 DPI）
  - 图片：PNG / JPG / BMP / TIF / GIF / WEBP（自动适配页面）
  - 文本：TXT / MD / LOG / CSV（自动换行、自动分页、自动识别编码 GBK/UTF-8）
  - 在线文字：软件内直接输入，自由选择字体、字号、粗体/斜体（无需准备文件）
  - Office：Word(.doc/.docx) / Excel(.xls/.xlsx)（需本机安装 Microsoft Office 或 WPS，自动调用 COM 转 PDF 后打印）
- **一键调色**：专为手机拍照的试卷设计——自动色阶去灰雾、提亮、增强对比度、锐化，暗图秒变清晰黑白打印稿
- **一键修整**：自动检测纸张边缘，透视矫正摆正（拍歪自动拉正）、竖向判断、裁剪桌面背景与白边
- **USB 设备自动检测**：SetupAPI 枚举打印机 USB 接口，自动识别；检测不到时回退内置路径
- **界面美观**：现代化渐变 UI、拖拽文件、多文件批量打印、实时日志
- **命令行模式**：支持脚本化调用（`-print`）

## 使用方法

### 图形界面（推荐）

1. 双击 `SamsungPrinter.exe`
2. 点击"添加文件"或直接把文件拖进窗口（支持多选）
3. **打印手机拍照的试卷时，勾选"智能调色"和"自动修整"**（默认已勾选）——自动提亮并矫正摆正
4. 按需调整 DPI（默认 150，字密可调 200）
5. 点"开始打印"，稍候取纸

### 直接打印软件里输入的文字

1. 点"＋ 文字"
2. 在编辑区输入要打印的内容（作业、通知、便条等，支持多行）
3. 上方选择**字体、字号**，需要时打开**粗体 / 斜体**（编辑区即为排版预览）
4. 点"加入打印列表"，再点"开始打印"即可

> 文字任务会生成临时的 `.sptxt` 文件（保存了字体、字号、内容），打印完成后随临时目录自动清理。

### 命令行

```powershell
# 打印单个文件
SamsungPrinter.exe -print "C:\文档.pdf"

# 打印多个文件，指定清晰度
SamsungPrinter.exe -print "a.pdf" "b.png" "c.txt" -dpi 200

# 打印手机拍的试卷：调色 + 自动修整
SamsungPrinter.exe -print "C:\试卷照片.jpg" -enhance -fix

# 只调色不开修整
SamsungPrinter.exe -print "C:\试卷照片.jpg" -enhance

# 保留中间文件（调试用）
SamsungPrinter.exe -print "a.pdf" -keep
```

命令行日志输出到 `%TEMP%\samsung-print-cli.log`。

> 提示：调色/修整仅对图片生效（PDF 为矢量渲染，无需处理）。需安装 opencv-python：`pip install opencv-python numpy`。

## 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 7 SP1 / 10 / 11（x64） |
| 运行时 | .NET Framework 4.8（Windows 自带） |
| PDF 渲染 | Python 3 + PyMuPDF（`pip install pymupdf`） |
| 图片调色/修整 | Python 3 + OpenCV（`pip install opencv-python numpy`） |

> 软件会自动定位已安装的 Python（系统 PATH / `py` 启动器 / 注册表 / 常见安装目录），
> 无需手动配置。若本机未安装 Python，打印 PDF 或使用图片调色时会给出中文安装指引。
| Office 打印 | 可选：Microsoft Office 或 WPS（仅打印 Word/Excel 时需要） |

## 从源码构建

无需 Visual Studio，用系统自带的 C# 编译器即可：

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe `
    /win32icon:app.ico `
    /out:SamsungPrinter.exe `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:Microsoft.CSharp.dll `
    /reference:System.Core.dll `
    SamsungPrinter.cs
```

> `app.ico` 是程序图标；可用 `make_icon.py`（需 Pillow：`pip install pillow`）重新生成：
> `python make_icon.py`

## 常见问题

**Q: 提示"未找到可用的 Python 3"或 "Python was not found"？**
A: 软件会按顺序自动查找：系统 PATH 中的 Python → `py` 启动器 → 注册表 → 常见安装目录；
Windows 应用商店的"假 Python"占位符会被自动识别并跳过。
若仍找不到，说明本机没有安装真正的 Python 3，请：
1. 到 https://www.python.org/downloads/ 下载安装（**务必勾选 "Add Python to PATH"**）
2. 打开命令提示符运行：`pip install pymupdf opencv-python numpy`
3. 重新打开本软件即可（PDF 打印与图片调色/修整都依赖 Python）

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
