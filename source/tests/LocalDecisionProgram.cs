using SpireAutopilot;
int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Local decision invariant failed"); passed++; }
DecisionFrame Frame(bool certain, string id = "cell", string kind = "scry_cell") => new("t", "NCrystalSphereScreen", new { context = new { recommendationIsCertain = certain, recommendedActionId = id } }, new[] { new LegalAction("cell", "cell", kind), new LegalAction("tool", "tool", "scry_tool") });
Check(LocalDecisionPolicy.Choose(Frame(true))?.Id == "cell");
Check(LocalDecisionPolicy.Choose(Frame(true,"tool"))?.Id == "tool");
Check(LocalDecisionPolicy.Choose(Frame(false)) == null);
Check(LocalDecisionPolicy.Choose(Frame(true,"expired")) == null);
Check(LocalDecisionPolicy.Choose(Frame(true,kind:"unrelated")) == null);
Check(LocalDecisionPolicy.Choose(Frame(true) with { Scene = "NEventRoom" }) == null);
Check(LocalDecisionPolicy.Choose(new("t","NEventRoom",new {},new[]{new LegalAction("go","go","continue")}))?.Id == "go");
Console.WriteLine($"RESULT passed={passed} failed=0");
