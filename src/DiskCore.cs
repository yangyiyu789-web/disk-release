using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DiskRelease
{
    public sealed class DiskTarget
    {
        public string Root { get; set; }
        public string Label { get; set; }
        public string Model { get; set; }
        public string Identity { get; set; }
        public string PnpId { get; set; }
        public int DiskNumber { get; set; }
        // Legacy property name: in 1.0.2 this means software management is allowed.
        // It never authorizes or triggers disk removal.
        public bool CanEject { get; set; }
        public string SafetyNote { get; set; }
        public List<string> RelatedRoots { get; set; }
        internal List<VolumeRecord> Volumes = new List<VolumeRecord>();
        public DiskTarget() { RelatedRoots = new List<string>(); }
        public override string ToString()
        {
            return String.Join("、", RelatedRoots.ToArray()) + "  " + (String.IsNullOrWhiteSpace(Label) ? Model : Label) + (CanEject ? "" : "（仅诊断）");
        }
    }

    public sealed class Occupant
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public string Name { get; set; }
        public string ExePath { get; set; }
        public List<string> Paths { get; set; }
        public bool Protected { get; set; }
        public bool CanForce { get; set; }
        public bool CanClose { get; set; }
        public string ProtectionReason { get; set; }
        public Occupant() { Paths = new List<string>(); }
    }

    public sealed class ScanResult
    {
        public List<Occupant> Items { get; set; }
        public bool Complete { get; set; }
        public string Warning { get; set; }
        public int SkippedProcesses { get; set; }
        public ScanResult() { Items = new List<Occupant>(); Warning = ""; }
    }

    public sealed class ActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public ActionResult() { }
        public ActionResult(bool success, string message) { Success = success; Message = message; }
    }

    internal sealed class VolumeRecord
    {
        public string GuidPath;
        public string NtPath;
        public int DiskNumber;
        public List<string> Roots = new List<string>();
    }

    internal sealed class WorkerRequest
    {
        public DiskTarget Target { get; set; }
        public int ParentPid { get; set; }
        public int TimeoutSeconds { get; set; }
    }

    internal sealed class WorkerEvent
    {
        public string Kind { get; set; }
        public Occupant Item { get; set; }
        public string Message { get; set; }
        public bool Complete { get; set; }
        public int Skipped { get; set; }
    }

    internal sealed class PhysicalRecord
    {
        public int Number;
        public string Model;
        public string Serial;
        public string Pnp;
        public string Interface;
        public string Size;
        public bool System;
    }

    public static class DiskEngine
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint SYNCHRONIZE = 0x00100000;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
        private static readonly object WorkerGate = new object();
        private static readonly List<Process> ActiveWorkers = new List<Process>();
        private static readonly HashSet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit",
            "services", "lsass", "winlogon", "svchost", "fontdrvhost", "dwm", "audiodg", "sihost", "taskhostw",
            "MsMpEng", "NisSrv", "SecurityHealthService", "spoolsv", "WmiPrvSE", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost"
        };

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void StopOwnWorkers()
        {
            lock (WorkerGate)
            {
                foreach (Process worker in ActiveWorkers)
                    try { if (!worker.HasExited) worker.Kill(); } catch { }
            }
        }

        private static ManagementObjectCollection Query(string scope, string text)
        {
            EnumerationOptions options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(8) };
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, text, options))
                return searcher.Get();
        }

        private static string Text(ManagementBaseObject row, string key)
        {
            object value = row[key];
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        public static List<DiskTarget> ListDisks()
        {
            List<PhysicalRecord> physical = new List<PhysicalRecord>();
            using (ManagementObjectCollection rows = Query(@"root\cimv2", "SELECT Index,Model,SerialNumber,PNPDeviceID,InterfaceType,Size FROM Win32_DiskDrive"))
                foreach (ManagementObject row in rows)
                {
                    using (row)
                        physical.Add(new PhysicalRecord { Number = Convert.ToInt32(row["Index"], CultureInfo.InvariantCulture), Model = Text(row, "Model"), Serial = Text(row, "SerialNumber"), Pnp = Text(row, "PNPDeviceID"), Interface = Text(row, "InterfaceType"), Size = Text(row, "Size") });
                }
            bool systemFlagsKnown = false;
            try
            {
                using (ManagementObjectCollection rows = Query(@"root\Microsoft\Windows\Storage", "SELECT Number,IsBoot,IsSystem FROM MSFT_Disk"))
                {
                    foreach (ManagementObject row in rows)
                    {
                        using (row)
                        {
                            int n = Convert.ToInt32(row["Number"], CultureInfo.InvariantCulture);
                            PhysicalRecord record = physical.Find(delegate(PhysicalRecord p) { return p.Number == n; });
                            if (record != null) record.System = Convert.ToBoolean(row["IsBoot"], CultureInfo.InvariantCulture) || Convert.ToBoolean(row["IsSystem"], CultureInfo.InvariantCulture);
                        }
                    }
                    systemFlagsKnown = true;
                }
            }
            catch { }
            List<VolumeRecord> volumes = ReadVolumes();
            string windowsRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string ownExecutable = Process.GetCurrentProcess().MainModule.FileName;
            HashSet<int> pagefileDisks = new HashSet<int>();
            try
            {
                using (ManagementObjectCollection rows = Query(@"root\cimv2", "SELECT Name FROM Win32_PageFileUsage"))
                    foreach (ManagementObject row in rows)
                    {
                        using (row)
                        {
                            string name = Text(row, "Name");
                            foreach (VolumeRecord v in volumes)
                                if (v.Roots.Any(delegate(string r) { return StartsWithRoot(name, r); })) pagefileDisks.Add(v.DiskNumber);
                        }
                    }
            }
            catch { }
            List<DiskTarget> result = new List<DiskTarget>();
            foreach (PhysicalRecord p in physical)
            {
                List<VolumeRecord> own = volumes.FindAll(delegate(VolumeRecord v) { return v.DiskNumber == p.Number; });
                List<string> roots = own.SelectMany(delegate(VolumeRecord v) { return v.Roots; }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(delegate(string r) { return r; }, StringComparer.OrdinalIgnoreCase).ToList();
                if (roots.Count == 0) continue;
                bool isSystem = p.System || roots.Any(delegate(string r) { return String.Equals(r, windowsRoot, StringComparison.OrdinalIgnoreCase); });
                bool containsTool = own.Any(delegate(VolumeRecord v) { return v.Roots.Any(delegate(string r) { return StartsWithRoot(ownExecutable, r); }); });
                int bus; bool hotplug;
                ReadStorageKind(p.Number, out bus, out hotplug);
                bool external = bus == 7 || bus == 4 || bus == 12 || bus == 13 || String.Equals(p.Interface, "USB", StringComparison.OrdinalIgnoreCase) || p.Pnp.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase);
                string label = "";
                try { label = new DriveInfo(roots[0]).VolumeLabel; } catch { }
                string volumeIdentity = String.Join(";", own.Select(delegate(VolumeRecord v) { return v.GuidPath; }).OrderBy(delegate(string v) { return v; }, StringComparer.OrdinalIgnoreCase).ToArray());
                DiskTarget d = new DiskTarget { Root = roots[0], Label = label, Model = p.Model, PnpId = p.Pnp, DiskNumber = p.Number, RelatedRoots = roots, Identity = p.Pnp + "|" + p.Serial + "|" + p.Size + "|" + volumeIdentity, Volumes = own };
                d.CanEject = external && !isSystem && !containsTool && !pagefileDisks.Contains(p.Number) && !String.IsNullOrWhiteSpace(p.Pnp) && systemFlagsKnown;
                if (isSystem) d.SafetyNote = "系统或启动磁盘，仅允许只读诊断。";
                else if (containsTool) d.SafetyNote = "此工具正在该物理磁盘上运行，仅允许检查。";
                else if (pagefileDisks.Contains(p.Number)) d.SafetyNote = "磁盘包含 Windows 分页文件，仅允许检查。";
                else if (!external) d.SafetyNote = "内部磁盘，仅允许只读诊断。";
                else if (!systemFlagsKnown) d.SafetyNote = "无法确认启动磁盘信息，仅允许检查；请以管理员身份刷新。";
                else if (String.IsNullOrWhiteSpace(p.Pnp)) d.SafetyNote = "无法确认设备身份，仅允许检查。";
                else d.SafetyNote = "可检查占用并关闭所选软件。";
                result.Add(d);
            }
            return result.OrderByDescending(delegate(DiskTarget d) { return d.CanEject; }).ThenBy(delegate(DiskTarget d) { return d.Root; }, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static DiskTarget Revalidate(DiskTarget original)
        {
            if (original == null || String.IsNullOrWhiteSpace(original.Identity) || String.IsNullOrWhiteSpace(original.Root)) throw new InvalidOperationException("磁盘信息无效，请刷新磁盘列表。");
            DiskTarget current = ListDisks().Find(delegate(DiskTarget d) { return d.DiskNumber == original.DiskNumber && String.Equals(d.Identity, original.Identity, StringComparison.OrdinalIgnoreCase) && String.Equals(d.Root, original.Root, StringComparison.OrdinalIgnoreCase); });
            if (current == null || !SameRoots(current.RelatedRoots, original.RelatedRoots)) throw new InvalidOperationException("磁盘身份、盘符或分区已变化。已取消操作，请刷新并重新选择。");
            return current;
        }

        private static bool SameRoots(List<string> a, List<string> b)
        {
            return a != null && b != null && new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
        }

        public static ActionResult ValidateDisk(DiskTarget original)
        {
            try { Revalidate(original); return new ActionResult(true, "磁盘身份已确认。"); }
            catch (Exception ex) { return new ActionResult(false, ex.Message); }
        }

        public static ScanResult Scan(DiskTarget d, int timeoutSeconds)
        {
            timeoutSeconds = Math.Max(5, Math.Min(timeoutSeconds, 120));
            ScanResult result = new ScanResult();
            Dictionary<int, Occupant> found = new Dictionary<int, Occupant>();
            object gate = new object();
            bool ended = false;
            string warning = "";
            try
            {
                DiskTarget current = Revalidate(d);
                WorkerRequest request = new WorkerRequest { Target = current, ParentPid = Process.GetCurrentProcess().Id, TimeoutSeconds = timeoutSeconds };
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(request)));
                ProcessStartInfo info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--scan-worker " + encoded)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                };
                using (Process worker = new Process())
                {
                    worker.StartInfo = info;
                    worker.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (String.IsNullOrWhiteSpace(e.Data)) return;
                        try
                        {
                            WorkerEvent message = new JavaScriptSerializer { MaxJsonLength = 16777216 }.Deserialize<WorkerEvent>(e.Data);
                            lock (gate)
                            {
                                if (message.Kind == "item" && message.Item != null) found[message.Item.Pid] = message.Item;
                                if (message.Kind == "end") { ended = true; result.Complete = message.Complete; result.SkippedProcesses = message.Skipped; warning = message.Message ?? ""; }
                                if (message.Kind == "error") warning = message.Message ?? "扫描失败。";
                            }
                        }
                        catch { lock (gate) { warning = "扫描辅助进程返回了无法读取的数据，结果可能不完整。"; } }
                    };
                    worker.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { };
                    if (!worker.Start()) throw new InvalidOperationException("无法启动扫描辅助进程。");
                    lock (WorkerGate) { ActiveWorkers.Add(worker); }
                    try
                    {
                    worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
                    if (!worker.WaitForExit(timeoutSeconds * 1000))
                    {
                        try { worker.Kill(); } catch { }
                        worker.WaitForExit(2000);
                        lock (gate) { result.Complete = false; warning = "扫描达到 " + timeoutSeconds.ToString(CultureInfo.InvariantCulture) + " 秒上限，已停止辅助进程。以下为已确认的占用；结果不完整，可关闭已知软件后重扫。"; }
                    }
                    else worker.WaitForExit();
                    }
                    finally { lock (WorkerGate) { ActiveWorkers.Remove(worker); } }
                }
                try { Revalidate(current); }
                catch (Exception ex)
                {
                    lock (gate) { found.Clear(); result.Complete = false; ended = false; warning = "扫描期间磁盘发生变化，已丢弃旧结果。" + ex.Message; }
                }
            }
            catch (Exception ex) { warning = "无法完成扫描：" + ex.Message; }
            lock (gate)
            {
                if (!ended) result.Complete = false;
                result.Items = found.Values.OrderBy(delegate(Occupant o) { return o.Protected; }).ThenBy(delegate(Occupant o) { return o.Name; }, StringComparer.OrdinalIgnoreCase).ToList();
                result.Warning = warning;
            }
            return result;
        }

        public static int WorkerMain(string[] args)
        {
            try
            {
                // A GUI worker has redirected pipes but no console. Setting
                // Console.OutputEncoding calls SetConsoleOutputCP on Framework
                // and throws ERROR_INVALID_HANDLE before the scan can start.
                // Write UTF-8 directly to the pipe instead.
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                if (args.Length < 2) throw new ArgumentException("缺少扫描参数。");
                WorkerRequest request = Json.Deserialize<WorkerRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
                if (request == null || request.Target == null) throw new ArgumentException("扫描参数无效。");
                EnableDebugPrivilege();
                DiskTarget current = Revalidate(request.Target);
                ScanWorker(current, request.ParentPid, Math.Max(3, request.TimeoutSeconds - 2));
                return 0;
            }
            catch (Exception ex) { Emit(new WorkerEvent { Kind = "error", Message = ex.Message }); return 1; }
        }

        private static void Emit(WorkerEvent message)
        {
            Console.WriteLine(Json.Serialize(message)); Console.Out.Flush();
        }

        private static void ScanWorker(DiskTarget target, int parentPid, int seconds)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Dictionary<int, Occupant> records = new Dictionary<int, Occupant>();
            HashSet<int> services;
            bool servicesKnown = ReadServicePids(out services);
            HashSet<int> skipped = new HashSet<int>();
            HashSet<int> ignored = new HashSet<int> { Process.GetCurrentProcess().Id, parentPid };
            Dictionary<int, IntPtr> processHandles = new Dictionary<int, IntPtr>();
            bool complete = true;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        int pid;
                        try { pid = p.Id; } catch { continue; }
                        if (ignored.Contains(pid)) continue;
                        IntPtr h = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (h == IntPtr.Zero) { skipped.Add(pid); continue; }
                        try
                        {
                            string exe = GetImagePath(h);
                            if (MatchesTarget(exe, target)) AddOccupant(records, pid, h, target, exe, services, servicesKnown);
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    if (clock.Elapsed.TotalSeconds >= seconds) { complete = false; break; }
                }
                if (complete)
                {
                    IntPtr nul = Native.CreateFile("NUL", 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    IntPtr buffer = IntPtr.Zero;
                    try
                    {
                        int length = 1048576, required = 0, status;
                        while (true)
                        {
                            buffer = Marshal.AllocHGlobal(length);
                            status = Native.NtQuerySystemInformation(64, buffer, length, out required);
                            if (status == 0) break;
                            Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero;
                            if (status != unchecked((int)0xC0000004) && status != unchecked((int)0xC0000023)) throw new InvalidOperationException("Windows 拒绝系统句柄查询（0x" + status.ToString("X8", CultureInfo.InvariantCulture) + "）；请尝试管理员身份扫描。");
                            long next = Math.Max((long)length * 2, (long)required + 65536);
                            if (next > 268435456) throw new InvalidOperationException("系统句柄表过大，已安全停止扫描。");
                            length = (int)next;
                        }
                        int entrySize = IntPtr.Size == 8 ? 40 : 28;
                        int header = IntPtr.Size * 2;
                        long count = ReadPointerNumber(buffer, 0);
                        if (count < 0 || count > (length - header) / entrySize) throw new InvalidOperationException("系统句柄结构与当前版本不兼容，已安全停止扫描。");
                        int fileType = -1;
                        int ownPid = Process.GetCurrentProcess().Id;
                        for (long i = 0; i < count; i++)
                        {
                            IntPtr row = IntPtr.Add(buffer, header + checked((int)(i * entrySize)));
                            if (ReadPointerNumber(row, IntPtr.Size) == ownPid && ReadPointerNumber(row, IntPtr.Size * 2) == nul.ToInt64()) { fileType = (ushort)Marshal.ReadInt16(row, IntPtr.Size * 3 + 6); break; }
                        }
                        if (fileType < 0) throw new InvalidOperationException("无法确认文件句柄类型，已停止深度扫描。");
                        for (long i = 0; i < count; i++)
                        {
                            if (clock.Elapsed.TotalSeconds >= seconds) { complete = false; break; }
                            IntPtr row = IntPtr.Add(buffer, header + checked((int)(i * entrySize)));
                            if ((ushort)Marshal.ReadInt16(row, IntPtr.Size * 3 + 6) != fileType) continue;
                            long pidValue = ReadPointerNumber(row, IntPtr.Size);
                            if (pidValue <= 0 || pidValue > Int32.MaxValue) continue;
                            int pid = (int)pidValue;
                            if (ignored.Contains(pid)) continue;
                            IntPtr process;
                            if (!processHandles.TryGetValue(pid, out process))
                            {
                                process = Native.OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                                processHandles[pid] = process;
                                if (process == IntPtr.Zero) skipped.Add(pid);
                            }
                            if (process == IntPtr.Zero) continue;
                            IntPtr duplicate;
                            if (!Native.DuplicateHandle(process, new IntPtr(ReadPointerNumber(row, IntPtr.Size * 2)), Native.GetCurrentProcess(), out duplicate, 0, false, 2)) continue;
                            try
                            {
                                if (Native.GetFileType(duplicate) != 1) continue;
                                string path = NameOfFile(duplicate);
                                if (MatchesTarget(path, target)) AddOccupant(records, pid, process, target, path, services, servicesKnown);
                            }
                            finally { Native.CloseHandle(duplicate); }
                        }
                    }
                    finally
                    {
                        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                        if (nul != new IntPtr(-1) && nul != IntPtr.Zero) Native.CloseHandle(nul);
                    }
                }
                // Supplement open handles with loaded DLL mappings. Do this last so actual
                // open files take priority in the bounded list of displayed paths.
                if (complete)
                {
                    foreach (Process p in Process.GetProcesses())
                    {
                        using (p)
                        {
                            int pid; try { pid = p.Id; } catch { continue; }
                            if (ignored.Contains(pid)) continue;
                            IntPtr h = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                            if (h == IntPtr.Zero) continue;
                            try
                            {
                                foreach (ProcessModule module in p.Modules)
                                    if (MatchesTarget(module.FileName, target)) AddOccupant(records, pid, h, target, module.FileName, services, servicesKnown);
                            }
                            catch { }
                            finally { Native.CloseHandle(h); }
                        }
                        if (clock.Elapsed.TotalSeconds >= seconds) { complete = false; break; }
                    }
                }
            }
            finally { foreach (IntPtr h in processHandles.Values) if (h != IntPtr.Zero) Native.CloseHandle(h); }
            string warning = "扫描仅显示已确认的占用。受保护进程、内核驱动及部分内存映射可能无法列出；最终以 Windows 安全弹出结果为准。";
            if (skipped.Count > 0) warning += " 有 " + skipped.Count.ToString(CultureInfo.InvariantCulture) + " 个进程受权限限制。";
            if (!servicesKnown) warning += " 服务信息不可用，已禁用自动关闭。";
            if (!complete) warning = "扫描达到时间上限，以下结果不完整。" + warning;
            foreach (Occupant item in records.Values) Emit(new WorkerEvent { Kind = "item", Item = item });
            Emit(new WorkerEvent { Kind = "end", Complete = complete, Skipped = skipped.Count, Message = warning });
        }

        private static long ReadPointerNumber(IntPtr p, int offset)
        {
            return IntPtr.Size == 8 ? Marshal.ReadInt64(p, offset) : (long)(uint)Marshal.ReadInt32(p, offset);
        }

        private static void AddOccupant(Dictionary<int, Occupant> records, int pid, IntPtr handle, DiskTarget target, string path, HashSet<int> services, bool servicesKnown)
        {
            Occupant o;
            if (!records.TryGetValue(pid, out o))
            {
                o = DescribeProcess(pid, handle, services, servicesKnown);
                records.Add(pid, o);
            }
            string display = DisplayPath(path, target);
            if (!o.Paths.Contains(display, StringComparer.OrdinalIgnoreCase) && o.Paths.Count < 40)
            {
                o.Paths.Add(display);
                if (o.Paths.Count == 1 || o.Paths.Count == 8 || o.Paths.Count == 24 || o.Paths.Count == 40) Emit(new WorkerEvent { Kind = "item", Item = o });
            }
        }

        private static Occupant DescribeProcess(int pid, IntPtr handle, HashSet<int> services, bool servicesKnown)
        {
            Occupant o = new Occupant { Pid = pid, ExePath = GetImagePath(handle) };
            try { using (Process p = Process.GetProcessById(pid)) o.Name = p.ProcessName; } catch { o.Name = "PID " + pid.ToString(CultureInfo.InvariantCulture); }
            o.StartTicks = StartTime(handle);
            string reason = ProtectionReason(pid, handle, o.Name, o.ExePath, o.StartTicks, services, servicesKnown);
            o.Protected = !String.IsNullOrEmpty(reason);
            o.ProtectionReason = reason;
            o.CanForce = !o.Protected;
            o.CanClose = !o.Protected && FindWindows(pid).Count > 0;
            return o;
        }

        private static string ProtectionReason(int pid, IntPtr handle, string name, string exe, long start, HashSet<int> services, bool servicesKnown)
        {
            if (pid <= 4 || ProtectedNames.Contains(name)) return "Windows 系统进程，禁止自动关闭。";
            if (String.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)) return "资源管理器进程受保护；请关闭此磁盘对应的文件夹窗口。";
            if (pid == Process.GetCurrentProcess().Id || String.Equals(exe, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase)) return "此工具自身，禁止自动关闭。";
            uint session;
            if (!Native.ProcessIdToSessionId((uint)pid, out session) || session != (uint)Process.GetCurrentProcess().SessionId) return "其它登录会话的进程，禁止自动关闭。";
            if (!servicesKnown) return "无法核实系统服务信息，已禁止自动关闭。";
            if (services.Contains(pid)) return "Windows 服务进程，禁止自动关闭。";
            bool critical;
            if (!Native.IsProcessCritical(handle, out critical)) return "无法核实系统关键进程标记，已禁止自动关闭。";
            if (critical) return "系统关键进程，禁止自动关闭。";
            if (start <= 0 || String.IsNullOrWhiteSpace(exe)) return "无法确认进程身份，已禁止自动关闭。";
            return "";
        }

        private static bool ReadServicePids(out HashSet<int> result)
        {
            result = new HashSet<int>();
            try
            {
                using (ManagementObjectCollection rows = Query(@"root\cimv2", "SELECT ProcessId FROM Win32_Service WHERE ProcessId > 0"))
                    foreach (ManagementObject row in rows) { using (row) result.Add(Convert.ToInt32(row["ProcessId"], CultureInfo.InvariantCulture)); }
                return true;
            }
            catch { return false; }
        }

        public static ActionResult Close(Occupant o, bool force)
        {
            if (o == null || o.Pid <= 4 || o.StartTicks <= 0) return new ActionResult(false, "无法确认进程身份，已取消关闭。");
            IntPtr handle = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE | (force ? PROCESS_TERMINATE : 0), false, o.Pid);
            if (handle == IntPtr.Zero) return new ActionResult(false, "进程已退出或权限不足，请重新扫描。");
            try
            {
                if (Native.WaitForSingleObject(handle, 0) == 0) return new ActionResult(true, "进程已经退出。");
                if (StartTime(handle) != o.StartTicks || !String.Equals(GetImagePath(handle), o.ExePath, StringComparison.OrdinalIgnoreCase)) return new ActionResult(false, "进程身份已变化，已取消操作。请重新扫描。");
                HashSet<int> services; bool servicesKnown = ReadServicePids(out services);
                Occupant current = DescribeProcess(o.Pid, handle, services, servicesKnown);
                if (current.Protected) return new ActionResult(false, current.ProtectionReason);
                if (force)
                {
                    if (!Native.TerminateProcess(handle, 1)) return new ActionResult(false, "Windows 拒绝结束该进程（错误 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "）。");
                    return Native.WaitForSingleObject(handle, 3000) == 0 ? new ActionResult(true, "已强制结束 " + current.Name + "。") : new ActionResult(false, "已请求强制结束，但进程仍未退出；请重新扫描。");
                }
                List<IntPtr> windows = FindWindows(o.Pid);
                if (windows.Count == 0) return new ActionResult(false, "此进程没有可正常关闭的窗口。请在软件内退出，或选择强制结束。");
                bool sent = false;
                if (Native.WaitForSingleObject(handle, 0) == 0) return new ActionResult(true, "进程已经退出。");
                foreach (IntPtr window in windows) sent |= Native.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!sent) return new ActionResult(false, "Windows 拒绝发送关闭请求；可能需要管理员权限。");
                if (Native.WaitForSingleObject(handle, 5000) == 0) return new ActionResult(true, "已正常关闭 " + current.Name + "。");
                return new ActionResult(false, "已请求 " + current.Name + " 正常退出。软件可能正在等待保存或仍在后台运行，请处理后重扫。");
            }
            catch (Exception ex) { return new ActionResult(false, "关闭失败：" + ex.Message); }
            finally { Native.CloseHandle(handle); }
        }

        private static List<IntPtr> FindWindows(int pid)
        {
            List<IntPtr> result = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr window, IntPtr state)
            {
                uint owner; Native.GetWindowThreadProcessId(window, out owner);
                if (owner == (uint)pid && Native.IsWindowVisible(window) && Native.GetWindow(window, 4) == IntPtr.Zero) result.Add(window);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static ActionResult Eject(DiskTarget original)
        {
            // Retain the old API only for compatibility with existing diagnostics.
            // This build never requests removal from Windows.
            return new ActionResult(false, "此版本仅检查占用和关闭软件，不执行磁盘弹出。");
        }

        private static List<VolumeRecord> ReadVolumes()
        {
            List<VolumeRecord> result = new List<VolumeRecord>();
            StringBuilder name = new StringBuilder(1024);
            IntPtr find = Native.FindFirstVolume(name, name.Capacity);
            if (find == new IntPtr(-1)) throw new IOException("无法枚举 Windows 磁盘卷。");
            try
            {
                do
                {
                    string guid = name.ToString();
                    int number = DeviceNumber(guid.TrimEnd('\\'));
                    if (number < 0) continue;
                    VolumeRecord record = new VolumeRecord { GuidPath = guid, DiskNumber = number };
                    string dosName = guid.StartsWith(@"\\?\", StringComparison.Ordinal) ? guid.Substring(4).TrimEnd('\\') : guid.TrimEnd('\\');
                    StringBuilder nt = new StringBuilder(32768);
                    if (Native.QueryDosDevice(dosName, nt, nt.Capacity) > 0) record.NtPath = nt.ToString();
                    char[] paths = new char[32768]; uint needed;
                    if (Native.GetVolumePathNamesForVolumeName(guid, paths, (uint)paths.Length, out needed))
                        foreach (string path in new String(paths).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)) record.Roots.Add(path);
                    result.Add(record);
                } while (Native.FindNextVolume(find, name, name.Capacity));
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new IOException("磁盘卷枚举中断，错误 " + error.ToString(CultureInfo.InvariantCulture) + "。");
            }
            finally { Native.FindVolumeClose(find); }
            return result;
        }

        private static int DeviceNumber(string path)
        {
            IntPtr handle = Native.CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return -1;
            try
            {
                byte[] output = new byte[12]; int returned;
                return Native.DeviceIoControl(handle, 0x002D1080, null, 0, output, output.Length, out returned, IntPtr.Zero) && returned >= 12 ? BitConverter.ToInt32(output, 4) : -1;
            }
            finally { Native.CloseHandle(handle); }
        }

        private static void ReadStorageKind(int number, out int bus, out bool hotplug)
        {
            bus = -1; hotplug = false;
            IntPtr handle = Native.CreateFile(@"\\.\PhysicalDrive" + number.ToString(CultureInfo.InvariantCulture), 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return;
            try
            {
                int returned; byte[] output = new byte[4096];
                if (Native.DeviceIoControl(handle, 0x002D1400, new byte[12], 12, output, output.Length, out returned, IntPtr.Zero) && returned >= 32) bus = BitConverter.ToInt32(output, 28);
                output = new byte[8];
                if (Native.DeviceIoControl(handle, 0x002D0C14, null, 0, output, output.Length, out returned, IntPtr.Zero) && returned >= 8) hotplug = output[6] != 0;
            }
            finally { Native.CloseHandle(handle); }
        }

        private static bool StartsWithRoot(string path, string root)
        {
            return !String.IsNullOrWhiteSpace(path) && !String.IsNullOrWhiteSpace(root) && path.StartsWith(root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesTarget(string path, DiskTarget target)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':') path = path.Substring(4);
            foreach (VolumeRecord v in target.Volumes)
            {
                if (!String.IsNullOrEmpty(v.NtPath) && (String.Equals(path, v.NtPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(v.NtPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return true;
                if (String.Equals(path.TrimEnd('\\'), v.GuidPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || StartsWithRoot(path, v.GuidPath)) return true;
                foreach (string root in v.Roots) if (String.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || StartsWithRoot(path, root)) return true;
            }
            return false;
        }

        private static string DisplayPath(string path, DiskTarget target)
        {
            foreach (VolumeRecord v in target.Volumes)
                if (v.Roots.Count > 0 && !String.IsNullOrEmpty(v.NtPath) && (String.Equals(path, v.NtPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(v.NtPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return v.Roots[0].TrimEnd('\\') + path.Substring(v.NtPath.Length);
            return path;
        }

        private static string GetImagePath(IntPtr process)
        {
            StringBuilder value = new StringBuilder(32768); int length = value.Capacity;
            return Native.QueryFullProcessImageName(process, 0, value, ref length) ? value.ToString() : "";
        }

        private static long StartTime(IntPtr process)
        {
            Native.FILETIME created, exited, kernel, user;
            if (!Native.GetProcessTimes(process, out created, out exited, out kernel, out user)) return 0;
            try { return DateTime.FromFileTimeUtc(((long)created.High << 32) | created.Low).Ticks; } catch { return 0; }
        }

        private static string NameOfFile(IntPtr file)
        {
            StringBuilder path = new StringBuilder(2048);
            uint size = Native.GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 10);
            if (size > 0 && size < path.Capacity) return path.ToString();
            if (size >= path.Capacity && size < 32768)
            {
                path = new StringBuilder((int)size + 1); size = Native.GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 10);
                if (size > 0 && size < path.Capacity) return path.ToString();
            }
            IntPtr data = IntPtr.Zero;
            try
            {
                int length = 2048, needed;
                while (length <= 65536)
                {
                    data = Marshal.AllocHGlobal(length);
                    int status = Native.NtQueryObject(file, 1, data, length, out needed);
                    if (status == 0)
                    {
                        ushort chars = (ushort)Marshal.ReadInt16(data);
                        IntPtr text = Marshal.ReadIntPtr(data, IntPtr.Size == 8 ? 8 : 4);
                        return chars > 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text, chars / 2) : "";
                    }
                    Marshal.FreeHGlobal(data); data = IntPtr.Zero;
                    if (needed <= length) break;
                    length = needed;
                }
                return "";
            }
            finally { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); }
        }

        private static void EnableDebugPrivilege()
        {
            IntPtr token;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0020 | 0x0008, out token)) return;
            try
            {
                Native.LUID luid;
                if (Native.LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                {
                    Native.TOKEN_PRIVILEGES privilege = new Native.TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };
                    Native.AdjustTokenPrivileges(token, false, ref privilege, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally { Native.CloseHandle(token); }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FILETIME { public uint Low; public uint High; }
        [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
        internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateHandle(IntPtr source, IntPtr sourceHandle, IntPtr target, out IntPtr targetHandle, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(IntPtr handle, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessCritical(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeviceIoControl(IntPtr device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr FindFirstVolume(StringBuilder name, int capacity);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FindNextVolume(IntPtr find, StringBuilder name, int capacity);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FindVolumeClose(IntPtr find);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] paths, uint length, out uint needed);
        [DllImport("ntdll.dll")] internal static extern int NtQuerySystemInformation(int kind, IntPtr buffer, int length, out int returned);
        [DllImport("ntdll.dll")] internal static extern int NtQueryObject(IntPtr handle, int kind, IntPtr buffer, int length, out int returned);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES privileges, uint size, IntPtr previous, IntPtr returned);
    }
}
