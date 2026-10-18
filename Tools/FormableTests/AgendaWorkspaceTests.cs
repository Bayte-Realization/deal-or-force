using System;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Rules;

static class AgendaWorkspaceTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("agenda: " + text); Console.WriteLine("PASS agenda: " + text); }

    public static void Run()
    {
        var values = new[] { int.MinValue, -1, 0, 1, 4, 99, 100, 101, int.MaxValue };
        var cases = 0;
        foreach (var total in values)
        foreach (var size in values)
        foreach (var page in values)
        {
            var slice = CatalogueWindow.Plan(total, size, page);
            var n = Math.Max(0, total); var capacity = Math.Clamp(size, 1, 100);
            var pages = (int)Math.Max(1L, ((long)n + capacity - 1) / capacity);
            var expectedPage = Math.Clamp(page, 0, pages - 1);
            if (slice.Page != expectedPage || slice.PageCount != pages ||
                slice.Start != (long)expectedPage * capacity ||
                slice.Count != Math.Min(capacity, n - slice.Start)) throw new Exception("catalogue boundary mismatch");
            cases++;
        }
        Check(cases == 729, "729 F# page boundary cases, including int32 overflow and empty catalogues");
        for (var i = 0; i < 1000; i++) _ = CatalogueWindow.Plan(500, 4, 3);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 10000; i++) sum += CatalogueWindow.Plan(500, 4, 3).Count;
        Check(sum == 40000 && GC.GetAllocatedBytesForCurrentThread() == allocated, "10000 F# page plans allocate zero bytes");

        var c = Campaign.CreateBootstrap(20261018); var ntn = c.Player;
        Check(AgendaWorkspace.Entries(null, ntn, AgendaWorkspace.Section.Decisions).Count == 0 &&
              AgendaWorkspace.Entries(c, null, AgendaWorkspace.Section.Decisions).Count == 0, "missing context is an empty projection");
        var fingerprint = CampaignFingerprint.Of(c); var rng = c.Random.State;
        var decisions = AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Decisions);
        Check(decisions.Count == DecisionCatalog.All.Count, "every mod/base decision remains discoverable");
        Check(decisions.All(row => row.Reason == DecisionCatalog.BlockReason(c, ntn, DecisionCatalog.All.First(d => d.Id == row.Id)) &&
            (row.State == AgendaWorkspace.Status.Ready) == (row.Reason.Length == 0)), "availability and refusal use the real executor's gates");
        var id = decisions.Last().Id;
        Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Decisions, id.ToUpperInvariant()).Any(r => r.Id == id),
            "case-insensitive ID search finds an entry outside the initial page");
        Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Decisions, "__no_such_initiative__").Count == 0,
            "no-match search invents no fallback action");
        Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Decisions, filter: AgendaWorkspace.Filter.Ready)
            .All(r => r.State == AgendaWorkspace.Status.Ready), "ready filter never admits a blocked action");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var translated = Loc.Get(decisions[0].Name);
            Check(Loc.Get("The peace dividend") == "Дивиденд мира", "Russian goal title is not overwritten by a corrupt translation layer");
            Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Decisions, translated).Any(r => r.Id == decisions[0].Id),
                "search uses translated names, not English-only names");
        }
        finally { Loc.Language = language; }
        Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == rng, "browsing all filters and languages changes no state or RNG");

        var promise = PromiseCatalog.All.First();
        ntn.Promises.Add(new ActivePromise { Id = promise.Id, MadeDay = 0, DueDay = 20 });
        var activePromise = AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Promises, filter: AgendaWorkspace.Filter.Active);
        Check(activePromise.Any(r => r.Id == promise.Id && r.State == AgendaWorkspace.Status.Active), "an active promise is one entry, not a duplicate offer");
        var goal = GoalCatalog.All.First(); ntn.Goals.Add(new ActiveGoal { Id = goal.Id });
        Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Goals, filter: AgendaWorkspace.Filter.Active).Any(r => r.Id == goal.Id),
            "an adopted goal is distinguishable from a new offer");
        ntn.Goals[0].CompletedDay = 1;
        Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Goals, filter: AgendaWorkspace.Filter.Completed).Any(r => r.Id == goal.Id),
            "completed goals remain visible as records");
        c.FeatureOverrides["strategic_goals"] = false; c.FeatureOverrides["government_promises"] = false;
        Check(AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Goals).Count == 0 &&
            AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Promises).Count == 0, "disabled mechanics expose no actionable rows");

        try
        {
            for (var i = 0; i < 500; i++)
                DecisionCatalog.Override("agenda_mod_" + i, "Mod initiative " + i, "Mod description", 7, 0, DecisionKind.Scripted);
            var large = AgendaWorkspace.Entries(c, ntn, AgendaWorkspace.Section.Decisions);
            var seen = 0;
            for (var page = 0; page < CatalogueWindow.Plan(large.Count, 4, 0).PageCount; page++)
                seen += CatalogueWindow.Plan(large.Count, 4, page).Count;
            Check(seen == large.Count && large.Count >= 500, "large mod catalogues page through every entry with a four-row viewport");
        }
        finally { DecisionCatalog.ResetCatalog(); }
    }
}
