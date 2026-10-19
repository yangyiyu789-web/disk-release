using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using DiskRelease;

internal static class PrimaryCloseActionBootstrap
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string app = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "bin", "DiskRelease.exe"));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
        {
            return new AssemblyName(request.Name).Name == "DiskRelease" ? Assembly.LoadFrom(app) : null;
        };
        return PrimaryCloseActionTests.Run();
    }
}

internal static class PrimaryCloseActionTests
{
    static int checks;
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
    static void Put(MainForm form, string name, object value) { typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, value); }
    static object Call(MainForm form, string name, params object[] args) { return typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Invoke(form, args); }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static Occupant Item(int pid, string name, bool normal, bool forced, bool protect)
    {
        return new Occupant
        {
            Pid = pid, StartTicks = pid, Name = name, ExePath = "C:\\QA\\" + name + ".exe",
            CanClose = normal, CanForce = forced, Protected = protect,
            ProtectionReason = protect ? "QA protected" : "",
            Paths = new List<string> { "Y:\\QA\\held.bin" }
        };
    }
    static bool RequiresForce(IEnumerable<Occupant> items)
    {
        return (bool)typeof(MainForm).GetMethod("PrimaryCloseRequiresForce", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { items });
    }
    static List<Occupant> Selected(MainForm form) { return (List<Occupant>)Call(form, "CheckedItems"); }
    static void Populate(MainForm form, params Occupant[] items)
    {
        Call(form, "Populate", new ScanResult { Complete = true, Items = new List<Occupant>(items), Warning = "QA fixture only" });
    }
    static void State(MainForm form, string label, bool primaryEnabled, bool forceEnabled, bool forceRoute, string reason)
    {
        var primary = (Button)Field(form, "close");
        Assert(primary.Text == label, reason + ": primary label is " + label + ", actual=" + primary.Text);
        Assert(primary.Enabled == primaryEnabled, reason + ": primary enabled=" + primaryEnabled);
        Assert(((Button)Field(form, "force")).Enabled == forceEnabled, reason + ": force action enabled=" + forceEnabled);
        Assert(((ToolStripMenuItem)Field(form, "menuForce")).Enabled == forceEnabled, reason + ": force menu enabled=" + forceEnabled);
        Assert(RequiresForce(Selected(form)) == forceRoute, reason + ": primary click route force=" + forceRoute);
    }
    static void Select(MainForm form, params int[] pids)
    {
        var grid = (DataGridView)Field(form, "grid");
        foreach (DataGridViewRow row in grid.Rows)
        {
            Occupant item = (Occupant)row.Tag;
            row.Cells[0].Value = Array.IndexOf(pids, item.Pid) >= 0;
        }
        // CellValueChanged is the production path. No manual UpdateButtons call here.
    }
    static void CreateChildHandles(Control control)
    {
        IntPtr handle = control.Handle;
        foreach (Control child in control.Controls) CreateChildHandles(child);
    }
    static string RenderForceOnly(MainForm form, float dpi)
    {
        float scale = dpi / 96F;
        Size size = new Size((int)Math.Round(700 * scale), (int)Math.Round(540 * scale));
        Control surface = form.Controls[0];
        surface.Font = form.Font;
        surface.ForeColor = form.ForeColor;
        form.Controls.Remove(surface);
        surface.Dock = DockStyle.None;
        surface.BackColor = form.BackColor;
        try
        {
            surface.Size = size;
            surface.CreateControl();
            CreateChildHandles(surface);
            surface.PerformLayout();
            Application.DoEvents();
            var primary = (Button)Field(form, "close");
            Size text = TextRenderer.MeasureText(primary.Text, primary.Font);
            Assert(primary.Enabled && primary.Text == "强制结束…", "Rendered force-only primary action is available");
            Assert(text.Width <= primary.ClientSize.Width && text.Height <= primary.ClientSize.Height, "Force label fits the minimum-size button at actual DPI");
            for (Control child = primary; child != surface; child = child.Parent)
                Assert(child.Parent != null && child.Parent.ClientRectangle.Contains(child.Bounds), "Force action is not clipped by any layout parent");
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "previews-v111");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "force-only-700x540-" + Math.Round(dpi) + "dpi.png");
            using (var bitmap = new Bitmap(size.Width, size.Height))
            {
                bitmap.SetResolution(dpi, dpi);
                surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
                bitmap.Save(path, ImageFormat.Png);
            }
            Assert(new FileInfo(path).Length > 4096, "Actual force-only interface snapshot contains rendered content");
            return path;
        }
        finally { surface.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        try
        {
            Application.EnableVisualStyles();
            float dpi;
            string preview;
            var background = Item(21001, "codex", false, true, false);
            var secondBackground = Item(21002, "background-worker", false, true, false);
            var windowed = Item(21003, "EXCEL", true, true, false);
            var normalOnly = Item(21004, "normal-only", true, false, false);
            var protectedItem = Item(21005, "protected-fixture", true, true, true);
            var manual = Item(21006, "manual-only", false, false, false);
            using (var form = new MainForm(true))
            {
                var handle = form.Handle;
                var grid = (DataGridView)Field(form, "grid");
                var gridHandle = grid.Handle;
                form.PerformAutoScale();
                using (var graphics = Graphics.FromHwnd(handle)) dpi = graphics.DpiX;
                var target = (DiskTarget)Field(form, "selectedDisk");
                Assert(target != null && target.CanEject, "Preview supplies a manageable external target");
                Assert(!form.Visible, "Test never shows a desktop window");

                Populate(form, background);
                Assert(Selected(form).Count == 1 && Selected(form)[0] == background, "Background process is selected after scan projection");
                State(form, "强制结束…", true, true, true, "One background process");

                Populate(form, background, secondBackground);
                State(form, "强制结束…", true, true, true, "Several background processes");

                Populate(form, windowed);
                State(form, "关闭所选", true, true, false, "Windowed process supports graceful close");

                Populate(form, normalOnly);
                State(form, "关闭所选", true, false, false, "Normal-close-only process");

                Populate(form, background, windowed);
                State(form, "关闭所选", true, true, false, "Mixed selection never escalates graceful close to forced termination");
                Select(form, background.Pid);
                State(form, "强制结束…", true, true, true, "Deselecting last windowed process switches to explicit force");
                Select(form, background.Pid, windowed.Pid);
                State(form, "关闭所选", true, true, false, "Reselecting windowed process restores graceful action");
                Select(form);
                State(form, "关闭所选", false, false, false, "Cleared selection");
                Select(form, windowed.Pid);
                State(form, "关闭所选", true, true, false, "Selection change restores graceful action");
                Select(form, background.Pid);
                State(form, "强制结束…", true, true, true, "Selection change restores force-only action");

                Populate(form, background, normalOnly);
                State(form, "关闭所选", true, true, false, "Normal-only capability also prevents escalation in mixed selection");

                Populate(form, protectedItem);
                Assert(Selected(form).Count == 0 && grid.Rows[0].Cells[0].ReadOnly, "Protected row cannot be selected by the user");
                State(form, "关闭所选", false, false, false, "Protected-only scan");
                Select(form, protectedItem.Pid);
                State(form, "关闭所选", false, false, false, "Even a forged protected selection cannot enable closure");

                Populate(form, background, protectedItem);
                Select(form, background.Pid, protectedItem.Pid);
                State(form, "强制结束…", true, true, true, "Protected graceful capability is ignored when selecting a background process");

                Populate(form, manual);
                Assert(Selected(form).Count == 0, "Manual-only row is not preselected");
                Select(form, manual.Pid);
                State(form, "关闭所选", false, false, false, "No supported closure capability");

                Populate(form);
                State(form, "关闭所选", false, false, false, "Empty scan");

                Populate(form, background);
                target.CanEject = false; Call(form, "UpdateButtons");
                State(form, "强制结束…", false, false, true, "Internal or diagnosis-only disk disables forced primary action");
                Assert(((Button)Field(form, "scan")).Enabled, "Diagnosis remains available for internal disk");
                Populate(form, windowed);
                State(form, "关闭所选", false, false, false, "Internal disk disables graceful primary action");
                target.CanEject = true;

                Populate(form, background);
                Put(form, "busy", true); Call(form, "UpdateButtons");
                State(form, "强制结束…", false, false, true, "Busy background selection");
                Assert(!grid.Enabled, "Busy state freezes process selection");
                Put(form, "busy", false); Call(form, "UpdateButtons");
                State(form, "强制结束…", true, true, true, "Force action returns after busy state");

                Populate(form, windowed);
                Put(form, "busy", true); Call(form, "UpdateButtons");
                State(form, "关闭所选", false, false, false, "Busy normal selection");
                Put(form, "busy", false); Call(form, "UpdateButtons");
                State(form, "关闭所选", true, true, false, "Graceful action returns after busy state");

                Populate(form, background);
                Put(form, "selectedDisk", null); Call(form, "UpdateButtons");
                State(form, "强制结束…", false, false, true, "No target disables forced primary action");
                Assert(!((Button)Field(form, "scan")).Enabled, "No target disables diagnosis");
                Put(form, "selectedDisk", target); Call(form, "UpdateButtons");
                State(form, "强制结束…", true, true, true, "Restoring target restores explicit force action");
                Assert(!form.Visible, "No test state transition opened a desktop window");
                preview = RenderForceOnly(form, dpi);
            }
            Console.WriteLine("{\"passed\":" + checks + ",\"dpiX\":" + dpi.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"visibleWindowsOpened\":0,\"processCloseCalls\":0,\"scanCalls\":0,\"testedArtifact\":\"bin/DiskRelease.exe\",\"preview\":\"" + preview.Replace('\\', '/') + "\"}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
