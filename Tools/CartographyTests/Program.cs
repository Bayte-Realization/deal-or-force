using DealOrForce.Presentation;
var checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
foreach(var population in new long[]{long.MinValue,-1,0,1000,150000,2000000,35000000,long.MaxValue})
foreach(var id in new string?[]{null,"","warsaw","milan","Санкт-Петербург","hong-kong"})
{
    var a=SettlementSilhouette.Build(id!,population);
    var b=SettlementSilhouette.Build(id!,population);
    Check(a.Length==SettlementSilhouette.MaximumBlocks,"bounded cluster");
    Check(a.SequenceEqual(b),"deterministic shape without campaign RNG");
    foreach(var block in a)
    {
        Check(float.IsFinite(block.East+block.North+block.Width+block.Depth+block.Height+block.Shade),"finite values");
        Check(block.Width>0&&block.Depth>0&&block.Height>0&&block.Height<.01f,"bounded positive dimensions");
        Check(Math.Abs(block.East)>.01f||Math.Abs(block.North)>.01f,"pin clear space");
        Check(Math.Abs(block.East)<.4f&&Math.Abs(block.North)<.3f,"bounded geographic footprint");
    }
}
foreach(var flat in new[]{false,true})
{
    var last=1f;
    for(var i=0;i<=1000;i++)
    {
        var value=flat?i*.01f:5+i*.01f;
        var fade=SettlementSilhouette.Visibility(flat,value,value);
        Check(float.IsFinite(fade)&&fade>=0&&fade<=1,"valid zoom fade");
        Check(fade<=last+.00001f,"zoom out never increases visual clutter");last=fade;
    }
    Check(SettlementSilhouette.Visibility(flat,float.NaN,float.NaN)==0,"invalid zoom hides layer");
    Check(SettlementSilhouette.Visibility(flat,15,11)==0,"strategic view has no miniatures");
}
Check(!SettlementSilhouette.Build("a",2000000).SequenceEqual(SettlementSilhouette.Build("b",2000000)),"distinct city identity");
Console.WriteLine($"CARTOGRAPHY_TESTS passed={checks} failures=0");
