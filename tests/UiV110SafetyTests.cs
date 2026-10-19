using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DiskRelease;

internal static class UiV110Bootstrap
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        string app = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "bin", "DiskRelease.exe"));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request) { return new AssemblyName(request.Name).Name == "DiskRelease" ? Assembly.LoadFrom(app) : null; };
        return UiV110SafetyTests.Run();
    }
}

internal static class UiV110SafetyTests
{
    [DllImport("user32.dll", EntryPoint="SendMessageW")] static extern IntPtr ComboMessage(IntPtr window, uint message, IntPtr parameter, IntPtr data);
    [DllImport("user32.dll", EntryPoint="SendMessageW", CharSet=CharSet.Unicode)] static extern IntPtr ComboText(IntPtr window, uint message, IntPtr parameter, StringBuilder text);
    static int checks;
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
    static void Put(MainForm form, string name, object value) { typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, value); }
    static void Call(MainForm form, string name, params object[] args) { typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Invoke(form, args); }
    static void Assert(bool value, string text) { if (!value) throw new Exception(text); checks++; }
    static Rectangle BoundsInForm(Control control, Form form) { Point point = Point.Empty; Control item = control; while(item != form) { point.Offset(item.Location); item=item.Parent; } return new Rectangle(point,control.ClientSize); }
    static bool FitsParents(Control control, Form form)
    {
        for(Control child=control; child!=form; child=child.Parent)
            if(child.Parent==null || !child.Parent.ClientRectangle.Contains(child.Bounds)) return false;
        return true;
    }
    static void CheckNoEjectControls(Control container)
    {
        foreach(Control child in container.Controls)
        {
            if(child is Button) Assert(!child.Text.Contains("弹出"), "Primary controls contain no eject action");
            CheckNoEjectControls(child);
        }
    }
    static void CheckNoEjectMenu(ToolStripItemCollection items)
    {
        foreach(ToolStripItem item in items)
        {
            Assert(!item.Text.Contains("弹出"), "More menu contains no eject action");
            var nested=item as ToolStripDropDownItem;
            if(nested!=null) CheckNoEjectMenu(nested.DropDownItems);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        try
        {
            Application.EnableVisualStyles();
            float dpi;
            using(var form=new MainForm(true))
            {
                var handle=form.Handle;
                form.PerformAutoScale();
                using(var graphic=Graphics.FromHwnd(handle)) dpi=graphic.DpiX;
                var target=(DiskTarget)Field(form,"selectedDisk");
                var drives=(ComboBox)Field(form,"drives");
                var driveHandle=drives.Handle;
                Assert(drives.SelectedIndex>=0 && drives.SelectedItem==target, "Drive remains selected after native handle creation");
                Assert(!String.IsNullOrWhiteSpace(drives.Text), "Drive label is nonempty");
                int nativeIndex=ComboMessage(driveHandle,0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
                Assert(nativeIndex==drives.SelectedIndex,"Native combo selection matches managed selection");
                var label=new StringBuilder(1024); ComboText(driveHandle,0x148,new IntPtr(nativeIndex),label);
                Assert(label.ToString()==drives.Text,"Native combo label matches managed label");
                Assert(((Button)Field(form,"scan")).Enabled,"External disk can be scanned");
                CheckNoEjectControls(form);
                CheckNoEjectMenu(((ContextMenuStrip)Field(form,"moreMenu")).Items);
                target.CanEject=false; Call(form,"UpdateButtons");
                Assert(!((Button)Field(form,"close")).Enabled,"Read-only disk disables normal close");
                Assert(!((ToolStripMenuItem)Field(form,"menuForce")).Enabled,"Read-only disk disables forced termination");
                Assert(((Button)Field(form,"scan")).Enabled,"Read-only disk retains diagnosis");
                target.CanEject=true; Put(form,"busy",true); Call(form,"UpdateButtons");
                foreach(string name in new[]{"close","scan","refresh"}) Assert(!((Button)Field(form,name)).Enabled,"Busy disables "+name);
                Assert(!drives.Enabled && !((DataGridView)Field(form,"grid")).Enabled,"Busy freezes disk and process selection");
                foreach(string name in new[]{"menuForce","menuShowAll"}) Assert(!((ToolStripMenuItem)Field(form,name)).Enabled,"Busy disables "+name);
                Put(form,"busy",false); Put(form,"selectedDisk",null); Call(form,"UpdateButtons");
                Assert(!((Button)Field(form,"scan")).Enabled && !((Button)Field(form,"close")).Enabled,"No target disables scan and close");
                Assert(!((ToolStripMenuItem)Field(form,"menuForce")).Enabled,"No target disables forced termination");
                Put(form,"selectedDisk",target); Call(form,"UpdateButtons");
                float scale=dpi/96F;
                foreach(Size logical in new[]{new Size(700,540),new Size(900,600),new Size(1440,900),new Size(700,1000),new Size(900,600)})
                {
                    Size size=new Size((int)Math.Round(logical.Width*scale),(int)Math.Round(logical.Height*scale));
                    form.ClientSize=size; form.PerformLayout(); Application.DoEvents();
                    using(var bitmap=new Bitmap(size.Width,size.Height)) form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,size));
                    foreach(string name in new[]{"close","scan","more","drives"})
                    {
                        var control=(Control)Field(form,name);
                        Assert(control.Parent!=null && form.ClientRectangle.Contains(BoundsInForm(control,form)),"Core action fits at "+logical+": "+name+" rect="+BoundsInForm(control,form)+" client="+form.ClientRectangle+" root="+form.Controls[0].Bounds+" dpi="+dpi);
                        Assert(FitsParents(control,form),"Core action is not clipped by a parent at "+logical+": "+name);
                    }
                    var grid=(DataGridView)Field(form,"grid");
                    Assert(form.ClientRectangle.Contains(BoundsInForm(grid,form)) && grid.Height>=80,"Results remain usable at "+logical);
                    Assert(grid.Columns[0].Visible && grid.Columns[1].Visible && grid.Columns[1].Width>=200*scale,"Selection and software content remain available at "+logical);
                    foreach(DataGridViewRow row in grid.Rows) Assert(row.Height>=64*scale,"Two-line rows fit actual DPI at "+logical);
                }
                Call(form,"SetChecked",false);
                Assert(!((Button)Field(form,"close")).Enabled && !((Button)Field(form,"force")).Enabled,"No selection disables both close actions");
                Call(form,"SetChecked",true);
                Assert(((Button)Field(form,"close")).Enabled,"Selecting a closeable app enables normal closure");
                Assert(((ToolStripMenuItem)Field(form,"menuForce")).Enabled,"Selecting a force-eligible app enables explicit force action");
                Call(form,"SetState","检查未完成","检查超时，请关闭已知软件后重试。",true);
                form.PerformLayout(); Application.DoEvents();
                var warning=(Label)Field(form,"stateDetail");
                Assert(warning.Height>=warning.Font.Height,"Failure explanation has enough room at actual DPI");
                Assert(warning.Text.Contains("重试"),"Failure preserves an actionable explanation");
            }
            Console.WriteLine("{\"passed\":"+checks+",\"dpiX\":"+dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"visibleWindowsOpened\":0,\"testedArtifact\":\"bin/DiskRelease.exe\"}");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
