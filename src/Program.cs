using System;
using System.Drawing;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("简单查盘器")]
[assembly: AssemblyDescription("检查外接磁盘占用并关闭所选软件")]
[assembly: AssemblyCompany("本地工具")]
[assembly: AssemblyProduct("简单查盘器")]
[assembly: AssemblyVersion("1.1.3.0")]
[assembly: AssemblyFileVersion("1.1.3.0")]

namespace DiskRelease
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--scan-worker")
                return DiskEngine.WorkerMain(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.ProcessExit += delegate { DiskEngine.StopOwnWorkers(); };
            if (args.Length > 1 && args[0] == "--render-preview")
            {
                string directory = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(directory);
                using (MainForm form = new MainForm(true))
                {
                    // Creating a handle establishes the monitor DPI without showing a window.
                    IntPtr formHandle = form.Handle;
                    form.PerformAutoScale();
                    float dpi;
                    using (Graphics monitor = Graphics.FromHwnd(formHandle)) dpi = monitor.DpiX;
                    float scale = dpi / 96F;
                    Control surface = form.Controls[0];
                    surface.Font = form.Font;
                    surface.ForeColor = form.ForeColor;
                    form.Controls.Remove(surface);
                    surface.Dock = DockStyle.None;
                    surface.BackColor = form.BackColor;
                    foreach (Size logical in new[] { new Size(700, 540), new Size(900, 600), new Size(1100, 760) })
                    {
                        Size size = new Size((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
                        surface.Size = size;
                        surface.CreateControl();
                        CreateChildHandles(surface);
                        surface.PerformLayout();
                        Application.DoEvents();
                        using (Bitmap bitmap = new Bitmap(size.Width, size.Height))
                        {
                            bitmap.SetResolution(dpi, dpi);
                            surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
                            string path = Path.Combine(directory, "preview-" + logical.Width + "x" + logical.Height + "-" + Math.Round(dpi) + "dpi.png");
                            bitmap.Save(path, ImageFormat.Png);
                            ValidatePreview(path, size);
                        }
                    }
                    surface.Dispose();
                }
                return 0;
            }
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("操作未完成：" + e.Exception.Message, "简单查盘器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Application.Run(new MainForm(false));
            return 0;
        }

        static void CreateChildHandles(Control control)
        {
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) CreateChildHandles(child);
        }

        static void ValidatePreview(string path, Size expected)
        {
            using (Bitmap bitmap = new Bitmap(path))
            {
                if (bitmap.Size != expected || new FileInfo(path).Length < 4096)
                    throw new InvalidOperationException("界面预览图未正确生成：" + path);
                HashSet<int> colors = new HashSet<int>();
                int stepX = Math.Max(1, bitmap.Width / 80), stepY = Math.Max(1, bitmap.Height / 60);
                for (int y = 0; y < bitmap.Height && colors.Count < 8; y += stepY)
                    for (int x = 0; x < bitmap.Width && colors.Count < 8; x += stepX)
                        colors.Add(bitmap.GetPixel(x, y).ToArgb());
                if (colors.Count < 3)
                    throw new InvalidOperationException("界面预览图为空或缺少内容：" + path);
            }
        }
    }
}
