// Hardware-free regression for the USB-update redline gate.
//
// The gate lives inside USBUpdateFlasherService.FlashPartitions(): the partition names
// only exist after the package has been unpacked, so "refuse before writing" can only be
// proven end to end by driving that method with a synthetic UPDATE.APP. No device, no
// fastboot, no USB: the refusal path never reaches the transport, and the allowed path
// stops at the transport handshake which fails offline.
using Kirin_Tool.Services.USBUpdate;
using System.Text;
using System.Collections.Concurrent;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

string work = Path.Combine(Path.GetTempPath(), "kirin-redline-gate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);

try
{
    string CleanApp = MakeApp("UPDATE-clean.APP", ("BOOT", new byte[] { 1, 2, 3, 4 }));
    string RedlineApp = MakeApp("UPDATE-redline.APP", ("BOOT", new byte[] { 1, 2, 3, 4 }), ("OEMINFO", new byte[] { 5, 6, 7, 8 }));

    // ---- case 1: clean plan, gate must not interfere ----
    {
        string dir = Path.Combine(work, "clean");
        var log = new ConcurrentQueue<string>();
        var svc = new USBUpdateFlasherService(m => log.Enqueue(m), dir, (_, _) => Task.FromResult(false));
        Exception? failure = await RunBounded(svc, CleanApp, dir, allowRedline: false);
        if (failure != null) Console.WriteLine("  (clean plan stopped later at: " + failure.GetType().Name + ": " + failure.Message.Split('\n')[0] + ")");
        Check(failure is not InvalidOperationException || !failure.Message.Contains("redline", StringComparison.OrdinalIgnoreCase),
            "clean plan is not refused by the redline gate");
        Check(File.Exists(Path.Combine(dir, "list.txt")), "clean plan reaches the writer stage (list.txt written)");
        Check(!log.Any(l => l.Contains("redline:")) , "clean plan logs no redline line");
    }

    // ---- case 2: redline partition, not allowed -> refuse before any write ----
    {
        string dir = Path.Combine(work, "refused");
        var log = new ConcurrentQueue<string>();
        var svc = new USBUpdateFlasherService(m => log.Enqueue(m), dir, (_, _) => Task.FromResult(false));
        Exception? failure = await RunBounded(svc, RedlineApp, dir, allowRedline: false);
        Check(failure is InvalidOperationException, "redline plan throws InvalidOperationException");
        Check(failure!.Message.Contains("OEMINFO"), "refusal names the offending partition (OEMINFO)");
        Check(failure.Message.Contains("No bytes have been written"), "refusal states nothing was written");
        Check(!File.Exists(Path.Combine(dir, "list.txt")), "refusal happens before list.txt is written");
        Check(!File.Exists(Path.Combine(dir, "partition_mapping.txt")), "refusal happens before partition_mapping.txt is written");
        Check(log.Any(l => l.Contains("redline: refused")), "refusal is logged");
    }

    // ---- case 3: redline partition, explicitly allowed -> gate opens ----
    {
        string dir = Path.Combine(work, "allowed");
        var log = new ConcurrentQueue<string>();
        var svc = new USBUpdateFlasherService(m => log.Enqueue(m), dir, (_, _) => Task.FromResult(false));
        Exception? failure = await RunBounded(svc, RedlineApp, dir, allowRedline: true);
        Check(failure is not InvalidOperationException, "allowed redline plan is not refused");
        Check(File.Exists(Path.Combine(dir, "list.txt")), "allowed redline plan reaches the writer stage");
        Check(File.ReadAllText(Path.Combine(dir, "list.txt")).Contains("OEMINFO", StringComparison.OrdinalIgnoreCase),
            "allowed plan lists OEMINFO for transfer");
        Check(log.Any(l => l.Contains("proceeding because allowRedline=true")), "explicit allowance is logged");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " redline gate checks passed; no device accessed.");

static async Task<Exception?> RunBounded(USBUpdateFlasherService svc, string app, string dir, bool allowRedline)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    // SelectedPartitions = null means "every partition in the package"; an empty list
    // would filter everything out and the service would stop before discovery.
    var data = new List<(string? FilePath, string Label, List<string> SelectedPartitions)>
    {
        (app, "synthetic", null!),
    };
    try
    {
        await svc.FlashPartitions(data, cts.Token, allowRedline);
        return null;
    }
    catch (OperationCanceledException)
    {
        // Reached the transport stage and blocked on a device that is not there:
        // that is "past the gate", which is what the assertions check.
        return null;
    }
    catch (Exception ex)
    {
        return ex;
    }
}

static string MakeApp(string fileName, params (string Name, byte[] Data)[] entries)
{
    string path = Path.Combine(Path.GetTempPath(), "kirin-redline-app-" + Guid.NewGuid().ToString("N") + "-" + fileName);
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write(new byte[12]); // container prefix
    foreach ((string name, byte[] data) in entries)
    {
        writer.Write(new byte[] { 0x55, 0xaa, 0x5a, 0xa5 });
        writer.Write(102); writer.Write(0); writer.Write(Encoding.ASCII.GetBytes("HWTEST00")); writer.Write(0);
        writer.Write((uint)data.Length); writer.Write(new byte[32]);
        byte[] raw = new byte[32]; Encoding.ASCII.GetBytes(name).CopyTo(raw, 0); writer.Write(raw);
        writer.Write(new byte[10]); writer.Write(data);
        while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
    }
    return path;
}
