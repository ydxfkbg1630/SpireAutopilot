using SpireAutopilot;
var cells = new List<ScryCell>();
for(int y=0;y<5;y++) for(int x=0;x<5;x++) cells.Add(new(x,y,true,null,null));
void Reveal(int x,int y,string? id,string? type) { int n=cells.FindIndex(c=>c.x==x&&c.y==y);cells[n]=new(x,y,false,id,type); }
Reveal(1,1,"gold","CrystalSphereGoldLarge"); Reveal(0,1,null,null);
var a=CrystalSpherePlanner.Analyze(cells,5,5,2);
var finish=a.candidates.Single(c=>c.x==2&&c.y==1&&c.tool=="Small");
if(!finish.certain || finish.certainRewards!=1 || finish.unknownCells!=0) throw new Exception("Visible gold edge must imply safe adjacent completion");
Reveal(3,2,"curse","CrystalSphereCurse");Reveal(4,2,"curse","CrystalSphereCurse");Reveal(3,3,"curse","CrystalSphereCurse");
a=CrystalSpherePlanner.Analyze(cells,5,5,2);
if(a.candidates.Single(c=>c.x==4&&c.y==3&&c.tool=="Small").visibleRisk!=1) throw new Exception("Fourth curse cell must show certain harm");
if(a.candidates.Any(c=>c.x==1&&c.y==1)) throw new Exception("Only covered native cells are clickable");
var before=a.candidates.Single(c=>c.x==2&&c.y==1&&c.tool=="Small"); Reveal(2,1,"gold","CrystalSphereGoldLarge");
a=CrystalSpherePlanner.Analyze(cells,5,5,1);
if(a.candidates.Any(c=>c.x==2&&c.y==1)) throw new Exception("Revealed actions must disappear");
if(a.items.Single(i=>i.id=="gold").completed!=true) throw new Exception("Complete observed reward recognized");
if(a.candidates.Any(c=>c.certain&&c.unknownCells>0)) throw new Exception("Exploration cannot be classified certain");
Console.WriteLine("PASS: partial gold inference, curse avoidance, legal cells, progressive observation, completion, certainty boundary");

var watch=System.Diagnostics.Stopwatch.StartNew();
for(int i=0;i<200;i++) CrystalSpherePlanner.Analyze(cells,5,5,2);
Console.WriteLine($"Planner benchmark: 200 fixture analyses in {watch.Elapsed.TotalMilliseconds:F1} ms");
// Last click: visible relic progress has no terminal reward value unless the object completes now.
var finalBoard = new List<ScryCell>();
for(int y=0;y<7;y++) for(int x=0;x<7;x++) finalBoard.Add(new(x,y,false,null,null));
void SetFinal(int x,int y,bool hidden,string? id=null,string? kind=null){int i=finalBoard.FindIndex(c=>c.x==x&&c.y==y);finalBoard[i]=new(x,y,hidden,id,kind);}
for(int y=0;y<4;y++) for(int x=0;x<4;x++) SetFinal(x,y,false,"relic","CrystalSphereRelic");
foreach(var p in new[]{(0,0),(3,0),(0,3),(3,3)})SetFinal(p.Item1,p.Item2,true);
SetFinal(5,5,false,"gold","CrystalSphereGoldLarge");SetFinal(6,5,true);
var last=CrystalSpherePlanner.Analyze(finalBoard,7,7,1);
if(last.candidates[0].certainRewards!=1)throw new Exception("Last click must complete visible gold instead of incomplete dispersed relic");
Console.WriteLine("PASS: last-click completion beats dispersed relic progress");
var dispersed=last.candidates.Single(c=>c.x==0&&c.y==0&&c.tool=="Big");
if(dispersed.score!=Math.Round(dispersed.newCells*0.35,3))throw new Exception("Terminal incomplete relic must have zero progress value");
