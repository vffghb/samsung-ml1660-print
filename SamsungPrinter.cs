// ============================================================
// 打印助手（开源版）- Samsung ML-1660 多格式直通打印工具
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
// 支持格式：PDF / 图片(PNG·JPG·BMP·TIF·GIF·WEBP) /
//          文本(TXT·MD·LOG·CSV) / Office(Word·Excel，需安装 Office 或 WPS)
// 用法：
//   GUI:  双击运行，添加/拖入文件，点"开始打印"
//   CLI:  SamsungPrinter.exe -print <文件...> [-dpi 150] [-keep]
// 开源协议：MIT License
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SamsungML1660Print
{
    // ============ 入口 ============
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 命令行模式: SamsungPrinter.exe -print <file...> [-dpi 150] [-keep]
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
                string logPath = Path.Combine(Path.GetTempPath(), "samsung-print-cli.log");
                try
                {
                    using (StreamWriter sw = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(true)))
                    {
                        PrintCore.Print(files.ToArray(), dpi, keep, enhance, fix,
                            (m) => { sw.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m); sw.Flush(); });
                    }
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logPath, "错误: " + ex.Message + "\r\n", new System.Text.UTF8Encoding(true)); } catch { }
                    Environment.Exit(1);
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ============ 美化主界面 ============
    public class MainForm : Form
    {
        private ListView lstFiles;
        private GradientButton btnAdd, btnClear, btnPrint;
        private NumericUpDown numDpi;
        private CheckBox chkKeep;
        private RichTextBox txtLog;
        private Label lblStatus;
        private System.Windows.Forms.Timer tmrStatus;
        private int animTick;
        private CheckBox chkEnhance, chkFix;

        public MainForm()
        {
            Text = "打印助手 · 多格式直通打印";
            ClientSize = new Size(780, 620);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei UI", 9F);

            // ---- 顶部品牌区（渐变横幅）----
            Panel header = new Panel { Dock = DockStyle.Top, Height = 88 };
            header.Paint += (s, e) => {
                Rectangle rc = header.ClientRectangle;
                using (LinearGradientBrush br = new LinearGradientBrush(rc,
                    Color.FromArgb(37, 99, 235), Color.FromArgb(79, 70, 229), 0f))
                    e.Graphics.FillRectangle(br, rc);
                e.Graphics.DrawString("打印助手 · 多格式直通打印", new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
                    Brushes.White, new PointF(22, 14));
                e.Graphics.DrawString("PDF / 图片 / 文本 / Office · 绕开系统打印队列，直接写入打印机 USB · 开源版",
                    new Font("Microsoft YaHei UI", 9F), new SolidBrush(Color.FromArgb(220, 230, 255)), new PointF(24, 52));
            };
            Controls.Add(header);

            // ---- 文件列表卡片 ----
            Panel card = new Panel { BackColor = Color.White, Bounds = new Rectangle(14, 102, 752, 262) };
            card.Paint += (s, e) => {
                using (Pen p = new Pen(Color.FromArgb(226, 232, 240)))
                    e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            };
            lstFiles = new ListView
            {
                Bounds = new Rectangle(12, 10, 728, 216),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                MultiSelect = true
            };
            lstFiles.Columns.Add("文件", 400);
            lstFiles.Columns.Add("类型", 120);
            lstFiles.Columns.Add("DPI", 60);
            lstFiles.Columns.Add("状态", 110);
            btnAdd = new GradientButton { Text = "＋ 添加文件", Bounds = new Rectangle(12, 232, 130, 26) };
            btnClear = new GradientButton { Text = "清空列表", Bounds = new Rectangle(150, 232, 100, 26) };
            card.Controls.Add(lstFiles); card.Controls.Add(btnAdd); card.Controls.Add(btnClear);
            Controls.Add(card);

            // ---- 选项行 ----
            lblStatus = new Label
            {
                Text = "设备: 检测中…",
                AutoSize = true,
                ForeColor = Color.FromArgb(71, 85, 105),
                Location = new Point(16, 380)
            };
            Label lblDpi = new Label { Text = "DPI:", AutoSize = true, Location = new Point(16, 412), ForeColor = Color.FromArgb(71, 85, 105) };
            numDpi = new NumericUpDown { Location = new Point(54, 409), Width = 70, Minimum = 100, Maximum = 300, Value = 150 };
            chkKeep = new CheckBox { Text = "保留临时文件", Location = new Point(142, 412), AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) };
            chkEnhance = new CheckBox { Text = "智能调色", Location = new Point(268, 412), AutoSize = true, Checked = true, ForeColor = Color.FromArgb(37, 99, 235), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            chkFix = new CheckBox { Text = "自动修整", Location = new Point(360, 412), AutoSize = true, Checked = true, ForeColor = Color.FromArgb(37, 99, 235), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            Controls.Add(lblStatus); Controls.Add(lblDpi); Controls.Add(numDpi); Controls.Add(chkKeep);
            Controls.Add(chkEnhance); Controls.Add(chkFix);

            // ---- 日志区 ----
            txtLog = new RichTextBox
            {
                Bounds = new Rectangle(14, 444, 752, 122),
                ReadOnly = true,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F)
            };
            Controls.Add(txtLog);

            // ---- 打印按钮 ----
            btnPrint = new GradientButton { Text = "开 始 打 印", Bounds = new Rectangle(14, 574, 752, 38) };
            Controls.Add(btnPrint);

            // 事件
            btnAdd.Click += (s, e) => AddFiles();
            btnClear.Click += (s, e) => { lstFiles.Items.Clear(); Log("已清空列表"); };
            btnPrint.Click += (s, e) => StartPrint();

            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null) foreach (string f in files) AddFileToList(f);
            };

            // 打印中状态动画
            tmrStatus = new System.Windows.Forms.Timer { Interval = 400 };
            tmrStatus.Tick += (s, e) => {
                animTick++;
                lblStatus.Text = "设备: " + PrintCore.CurrentDeviceLabel + "    状态: 打印中" + new string('.', animTick % 4);
            };

            // 初始检测设备（后台线程）
            Thread t = new Thread(() => {
                string label = PrintCore.DetectDevice();
                Invoke((MethodInvoker)(() => lblStatus.Text = "设备: " + label));
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

        private void AddFileToList(string path)
        {
            if (!File.Exists(path)) { Log("文件不存在: " + path); return; }
            if (lstFiles.Items.Cast<ListViewItem>().Any(i => i.Tag.ToString() == path)) return;
            ListViewItem it = new ListViewItem(Path.GetFileName(path));
            it.SubItems.Add(PrintCore.GetFileKindLabel(path));
            it.SubItems.Add(numDpi.Value.ToString());
            it.SubItems.Add("就绪");
            it.Tag = path;
            lstFiles.Items.Add(it);
        }

        private void StartPrint()
        {
            if (lstFiles.Items.Count == 0) { Log("请先添加要打印的文件"); return; }
            List<string> files = lstFiles.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
            foreach (ListViewItem i in lstFiles.Items) i.SubItems[3].Text = "等待…";
            btnPrint.Enabled = false;
            tmrStatus.Start();
            Thread t = new Thread(() => {
                try
                {
                    PrintCore.Print(files.ToArray(), (int)numDpi.Value, chkKeep.Checked,
                        chkEnhance.Checked, chkFix.Checked,
                        (m) => Invoke((MethodInvoker)(() => Log(m))));
                    Invoke((MethodInvoker)(() => {
                        tmrStatus.Stop();
                        lblStatus.Text = "设备: " + PrintCore.CurrentDeviceLabel + "    状态: 打印完成";
                        foreach (ListViewItem i in lstFiles.Items) i.SubItems[3].Text = "完成";
                    }));
                }
                catch (Exception ex)
                {
                    Invoke((MethodInvoker)(() => {
                        tmrStatus.Stop();
                        Log("错误: " + ex.Message);
                        lblStatus.Text = "设备: " + PrintCore.CurrentDeviceLabel + "    状态: 出错";
                    }));
                }
                finally
                {
                    Invoke((MethodInvoker)(() => btnPrint.Enabled = true));
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void Log(string msg)
        {
            if (txtLog.IsDisposed) return;
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + msg + "\r\n");
            txtLog.ScrollToCaret();
        }
    }

    // 圆角渐变按钮
    public class GradientButton : Button
    {
        private bool hover, down;

        public GradientButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { down = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { down = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rc = ClientRectangle;
            GraphicsPath path = new GraphicsPath();
            int r = 6;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(rc.Width - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(rc.Width - r * 2, rc.Height - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, rc.Height - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            Color c1 = Color.FromArgb(37, 99, 235), c2 = Color.FromArgb(79, 70, 229);
            if (hover) { c1 = Color.FromArgb(49, 112, 246); c2 = Color.FromArgb(99, 90, 242); }
            if (down) { c1 = Color.FromArgb(30, 80, 190); c2 = Color.FromArgb(60, 55, 185); }
            using (LinearGradientBrush br = new LinearGradientBrush(rc, c1, c2, 0f))
                g.FillPath(br, path);
            TextRenderer.DrawText(g, Text, Font, rc, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            path.Dispose();
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

        /// <summary>自动检测 USB 打印机接口；失败回退内置路径</summary>
        public static string DetectDevice()
        {
            try
            {
                string p = UsbEnum.FindFirstPrinterInterface();
                if (!string.IsNullOrEmpty(p) && CanOpen(p))
                {
                    _devicePath = p;
                    CurrentDeviceLabel = "自动检测 (已找到打印机 USB 接口)";
                    return CurrentDeviceLabel;
                }
            }
            catch { }
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
                case ".doc": case ".docx": return "Word";
                case ".xls": case ".xlsx": return "Excel";
                default: return ext.TrimStart('.') + " 文件";
            }
        }

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
                            if (pr == file) { pages.Add(file); log("    图片处理失败，已回退使用原图"); }
                            else { pages.Add(pr); log("    图片已处理（调色/修整）"); }
                        }
                        else
                        {
                            CopyImage(file, workDir, pages);
                            log("    图片已就绪");
                        }
                        break;
                    case ".txt": case ".md": case ".log": case ".csv":
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

        // ---- PDF -> PNG（PyMuPDF）----
        private static int RenderPdf(string pdf, string workDir, int dpi, List<string> pages, Action<string> log)
        {
            string baseName = "pg" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string py =
                "import pymupdf, os\n" +
                "pdf = r'" + pdf.Replace("'", "\\'") + "'\n" +
                "outdir = r'" + workDir.Replace("'", "\\'") + "'\n" +
                "doc = pymupdf.open(pdf)\n" +
                "base = '" + baseName + "'\n" +
                "n = 0\n" +
                "for i, page in enumerate(doc):\n" +
                "    pix = page.get_pixmap(dpi=" + dpi + ")\n" +
                "    p = os.path.join(outdir, base + '_%d.png' % i)\n" +
                "    pix.save(p)\n" +
                "    print(p)\n" +
                "    n += 1\n" +
                "print('DONE')\n";
            ProcessStartInfo psi = new ProcessStartInfo("python", "-c \"" + py.Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("PDF 渲染失败(python): " + err.Trim());
                int n = 0;
                foreach (string line in outp.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t == "DONE") continue;
                    pages.Add(t);
                    n++;
                }
                if (n == 0) throw new Exception("PDF 渲染失败（请确认已安装 python 和 pymupdf）");
                return n;
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
            string outPath = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".png");
            string script = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "enhance_fix.py");
            if (!File.Exists(script))
                script = Path.Combine(@"D:\SamsungML1660Print", "enhance_fix.py");
            if (!File.Exists(script))
                return img;
            string pyArgs = string.Format("\"{0}\" \"{1}\" \"{2}\" {3} {4}",
                script, img, outPath, enhance ? "1" : "0", fix ? "1" : "0");
            ProcessStartInfo psi = new ProcessStartInfo("python", pyArgs)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false)
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && File.Exists(outPath))
                    {
                        if (!string.IsNullOrEmpty(outp))
                        {
                            string t = outp.Trim();
                            if (t.Length > 200) t = t.Substring(t.Length - 200);
                            log("    " + t);
                        }
                        return outPath;
                    }
                    if (!string.IsNullOrEmpty(err))
                    {
                        string t = err.Trim();
                        if (t.Length > 300) t = t.Substring(t.Length - 300);
                        log("    图像脚本提示: " + t);
                    }
                }
            }
            catch (Exception ex) { log("    图像处理异常: " + ex.Message); }
            return img;
        }

        // ---- 文本 -> 分页位图 ----
        private static int RenderTextFile(string txt, string workDir, int dpi, List<string> pages, Action<string> log)
        {
            string[] lines = File.ReadAllLines(txt, DetectEncoding(txt));
            if (lines.Length == 0) lines = new[] { "" };
            // A4 @ dpi: 8.27in x 11.69in
            int w = (int)(8.27 * dpi), h = (int)(11.69 * dpi);
            int margin = dpi / 2; // 0.5 英寸
            float fontSize = 10.5F * dpi / 96F;
            using (Font f = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Bitmap bm = new Bitmap(w, h))
            using (Graphics g = Graphics.FromImage(bm))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                int lineH = (int)Math.Ceiling(f.GetHeight(g)) + 2;
                int perPage = Math.Max(1, (h - margin * 2) / lineH);
                int maxW = w - margin * 2;
                List<string> wrap = new List<string>();
                foreach (string ln in lines)
                {
                    if (ln.Length == 0) { wrap.Add(""); continue; }
                    string cur = ln;
                    while (cur.Length > 0)
                    {
                        int fit = cur.Length;
                        while (fit > 1 && g.MeasureString(cur.Substring(0, fit), f).Width > maxW) fit--;
                        if (fit == 0) fit = 1;
                        wrap.Add(cur.Substring(0, fit));
                        cur = cur.Substring(fit);
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
                            pg2.DrawString(wrap[start + i], f, Brushes.Black, margin, margin + i * lineH);
                        string p = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".png");
                        pb.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                        pages.Add(p);
                    }
                }
                return pageCount;
            }
        }

        private static System.Text.Encoding DetectEncoding(string path)
        {
            byte[] head = new byte[4];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                int n = fs.Read(head, 0, 4);
                if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return new System.Text.UTF8Encoding(true);
                if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return System.Text.Encoding.Unicode;
                if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode;
            }
            return System.Text.Encoding.GetEncoding(936); // GBK
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
                }
            }
            catch { }
            return "Samsung ML-1660 Series";
        }

        // ---- 页面图像 -> PDL（GDI PrintToFile）----
        private static byte[] RenderToPdl(List<string> pages, string prnPath, string docName)
        {
            PrintDocument doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = ResolvePrinterName();
            doc.PrinterSettings.PrintToFile = true;
            doc.PrinterSettings.PrintFileName = prnPath;
            doc.DocumentName = docName;
            int idx = 0;
            doc.PrintPage += (sender, ev) => {
                if (idx < pages.Count)
                {
                    using (Image img = Image.FromFile(pages[idx]))
                    {
                        RectangleF b = ev.PageBounds;
                        // 保持比例适配页面
                        float sx = b.Width / img.Width, sy = b.Height / img.Height;
                        float scale = Math.Min(sx, sy);
                        float iw = img.Width * scale, ih = img.Height * scale;
                        ev.Graphics.DrawImage(img, (b.Width - iw) / 2, (b.Height - ih) / 2, iw, ih);
                    }
                    idx++;
                }
                ev.HasMorePages = idx < pages.Count;
            };
            doc.Print();
            return File.ReadAllBytes(prnPath);
        }

        // ---- USB 直写 ----
        private static long WriteUsb(byte[] data)
        {
            string path = GetDevicePath();
            IntPtr h = NativeMethods.CreateFile(path, 0x40000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
                throw new Exception("无法打开打印机 USB 设备（错误码 " + Marshal.GetLastWin32Error() +
                    "）。请确认打印机已开机并连接 USB 线。");
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
                        throw new Exception("写入失败（错误码 " + Marshal.GetLastWin32Error() + "）");
                    total += written;
                }
                return total;
            }
            finally { NativeMethods.CloseHandle(h); }
        }

        private static void Cleanup(string workDir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(workDir, "*.png")) File.Delete(f);
                foreach (string f in Directory.GetFiles(workDir, "*_conv.pdf")) File.Delete(f);
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

            public static string FindFirstPrinterInterface()
            {
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
                                // SP_DEVICE_INTERFACE_DETAIL_DATA: cbSize + DevicePath(可变长)
                                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                                if (SetupDiGetDeviceInterfaceDetail(devs, ref did, buffer, size, ref size, IntPtr.Zero))
                                {
                                    string path = Marshal.PtrToStringUni(
                                        new IntPtr(buffer.ToInt64() + (IntPtr.Size == 8 ? 8 : 4)));
                                    // 只接受真实 USB 数据接口（排除 SWD/虚拟打印接口）
                                    if (!string.IsNullOrEmpty(path) && path.Contains("USB")) return path;
                                }
                            }
                            finally { Marshal.FreeHGlobal(buffer); }
                        }
                    }
                    finally { SetupDiDestroyDeviceInfoList(devs); }
                }
                return null;
            }
        }
    }
}
