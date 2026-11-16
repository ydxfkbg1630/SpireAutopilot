namespace SpireAutopilot;

// Input contains the revealed image fragments only. Hidden cell contents and item origins never enter this planner.
public sealed record ScryCell(int x, int y, bool hidden, string? itemId, string? visibleItem);
public sealed record ScryItem(string id, string kind, int width, int height, int revealedCells, int compatiblePlacements, bool completed);
public sealed record ScryCandidate(int x, int y, string tool, int newCells, int unknownCells, double expectedValue,
    double visibleRisk, int certainRewards, bool certain, double score);
public sealed record ScryAnalysis(ScryItem[] items, ScryCandidate[] candidates);
public static class CrystalSpherePlanner
{
    private sealed record Placement(HashSet<(int x, int y)> cells);
    private sealed record Target(ScryItem item, List<Placement> placements, double value, bool good);
    public static ScryAnalysis Analyze(IReadOnlyList<ScryCell> cells, int width, int height, int remaining)
    {
        var board = cells.ToDictionary(c => (c.x,c.y));
        var targets = new List<Target>();
        foreach (var group in cells.Where(c => !c.hidden && c.itemId != null).GroupBy(c=>c.itemId!))
        {
            string kind = group.First().visibleItem ?? "unknown";
            var (w,h,value,good) = Rule(kind);
            var observed = group.Select(c=>(c.x,c.y)).ToArray();
            var placements = new List<Placement>();
            for(int y=0;y<=height-h;y++) for(int x=0;x<=width-w;x++)
            {
                if(observed.Any(p=>p.x<x || p.x>=x+w || p.y<y || p.y>=y+h)) continue;
                var area = new HashSet<(int x,int y)>();
                for(int dy=0;dy<h;dy++) for(int dx=0;dx<w;dx++) area.Add((x+dx,y+dy));
                if(area.Any(p=>board.TryGetValue(p,out var c) && !c.hidden && c.itemId!=group.Key)) continue;
                placements.Add(new(area));
            }
            var item = new ScryItem(group.Key,kind,w,h,observed.Length,placements.Count,observed.Length==w*h);
            targets.Add(new(item,placements,value,good));
        }
        var result = new List<ScryCandidate>();
        foreach(var center in cells.Where(c=>c.hidden)) foreach(string tool in new[]{"Big","Small"})
        {
            int radius=tool=="Big"?1:0;
            var reveal=cells.Where(c=>c.hidden && Math.Abs(c.x-center.x)<=radius && Math.Abs(c.y-center.y)<=radius).Select(c=>(c.x,c.y)).ToHashSet();
            var knownSafe=new HashSet<(int x,int y)>();
            double reward=0,risk=0,progress=0;int certainRewards=0;
            foreach(var target in targets.Where(t=>!t.item.completed && t.placements.Count>0))
            {
                int complete=0; double uncovered=0;
                foreach(var placement in target.placements)
                {
                    var hidden=placement.cells.Where(p=>board[p].hidden).ToArray();
                    if(hidden.All(reveal.Contains)) complete++;
                    // A budget-aware progress bonus helps finish large objects across several clicks.
                    int hit=hidden.Count(reveal.Contains);
                    if(target.good && hidden.Length <= remaining*9 && (remaining > 1 || hidden.All(reveal.Contains))) uncovered+=(double)hit/Math.Max(1,hidden.Length);
                }
                double probability=(double)complete/target.placements.Count;
                if(target.good)
                {
                    reward+=probability*target.value;
                    progress+=uncovered/target.placements.Count*target.value;
                    if(complete==target.placements.Count) certainRewards++;
                    var certainArea=new HashSet<(int x,int y)>(target.placements[0].cells);
                    foreach(var p in target.placements.Skip(1)) certainArea.IntersectWith(p.cells);
                    knownSafe.UnionWith(certainArea);
                }
                else risk+=probability;
            }
            int unknown=reveal.Count(p=>!knownSafe.Contains(p));
            bool certain=certainRewards>0 && risk==0 && unknown==0;
            double score=reward*8 + progress*2 + reveal.Count*0.35 - risk*150;
            result.Add(new(center.x,center.y,tool,reveal.Count,unknown,Math.Round(reward,3),Math.Round(risk,3),certainRewards,certain,Math.Round(score,3)));
        }
        return new(targets.Select(t=>t.item).ToArray(),result.OrderByDescending(c=>c.score).ThenBy(c=>c.unknownCells).ThenBy(c=>c.y).ThenBy(c=>c.x).ToArray());
    }
    // Public, fixed icon footprints, verified against the native event rules. No board placement is read.
    private static (int w,int h,double value,bool good) Rule(string kind) => kind switch
    {
        "CrystalSphereRelic" => (4,4,18,true),
        "CrystalSphereCurse" => (2,2,0,false),
        "CrystalSphereGoldLarge" => (2,1,3,true),
        "CrystalSphereGold" => (1,1,1,true),
        "CrystalSpherePotionRare" => (2,2,4,true),
        "CrystalSpherePotion" => (1,3,2,true),
        "CrystalSphereCardReward" => (2,2,4,true),
        _ => (1,1,0,true)
    };
}

