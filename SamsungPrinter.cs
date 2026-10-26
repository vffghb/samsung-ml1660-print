// ============================================================
// 打印助手（开源版 v2.1）- Samsung ML-1660 多格式直通打印工具
// ------------------------------------------------------------
// 解决的问题：
//   本机打印服务(spooler)可能因精简系统或安全机制无法加载
//   打印机驱动 DLL，导致系统打印队列"提交成功但不出纸"。
// 方案：
//   绕开 spooler——
//     1) 把文档渲染成页面图像（PyMuPDF / System.Drawing）
//     2) 用打印机驱动(GDI PrintToFile)生成打印机语言数据(PDL)
//     3) 直接通过 USB 设备路径写入打印机
//   对正常系统同样可用（等价于"打印到文件"+直接发送）。
//
// v2.1 主要改进：
//   * 新增在线文字打印：GUI 内直接输入文字，可选字体/字号/粗体/斜体，
//     自动排版 A4 并分页（生成 .sptxt 临时文件）
//   * 新增应用程序图标（app.ico，蓝色"印"字）
//
// v2.0 主要改进：
//   * Python 智能定位：不再裸调 "python"，自动探测 PATH / py 启动器 /
//     注册表 / 常见安装目录，并识别过滤微软商店的假 Python 占位符
//   * PDF 渲染改为临时脚本文件执行，消除命令行转义与编码问题
//   * 子进程输出改为异步读取，修复 stderr 缓冲导致的死锁
//   * PDF 页路径不再依赖解析 Python stdout（修复中文路径乱码）
//   * 打印固定 A4 纸张并保留边距，避免内容贴边被裁
//   * USB 打开失败自动重试，错误码转为中文可读信息
//   * 设备枚举优先三星 VID，且验证设备可打开
//   * GUI 关闭时后台线程安全退出（不再因窗口关闭抛异常）
//   * CLI 增加 -help 用法说明
//
// 用法：
//   GUI:  双击运行，添加/拖入文件，点"开始打印"
//   CLI:  SamsungPrinter.exe -print <文件...> [-dpi 150] [-keep]
// 开源协议：MIT License
// ============================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SamsungML1660Print
{
    // ============ 入口 ============
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 命令行模式: SamsungPrinter.exe -print <file...> [-dpi 150] [-keep] [-enhance] [-fix]
            if (args.Length >= 1 && (args[0] == "-help" || args[0] == "-h" || args[0] == "--help" || args[0] == "/?"))
            {
                string logPath = Path.Combine(Path.GetTempPath(), "samsung-print-cli.log");
                try
                {
                    File.WriteAllText(logPath,
                        "打印助手 CLI 用法:\r\n" +
                        "  SamsungPrinter.exe -print <文件...> [-dpi 150] [-keep] [-enhance] [-fix]\r\n" +
                        "  -dpi 100~300   清晰度（默认 150）\r\n" +
                        "  -keep          保留临时文件\r\n" +
                        "  -enhance       智能调色（仅图片）\r\n" +
                        "  -fix           自动修整（仅图片）\r\n" +
                        "日志输出: " + logPath + "\r\n", new UTF8Encoding(true));
                }
                catch { }
                Environment.Exit(0);
            }
            if (args.Length >= 2 && args[0] == "-print")
            {
                List<string> files = new List<string>();
                int dpi = 150; bool keep = false, enhance = false, fix = false;
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "-dpi" && i + 1 < args.Length) { int.TryParse(args[i + 1], out dpi); i++; }
                    else if (args[i] == "-keep") keep = true;
                    else if (args[i] == "-enhance") enhance = true;
                    else if (args[i] == "-fix") fix = true;
                    else files.Add(args[i]);
                }
                if (dpi < 100) dpi = 100;
                if (dpi > 300) dpi = 300;
                string logPath = Path.Combine(Path.GetTempPath(), "samsung-print-cli.log");
                try
                {
                    using (StreamWriter sw = new StreamWriter(logPath, false, new UTF8Encoding(true)))
                    {
                        PrintCore.Print(files.ToArray(), dpi, keep, enhance, fix,
                            (m) => { sw.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m); sw.Flush(); });
                    }
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logPath, "错误: " + ex.Message + "\r\n", new UTF8Encoding(true)); } catch { }
                    Environment.Exit(1);
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ============ 现代化主界面（Fluent 风格） ============
    public class MainForm : Form
    {
        private ListView lstFiles;
        private ModernButton btnAdd, btnText, btnClear, btnPrint;
        private NumericUpDown numDpi;
        private ToggleSwitch chkKeep, chkEnhance, chkFix;
        private RichTextBox txtLog;
        private Label lblStatus;
        private StatusDot dotStatus;
        private RoundedPanel pnlFiles, pnlOptions, pnlLog;
        private System.Windows.Forms.Timer tmrStatus;
        private int animTick;
        private volatile bool _closing;

        // 列表绘制字体
        private static readonly Font FHead = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        private static readonly Font FName = new Font("Microsoft YaHei UI", 9F);
        private static readonly Font FPill = new Font("Microsoft YaHei UI", 8F);
        private static readonly Font FSub = new Font("Microsoft YaHei UI", 8.5F);
        private static readonly Font FIcon = new Font("Microsoft YaHei UI", 5.5F, FontStyle.Bold, GraphicsUnit.Point);
        private static readonly Font FTitle = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold);
        private static readonly Font FSubTitle = new Font("Microsoft YaHei UI", 8.5F);
        private static readonly Font FLogo = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);

        public MainForm()
        {
            Text = "打印助手 · 多格式直通打印";
            ClientSize = new Size(820, 672);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 顶部品牌区（白底 + 蓝色 logo 方块）----
            Panel header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.White };
            header.Paint += (s, e) => {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle logo = new Rectangle(20, 12, 40, 40);
                using (GraphicsPath lp = Gfx.RoundedRect(logo, 10))
                using (SolidBrush lb = new SolidBrush(Ui.Primary))
                    g.FillPath(lb, lp);
                TextRenderer.DrawText(g, "印", FLogo, logo, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "打印助手", FTitle,
                    new Rectangle(70, 9, 220, 30), Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "多格式直通打印 · 绕开系统打印队列 · v2.1", FSubTitle,
                    new Rectangle(70, 35, 460, 22), Ui.TextSec,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                using (Pen p = new Pen(Ui.Border))
                    g.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            Controls.Add(header);

            // ---- 文件列表卡片 ----
            pnlFiles = new RoundedPanel { Bounds = new Rectangle(16, 80, 788, 270) };
            Label lblFilesTitle = new Label { Text = "待打印文件", Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Ui.Text, BackColor = Color.White, AutoSize = true, Location = new Point(18, 12) };
            Label lblFilesHint = new Label { Text = "支持拖拽文件到窗口", Font = FSubTitle,
                ForeColor = Ui.TextMuted, BackColor = Color.White, AutoSize = true, Location = new Point(124, 17) };
            btnAdd = new ModernButton { Style = BtnStyle.Secondary, Text = "＋ 添加文件", BackColor = Color.White, Bounds = new Rectangle(648, 9, 124, 30) };
            btnText = new ModernButton { Style = BtnStyle.Secondary, Text = "＋ 文字", BackColor = Color.White, Bounds = new Rectangle(464, 9, 96, 30) };
            btnClear = new ModernButton { Style = BtnStyle.Ghost, Text = "清空列表", BackColor = Color.White, Bounds = new Rectangle(568, 9, 72, 30) };
            lstFiles = new ListView
            {
                Bounds = new Rectangle(14, 46, 760, 210),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                MultiSelect = true,
                OwnerDraw = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                HideSelection = false
            };
            // 用 1×30 的 ImageList 把行高撑到 32
            ImageList il = new ImageList { ImageSize = new Size(1, 30), ColorDepth = ColorDepth.Depth32Bit };
            lstFiles.SmallImageList = il;
            lstFiles.Columns.Add("文件", 470);
            lstFiles.Columns.Add("类型", 105);
            lstFiles.Columns.Add("DPI", 70);
            lstFiles.Columns.Add("状态", 115);
            lstFiles.DrawColumnHeader += LstFiles_DrawColumnHeader;
            lstFiles.DrawItem += LstFiles_DrawItem;
            lstFiles.DrawSubItem += LstFiles_DrawSubItem;
            pnlFiles.Controls.Add(lblFilesTitle);
            pnlFiles.Controls.Add(lblFilesHint);
            pnlFiles.Controls.Add(btnAdd);
            pnlFiles.Controls.Add(btnText);
            pnlFiles.Controls.Add(btnClear);
            pnlFiles.Controls.Add(lstFiles);
            Controls.Add(pnlFiles);

            // ---- 选项卡片 ----
            pnlOptions = new RoundedPanel { Bounds = new Rectangle(16, 358, 788, 80) };
            dotStatus = new StatusDot { Bounds = new Rectangle(18, 16, 12, 12) };
            dotStatus.SetStatus(Ui.Warning);
            lblStatus = new Label { Text = "设备: 检测中…", AutoSize = true, ForeColor = Ui.Text,
                BackColor = Color.White, Location = new Point(36, 12) };
            Panel divider = new Panel { BackColor = Ui.Border, Bounds = new Rectangle(18, 40, 752, 1) };
            Label lblDpi = new Label { Text = "DPI", AutoSize = true, ForeColor = Ui.TextSec,
                BackColor = Color.White, Location = new Point(18, 52) };
            numDpi = new NumericUpDown { Location = new Point(52, 48), Width = 66, Height = 26, Minimum = 100, Maximum = 300, Value = 150 };
            chkKeep = new ToggleSwitch { Text = "保留临时文件", Bounds = new Rectangle(140, 46, 190, 28) };
            chkEnhance = new ToggleSwitch { Text = "智能调色", Checked = true, Bounds = new Rectangle(340, 46, 190, 28) };
            chkFix = new ToggleSwitch { Text = "自动修整", Checked = true, Bounds = new Rectangle(540, 46, 190, 28) };
            pnlOptions.Controls.Add(dotStatus);
            pnlOptions.Controls.Add(lblStatus);
            pnlOptions.Controls.Add(divider);
            pnlOptions.Controls.Add(lblDpi);
            pnlOptions.Controls.Add(numDpi);
            pnlOptions.Controls.Add(chkKeep);
            pnlOptions.Controls.Add(chkEnhance);
            pnlOptions.Controls.Add(chkFix);
            Controls.Add(pnlOptions);

            // ---- 日志卡片 ----
            pnlLog = new RoundedPanel { Bounds = new Rectangle(16, 446, 788, 128) };
            Label lblLogTitle = new Label { Text = "运行日志", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(71, 85, 105), BackColor = Color.White, AutoSize = true, Location = new Point(18, 11) };
            txtLog = new RichTextBox
            {
                Bounds = new Rectangle(14, 34, 760, 80),
                ReadOnly = true,
                BackColor = Ui.LogBg,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5F),
                ForeColor = Ui.LogText
            };
            pnlLog.Controls.Add(lblLogTitle);
            pnlLog.Controls.Add(txtLog);
            Controls.Add(pnlLog);

            // ---- 主按钮 ----
            btnPrint = new ModernButton { Style = BtnStyle.Primary, Text = "开 始 打 印",
                BackColor = Ui.Bg, Bounds = new Rectangle(16, 584, 788, 46), Font = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold) };
            Controls.Add(btnPrint);

            // 事件
            btnAdd.Click += (s, e) => AddFiles();
            btnText.Click += (s, e) => NewText();
            btnClear.Click += (s, e) => { lstFiles.Items.Clear(); Log("已清空列表"); };
            btnPrint.Click += (s, e) => StartPrint();

            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null) foreach (string f in files) AddFileToList(f);
            };

            FormClosing += (s, e) => _closing = true;

            // 打印中状态动画
            tmrStatus = new System.Windows.Forms.Timer { Interval = 400 };
            tmrStatus.Tick += (s, e) => {
                animTick++;
                lblStatus.Text = "设备: " + PrintCore.CurrentDeviceLabel + "    状态: 打印中" + new string('.', animTick % 4);
            };

            // 初始检测设备（后台线程）
            Thread t = new Thread(() => {
                string label = PrintCore.DetectDevice();
                bool fallback = label.IndexOf("内置") >= 0;
                SafeInvoke(() => SetDeviceStatus("设备: " + label, fallback ? Ui.Warning : Ui.Success));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AddFiles()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Multiselect = true;
                dlg.Filter = "支持的文件|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.webp;*.txt;*.md;*.log;*.csv;*.doc;*.docx;*.xls;*.xlsx|PDF|*.pdf|图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.webp|文本|*.txt;*.md;*.log;*.csv|Office|*.doc;*.docx;*.xls;*.xlsx|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                    foreach (string f in dlg.FileNames) AddFileToList(f);
            }
        }

        private void NewText()
        {
            using (TextEditorForm dlg = new TextEditorForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    AddFileToList(dlg.OutFile);
                    Log("已添加文字任务: " + Path.GetFileName(dlg.OutFile));
                }
            }
        }

        private void AddFileToList(string path)
        {
            if (!File.Exists(path)) { Log("文件不存在: " + path); return; }
            if (lstFiles.Items.Cast<ListViewItem>().Any(i => (string)i.Tag == path)) return;
            ListViewItem it = new ListViewItem(Path.GetFileName(path));
            it.SubItems.Add(PrintCore.GetFileKindLabel(path));
            it.SubItems.Add(numDpi.Value.ToString());
            it.SubItems.Add("就绪");
            it.Tag = path;
            lstFiles.Items.Add(it);
        }

        // 线程安全地更新 UI（窗口关闭后不再 Invoke，避免抛异常）
        private void SafeInvoke(Action a)
        {
            if (_closing || IsDisposed || Disposing || !IsHandleCreated) return;
            try { Invoke((MethodInvoker)(() => a())); } catch { }
        }

        private void SetDeviceStatus(string text, Color dotColor)
        {
            if (lblStatus != null) lblStatus.Text = text;
            if (dotStatus != null) dotStatus.SetStatus(dotColor);
        }

        private void StartPrint()
        {
            if (lstFiles.Items.Count == 0) { Log("请先添加要打印的文件"); return; }
            List<string> files = lstFiles.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
            int dpi = (int)numDpi.Value;
            foreach (ListViewItem i in lstFiles.Items) { i.SubItems[2].Text = dpi.ToString(); i.SubItems[3].Text = "等待…"; }
            btnPrint.Enabled = false;
            dotStatus.SetStatus(Ui.Warning);
            tmrStatus.Start();
            Thread t = new Thread(() => {
                try
                {
                    PrintCore.Print(files.ToArray(), (int)numDpi.Value, chkKeep.Checked,
                        chkEnhance.Checked, chkFix.Checked,
                        (m) => SafeInvoke(() => Log(m)));
                    SafeInvoke(() => {
                        tmrStatus.Stop();
                        SetDeviceStatus("设备: " + PrintCore.CurrentDeviceLabel + "    状态: 打印完成", Ui.Success);
                        foreach (ListViewItem i in lstFiles.Items) i.SubItems[3].Text = "完成";
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(() => {
                        tmrStatus.Stop();
                        Log("错误: " + ex.Message);
                        SetDeviceStatus("设备: " + PrintCore.CurrentDeviceLabel + "    状态: 出错", Ui.Danger);
                    });
                }
                finally
                {
                    SafeInvoke(() => btnPrint.Enabled = true);
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void Log(string msg)
        {
            if (txtLog.IsDisposed) return;
            Color c = Ui.LogText;
            if (msg.IndexOf("错误") >= 0) c = Color.FromArgb(248, 113, 113);
            else if (msg.IndexOf("完成") >= 0) c = Color.FromArgb(74, 222, 128);
            else if (msg.StartsWith("===")) c = Color.FromArgb(125, 211, 252);
            else if (msg.IndexOf("→") >= 0 || msg.IndexOf("已写入") >= 0 || msg.IndexOf("渲染完成") >= 0) c = Color.FromArgb(167, 243, 208);
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = c;
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + msg + "\r\n");
            txtLog.SelectionColor = Ui.LogText;
            txtLog.ScrollToCaret();
        }

        // ---- 文件列表自绘（表头）----
        private void LstFiles_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(248, 250, 252)))
                e.Graphics.FillRectangle(b, e.Bounds);
            Rectangle r = e.Bounds;
            r.X += 10; r.Width -= 10;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, FHead, r, Ui.TextSec,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (Pen p = new Pen(Ui.Border))
                e.Graphics.DrawLine(p, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        // ---- 文件列表自绘（整行背景）----
        private void LstFiles_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            bool sel = e.Item.Selected;
            using (SolidBrush b = new SolidBrush(sel ? Ui.PrimarySoft : Color.White))
                e.Graphics.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Ui.BorderSoft))
                e.Graphics.DrawLine(p, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        // ---- 文件列表自绘（各列内容）----
        private void LstFiles_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            Graphics g = e.Graphics;
            string path = e.Item.Tag as string;
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            Color dark, soft;
            KindColors(ext, out dark, out soft);
            Rectangle b = e.Bounds;
            bool sel = e.Item.Selected;
            Color rowBg = sel ? Ui.PrimarySoft : Color.White;

            if (e.ColumnIndex == 0)
            {
                // 文件类型图标
                int ix = b.X + 12, iy = b.Y + (b.Height - 26) / 2;
                DrawFileIcon(g, new Rectangle(ix, iy, 22, 26), KindAbbr(ext), dark, soft);
                // 文件名
                Rectangle tr = new Rectangle(b.X + 44, b.Y, b.Width - 52, b.Height);
                TextRenderer.DrawText(g, e.Item.Text, FName, tr, Ui.Text, rowBg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else if (e.ColumnIndex == 1)
            {
                // 类型徽章
                string kind = e.SubItem.Text;
                Size ts = TextRenderer.MeasureText(kind, FPill);
                int pw = ts.Width + 18, ph = 18;
                Rectangle pr = new Rectangle(b.X + (b.Width - pw) / 2, b.Y + (b.Height - ph) / 2, pw, ph);
                using (GraphicsPath pp = Gfx.RoundedRect(pr, 9))
                using (SolidBrush sb = new SolidBrush(soft))
                    g.FillPath(sb, pp);
                TextRenderer.DrawText(g, kind, FPill, pr, dark, soft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else if (e.ColumnIndex == 2)
            {
                TextRenderer.DrawText(g, e.SubItem.Text, FSub, b, Ui.TextSec, rowBg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else if (e.ColumnIndex == 3)
            {
                // 状态指示灯 + 文字
                string st = e.SubItem.Text;
                Color c;
                if (st.IndexOf("完成") >= 0) c = Ui.Success;
                else if (st.IndexOf("错") >= 0) c = Ui.Danger;
                else if (st.IndexOf("等待") >= 0) c = Ui.Warning;
                else c = Ui.TextMuted;
                int d = 8;
                int cx = b.X + 14;
                int cy = b.Y + (b.Height - d) / 2;
                using (SolidBrush cb = new SolidBrush(c))
                    g.FillEllipse(cb, cx, cy, d, d);
                Rectangle tr = new Rectangle(b.X + 28, b.Y, b.Width - 30, b.Height);
                TextRenderer.DrawText(g, st, FSub, tr, c, rowBg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private static void KindColors(string ext, out Color dark, out Color soft)
        {
            switch (ext)
            {
                case ".pdf": dark = Color.FromArgb(220, 38, 38); soft = Color.FromArgb(254, 226, 226); break;
                case ".doc": case ".docx": dark = Color.FromArgb(37, 99, 235); soft = Color.FromArgb(219, 234, 254); break;
                case ".xls": case ".xlsx": dark = Color.FromArgb(21, 128, 61); soft = Color.FromArgb(220, 252, 231); break;
                case ".txt": case ".md": case ".log": case ".csv": dark = Color.FromArgb(100, 116, 139); soft = Color.FromArgb(241, 245, 249); break;
                case ".sptxt": dark = Color.FromArgb(37, 99, 235); soft = Color.FromArgb(219, 234, 254); break;
                case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".tif": case ".tiff": case ".gif": case ".webp":
                    dark = Color.FromArgb(22, 163, 74); soft = Color.FromArgb(220, 252, 231); break;
                default: dark = Color.FromArgb(100, 116, 139); soft = Color.FromArgb(241, 245, 249); break;
            }
        }

        private static string KindAbbr(string ext)
        {
            switch (ext)
            {
                case ".pdf": return "PDF";
                case ".doc": case ".docx": return "DOC";
                case ".xls": case ".xlsx": return "XLS";
                case ".txt": case ".md": case ".log": case ".csv": return "TXT";
                case ".sptxt": return "字";
                case ".jpeg": return "JPG";
                case ".tiff": return "TIF";
                default:
                    string s = ext.TrimStart('.').ToUpperInvariant();
                    if (s.Length > 3) s = s.Substring(0, 3);
                    return s;
            }
        }

        private static void DrawFileIcon(Graphics g, Rectangle r, string abbr, Color dark, Color soft)
        {
            int fold = 7;
            Point[] pts = new Point[] {
                new Point(r.X, r.Y),
                new Point(r.Right - fold, r.Y),
                new Point(r.Right, r.Y + fold),
                new Point(r.Right, r.Bottom),
                new Point(r.X, r.Bottom)
            };
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddPolygon(pts);
                p.CloseFigure();
                using (SolidBrush sb = new SolidBrush(soft))
                    g.FillPath(sb, p);
                using (Pen pen = new Pen(Color.FromArgb(90, dark)))
                    g.DrawPath(pen, p);
            }
            // 折角
            Point[] tri = new Point[] {
                new Point(r.Right - fold, r.Y),
                new Point(r.Right - fold, r.Y + fold),
                new Point(r.Right, r.Y + fold)
            };
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(150, dark)))
                g.FillPolygon(tb, tri);
            // 类型缩写
            Rectangle tr = new Rectangle(r.X, r.Y + 7, r.Width + 1, r.Height - 9);
            TextRenderer.DrawText(g, abbr, FIcon, tr, dark, soft,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // ============ 新建文字打印对话框（在线编辑 + 字体/字号） ============
    public class TextEditorForm : Form
    {
        public string OutFile = null;
        private ComboBox cboFont, cboSize;
        private ToggleSwitch chkBold, chkItalic;
        private TextBox txtContent;

        public TextEditorForm()
        {
            Text = "新建文字打印";
            ClientSize = new Size(640, 548);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 工具栏卡片 ----
            RoundedPanel pnlTool = new RoundedPanel { Bounds = new Rectangle(12, 12, 616, 66) };
            Label lblFont = new Label { Text = "字体", ForeColor = Ui.Text, BackColor = Color.White, AutoSize = true, Location = new Point(18, 22) };
            cboFont = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(56, 18, 210, 26), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            Label lblSize = new Label { Text = "字号", ForeColor = Ui.Text, BackColor = Color.White, AutoSize = true, Location = new Point(282, 22) };
            cboSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Bounds = new Rectangle(322, 18, 58, 26), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            chkBold = new ToggleSwitch { Text = "粗体", Bounds = new Rectangle(400, 18, 92, 30) };
            chkItalic = new ToggleSwitch { Text = "斜体", Bounds = new Rectangle(500, 18, 92, 30) };

            // 填充字体列表（常用中文字体排在最前）
            List<string> fonts = new List<string>();
            string[] preferred = new[] { "Microsoft YaHei", "微软雅黑", "Microsoft YaHei UI", "SimSun", "宋体", "SimHei", "黑体", "KaiTi", "楷体", "FangSong", "仿宋", "DengXian", "等线", "YouYuan", "幼圆", "LiSu", "隶书", "Arial", "Times New Roman", "Consolas", "Courier New" };
            HashSet<string> installed = new HashSet<string>();
            try
            {
                System.Drawing.Text.InstalledFontCollection ifc = new System.Drawing.Text.InstalledFontCollection();
                foreach (System.Drawing.FontFamily ff in ifc.Families) installed.Add(ff.Name);
            }
            catch { }
            foreach (string fn in preferred) if (installed.Contains(fn)) fonts.Add(fn);
            List<string> rest = new List<string>(installed);
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string fn in rest) if (!fonts.Contains(fn)) fonts.Add(fn);
            foreach (string fn in fonts) cboFont.Items.Add(fn);
            int def = cboFont.Items.IndexOf("Microsoft YaHei");
            if (def < 0) def = cboFont.Items.IndexOf("微软雅黑");
            if (def < 0 && cboFont.Items.Count > 0) def = 0;
            if (def >= 0) cboFont.SelectedIndex = def;

            // 字号
            string[] sizes = new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "72" };
            foreach (string s in sizes) cboSize.Items.Add(s);
            cboSize.Text = "14";

            pnlTool.Controls.Add(lblFont);
            pnlTool.Controls.Add(cboFont);
            pnlTool.Controls.Add(lblSize);
            pnlTool.Controls.Add(cboSize);
            pnlTool.Controls.Add(chkBold);
            pnlTool.Controls.Add(chkItalic);
            Controls.Add(pnlTool);

            // ---- 编辑区卡片 ----
            RoundedPanel pnlEdit = new RoundedPanel { Bounds = new Rectangle(12, 88, 616, 388) };
            txtContent = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                WordWrap = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = Ui.Text,
                Bounds = new Rectangle(16, 16, 584, 356),
                Font = new Font("Microsoft YaHei UI", 14F)
            };
            pnlEdit.Controls.Add(txtContent);
            // 输入提示（聚焦或有内容时隐藏）
            Label lblPlaceholder = new Label
            {
                Text = "在这里输入要打印的内容…\r\n支持多行，将按所选字体、字号自动排版到 A4 纸",
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10F),
                Bounds = new Rectangle(24, 24, 540, 44),
                TextAlign = ContentAlignment.TopLeft
            };
            pnlEdit.Controls.Add(lblPlaceholder);
            lblPlaceholder.BringToFront();
            lblPlaceholder.Click += (s, e) => txtContent.Focus();
            txtContent.GotFocus += (s, e) => lblPlaceholder.Visible = false;
            txtContent.LostFocus += (s, e) => { if (txtContent.Text.Length == 0) lblPlaceholder.Visible = true; };
            Controls.Add(pnlEdit);

            // ---- 底部操作 ----
            Label lblHint = new Label { Text = "按所选字体、字号排版，A4 自动分页", ForeColor = Ui.TextMuted, BackColor = Ui.Bg, AutoSize = true, Location = new Point(14, 506), Font = new Font("Microsoft YaHei UI", 8.5F) };
            Controls.Add(lblHint);
            ModernButton btnCancel = new ModernButton { Style = BtnStyle.Ghost, Text = "取消", BackColor = Ui.Bg, Bounds = new Rectangle(380, 494, 104, 40) };
            ModernButton btnOk = new ModernButton { Style = BtnStyle.Primary, Text = "加入打印列表", BackColor = Ui.Bg, Bounds = new Rectangle(492, 494, 136, 40) };
            Controls.Add(btnCancel);
            Controls.Add(btnOk);

            cboFont.SelectedIndexChanged += (s, e) => UpdateContentFont();
            cboSize.TextChanged += (s, e) => UpdateContentFont();
            cboSize.SelectedIndexChanged += (s, e) => UpdateContentFont();
            chkBold.CheckedChanged += (s, e) => UpdateContentFont();
            chkItalic.CheckedChanged += (s, e) => UpdateContentFont();
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnOk.Click += (s, e) => SaveAndClose();
            UpdateContentFont();
            txtContent.Focus();
        }

        private void UpdateContentFont()
        {
            string family = cboFont.SelectedItem as string;
            if (string.IsNullOrEmpty(family)) family = "Microsoft YaHei UI";
            float size = 14F;
            float parsed;
            if (float.TryParse(cboSize.Text.Trim(), out parsed))
            {
                if (parsed < 6) parsed = 6;
                if (parsed > 200) parsed = 200;
                size = parsed;
            }
            FontStyle st = FontStyle.Regular;
            if (chkBold.Checked) st |= FontStyle.Bold;
            if (chkItalic.Checked) st |= FontStyle.Italic;
            // 该字体若不支持所选粗/斜体，自动降级为可用样式
            Font nf = null;
            try
            {
                using (FontFamily ff = new FontFamily(family))
                {
                    if ((st & FontStyle.Bold) != 0 && !ff.IsStyleAvailable(FontStyle.Bold)) st &= ~FontStyle.Bold;
                    if ((st & FontStyle.Italic) != 0 && !ff.IsStyleAvailable(FontStyle.Italic)) st &= ~FontStyle.Italic;
                    if (st == FontStyle.Regular && !ff.IsStyleAvailable(FontStyle.Regular))
                    {
                        if (ff.IsStyleAvailable(FontStyle.Bold)) st = FontStyle.Bold;
                        else if (ff.IsStyleAvailable(FontStyle.Italic)) st = FontStyle.Italic;
                    }
                }
                nf = new Font(family, size, st);
            }
            catch
            {
                try { nf = new Font("Microsoft YaHei UI", size, st); }
                catch { nf = new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular); }
            }
            // 不能 Dispose 旧字体：当新旧字体等价时，控件内部仍引用旧对象
            txtContent.Font = nf;
        }

        private void SaveAndClose()
        {
            string content = txtContent.Text;
            if (content.Trim().Length == 0)
            {
                MessageBox.Show(this, "请先输入要打印的文字内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string family = cboFont.SelectedItem as string;
            if (string.IsNullOrEmpty(family)) family = "Microsoft YaHei";
            string size = cboSize.Text.Trim();
            string bold = chkBold.Checked ? "1" : "0";
            string italic = chkItalic.Checked ? "1" : "0";
            string header = "#SPTEXT#fontFamily=" + family + ";size=" + size + ";bold=" + bold + ";italic=" + italic;
            string dir = Path.Combine(Path.GetTempPath(), "samsung-print", "texts");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "文字_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".sptxt");
            File.WriteAllText(file, header + "\r\n" + content, new UTF8Encoding(true));
            OutFile = file;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ============ 配色（Fluent 风格） ============
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(241, 245, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color BorderSoft = Color.FromArgb(241, 245, 249);
        public static readonly Color Text = Color.FromArgb(15, 23, 42);
        public static readonly Color TextSec = Color.FromArgb(100, 116, 139);
        public static readonly Color TextMuted = Color.FromArgb(148, 163, 184);
        public static readonly Color Primary = Color.FromArgb(37, 99, 235);
        public static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
        public static readonly Color PrimaryPress = Color.FromArgb(30, 64, 175);
        public static readonly Color PrimarySoft = Color.FromArgb(239, 246, 255);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color LogBg = Color.FromArgb(30, 41, 59);
        public static readonly Color LogText = Color.FromArgb(203, 213, 225);
    }

    // 绘图辅助
    internal static class Gfx
    {
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // 圆角卡片面板
    public class RoundedPanel : Panel
    {
        public int Radius = 10;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Gfx.RoundedRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(Ui.Card))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Ui.Border))
                    g.DrawPath(pen, p);
            }
        }
    }

    // 现代按钮（主按钮 / 次按钮 / 幽灵按钮）
    public enum BtnStyle { Primary, Secondary, Ghost }

    public class ModernButton : Button
    {
        public BtnStyle Style = BtnStyle.Primary;
        private bool hover, down;

        public ModernButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.BorderColor = Color.FromArgb(0, 0, 0, 0);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        }

        // 不绘制系统焦点黑框（自绘按钮已自行呈现状态）
        protected override bool ShowFocusCues { get { return false; } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { down = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { down = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 先用父背景色覆盖整个矩形，抹掉系统默认绘制的边框/残影
            using (SolidBrush pbg = new SolidBrush(BackColor))
                g.FillRectangle(pbg, ClientRectangle);
            Rectangle rc = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg, fg, border;
            if (!Enabled)
            {
                bg = Color.FromArgb(226, 232, 240); fg = Color.FromArgb(148, 163, 184); border = Color.FromArgb(226, 232, 240);
            }
            else if (Style == BtnStyle.Primary)
            {
                fg = Color.White; border = Color.Empty;
                if (down) bg = Ui.PrimaryPress;
                else if (hover) bg = Ui.PrimaryHover;
                else bg = Ui.Primary;
            }
            else if (Style == BtnStyle.Secondary)
            {
                fg = Ui.Primary; border = Color.FromArgb(203, 213, 225);
                if (down) bg = Color.FromArgb(219, 234, 254);
                else if (hover) bg = Ui.PrimarySoft;
                else bg = Color.White;
            }
            else
            {
                fg = Ui.TextSec; border = Ui.Border;
                if (down) bg = Color.FromArgb(241, 245, 249);
                else if (hover) bg = Color.FromArgb(248, 250, 252);
                else bg = Color.White;
            }
            using (GraphicsPath p = Gfx.RoundedRect(rc, 8))
            {
                using (SolidBrush b = new SolidBrush(bg))
                    g.FillPath(b, p);
                if (border != Color.Empty)
                    using (Pen pen = new Pen(border))
                        g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Text, Font, rc, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // 开关（Toggle Switch）
    public class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 强制用白色填充整个客户区，避免兄弟控件残影
            using (SolidBrush bg = new SolidBrush(Color.White))
                g.FillRectangle(bg, ClientRectangle);
            int trackW = 38, trackH = 22;
            Size tsz = TextRenderer.MeasureText(g, Text, Font);
            int trackX = tsz.Width + 10;
            int trackY = (Height - trackH) / 2;
            Rectangle track = new Rectangle(trackX, trackY, trackW, trackH);
            bool on = Checked;
            Color tc = on ? Ui.Primary : Color.FromArgb(203, 213, 225);
            using (GraphicsPath tp = Gfx.RoundedRect(track, trackH / 2))
            using (SolidBrush b = new SolidBrush(tc))
                g.FillPath(b, tp);
            int knob = trackH - 6;
            int kx = on ? track.Right - knob - 3 : track.X + 3;
            int ky = track.Y + 3;
            using (SolidBrush kb = new SolidBrush(Color.White))
                g.FillEllipse(kb, kx, ky, knob, knob);
            Rectangle tr = new Rectangle(0, 0, tsz.Width, Height);
            TextRenderer.DrawText(g, Text, Font, tr, Ui.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }
    }

    // 状态指示灯
    public class StatusDot : Control
    {
        private Color _color;

        public StatusDot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _color = Ui.TextMuted;
            BackColor = Color.White;
        }

        public void SetStatus(Color c) { _color = c; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.White))
                g.FillRectangle(bg, ClientRectangle);
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(45, _color)))
                g.FillEllipse(halo, 0, 0, Width - 1, Height - 1);
            int pad = 2;
            using (SolidBrush b = new SolidBrush(_color))
                g.FillEllipse(b, pad, pad, Width - 1 - pad * 2, Height - 1 - pad * 2);
        }
    }

    // ============ 打印核心（多格式） ============
    public static class PrintCore
    {
        // 本机已验证的设备路径（自动检测失败时的回退路径）
        public const string FallbackDevicePath =
            @"\\?\USB#VID_04E8&PID_3301#Z4V7BKBZ700826Z.#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";

        private static string _devicePath;
        public static string CurrentDeviceLabel { get; private set; }
        public static string DeviceDetectDetail { get; private set; }

        /// <summary>自动检测 USB 打印机接口；失败回退内置路径</summary>
        public static string DetectDevice()
        {
            try
            {
                string p = UsbEnum.FindPrinterInterface();
                if (!string.IsNullOrEmpty(p))
                {
                    if (CanOpen(p))
                    {
                        _devicePath = p;
                        CurrentDeviceLabel = "自动检测 (已找到打印机 USB 接口)";
                        DeviceDetectDetail = "自动检测成功: " + p;
                        return CurrentDeviceLabel;
                    }
                    // 找到接口但打不开：保留作为报错线索
                    DeviceDetectDetail = "检测到接口但打开失败: " + p;
                }
                else
                {
                    DeviceDetectDetail = "未枚举到打印机 USB 数据接口";
                }
            }
            catch (Exception ex)
            {
                DeviceDetectDetail = "自动检测异常: " + ex.Message;
            }
            // 回退：尝试打开内置路径
            if (CanOpen(FallbackDevicePath))
            {
                _devicePath = FallbackDevicePath;
                CurrentDeviceLabel = "内置设备路径（三星 ML-1660）";
                return CurrentDeviceLabel;
            }
            _devicePath = FallbackDevicePath;
            CurrentDeviceLabel = "内置设备路径（三星 ML-1660）";
            return CurrentDeviceLabel;
        }

        private static bool CanOpen(string path)
        {
            IntPtr h = NativeMethods.CreateFile(path, 0x40000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            NativeMethods.CloseHandle(h);
            return true;
        }

        public static string GetDevicePath()
        {
            if (string.IsNullOrEmpty(_devicePath)) DetectDevice();
            return _devicePath;
        }

        public static string GetFileKindLabel(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".pdf": return "PDF 文档";
                case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".tif": case ".tiff": case ".gif": case ".webp": return "图片";
                case ".txt": case ".md": case ".log": case ".csv": return "文本";
                case ".sptxt": return "文字（自定义）";
                case ".doc": case ".docx": return "Word";
                case ".xls": case ".xlsx": return "Excel";
                default: return ext.TrimStart('.') + " 文件";
            }
        }

        // ================= Python 智能定位 =================
        // 系统 PATH 里的 "python" 可能是微软商店的假占位符
        // （App Execution Alias，运行后提示 "Python was not found..."）。
        // 这里按优先级探测：PATH 真 Python → py 启动器 → 注册表 → 常见安装目录。
        public class PythonEnv
        {
            public string Exe;        // 可执行文件路径
            public string ArgsPrefix; // 如 py 启动器需要 "-3 "
            public string Source;     // 来源说明
        }

        private static class PythonLocator
        {
            private static PythonEnv _cached;
            private static string _cachedReason;

            public static PythonEnv Find()
            {
                if (_cached != null) return _cached;
                List<string> tried = new List<string>();

                // 1) PATH 中的 python（验证不是商店占位符）
                foreach (string cand in new[] { "python.exe", "python", "python3.exe", "python3" })
                {
                    string p = Which(cand);
                    if (string.IsNullOrEmpty(p)) continue;
                    tried.Add(p);
                    if (IsRealPython(p, null))
                    {
                        _cached = new PythonEnv { Exe = p, ArgsPrefix = "", Source = "系统 PATH 中的 " + p };
                        return _cached;
                    }
                }

                // 2) py 启动器（Windows Python 安装器自带）
                string py = Which("py.exe");
                if (!string.IsNullOrEmpty(py))
                {
                    tried.Add(py);
                    if (IsRealPython(py, "-3"))
                    {
                        _cached = new PythonEnv { Exe = py, ArgsPrefix = "-3 ", Source = "py 启动器 (" + py + ")" };
                        return _cached;
                    }
                }

                // 3) 注册表 SOFTWARE\Python\PythonCore\<版本>\InstallPath
                string regExe = FindFromRegistry();
                if (!string.IsNullOrEmpty(regExe))
                {
                    tried.Add(regExe);
                    if (IsRealPython(regExe, null))
                    {
                        _cached = new PythonEnv { Exe = regExe, ArgsPrefix = "", Source = "注册表中的 " + regExe };
                        return _cached;
                    }
                }

                // 4) 常见安装目录
                string dirExe = FindFromCommonDirs();
                if (!string.IsNullOrEmpty(dirExe))
                {
                    tried.Add(dirExe);
                    if (IsRealPython(dirExe, null))
                    {
                        _cached = new PythonEnv { Exe = dirExe, ArgsPrefix = "", Source = "安装目录中的 " + dirExe };
                        return _cached;
                    }
                }

                _cachedReason = tried.Count == 0
                    ? "系统中未找到任何 Python"
                    : "已尝试以下位置但均不可用:\r\n  " + string.Join("\r\n  ", tried);
                _cached = null;
                return null;
            }

            public static string LastReason
            {
                get { return _cachedReason ?? "未找到可用的 Python 3"; }
            }

            private static string Which(string cmd)
            {
                if (File.Exists(cmd)) return Path.GetFullPath(cmd);
                string pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathEnv)) return null;
                foreach (string dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string full = Path.Combine(dir.Trim(), cmd);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
                return null;
            }

            // 验证 exe 真的是可运行的 Python（排除商店占位符）
            private static bool IsRealPython(string exe, string prefixArgs)
            {
                try
                {
                    string args = (prefixArgs ?? "") + "-c \"import sys;print(sys.version_info[0],sys.version.split()[0])\"";
                    RunResult r = RunProcess(exe, args, 8000);
                    if (r.TimedOut) return false;
                    if (r.ExitCode != 0) return false;
                    string t = (r.StdOut ?? "").Trim();
                    // 商店占位符会输出 "Python was not found..." / "Microsoft Store"
                    if (t.Length == 0) return false;
                    if (t.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (t.IndexOf("Microsoft Store", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (t.IndexOf("alias", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    return t.StartsWith("3", StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            }

            private static string FindFromRegistry()
            {
                try
                {
                    foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                        {
                            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                            {
                                if (baseKey == null) continue;
                                using (RegistryKey core = baseKey.OpenSubKey(@"SOFTWARE\Python\PythonCore"))
                                {
                                    if (core == null) continue;
                                    foreach (string sub in core.GetSubKeyNames())
                                    {
                                        using (RegistryKey inst = core.OpenSubKey(sub + @"\InstallPath"))
                                        {
                                            if (inst == null) continue;
                                            string dir = (inst.GetValue("") ?? "").ToString();
                                            if (!string.IsNullOrEmpty(dir))
                                            {
                                                string exe = Path.Combine(dir, "python.exe");
                                                if (File.Exists(exe)) return exe;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                return null;
            }

            private static string FindFromCommonDirs()
            {
                try
                {
                    List<string> roots = new List<string>();
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    roots.Add(Path.Combine(local, "Programs", "Python"));
                    roots.Add("C:\\Python");
                    roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"));
                    roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Python"));
                    foreach (string root in roots)
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (string d in Directory.GetDirectories(root, "Python*"))
                        {
                            string exe = Path.Combine(d, "python.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch { }
                return null;
            }
        }

        // ================= 子进程运行封装 =================
        public class RunResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
            public bool TimedOut;
            public bool LaunchFailed;
            public string LaunchError;
        }

        // 统一运行外部程序：异步读两个输出流（防 stderr 缓冲死锁）+ 超时
        private static RunResult RunProcess(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    Task<string> tOut = p.StandardOutput.ReadToEndAsync();
                    Task<string> tErr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        string so = "", se = "";
                        try { so = tOut.Result; } catch { }
                        try { se = tErr.Result; } catch { }
                        return new RunResult { TimedOut = true, StdOut = so, StdErr = se };
                    }
                    return new RunResult
                    {
                        ExitCode = p.ExitCode,
                        StdOut = tOut.Result,
                        StdErr = tErr.Result
                    };
                }
            }
            catch (Exception ex)
            {
                return new RunResult { LaunchFailed = true, LaunchError = ex.Message };
            }
        }

        // 把参数安全拼进命令行（含空格/引号转义）
        private static string Q(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        // 限制字符串长度用于日志
        private static string Tail(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            if (t.Length > max) t = t.Substring(t.Length - max);
            return t;
        }

        // ================= 主流程 =================
        public static void Print(string[] files, int dpi, bool keep, bool enhance, bool fix, Action<string> log)
        {
            if (files == null || files.Length == 0) throw new Exception("没有文件可打印");
            string workDir = Path.Combine(Path.GetTempPath(), "samsung-print");
            Directory.CreateDirectory(workDir);

            log("=== 步骤1/3: 渲染文档页面 ===");
            List<string> pages = new List<string>();
            foreach (string file in files)
            {
                if (!File.Exists(file)) throw new Exception("文件不存在: " + file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                log("  [" + Path.GetFileName(file) + "]");
                switch (ext)
                {
                    case ".pdf":
                        int n = RenderPdf(file, workDir, dpi, pages, log);
                        log("    PDF → " + n + " 页");
                        break;
                    case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".tif": case ".tiff": case ".gif": case ".webp":
                        if (enhance || fix)
                        {
                            string pr = ProcessImage(file, workDir, enhance, fix, log);
                            if (pr == file) { pages.Add(file); log("    图片处理未生效，已回退使用原图"); }
                            else { pages.Add(pr); log("    图片已处理（调色/修整）"); }
                        }
                        else
                        {
                            CopyImage(file, workDir, pages);
                            log("    图片已就绪");
                        }
                        break;
                    case ".txt": case ".md": case ".log": case ".csv": case ".sptxt":
                        int tn = RenderTextFile(file, workDir, dpi, pages, log);
                        log("    文本 → " + tn + " 页");
                        break;
                    case ".doc": case ".docx": case ".xls": case ".xlsx":
                        string pdf = TryConvertOffice(file, workDir, log);
                        int on = RenderPdf(pdf, workDir, dpi, pages, log);
                        log("    Office → PDF → " + on + " 页");
                        break;
                    default:
                        throw new Exception("暂不支持的文件类型: " + Path.GetExtension(file));
                }
            }
            if (pages.Count == 0) throw new Exception("没有可打印的页面");

            log("=== 步骤2/3: 打印机驱动渲染为打印数据 ===");
            string prn = Path.Combine(workDir, "out.prn");
            if (File.Exists(prn)) File.Delete(prn);
            byte[] data = RenderToPdl(pages, prn,
                files.Length == 1 ? Path.GetFileNameWithoutExtension(files[0]) : "多文档");
            log(string.Format("  渲染完成: PDL {0} KB", data.Length / 1024));

            log("=== 步骤3/3: 写入打印机 USB ===");
            long written = WriteUsb(data);
            log(string.Format("  已写入: {0} / {1} 字节", written, data.Length));
            log("=== 完成！打印机正在打印，请稍候取纸 ===");

            if (!keep) Cleanup(workDir);
        }

        // ---- PDF -> PNG（PyMuPDF，通过临时脚本文件执行）----
        private static int RenderPdf(string pdf, string workDir, int dpi, List<string> pages, Action<string> log)
        {
            PythonEnv env = PythonLocator.Find();
            if (env == null)
                throw new Exception("PDF 渲染需要 Python 3 + PyMuPDF。\r\n" + PythonLocator.LastReason +
                    "\r\n解决办法：到 python.org 安装 Python 3（安装时勾选 Add Python to PATH），然后运行: pip install pymupdf");

            string baseName = "pg" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string scriptPath = Path.Combine(workDir, "render_" + Guid.NewGuid().ToString("N") + ".py");
            // 脚本固定内容，路径全部经命令行参数传入（避免引号/编码问题）
            string script =
                "import sys, os\r\n" +
                "try:\r\n" +
                "    import pymupdf\r\n" +
                "except ImportError:\r\n" +
                "    import fitz as pymupdf\r\n" +
                "pdf, outdir, dpi, base = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]\r\n" +
                "doc = pymupdf.open(pdf)\r\n" +
                "n = len(doc)\r\n" +
                "for i in range(n):\r\n" +
                "    pix = doc[i].get_pixmap(dpi=dpi)\r\n" +
                "    pix.save(os.path.join(outdir, '%s_%d.png' % (base, i)))\r\n" +
                "print(n)\r\n";
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            try
            {
                string args = env.ArgsPrefix + Q(scriptPath) + " " + Q(pdf) + " " + Q(workDir) + " " + dpi + " " + baseName;
                RunResult r = RunProcess(env.Exe, args, 300000); // 大 PDF 允许 5 分钟
                if (r.LaunchFailed)
                    throw new Exception("无法启动 Python（" + env.Source + "）: " + r.LaunchError);
                if (r.TimedOut)
                    throw new Exception("PDF 渲染超时（超过 5 分钟），请降低 DPI 后重试");
                if (r.ExitCode != 0)
                {
                    string detail = Tail(r.StdErr, 600);
                    if (detail.Length == 0) detail = Tail(r.StdOut, 200);
                    throw new Exception("PDF 渲染失败（python 退出码 " + r.ExitCode + "）:\r\n" +
                        (detail.Length == 0 ? "请确认已安装 pymupdf: pip install pymupdf" : detail));
                }
                int n = 0;
                foreach (string line in (r.StdOut ?? "").Split('\n'))
                {
                    int v;
                    if (int.TryParse(line.Trim(), out v) && v > 0) { n = v; break; }
                }
                if (n <= 0) throw new Exception("PDF 渲染失败（未返回有效页数），请确认已安装 pymupdf: pip install pymupdf");
                // 由程序自己拼接页路径，不依赖解析 Python 输出（规避中文路径/编码乱码）
                for (int i = 0; i < n; i++)
                    pages.Add(Path.Combine(workDir, baseName + "_" + i + ".png"));
                return n;
            }
            finally
            {
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            }
        }

        // ---- 图片 -> 复制到工作目录 ----
        private static void CopyImage(string img, string workDir, List<string> pages)
        {
            string dst = Path.Combine(workDir, Guid.NewGuid().ToString("N") + Path.GetExtension(img).ToLowerInvariant());
            File.Copy(img, dst, true);
            pages.Add(dst);
        }

        // ---- 图片 -> 调色/修整（调用 enhance_fix.py）----
        private static string ProcessImage(string img, string workDir, bool enhance, bool fix, Action<string> log)
        {
            PythonEnv env = PythonLocator.Find();
            if (env == null)
            {
                log("    未找到可用的 Python 3，跳过调色/修整（" + PythonLocator.LastReason + "）");
                return img;
            }
            string outPath = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".png");
            string script = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "enhance_fix.py");
            if (!File.Exists(script))
                script = Path.Combine(@"D:\SamsungML1660Print", "enhance_fix.py");
            if (!File.Exists(script))
            {
                log("    未找到 enhance_fix.py，跳过调色/修整");
                return img;
            }
            string pyArgs = env.ArgsPrefix + Q(script) + " " + Q(img) + " " + Q(outPath) + " " +
                (enhance ? "1" : "0") + " " + (fix ? "1" : "0");
            RunResult r = RunProcess(env.Exe, pyArgs, 120000);
            if (r.LaunchFailed)
            {
                log("    图像处理无法启动 Python: " + r.LaunchError);
                return img;
            }
            if (r.TimedOut)
            {
                log("    图像处理超时（2 分钟），已回退使用原图");
                return img;
            }
            if (r.ExitCode == 0 && File.Exists(outPath))
            {
                string t = Tail(r.StdOut, 200);
                if (t.Length > 0) log("    " + t);
                return outPath;
            }
            string err = Tail(r.StdErr, 300);
            if (err.Length > 0) log("    图像脚本提示: " + err);
            return img;
        }

        // ---- 文本 -> 分页位图 ----
        // 支持 .sptxt（本程序“新建文字”生成的格式）：首行
        //   #SPTEXT#fontFamily=微软雅黑;size=14;bold=0;italic=0
        // 其后为正文；普通 .txt 仍用等宽字体默认排版。
        private static int RenderTextFile(string txt, string workDir, int dpi, List<string> pages, Action<string> log)
        {
            string[] rawLines = File.ReadAllLines(txt, DetectEncoding(txt));
            if (rawLines.Length == 0) rawLines = new[] { "" };

            string fontFamily = null;
            float fontSizePt = 10.5F;
            FontStyle fontStyle = FontStyle.Regular;
            string[] lines;

            if (rawLines[0].StartsWith("#SPTEXT#"))
            {
                string header = rawLines[0].Substring("#SPTEXT#".Length);
                Dictionary<string, string> kv = new Dictionary<string, string>();
                foreach (string part in header.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) kv[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                }
                if (kv.ContainsKey("fontFamily") && kv["fontFamily"].Length > 0) fontFamily = kv["fontFamily"];
                float fs;
                if (kv.ContainsKey("size") && float.TryParse(kv["size"], out fs) && fs >= 6 && fs <= 200) fontSizePt = fs;
                if (kv.ContainsKey("bold") && kv["bold"] == "1") fontStyle |= FontStyle.Bold;
                if (kv.ContainsKey("italic") && kv["italic"] == "1") fontStyle |= FontStyle.Italic;
                lines = new string[rawLines.Length - 1];
                Array.Copy(rawLines, 1, lines, 0, lines.Length);
                if (lines.Length == 0) lines = new[] { "" };
            }
            else
            {
                lines = rawLines;
            }

            // A4 @ dpi: 8.27in x 11.69in
            int w = (int)(8.27 * dpi), h = (int)(11.69 * dpi);
            int margin = dpi / 2; // 0.5 英寸
            float fontSize = fontSizePt * dpi / 96F;
            Font f = null;
            if (fontFamily != null)
            {
                try { f = new Font(fontFamily, fontSize, fontStyle, GraphicsUnit.Pixel); }
                catch { try { f = new Font("Microsoft YaHei", fontSize, fontStyle, GraphicsUnit.Pixel); } catch { f = new Font(FontFamily.GenericSansSerif, fontSize, fontStyle, GraphicsUnit.Pixel); } }
            }
            else
            {
                f = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            using (Font fnt = f)
            using (Bitmap bm = new Bitmap(w, h))
            using (Graphics g = Graphics.FromImage(bm))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                int lineH = (int)Math.Ceiling(fnt.GetHeight(g)) + 2;
                int perPage = Math.Max(1, (h - margin * 2) / lineH);
                int maxW = w - margin * 2;
                List<string> wrap = new List<string>();
                foreach (string ln in lines)
                {
                    if (ln.Length == 0) { wrap.Add(""); continue; }
                    string cur = ln;
                    while (cur.Length > 0)
                    {
                        // 二分查找能放下的最大字符数（大文件下远快于逐字递减）
                        int lo = 1, hi = cur.Length;
                        while (lo < hi)
                        {
                            int mid = (lo + hi + 1) / 2;
                            if (g.MeasureString(cur.Substring(0, mid), fnt).Width <= maxW) lo = mid;
                            else hi = mid - 1;
                        }
                        if (lo == 0) lo = 1;
                        wrap.Add(cur.Substring(0, lo));
                        cur = cur.Substring(lo);
                    }
                }
                int pageCount = (int)Math.Ceiling(wrap.Count / (double)perPage);
                if (pageCount == 0) pageCount = 1;
                for (int pg = 0; pg < pageCount; pg++)
                {
                    using (Bitmap pb = new Bitmap(w, h))
                    using (Graphics pg2 = Graphics.FromImage(pb))
                    {
                        pg2.Clear(Color.White);
                        pg2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        int start = pg * perPage;
                        for (int i = 0; i < perPage && start + i < wrap.Count; i++)
                            pg2.DrawString(wrap[start + i], fnt, Brushes.Black, margin, margin + i * lineH);
                        string p = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".png");
                        pb.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                        pages.Add(p);
                    }
                }
                return pageCount;
            }
        }

        private static Encoding DetectEncoding(string path)
        {
            byte[] head = new byte[4];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                int n = fs.Read(head, 0, 4);
                if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return new UTF8Encoding(true);
                if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
                if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return Encoding.BigEndianUnicode;
            }
            return Encoding.GetEncoding(936); // GBK
        }

        // ---- Office -> PDF（检测 Word/Excel/WPS COM，无则报错）----
        private static string TryConvertOffice(string file, string workDir, Action<string> log)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string outPdf = Path.Combine(workDir, Guid.NewGuid().ToString("N") + "_conv.pdf");
            bool isWord = ext == ".doc" || ext == ".docx";
            string[] progIds = isWord
                ? new[] { "Word.Application", "KWPS.Application", "wps.Application" }
                : new[] { "Excel.Application", "KET.Application", "et.Application" };
            foreach (string prog in progIds)
            {
                Type t;
                try { t = Type.GetTypeFromProgID(prog); } catch { continue; }
                if (t == null) continue;
                dynamic app = null;
                try
                {
                    app = Activator.CreateInstance(t);
                    if (app == null) continue;
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    if (isWord)
                    {
                        dynamic doc = app.Documents.Open(file, false, true);
                        doc.ExportAsFixedFormat(outPdf, 17); // 17 = wdExportFormatPDF
                        doc.Close(false);
                    }
                    else
                    {
                        dynamic wb = app.Workbooks.Open(file, false, true);
                        wb.ExportAsFixedFormat(0, outPdf);
                        wb.Close(false);
                    }
                    if (File.Exists(outPdf)) return outPdf;
                }
                catch { }
                finally
                {
                    try { if (app != null) app.Quit(); } catch { }
                    try { Marshal.FinalReleaseComObject(app); } catch { }
                }
            }
            throw new Exception("未安装 Word/Excel/WPS，无法转换 Office 文件: " + Path.GetFileName(file) +
                "（请安装 Office 或 WPS 后重试，或先把文档另存为 PDF 再打印）");
        }

        // ---- 自动匹配实际打印机队列名（防止驱动重装后名称变化导致"设置无效"）----
        private static string ResolvePrinterName()
        {
            try
            {
                foreach (string name in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
                {
                    if (name.IndexOf("Samsung ML-1660", StringComparison.OrdinalIgnoreCase) >= 0)
                        return name;
                    if (name.IndexOf("ML-1660", StringComparison.OrdinalIgnoreCase) >= 0)
                        return name;
                    if (name.IndexOf("ML1660", StringComparison.OrdinalIgnoreCase) >= 0)
                        return name;
                    if (name.IndexOf("Samsung", StringComparison.OrdinalIgnoreCase) >= 0)
                        return name;
                }
            }
            catch { }
            return "Samsung ML-1660 Series";
        }

        // ---- 页面图像 -> PDL（GDI PrintToFile）----
        private static byte[] RenderToPdl(List<string> pages, string prnPath, string docName)
        {
            int installed = 0;
            try { installed = System.Drawing.Printing.PrinterSettings.InstalledPrinters.Count; } catch { }
            if (installed == 0)
                throw new Exception("本机未检测到任何打印机驱动，无法生成打印数据。\r\n" +
                    "请先安装打印机驱动（设备与打印机 → 添加打印机），或确认驱动未被精简系统移除。");

            PrintDocument doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = ResolvePrinterName();
            doc.PrinterSettings.PrintToFile = true;
            doc.PrinterSettings.PrintFileName = prnPath;
            doc.DocumentName = docName;
            // 固定 A4 竖版 + 边距（0.3 英寸），避免内容贴边被打印机裁掉
            try
            {
                doc.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169);
                doc.DefaultPageSettings.Landscape = false;
                doc.DefaultPageSettings.Margins = new Margins(30, 30, 30, 30);
            }
            catch { }
            int idx = 0;
            doc.PrintPage += (sender, ev) => {
                if (idx >= pages.Count) { ev.HasMorePages = false; return; }
                string pgPath = pages[idx];
                try
                {
                    using (Image img = Image.FromFile(pgPath))
                    {
                        RectangleF rc = ev.MarginBounds;
                        if (rc.Width <= 0 || rc.Height <= 0) rc = ev.PageBounds;
                        // 保持比例适配可打印区域并居中
                        float sx = rc.Width / img.Width, sy = rc.Height / img.Height;
                        float scale = Math.Min(sx, sy);
                        float iw = img.Width * scale, ih = img.Height * scale;
                        ev.Graphics.DrawImage(img, rc.X + (rc.Width - iw) / 2, rc.Y + (rc.Height - ih) / 2, iw, ih);
                    }
                }
                catch (Exception ex)
                {
                    // 坏页跳过，不让单页失败中断整批
                    Debug.WriteLine("跳过坏页 " + pgPath + ": " + ex.Message);
                }
                idx++;
                ev.HasMorePages = idx < pages.Count;
            };
            doc.Print();
            return File.ReadAllBytes(prnPath);
        }

        // ---- USB 直写（失败自动重试一次）----
        private static long WriteUsb(byte[] data)
        {
            string path = GetDevicePath();
            IntPtr h = OpenDevice(path);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                // 设备可能正忙，稍等重试一次
                Thread.Sleep(500);
                h = OpenDevice(path);
            }
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                string hint = DeviceDetectDetail ?? "";
                throw new Exception("无法打开打印机 USB 设备（错误码 " + err + ": " +
                    new Win32Exception(err).Message + "）。\r\n" +
                    "请确认打印机已开机并连接 USB 线。" +
                    (hint.Length > 0 ? "\r\n检测详情: " + hint : ""));
            }
            try
            {
                long total = 0;
                int chunk = 32768;
                for (int i = 0; i < data.Length; i += chunk)
                {
                    int len = Math.Min(chunk, data.Length - i);
                    byte[] buf = new byte[len];
                    Array.Copy(data, i, buf, 0, len);
                    uint written;
                    if (!NativeMethods.WriteFile(h, buf, (uint)len, out written, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        throw new Exception("写入失败（错误码 " + err + ": " +
                            new Win32Exception(err).Message + "）");
                    }
                    total += written;
                }
                if (total == 0) throw new Exception("已连接设备但写入 0 字节，请检查打印机状态后重试");
                return total;
            }
            finally { NativeMethods.CloseHandle(h); }
        }

        private static IntPtr OpenDevice(string path)
        {
            return NativeMethods.CreateFile(path, 0x40000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        private static void Cleanup(string workDir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(workDir, "*.png")) File.Delete(f);
                foreach (string f in Directory.GetFiles(workDir, "*_conv.pdf")) File.Delete(f);
                foreach (string f in Directory.GetFiles(workDir, "render_*.py")) File.Delete(f);
                string prn = Path.Combine(workDir, "out.prn");
                if (File.Exists(prn)) File.Delete(prn);
            }
            catch { }
        }

        // ---- P/Invoke ----
        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
                IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
                out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);
        }

        // ---- SetupAPI 枚举打印机 USB 接口 ----
        private static class UsbEnum
        {
            private const uint DIGCF_PRESENT = 0x2;
            private const uint DIGCF_DEVICEINTERFACE = 0x10;

            [StructLayout(LayoutKind.Sequential)]
            private struct SP_DEVICE_INTERFACE_DATA
            {
                public int cbSize;
                public Guid InterfaceClassGuid;
                public int Flags;
                public IntPtr Reserved;
            }

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator,
                IntPtr hwndParent, uint Flags);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
                ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
                ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData,
                uint DeviceInterfaceDetailDataSize, ref uint RequiredSize, IntPtr DeviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

            public static string FindPrinterInterface()
            {
                List<string> paths = new List<string>();
                Guid[] guids = {
                    new Guid("28d78fad-5a12-11d1-ae5b-0000f803a8c2"),  // USB Printer 类
                    new Guid("0ecef634-6ef0-472a-8085-5ad023ecbccd")   // Printer 类
                };
                foreach (Guid g0 in guids)
                {
                    Guid gi = g0;
                    IntPtr devs = SetupDiGetClassDevs(ref gi, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                    if (devs == IntPtr.Zero || devs == new IntPtr(-1)) continue;
                    try
                    {
                        SP_DEVICE_INTERFACE_DATA did = new SP_DEVICE_INTERFACE_DATA();
                        did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                        uint idx = 0;
                        while (SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref gi, idx++, ref did))
                        {
                            uint size = 0;
                            SetupDiGetDeviceInterfaceDetail(devs, ref did, IntPtr.Zero, 0, ref size, IntPtr.Zero);
                            if (size < 8) continue;
                            IntPtr buffer = Marshal.AllocHGlobal((int)size);
                            try
                            {
                                // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize 为 8(x64) / 4(x86)
                                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4);
                                if (SetupDiGetDeviceInterfaceDetail(devs, ref did, buffer, size, ref size, IntPtr.Zero))
                                {
                                    string path = Marshal.PtrToStringUni(
                                        new IntPtr(buffer.ToInt64() + (IntPtr.Size == 8 ? 8 : 4)));
                                    if (!string.IsNullOrEmpty(path) && path.Contains("USB") && !paths.Contains(path))
                                        paths.Add(path);
                                }
                            }
                            finally { Marshal.FreeHGlobal(buffer); }
                        }
                    }
                    finally { SetupDiDestroyDeviceInfoList(devs); }
                }
                if (paths.Count == 0) return null;
                // 优先三星 VID 且可打开的接口；其次任意可打开；最后返回首个 USB 接口
                foreach (string p in paths)
                    if (p.IndexOf("VID_04E8", StringComparison.OrdinalIgnoreCase) >= 0 && CanOpen(p))
                        return p;
                foreach (string p in paths)
                    if (CanOpen(p))
                        return p;
                foreach (string p in paths)
                    if (p.IndexOf("VID_04E8", StringComparison.OrdinalIgnoreCase) >= 0)
                        return p;
                return paths[0];
            }
        }
    }
}
