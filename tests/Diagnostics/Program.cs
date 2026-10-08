using FlyingThumbManager;
using System.Text;

void Require(bool condition, string name) { if (!condition) throw new Exception(name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception(name); }
var results = DiagnosticReport.Parse("FTDIAG|0|BOOT|v2\nFTDIAG|1|RAM|FAIL|address mismatch\nFTDIAG|2|GPIO|DATA40|PASS|HIGH=1\nFTDIAG|3|PSRAM|SKIP|not enabled\nFTDIAG|4|LED|UNVERIFIED_NO_LIGHT_SENSOR\ntruncated|PASS");
Require(results.Count == 4 && results[0].Status == "FAIL" && results[1].Test == "GPIO / DATA40" && results[2].Status == "SKIP" && results[3].Status == "UNVERIFIED", "Result statuses must retain failures, skips and physical limits");
Require(DiagnosticReport.AssessActive("FTDIAG|0|RAM|PASS").Any(result => result.Status == "INCOMPLETE"), "A passing fragment must never represent a full passing suite");
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
