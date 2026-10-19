using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DiskRelease;

internal static class ExplorerProjectionBootstrap
{
    [STAThread]
    static int Main(string[] args)
    {
        string app = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "bin", "DiskRelease.exe"));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
        {
            return new AssemblyName(request.Name).Name == "DiskRelease" ? Assembly.LoadFrom(app) : null;
        };
        return ExplorerProjectionTests.Run();
    }
}

internal static class ExplorerProjectionTests
{
    static int checks;
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
    static void Call(MainForm form, string name, params object[] args) { typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args); }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static Occupant Item(int pid, string name, bool protect)
    {
        return new Occupant { Pid = pid, StartTicks = pid, Name = name, Protected = protect, CanClose = !protect, CanForce = !protect, ExePath = "C:\\QA\\" + name + ".exe", Paths = new List<string> { "Y:\\QA\\held.bin" }, ProtectionReason = protect ? "QA protected" : "" };
    }
    static void Show(MainForm form, ScanResult result)
    {
        Call(form, "Populate", result);
        Call(form, "SetScanState", result);
    }
    static string State(MainForm form) { return ((Label)Field(form, "stateTitle")).Text + "|" + ((Label)Field(form, "stateDetail")).Text; }
    static void VisibleCount(MainForm form, int expected)
    {
        var grid = (DataGridView)Field(form, "grid");
        Assert(grid.Rows.Count == expected, "Visible row count is " + expected);
        string summary = ((Label)Field(form, "summary")).Text + "|" + ((Label)Field(form, "stateTitle")).Text;
        Match count = Regex.Match(summary, @"\d+");
        Assert(expected == 0 ? (!count.Success || Int32.Parse(count.Value) == 0) : (count.Success && Int32.Parse(count.Value) == expected), "Displayed count matches visible row count " + expected + ": " + summary);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        try
        {
            Application.EnableVisualStyles();
            using (var form = new MainForm(true))
            {
                var hiddenNames = new[] { "explorer", "EXPLORER", "explorer.exe", "ExPlOrEr.ExE" };
                var all = new List<Occupant>();
                for (int i = 0; i < hiddenNames.Length; i++) all.Add(Item(10000 + i, hiddenNames[i], true));
                all.Add(Item(10010, "剪辑软件", false));
                all.Add(Item(10011, "explorer-helper", false));
                all.Add(Item(10012, "Explorer++", false));
                all.Add(Item(10013, "System", true));
                var mixed = new ScanResult { Complete = true, Items = all, Warning = "QA complete" };
                Show(form, mixed);
                VisibleCount(form, 4);
                var grid = (DataGridView)Field(form, "grid");
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var item = (Occupant)row.Tag;
                    Assert(!Array.Exists(hiddenNames, n => String.Equals(item.Name, n, StringComparison.OrdinalIgnoreCase)), "Exact Explorer names are hidden");
                }
                Assert(mixed.Items.Count == 8 && mixed.Items.Contains(all[0]), "Projection preserves the complete input scan evidence");
                Assert(((IList)Field(form, "occupants")).Count == 8, "Background occupant collection retains hidden Explorer evidence");
                bool protectedFound = false;
                Call(form, "SetChecked", true);
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var item = (Occupant)row.Tag;
                    if (item.Protected)
                    {
                        protectedFound = true;
                        Assert(row.Cells[0].ReadOnly && !Convert.ToBoolean(row.Cells[0].Value), "Protected visible process cannot be selected");
                    }
                }
                Assert(protectedFound, "Protected non-Explorer evidence remains visible");
                Assert(((Button)Field(form, "close")).Enabled && ((Button)Field(form, "force")).Enabled, "Ordinary selected software retains normal and force actions");
                Call(form, "SetChecked", false);
                Assert(!((Button)Field(form, "close")).Enabled && !((Button)Field(form, "force")).Enabled, "No selected process disables both close actions");

                var onlyExplorer = new ScanResult { Complete = true, Items = new List<Occupant> { Item(11000, "explorer.exe", true) }, Warning = "" };
                Show(form, onlyExplorer);
                VisibleCount(form, 0);
                Assert(State(form).Contains("没有需要关闭的软件"), "Explorer-only result explains that no software needs closure");
                Assert(!State(form).Contains("未发现占用"), "Hidden-only result does not claim the disk has no occupancy");
                Assert(!((Button)Field(form, "close")).Enabled && !((Button)Field(form, "force")).Enabled, "Hidden Explorer never enables process termination");

                var incomplete = new ScanResult { Complete = false, Items = onlyExplorer.Items, Warning = "扫描达到时间上限，结果不完整。" };
                Show(form, incomplete);
                VisibleCount(form, 0);
                Assert(Regex.IsMatch(State(form), "未完成|未能完整|不完整"), "Incomplete Explorer-only scan remains visibly incomplete");
                Assert(!((Label)Field(form, "stateTitle")).Text.Contains("没有需要关闭的软件"), "Incomplete scan is not presented as a completed empty result");

                var protectedOnly = new ScanResult { Complete = true, Items = new List<Occupant> { Item(12000, "System", true) }, Warning = "" };
                Show(form, protectedOnly);
                VisibleCount(form, 1);
                Call(form, "SetChecked", true);
                Assert(!((Button)Field(form, "close")).Enabled && !((Button)Field(form, "force")).Enabled, "Protected-only result cannot close or force terminate");
                Assert(!((ToolStripMenuItem)Field(form, "menuForce")).Enabled, "Protected-only result disables forced termination menu");
            }
            Console.WriteLine("{\"passed\":" + checks + ",\"visibleWindowsOpened\":0,\"testedArtifact\":\"bin/DiskRelease.exe\"}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
