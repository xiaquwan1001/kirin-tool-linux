// Hardware-free harness for the USB-update verdict (audit D-04, docs/usb-update-verdict.md).
//
// Everything here answers one question: can the USB-update channel claim a success it has no
// evidence for? Cases (numbering follows docs/usb-update-verdict.md §5):
//   1. handshake never answered + zero partitions written -> failed (was: "return true")
//   2. handshake never answered, every partition written   -> partial, never verified
//   3. a required partition's .img is missing              -> skipped + at most partial, no bytes
//   4. the image stream ends before its declared size      -> missing + degraded (was: success)
//   5. every required partition answered                   -> verified
//   6. Describe() carries every count                      -> GUI/CLI see all three states
//   7. the legacy bool                                     -> true only for verified
//   8. the verdict builder itself                          -> required set, fatal, non-selected
// No device is touched: the transport is FakeTransport below, the images are temp files.

using Kirin_Tool.Services.USBUpdate;
using Kirin_Tool.Services.USBUpdate.Transport;

int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

string dload = Path.Combine(Path.GetTempPath(), "kirin-usb-verdict-" + Guid.NewGuid().ToString("N"));
byte[] ImageBytes = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
byte[] HeaderBytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

void Prepare(IEnumerable<string> names, Func<string, bool>? skipImage = null,
    Func<string, bool>? skipHeader = null, Func<string, bool>? zeroImage = null,
    IEnumerable<string>? extraListLines = null)
{
    if (Directory.Exists(dload)) Directory.Delete(dload, true);
    Directory.CreateDirectory(dload);
    var list = names.Select(n => n + " 1").ToList();
    if (extraListLines != null) list.AddRange(extraListLines);
    File.WriteAllLines(Path.Combine(dload, "list.txt"), list);
    foreach (string name in names)
    {
        if (skipImage?.Invoke(name) != true)
        {
            byte[] bytes = zeroImage?.Invoke(name) == true ? Array.Empty<byte>() : ImageBytes;
            File.WriteAllBytes(Path.Combine(dload, name + ".img"), bytes);
        }
        if (skipHeader?.Invoke(name) != true)
            File.WriteAllBytes(Path.Combine(dload, name + ".img.header"), HeaderBytes);
    }
}

UsbUpdateVerdict Run(FakeTransport transport, IEnumerable<string>? required = null,
    Func<string, Stream>? openImage = null, List<UsbUpdateEvent>? events = null, List<string>? log = null)
{
    var service = new UsbDownloadService(line => log?.Add(line), dload, () => transport)
    {
        OpenImageStream = openImage,
    };
    return service.FlashImagesWithVerdict(required, events == null ? null : evt => events.Add(evt));
}

bool RunLegacy(FakeTransport transport)
{
    var service = new UsbDownloadService(_ => { }, dload, () => transport);
    return service.FlashImages();
}

// 1. handshake never answered + nothing written -> failed
Prepare(new[] { "XLOADER" });
var t1 = new FakeTransport { AnswerHandshake = false, AnswerCommands = false };
var log1 = new List<string>();
var events1 = new List<UsbUpdateEvent>();
UsbUpdateVerdict v1 = Run(t1, new[] { "XLOADER" }, null, events1, log1);
Check(v1.Outcome == UsbUpdateOutcome.Failed, "1 handshake missing + zero writes => failed");
Check(!v1.HandshakeEstablished, "1 HandshakeEstablished is false");
Check(v1.Written.Count == 0 && v1.RequiredWrittenCount == 0, "1 nothing is reported as written");
Check(log1.Any(line => line.StartsWith("degraded: ", StringComparison.Ordinal)),
    "1 the missing handshake is a degraded: line in the log");
Check(events1.Any(e => e.Kind == UsbUpdateEventKind.HandshakeMissing && e.Token == "degraded"),
    "1 the missing handshake is a structured degraded event");
Check(t1.Commands.Count(c => c.Length > 0 && c[0] == 0x26) == 3,
    "1 still exactly 3 handshake attempts (protocol unchanged)");

// 2. handshake never answered but the partition was written -> partial, never verified
Prepare(new[] { "XLOADER" });
var t2 = new FakeTransport { AnswerHandshake = false };
UsbUpdateVerdict v2 = Run(t2, new[] { "XLOADER" });
Check(v2.Outcome == UsbUpdateOutcome.Partial, "2 handshake missing + write ok => partial");
Check(v2.Written.Contains("XLOADER"), "2 the written partition is recorded");
Check(!v2.Verified, "2 an unhandshaken transfer is never verified");

// 3. a required partition's .img is missing -> skipped, at most partial, no commands for it
Prepare(new[] { "BOOT", "SYS" }, skipImage: name => name == "BOOT");
var t3 = new FakeTransport();
var events3 = new List<UsbUpdateEvent>();
UsbUpdateVerdict v3 = Run(t3, new[] { "BOOT", "SYS" }, null, events3);
Check(v3.Outcome == UsbUpdateOutcome.Partial, "3 missing required .img => at most partial");
Check(v3.Skipped.Any(skip => skip.Partition == "BOOT" && skip.Required), "3 BOOT is a required skip");
Check(events3.Any(e => e.Kind == UsbUpdateEventKind.Skipped && e.Token == "skipped"), "3 skipped: event prefix");
int headCommands = t3.Commands.Count(c => c.Length > 1 && c[0] == 0x7E && c[1] == 0x41);
Check(headCommands == 1, "3 the skipped partition sent no head command at all");

// 3b. a missing .img.header is a skip too
Prepare(new[] { "SYS", "BOOT" }, skipHeader: name => name == "BOOT");
UsbUpdateVerdict v3b = Run(new FakeTransport(), new[] { "SYS", "BOOT" });
Check(v3b.Outcome == UsbUpdateOutcome.Partial && v3b.Skipped.Any(s => s.Partition == "BOOT"),
    "3b missing .img.header => required skip, partial");

// 3c. a 0-byte image is a missing partition
Prepare(new[] { "SYS", "BOOT" }, zeroImage: name => name == "BOOT");
var events3c = new List<UsbUpdateEvent>();
UsbUpdateVerdict v3c = Run(new FakeTransport(), new[] { "SYS", "BOOT" }, null, events3c);
Check(v3c.Missing.Contains("BOOT"), "3c a 0-byte image is a missing partition");
Check(events3c.Any(e => e.Kind == UsbUpdateEventKind.Missing && e.Token == "missing"), "3c missing: event prefix");

// 4. the image stream ends before its declared size -> missing + degraded (was: success)
Prepare(new[] { "SYS", "BOOT" });
var events4 = new List<UsbUpdateEvent>();
var log4 = new List<string>();
UsbUpdateVerdict v4 = Run(new FakeTransport(), new[] { "SYS", "BOOT" },
    path => path.Contains("BOOT", StringComparison.Ordinal)
        ? new MemoryStream(Array.Empty<byte>())
        : new FileStream(path, FileMode.Open, FileAccess.Read),
    events4, log4);
Check(v4.Outcome == UsbUpdateOutcome.Partial, "4 an early stream end cannot be verified");
Check(v4.Missing.Contains("BOOT"), "4 the cut-off partition counts as missing");
Check(events4.Any(e => e.Kind == UsbUpdateEventKind.StreamEndedEarly), "4 stream-ended-early is a structured event");
Check(log4.Any(line => line.StartsWith("degraded: ", StringComparison.Ordinal)
        && line.Contains("stream ended early", StringComparison.Ordinal)),
    "4 stream-ended-early is a degraded: log line");

// 5. every required partition answered -> verified
Prepare(new[] { "BOOT", "SYS" });
var t5 = new FakeTransport();
var events5 = new List<UsbUpdateEvent>();
UsbUpdateVerdict v5 = Run(t5, new[] { "BOOT", "SYS" }, null, events5);
Check(v5.Outcome == UsbUpdateOutcome.Verified, "5 every required partition answered => verified");
Check(v5.Verified && v5.HandshakeEstablished, "5 verified implies handshaken");
Check(v5.RequiredWrittenCount == 2 && v5.Written.Count == 2, "5 both partitions are recorded as written");
Check(events5.Count(e => e.Kind == UsbUpdateEventKind.Written) == 2, "5 one ok: event per written partition");
Check(events5.All(e => e.Fields.Any(f => f.Key == "event") && e.Fields.Any(f => f.Key == "partition")),
    "5 every event carries structured fields");

// 6. Describe() names every count, for both the GUI and the CLI
string partialText = v3.Describe();
Check(partialText.Contains("partial", StringComparison.Ordinal)
    && partialText.Contains("required=1/2", StringComparison.Ordinal)
    && partialText.Contains("written=1", StringComparison.Ordinal)
    && partialText.Contains("skipped=1", StringComparison.Ordinal)
    && partialText.Contains("missing=0", StringComparison.Ordinal)
    && partialText.Contains("handshake=established", StringComparison.Ordinal),
    "6 Describe() on a partial transfer carries every count");
string failedText = v1.Describe();
Check(failedText.Contains("failed", StringComparison.Ordinal)
    && failedText.Contains("handshake=not-established", StringComparison.Ordinal),
    "6 Describe() on a failed transfer names the handshake state");
Check(v5.Describe().Contains("verified", StringComparison.Ordinal), "6 Describe() on a verified transfer");

// 7. the legacy bool is `verified`, not `did not throw`
Prepare(new[] { "XLOADER" });
Check(!RunLegacy(new FakeTransport { AnswerHandshake = false }), "7 FlashImages() is false for partial");
Prepare(new[] { "XLOADER" });
Check(RunLegacy(new FakeTransport()), "7 FlashImages() is true only for verified");

// 7b. a transfer whose device rejects the very first command is failed, not thrown
Prepare(new[] { "XLOADER" });
var t7 = new FakeTransport { AnswerCommands = false };
UsbUpdateVerdict v7 = Run(t7, new[] { "XLOADER" });
Check(v7.Outcome == UsbUpdateOutcome.Failed && !v7.Verified, "7b device-rejected head command => failed verdict (no throw)");

// 8. the builder rules themselves
var builder = new UsbUpdateVerdictBuilder(new[] { "BOOT" });
builder.HandshakeEstablished();
builder.NoteSkipped("BOOT", "image file is missing");
UsbUpdateVerdict builtMissing = builder.Build();
Check(builtMissing.Outcome == UsbUpdateOutcome.Failed, "8 no required partition written => failed");

var fatal = new UsbUpdateVerdictBuilder(new[] { "BOOT" });
fatal.HandshakeEstablished();
fatal.NoteWritten("BOOT");
fatal.NoteFatal("transfer preparation: list.txt was not found");
Check(fatal.Build().Outcome == UsbUpdateOutcome.Failed, "8 a fatal plan error outranks a written partition");

var planDecides = new UsbUpdateVerdictBuilder();
planDecides.DeclareRequired("SYS");
planDecides.DeclareRequired("BOOT");
planDecides.HandshakeEstablished();
planDecides.NoteWritten("SYS");
planDecides.NoteWritten("BOOT");
Check(planDecides.Build().Outcome == UsbUpdateOutcome.Verified, "8 an empty required set means \"the plan decides\"");

var decoy = new UsbUpdateVerdictBuilder(new[] { "BOOT" });
decoy.HandshakeEstablished();
decoy.NoteWritten("BOOT");
decoy.NoteSkipped("EXTRA", "not selected for transfer (list.txt flag=0)");
UsbUpdateVerdict decoyVerdict = decoy.Build();
Check(decoyVerdict.Outcome == UsbUpdateOutcome.Verified, "8 a non-selected partition is skipped, not required");
Check(decoyVerdict.Skipped.Single().Required == false, "8 the non-selected skip is marked not-required");

// 8b. list.txt flag != 1 is a declared skip, and list decides the required set
Prepare(new[] { "BOOT" }, extraListLines: new[] { "EXTRA 0" });
UsbUpdateVerdict v8 = Run(new FakeTransport());
Check(v8.Outcome == UsbUpdateOutcome.Verified, "8b only flag==1 entries are required");
Check(v8.Skipped.Any(skip => skip.Partition == "EXTRA" && !skip.Required), "8b the unselected entry is a declared skip");

// 8c. no list.txt at all => failed with a reason
if (Directory.Exists(dload)) Directory.Delete(dload, true);
Directory.CreateDirectory(dload);
UsbUpdateVerdict v9 = Run(new FakeTransport());
Check(v9.Outcome == UsbUpdateOutcome.Failed && v9.Reason.Contains("list.txt", StringComparison.Ordinal),
    "8c a missing list.txt is failed, with a reason");

try { Directory.Delete(dload, true); } catch { }
Console.WriteLine("all " + checks + " USB-update verdict checks passed");

sealed class FakeTransport : IUpdateTransport
{
    private readonly Queue<byte> _incoming = new();
    public readonly List<byte[]> Commands = new();
    public bool AnswerHandshake { get; init; } = true;
    public bool AnswerCommands { get; init; } = true;

    public int ReadTimeout { get; set; } = 5000;
    public int BytesToRead => _incoming.Count;
    public int ReadByte() => _incoming.Count > 0 ? _incoming.Dequeue() : -1;
    public int Read(byte[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count && _incoming.Count > 0) buffer[offset + read++] = _incoming.Dequeue();
        return read;
    }
    public void DiscardInBuffer() => _incoming.Clear();
    public void Close() { }
    public void Dispose() { }

    public void Write(byte[] buffer, int offset, int count)
    {
        byte[] command = buffer.Skip(offset).Take(count).ToArray();
        Commands.Add(command);
        if (command.Length > 0 && command[0] == 0x26)
        {
            if (AnswerHandshake) Enqueue(new byte[] { 0x7E, 0x26, 0x00, 0x00, 0x25, 0xA7, 0x00, 0x00, 0x7E });
            return;
        }
        if (!AnswerCommands) return;
        Enqueue(new byte[] { 0x7E, 0x02, 0x6A, 0xD3, 0x7E });
    }

    private void Enqueue(byte[] bytes)
    {
        foreach (byte value in bytes) _incoming.Enqueue(value);
    }
}
