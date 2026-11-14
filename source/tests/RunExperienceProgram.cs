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
Check(Directory.GetFiles(directory, "run-*.md").Length == 1 && Directory.GetFiles(directory, "checkpoint-*.json").Length == 0, "one run file checkpoint retired");
string content = File.ReadAllText(Directory.GetFiles(directory, "run-*.md").Single());
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
Check(Directory.GetFiles(directory, "run-*.md").Length == 2, "separate file per run");
Check(Encoding.UTF8.GetByteCount(store.ReadLessons()) <= 16 * 1024, "recent lessons bounded");
Check(store.ReadLessons().Contains("游戏经验"), "lessons labeled empirical");
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
Console.WriteLine($"Run experience tests passed: {checks}; evidence: {directory}");
