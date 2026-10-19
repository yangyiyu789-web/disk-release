using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Test-only process. It never accesses a drive supplied by the application UI.
// Arguments: fileToHold, readyMarker, stopMarker. Caller must use a task-local directory.
internal static class BlockerFixture
{
    private static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        string file = Path.GetFullPath(args[0]);
        string ready = Path.GetFullPath(args[1]);
        string stop = Path.GetFullPath(args[2]);
        string directory = Path.GetDirectoryName(file);
        if (!String.Equals(directory, Path.GetDirectoryName(ready), StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(directory, Path.GetDirectoryName(stop), StringComparison.OrdinalIgnoreCase)) return 3;
        using (var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            stream.WriteByte(0x44);
            stream.Flush();
            using (var process = Process.GetCurrentProcess())
                File.WriteAllText(ready, process.Id + "|" + process.StartTime.ToUniversalTime().Ticks);
            DateTime deadline = DateTime.UtcNow.AddMinutes(3);
            while (!File.Exists(stop) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        }
        return 0;
    }
}
