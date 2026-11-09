using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;

static class SaveSerializationWarmupTests
{
    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(61109);
        var state = campaign.States.First(s => s.NationMapIndex == campaign.Player.MapIndex);
        campaign.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Repeat((ushort)state.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)campaign.Player.MapIndex, 512).ToArray(), 32, 16, _ => false));
        campaign.Armies.Clear();
        foreach (var nation in campaign.Nations) TacticalDoctrines.EnsureShelves(nation);
        var fingerprint = CampaignFingerprint.Of(campaign);
        var random = campaign.Random.State;

        SaveStringTable.Use(new[] { "", "modded_faith", "modded_culture" });
        SaveStringTable.InternIds = false;
        var callerThread = Environment.CurrentManagedThreadId;
        var workerThread = Task.Run(() =>
        {
            SaveSystem.WarmSerializationContracts();
            return Environment.CurrentManagedThreadId;
        }).GetAwaiter().GetResult();
        Check(workerThread != callerThread, "contracts can be prepared on a worker without a campaign argument");
        Check(!SaveStringTable.InternIds && SaveStringTable.At(1) == "modded_faith",
            "warming leaves the active readable-save string context alone");
        SaveStringTable.InternIds = true;
        Check(SaveStringTable.IndexOf("modded_culture") == 2, "warming does not replace compact population IDs");
        Check(CampaignFingerprint.Of(campaign) == fingerprint && campaign.Random.State == random,
            "warming neither advances nor changes the existing world");

        var types = SaveDefaults.Types;
        var members = SaveDefaults.Members;
        Task.WaitAll(Enumerable.Range(0, 4).Select(_ => Task.Run(SaveSystem.WarmSerializationContracts)).ToArray());
        Check(SaveDefaults.Types == types && SaveDefaults.Members == members,
            "repeated and concurrent warming reuses the same contracts");

        var folder = Path.Combine(Path.GetTempPath(), "dof-save-warmup-" + Guid.NewGuid().ToString("N"));
        foreach (var text in new[] { false, true })
        {
            campaign.Defines.SaveTextFormat = text;
            fingerprint = CampaignFingerprint.Of(campaign);
            var prepared = SaveSystem.Prepare(folder, text ? "readable" : "compact", campaign, "warmup-test");
            Check(SaveDefaults.Types == types && SaveDefaults.Members == members,
                $"first {(text ? "readable" : "compact")} save does not discover more default contracts");
            Task.Run(prepared.Commit).GetAwaiter().GetResult();
            var loaded = SaveSystem.Load(folder, text ? "readable" : "compact", "warmup-test");
            Check(loaded.Success && CampaignFingerprint.Of(loaded.Campaign) == fingerprint,
                $"{(text ? "readable" : "compact")} save still loads as the captured world");
        }
        Check(CampaignFingerprint.Of(campaign) == fingerprint && campaign.Random.State == random,
            "preparing and committing both formats leaves the source world unchanged");
        Console.WriteLine("Save warmup fixtures retained: " + folder);
    }

    private static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("FAIL save warmup: " + note);
        Console.WriteLine("PASS save warmup: " + note);
    }
}
