using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAutopilot;

string directory = Path.Combine(Path.GetTempPath(), "SpireExperienceTests-" + Guid.NewGuid().ToString("N"));
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var store = new BattleExperienceStore(directory);
Check(store.ReadLessons() == "", "fresh lessons");
var start = new JsonObject { ["hp"] = 77, ["enemy"] = "TUNNELER", ["api_key"] = "hidden-value", ["authorization"] = "Bearer examplecredential012345", ["intent"] = "BITE_MOVE" };
store.BeginBattle("run", "battle-1", start);
start["hp"] = 2;
for (int i = 0; i < 160; i++) { store.Observe(new { turn = i, hp = 70, enemy = "TUNNELER", cards = new string('牌', 500) }); store.RecordAction("STRIKE", "防守后攻击"); }
var frame = store.CreateReflectionFrame(new { hp = 70, enemyHp = 0, result = "victory" })!;
Check(store.HasPendingReflection, "pending");
Check(frame.Scene == "BattleReflection" && frame.Actions.Count == 1 && frame.Actions[0].Id == "write_experience", "single reflection action");
Check(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(frame.State)) < 96 * 1024, "reflection state bounded");
var state = (JsonObject)frame.State;
Check(state["start"]!["hp"]!.GetValue<int>() == 77, "snapshot deep clone");
Check(!state["start"]!.ToJsonString().Contains("hidden-value"), "secret key removed");
Check(((JsonArray)state["observations_and_actions"]!).Count <= 80, "observation count bounded");
Check(state["dropped_entries"]!.GetValue<int>() > 0, "dropped count recorded");
Check(store.CreateReflectionFrame(null)!.Token == frame.Token, "retry stable token");
var decision = new ModelDecision("write_experience", "经验", ExperienceNote: "观察到战损 7。建议按敌人意图保留防御。sk-example0123456789012345 actualkey012345 api_key=hello012345");
Check(!store.CommitReflection("stale", decision, "actualkey012345"), "stale token rejected");
Check(!store.CommitReflection(frame.Token, decision with { ActionId = "play" }, null), "wrong action rejected");
Check(store.CommitReflection(frame.Token, decision, "actualkey012345"), "commit");
Check(!store.HasPendingReflection, "pending cleared");
Check(!store.CommitReflection(frame.Token, decision, null), "duplicate commit rejected");
string file = Path.Combine(directory, "experiences.md");
string text = File.ReadAllText(file);
Check(!text.Contains("actualkey012345") && !text.Contains("sk-example") && !text.Contains("hello012345"), "credentials redacted");
Check(text.Contains("战损 7"), "note persisted");
store.BeginBattle("run", "battle-1", new { hp = 77 });
Check(store.CreateReflectionFrame(null) == null, "battle identity idempotent");
store = new BattleExperienceStore(directory);
store.BeginBattle("run", "battle-1", new { hp = 77 });
Check(store.CreateReflectionFrame(null) == null, "identity across restart");
for (int i = 0; i < 70; i++)
{
    store.BeginBattle("run", "battle-" + (i + 2), new { hp = 77 });
    var f = store.CreateReflectionFrame(new { hp = 70 })!;
    Check(store.CommitReflection(f.Token, new ModelDecision("write_experience", "", ExperienceNote: "经验 " + i + " " + new string('牌', 5500)), null), "rolling commit");
}
Check(File.ReadAllText(file).Split("<!-- spire-autopilot-experience:").Length - 1 <= 64, "file count bounded");
Check(new FileInfo(file).Length <= 512 * 1024, "file byte bounded");
Check(Encoding.UTF8.GetByteCount(store.ReadLessons()) <= 12 * 1024, "lessons byte bounded");
Check(store.ReadLessons().Contains("经验 69"), "long unicode notes remain readable");
var another = new BattleExperienceStore(Path.Combine(directory, "queue"));
another.BeginBattle("run", "a", new { hp = 10 }); var a = another.CreateReflectionFrame(new { hp = 9 })!;
another.BeginBattle("run", "b", new { hp = 9 }); another.CreateReflectionFrame(new { hp = 8 });
Check(another.CommitReflection(a.Token, new ModelDecision("write_experience", "first"), null), "queued first commit");
Check(another.HasPendingReflection, "next queued battle retained");
Check(another.CreateReflectionFrame(null)!.Token != a.Token, "queued identity different");
var oversized = new BattleExperienceStore(Path.Combine(directory, "oversized"));
oversized.BeginBattle("large", "battle", new
{
    combat = new
    {
        giantDescriptions = Enumerable.Range(0, 40).Select(i => new { name = "card" + i, description = new string('牌', 10000) }).ToArray(),
        player = new { hp = 65, maxHp = 77, energy = 3 },
        enemies = new[] { new { id = "TUNNELER", name = "掘地者", hp = 87, intent = "BITE_MOVE" }, new { id = "CRUSHER", name = "碾碎爪", hp = 55, intent = "ATTACK" } },
    },
    run = new { floor = 15, deck = Enumerable.Range(0, 40).Select(i => new { id = "card" + i, description = new string('牌', 10000) }).ToArray() },
});
var huge = oversized.CreateReflectionFrame(new { hp = 60, enemies = new[] { new { id = "TUNNELER", hp = 0 } } })!;
var hugeState = (JsonObject)huge.State;
Check(hugeState["start"]!["combat"]!["player"]!["hp"]!.GetValue<int>() == 65, "large snapshot preserves player hp");
Check(hugeState["start"]!["combat"]!["enemies"]![0]!["id"]!.GetValue<string>() == "TUNNELER", "large snapshot preserves enemy id");
Check(hugeState["start"]!["combat"]!["enemies"]![1]!["intent"]!.GetValue<string>() == "ATTACK", "large snapshot preserves second intent");
Check(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(huge.State)) <= 96 * 1024, "large frame bounded");
Check(hugeState["task"]!.GetValue<string>().Contains("experienceNote"), "provider note property matched");
Console.WriteLine($"Experience tests passed: {checks}; temp evidence: {directory}");
