using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;

static class DiagnosticLogTests
{
    private sealed class UnreadableMetadataCommand : ICampaignCommand
    {
        public int IssuerId => 0;
        public int ArmyId => throw new InvalidOperationException("synthetic metadata getter failure");
        public bool Applied;
        public void Apply(Campaign c) => Applied = true;
    }
    static void Check(bool value,string name)
    { if(!value) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); }
    public static void Run(Campaign c)
    {
        var folder=Path.GetFullPath(".fennara/diagnostic-test");
        Check(DiagnosticLogs.Start(folder),"diagnostic writer starts");
        folder = DiagnosticLogs.DirectoryPath;
        Parallel.For(0,4,worker=>
        {
            for(var i=0;i<100;i++) DiagnosticLogs.Write("orders",$"worker={worker} record={i}");
        });
        DiagnosticLogs.SetDay(c.ElapsedDays);
        DiagnosticLogs.TraceCommand(c,new RaiseArmyCommand{IssuerId=c.Player.Id,StateMapIndex=-1});
        var unusual = new UnreadableMetadataCommand();
        DiagnosticLogs.TraceCommand(c, unusual);
        Check(unusual.Applied,"diagnostic metadata failure does not block a valid command");
        DiagnosticLogs.Fatal("SYNTHETIC_MANAGED_CRASH_TEST (no actual crash)");
        // Less than the bounded queue capacity, large enough to exercise two rotations.
        var large=new string('x',30000);
        for(var i=0;i<440;i++) DiagnosticLogs.Write("performance",$"rotation-test={i} {large}");
        Check(DiagnosticLogs.Stop(10000),"diagnostic writer finishes draining before files are inspected");
        Check(DiagnosticLogs.LastFailure=="","background writes complete without I/O failure");
        var orders=File.ReadAllText(Path.Combine(folder,"orders.log"));
        Check(orders.Contains("worker=3 record=99") && orders.Contains("result=Rejected:") && orders.Contains("day="),"threaded logs and rejected command outcome retained");
        Check(File.ReadAllText(Path.Combine(folder,"errors.log")).Contains("SYNTHETIC_MANAGED_CRASH_TEST"),"fatal error text flushed synchronously");
        Check(File.Exists(Path.Combine(folder,"performance.log.2")),"large journals rotate with two backups");
        Check(Directory.GetFiles(folder,"performance.log*").Length==3 &&
            Directory.GetFiles(folder,"performance.log*").All(p=>new FileInfo(p).Length < 4*1024*1024+65536),"rotation bounds file count and file sizes");
        Check(File.ReadAllText(Path.Combine(folder,"system.log")).Contains("clean_shutdown=true"),"clean shutdown marker flushed");
    }
}
