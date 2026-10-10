using FlyingThumbManager;
using System.Text;

void Require(bool condition, string name) { if (!condition) throw new Exception(name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception(name); }
var results = DiagnosticReport.Parse("FTDIAG|0|BOOT|v2\nFTDIAG|1|RAM|FAIL|address mismatch\nFTDIAG|2|GPIO|DATA40|PASS|HIGH=1\nFTDIAG|3|PSRAM|SKIP|not enabled\nFTDIAG|4|LED|UNVERIFIED_NO_LIGHT_SENSOR\ntruncated|PASS");
Require(results.Count == 4 && results[0].Status == "FAIL" && results[1].Test == "GPIO / DATA40" && results[2].Status == "SKIP" && results[3].Status == "UNVERIFIED", "Result statuses must retain failures, skips and physical limits");
Require(DiagnosticReport.Parse("[DIFFERENT] Installed firmware comparison").Single().Status == "DIFFERENT", "A reference mismatch must not become a hardware failure");
Require(DiagnosticReport.AssessActive("FTDIAG|0|RAM|PASS").Any(result => result.Status == "INCOMPLETE"), "A passing fragment must never represent a full passing suite");
DiagnosticReport.ValidateSecurity("Secure Boot: Disabled\nFlash Encryption: Disabled");
DiagnosticReport.ValidateFlashCapacity("Detected flash size: 16MB");
Reject(() => DiagnosticReport.ValidateFlashCapacity("Detected flash size: 8MB"), "Flash smaller than the diagnostic layout must be rejected");
Reject(() => DiagnosticReport.ValidateFlashCapacity("Manufacturer: ef"), "Unknown flash capacity must be rejected");
Reject(() => DiagnosticReport.ValidateSecurity("Secure Boot: Enabled\nFlash Encryption: Disabled"), "Signed firmware requirement must prevent diagnostic replacement");
Reject(() => DiagnosticReport.ValidateSecurity("Secure Boot: Disabled\nFlash Encryption: Enabled"), "Encrypted flash must prevent diagnostic replacement");
Reject(() => DiagnosticReport.ValidateSecurity("Security Information:"), "Unknown security state must prevent diagnostic replacement");
Require(DiagnosticReport.ExtractMac("MAC: 44:1B:F6:ED:92:78") == "44:1b:f6:ed:92:78", "Stable identity normalization");
Reject(() => DiagnosticReport.ExtractMac("Chip is ESP32-S3"), "Missing identity must fail closed");
byte[] table = new byte[4096];
void Entry(int index, string label, byte type, byte subtype, uint offset, uint size) {
  int start = index * 32; BitConverter.GetBytes((ushort)0x50aa).CopyTo(table, start); table[start + 2] = type; table[start + 3] = subtype;
  BitConverter.GetBytes(offset).CopyTo(table, start + 4); BitConverter.GetBytes(size).CopyTo(table, start + 8); Encoding.ASCII.GetBytes(label).CopyTo(table, start + 12);
}
Entry(0,"app0",0,0x10,0x10000,0x640000); Entry(1,"otadata",1,0,0xe000,0x2000); Entry(2,"coredump",1,3,0xff0000,0x10000);
DiagnosticReport.ValidatePartitionLayout(table);
table[2 * 32 + 4] = 1;
Reject(() => DiagnosticReport.ValidatePartitionLayout(table), "Wrong scratch offset must be rejected before writes");
Reject(() => DiagnosticReport.ValidatePartitionLayout(new byte[3]), "Truncated partition table must fail closed");
Console.WriteLine("PASS: result statuses, malformed records, device identity, compatible layout, wrong offsets and truncated layout.");
var folder = Path.Combine(Path.GetTempPath(), "FlyingThumbDiagnosticTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try {
  foreach (var name in DiagnosticSession.BackupNames) File.WriteAllBytes(Path.Combine(folder, name), [1,2,3,4]);
  var session = DiagnosticSession.Create(folder, "44:1b:f6:ed:92:78");
  Require(DiagnosticSession.Load(Path.Combine(folder, "session.json")).Mac == session.Mac, "Session identity survives restart");
  File.WriteAllBytes(Path.Combine(folder, "installed-app0.bin"), [9,8,7]);
  Reject(() => DiagnosticSession.Load(Path.Combine(folder, "session.json")), "Modified recovery image must be rejected");
  Console.WriteLine("PASS: durable recovery identity and altered-backup rejection.");
} finally { Directory.Delete(folder, true); }

using var handler = new RecoveryHandler();
using var transport = new HttpClient(handler);
var client = new FlyingThumbClient(transport);
var drive = new Device { Ip = "127.0.0.1", Claimed = true };
await client.EnterUsbRecoveryAsync(drive, "test-shop-key");
Require(handler.Calls == 1, "Recovery request reaches the expected endpoint");
handler.Status = System.Net.HttpStatusCode.Conflict;
try { await client.EnterUsbRecoveryAsync(drive, "test-shop-key"); throw new Exception("Busy response was ignored"); }
catch (InvalidOperationException ex) { Require(ex.Message.Contains("HTTP 409"), "Busy reason reaches the user"); }
handler.Status = System.Net.HttpStatusCode.Unauthorized;
try { await client.EnterUsbRecoveryAsync(drive, "test-shop-key"); throw new Exception("Key rejection was ignored"); }
catch (UnauthorizedAccessException) { }
Console.WriteLine("PASS: authenticated recovery request, busy refusal and key rejection.");

using var cancellationTransport = new HttpClient(new CancellationHandler());
var cancellationClient = new FlyingThumbClient(cancellationTransport);
var transferTest = Path.Combine(Path.GetTempPath(), "FlyingThumb-cancellation-" + Guid.NewGuid().ToString("N"));
File.WriteAllBytes(transferTest, [1,2,3]);
try {
  using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
  try { await cancellationClient.UploadAsync(drive, transferTest, "cancel-test.bin", "", cancellationToken:cancel.Token); throw new Exception("Upload ignored cancellation"); }
  catch (OperationCanceledException) { }
  using var cancelDownload = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
  try { await cancellationClient.DownloadAsync(drive,"cancel-test.bin",transferTest + ".out",cancellationToken:cancelDownload.Token); throw new Exception("Download ignored cancellation"); }
  catch (OperationCanceledException) { }
} finally { File.Delete(transferTest); if(File.Exists(transferTest+".out"))File.Delete(transferTest+".out"); }
Console.WriteLine("PASS: upload and download cancellation reach the HTTP operation.");

var expandedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var browserFiles = new[] { "Patterns/Flowers/Rose.bin", "Patterns/Stars.bin", "README.txt", "patterns/Flowers/Tulip.bin" };
var collapsed = FileBrowserTree.Build(browserFiles,["Empty folder"],expandedFolders);
Require(collapsed.Count == 3 && collapsed[0].IsFolder && collapsed[^1].Path == "README.txt", "Root view hides nested files and orders folders first");
expandedFolders.Add("Patterns"); expandedFolders.Add("Patterns/Flowers");
var expanded = FileBrowserTree.Build(browserFiles,["Empty folder"],expandedFolders);
Require(expanded.Count == 7 && expanded.Count(e=>!e.IsFolder)==4,"Expansion reveals all files including mixed-case parent paths");
Require(expanded.Single(e=>e.Name=="Rose.bin").Depth==2,"Nested indentation reflects hierarchy");
Require(expanded.Select(e=>e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==expanded.Count,"Folder nodes deduplicate across drives");
Console.WriteLine("PASS: folder hierarchy, collapse/expansion, mixed-case paths and root-first sorting.");

sealed class CancellationHandler : HttpMessageHandler {
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) {
    await Task.Delay(Timeout.Infinite,cancellationToken);
    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
  }
}

sealed class RecoveryHandler : HttpMessageHandler {
  public int Calls;
  public System.Net.HttpStatusCode Status = System.Net.HttpStatusCode.Accepted;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/api/usb-recovery" || !request.Headers.TryGetValues("X-FlyingThumb-Key", out var values) || values.Single() != "test-shop-key") throw new Exception("Incorrect recovery protocol");
    Calls++;
    return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":\"USB writes are active\"}") });
  }
}
