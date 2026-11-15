using System.Text;
using System.Text.Json.Nodes;
using SpireAutopilot;

string directory = Path.Combine(Path.GetTempPath(), "SpireRunExperienceTests-" + Guid.NewGuid().ToString("N"));
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
var store = new RunExperienceStore(directory);
Check(!store.HasPendingReflection && store.ReadLessons() == "", "empty");
var snapshot = new JsonObject { ["hp"] = 77, ["floor"] = 1, ["api_key"] = "privatevalue", ["deck"] = new JsonArray("STRIKE", "DEFEND") };
store.BeginRun("run-1", snapshot); snapshot["hp"] = 1;
store.RecordDecision("Map", "route:1,2", "前往休息点");
Check(store.CreateReflectionFrame(null) == null && !store.HasPendingReflection, "idle polling leaves an unfinished run active");
store.Observe(new { hp = 70, floor = 2, enemy = "TUNNELER", hpLoss = 7 });
Check(!store.HasPendingReflection && !Directory.GetFiles(directory, "run-*.md").Any(), "active run produces checkpoint only");
Check(Directory.GetFiles(directory, "checkpoint-*.json").Length == 1, "active checkpoint saved");
store = new RunExperienceStore(directory); store.BeginRun("run-1", new { hp = 2 });
store.RecordDecision("Shop", "buy:DEFEND", "补防御");
var frame = store.CreateReflectionFrame(new { result = "victory", hp = 30, floor = 50 })!;
var state = (JsonObject)frame.State;
Check(state["start"]!["hp"]!.GetValue<int>() == 77, "resume preserves initial snapshot");
Check(!state.ToJsonString().Contains("privatevalue"), "secret fields removed");
Check(state["entries"]!.ToJsonString().Contains("Shop") && state["entries"]!.ToJsonString().Contains("Map"), "resume preserves decisions");
Check(frame.Scene == "RunReflection" && frame.Actions.Single().Id == "write_run_experience", "run only reflection contract");
Check(store.HasPendingReflection && store.CreateReflectionFrame(null)!.Token == frame.Token, "finished pending token stable");
Check(!store.CommitReflection(frame.Token, new ModelDecision("write_run_experience", "reason fallback"), null), "full note required");
Check(!store.CommitReflection("stale", new ModelDecision("write_run_experience", "", RunExperienceNote: "note"), null), "stale token rejected");
store = new RunExperienceStore(directory); var resumed = store.CreateReflectionFrame(null)!;
Check(resumed.Token == frame.Token, "pending report restored after restart");
var decision = new ModelDecision("write_run_experience", "", RunExperienceNote: "# 第一局\n结局胜利；路线先休息；商店买防御；掘地者战损7；actualprivate012345 sk-examplecredential123456");
Check(store.CommitReflection(resumed.Token, decision, "actualprivate012345"), "commit finished run");
Check(Directory.GetFiles(directory, "run-*.md").Length == 0 && File.Exists(Path.Combine(directory, "experience.md")) && Directory.GetFiles(directory, "checkpoint-*.json").Length == 0, "one run file checkpoint retired");
string content = File.ReadAllText(Path.Combine(directory, "experience.md"));
Check(content.Contains("掘地者战损7") && !content.Contains("actualprivate012345") && !content.Contains("sk-example"), "report contents sanitized");
Check(!store.CommitReflection(resumed.Token, decision, null), "retry no duplicate");
store.BeginRun("run-1", new { hp = 77 }); Check(store.CreateReflectionFrame(new { result = "victory" }) == null, "same run remains committed");
store.BeginRun("run-2", new { hp = 77 });
for (int i = 0; i < 240; i++) { store.RecordDecision("Event", "choice" + i, new string('牌', 1000)); store.Observe(new { floor = i, hp = 77, enemies = new[] { new { id = "TUNNELER", hpLoss = 7 } } }); }
foreach (string scene in new[] { "Map", "Shop", "Event", "Reward", "Combat", "Other" })
for (int i = 0; i < 30; i++) store.RecordDecision(scene, new string('行', 180), new string('理', 180));
var bounded = store.CreateReflectionFrame(new { result = "defeat", floor = 31 })!;
Check(((JsonArray)((JsonObject)bounded.State)["entries"]!).Count <= 120, "journal bounded");
Check(new FileInfo(Directory.GetFiles(directory, "checkpoint-*.json").Single()).Length <= 128 * 1024, "checkpoint bytes bounded");
Check(!store.CommitReflection(bounded.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: new string('字', 6001)), null), "overlong report rejected");
Check(store.CommitReflection(bounded.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: new string('字', 6000)), null), "6000 unicode accepted");
Check(Directory.GetFiles(directory, "*.md").Length == 1, "one rolling document after two runs");
Check(!File.ReadAllText(Path.Combine(directory, "experience.md")).Contains("掘地者战损7"), "model replacement updates rather than appends");
Check(Encoding.UTF8.GetByteCount(store.ReadLessons()) <= 24 * 1024, "recent lessons bounded");
Check(store.ReadLessons().Contains("经验总结"), "lessons labeled empirical");
File.WriteAllText(Path.Combine(directory, "experiences.md"), "legacy battle data");
Check(!store.ReadLessons().Contains("legacy battle data"), "old battle documents ignored");
var retryStore = new RunExperienceStore(Path.Combine(directory, "retry")); retryStore.BeginRun("retry", new { hp = 10 });
var retryFrame = retryStore.CreateReflectionFrame(new { result = "defeat" })!;
string retryCheckpoint = Directory.GetFiles(Path.Combine(directory, "retry"), "checkpoint-*.json").Single();
byte[] checkpointBefore = File.ReadAllBytes(retryCheckpoint); bool failed = false;
using (var held = new FileStream(retryCheckpoint, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{ try { retryStore.CommitReflection(retryFrame.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "本局经验"), null); } catch (IOException) { failed = true; } }
Check(failed && retryStore.HasPendingReflection && File.ReadAllBytes(retryCheckpoint).SequenceEqual(checkpointBefore), "failed write keeps checkpoint and pending");
Check(retryStore.CommitReflection(retryFrame.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "本局经验"), null), "retry successful");
string queuedDirectory = Path.Combine(directory, "queued");
var queued = new RunExperienceStore(queuedDirectory);
queued.BeginRun("first-finished", new { hp = 50 });
var firstPending = queued.CreateReflectionFrame(new { result = "defeat", floor = 5 })!;
queued.BeginRun("second-active", new { hp = 80 });
queued.RecordDecision("Shop", "second-run-purchase", "second-run-reason");
Check(Directory.GetFiles(queuedDirectory, "checkpoint-*.json").Length == 2, "pending reflection permits next run journal");
queued = new RunExperienceStore(queuedDirectory);
queued.BeginRun("second-active", new { hp = 1 });
var oldest = queued.CreateReflectionFrame(new { result = "victory", floor = 50 })!;
Check(oldest.Token == firstPending.Token, "older pending reflection survives new finished run and restart");
Check(queued.CommitReflection(oldest.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "第一局经验"), null), "commit oldest pending");
Check(queued.HasPendingReflection, "next pending remains after oldest commit");
var secondPending = queued.CreateReflectionFrame(null)!;
Check(((JsonObject)secondPending.State)["previousExperience"]!.GetValue<string>() == "第一局经验", "next reflection receives the previous complete summary");
Check(((JsonObject)secondPending.State)["start"]!["hp"]!.GetValue<int>() == 80
    && ((JsonObject)secondPending.State)["entries"]!.ToJsonString().Contains("second-run-purchase"), "new run retains its own initial state and decisions");
Check(queued.CommitReflection(secondPending.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "第二局经验"), null), "commit second pending");
Check(Directory.GetFiles(queuedDirectory, "*.md").Length == 1 && !queued.HasPendingReflection, "queued runs update the same summary");
string corruptDir=Path.Combine(directory,"corrupt");Directory.CreateDirectory(corruptDir);string corruptFile=Path.Combine(corruptDir,"checkpoint-"+new string('a',64)+".json");byte[] corruptBytes=Encoding.UTF8.GetBytes("{broken");File.WriteAllBytes(corruptFile,corruptBytes);var recovered=new RunExperienceStore(corruptDir);Check(!recovered.HasPendingReflection,"corrupt checkpoint does not abort construction");
Check(recovered.RestoreDiagnostic != null && Directory.GetFiles(corruptDir,"*.invalid-*").Any(p=>File.ReadAllBytes(p).SequenceEqual(corruptBytes)),"corrupt original bytes preserved and diagnosed");
string restoreDir=Path.Combine(directory,"restore");var seedRestore=new RunExperienceStore(restoreDir);seedRestore.BeginRun("restore",new{hp=77});seedRestore.CreateReflectionFrame(new{result="victory"});string restorePath=Directory.GetFiles(restoreDir,"checkpoint-*.json").Single();byte[] validRestore=File.ReadAllBytes(restorePath);File.WriteAllBytes(restorePath+".tmp",validRestore);File.WriteAllText(restorePath,"{broken");var tmpRestore=new RunExperienceStore(restoreDir);Check(tmpRestore.HasPendingReflection && tmpRestore.RestoreDiagnostic!.Contains("副本恢复"),"valid temp recovers pending");Check(File.ReadAllBytes(restorePath).SequenceEqual(validRestore),"recovered checkpoint bytes exact");
File.Copy(restorePath,restorePath+".bak",true);File.Delete(restorePath+".tmp");var invalid=JsonNode.Parse(File.ReadAllText(restorePath))!;invalid["pending"]="wrong";File.WriteAllText(restorePath,invalid.ToJsonString());var backupRestore=new RunExperienceStore(restoreDir);Check(backupRestore.HasPendingReflection,"semantic invalid primary recovered from backup");
string migrationDir = Path.Combine(directory, "migration"); Directory.CreateDirectory(migrationDir);
foreach (char id in new[] { 'a', 'b' })
    File.WriteAllText(Path.Combine(migrationDir, "run-" + new string(id, 64) + ".md"), "旧经验" + id);
var migrating = new RunExperienceStore(migrationDir);
Check(migrating.HasPendingReflection, "legacy notes schedule consolidation");
var migrationFrame = migrating.CreateReflectionFrame(null)!;
Check(((JsonArray)((JsonObject)migrationFrame.State)["legacyNotes"]!).Count == 2, "legacy notes supplied as model evidence");
Check(migrating.CommitReflection(migrationFrame.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "# 经验总结\n优先保障防御与回复。"), null), "legacy summary committed");
Check(Directory.GetFiles(migrationDir, "*.md").Length == 1 && Directory.GetFiles(migrationDir + ".legacy", "*.bak").Length == 2, "one active document and archived originals");
Check(!new RunExperienceStore(migrationDir).HasPendingReflection, "migration is idempotent across restart");
migrating.BeginRun("new-after-migration", new { hp = 80 });
var updated = migrating.CreateReflectionFrame(new { result = "victory" })!;
Check(((JsonObject)updated.State)["previousExperience"]!.GetValue<string>().Contains("回复"), "new run receives migrated summary");
File.AppendAllText(Path.Combine(migrationDir, "experience.md"), "\n手动补充");
Check(!migrating.CommitReflection(updated.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "陈旧回复"), null), "external summary edit rejects a stale response");
updated = migrating.CreateReflectionFrame(null)!;
Check(((JsonObject)updated.State)["previousExperience"]!.GetValue<string>().Contains("手动补充"), "retry reads the newest summary");
using (var held = new FileStream(Path.Combine(migrationDir, "experience.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    bool writeFailed = false;
    try { migrating.CommitReflection(updated.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "新总结"), null); }
    catch (IOException) { writeFailed = true; }
    Check(writeFailed && migrating.HasPendingReflection, "failed atomic replacement retains pending work");
}
Check(File.ReadAllText(Path.Combine(migrationDir, "experience.md")).Contains("手动补充"), "failed write preserves original content");
Check(migrating.CommitReflection(updated.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "新总结"), null), "atomic replacement retries successfully");
Check(migrating.ReadLessons().Contains("新总结") && !migrating.ReadLessons().Contains("spire-autopilot-experience"), "only summary prose enters future model context");
string interruptedDir = Path.Combine(directory, "interrupted-commit");
var interrupted = new RunExperienceStore(interruptedDir);
interrupted.BeginRun("interrupted", new { hp = 80 });
var interruptedFrame = interrupted.CreateReflectionFrame(new { result = "victory" })!;
string heldCheckpoint = Directory.GetFiles(interruptedDir, "checkpoint-*.json").Single();
using (var held = new FileStream(heldCheckpoint, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
{
    bool cleanupFailed = false;
    try { interrupted.CommitReflection(interruptedFrame.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: "已提交总结"), null); }
    catch (IOException) { cleanupFailed = true; }
    Check(cleanupFailed && File.Exists(Path.Combine(interruptedDir, "experience.md")), "summary is durable before checkpoint retirement");
}
Check(!interrupted.HasPendingReflection, "already committed update stays completed during cleanup retry");
var restartedCommit = new RunExperienceStore(interruptedDir);
Check(!restartedCommit.HasPendingReflection && Directory.GetFiles(interruptedDir, "checkpoint-*.json").Length == 0, "restart retires the checkpoint of an already committed summary");
Check(restartedCommit.ReadLessons().Contains("已提交总结"), "interrupted cleanup preserves the committed lesson");
string backlogDir = Path.Combine(directory, "backlog"); Directory.CreateDirectory(backlogDir);
for (int i = 0; i < 257; i++)
{
    string id = i.ToString("x64");
    var checkpoint = new JsonObject { ["id"] = id, ["startedUtc"] = "2026-01-01T00:00:00Z", ["finishedUtc"] = "2026-01-01T01:00:00Z", ["pending"] = i == 256, ["entries"] = new JsonArray(), ["droppedEntries"] = 0, ["decisionSummary"] = new JsonObject() };
    File.WriteAllText(Path.Combine(backlogDir, "checkpoint-" + id + ".json"), checkpoint.ToJsonString());
}
Check(new RunExperienceStore(backlogDir).HasPendingReflection, "old interrupted runs do not hide a newer pending update");
Console.WriteLine($"Run experience tests passed: {checks}; evidence: {directory}");
