using System;
using System.Linq;
using DealOrForce.Simulation;

static class ForeignAssistanceTests
{
    private static void Check(bool ok, string text)
    { if (!ok) throw new Exception("assistance: " + text); Console.WriteLine("PASS assistance: " + text); }

    private static (Campaign C, Nation Requester, Nation Donor, Nation Enemy) Setup(bool humanDonor = true)
    {
        var c = new Campaign(417) { ScenarioId = "assistance_fixture" };
        var requester = new Nation { Id = 0, MapIndex = 501, Tag = "AFA", Name = "Requester", Population = 1000000 };
        var donor = new Nation { Id = 1, MapIndex = 502, Tag = "AFB", Name = "Donor", Population = 1000000 };
        var enemy = new Nation { Id = 2, MapIndex = 503, Tag = "AFC", Name = "Enemy", Population = 1000000 };
        c.Nations.Add(requester); c.Nations.Add(donor); c.Nations.Add(enemy);
        foreach (var n in c.Nations)
            c.States.Add(new ScenarioState { MapIndex = 3001 + n.Id, Id = "assistance_" + n.Id,
                NationMapIndex = n.MapIndex, NationId = n.Tag, Name = n.Name, Population = n.Population });
        var regions = Enumerable.Range(0, 24).Select(p => (ushort)(3001 + (p % 12) / 4)).ToArray();
        var owners = Enumerable.Range(0, 24).Select(p => (ushort)(501 + (p % 12) / 4)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, 12, 2, _ => false));
        c.ConfigureSinglePlayer(humanDonor ? donor.Id : requester.Id);
        requester.Treasury = .25f; requester.Gdp = 100; requester.WeeklyBalance = -1;
        donor.Treasury = 40; donor.Gdp = 100; donor.Stability = .8f; enemy.Treasury = 10; enemy.Gdp = 100;
        c.Wars.Add(new StrategicWar { Id = 7, AttackerId = enemy.Id, DefenderId = requester.Id, CivilConflict = true });
        return (c, requester, donor, enemy);
    }

    public static void Run()
    {
        var (c, requester, donor, _) = Setup();
        var result = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        Check(result.State == ForeignAssistanceState.Pending && result.RequestId > 0 &&
            requester.Treasury == .25f && donor.Treasury == 40,
            "a civil-war money request waits for its human donor and reserves no money");
        var wrong = ForeignAssistance.Respond(c, requester.Id, result.RequestId, ForeignAssistanceReply.Accept);
        Check(wrong.State == ForeignAssistanceState.Invalidated && c.AssistanceRequests.Single().State == ForeignAssistanceState.Pending,
            "the requester cannot spend its human donor's treasury");
        var accepted = ForeignAssistance.Respond(c, donor.Id, result.RequestId, ForeignAssistanceReply.Accept);
        Check(accepted.State == ForeignAssistanceState.Granted && accepted.GrantedAmount == .5f &&
            requester.Treasury == .75f && donor.Treasury == 39.5f,
            "explicit donor acceptance transfers a bounded real payment exactly once");
        ForeignAssistance.Respond(c, donor.Id, result.RequestId, ForeignAssistanceReply.Accept);
        Check(requester.Treasury == .75f && donor.Treasury == 39.5f,
            "a repeated response cannot pay the same request twice");
        Assessment();
        Revalidation();
        NeedAndFeatures();
        DuplicateAndCooldown();
        BoundedInbox();
        ExpiredResponse();
        BoundedHistory();
        AssistanceFeature();
        LifecycleTick();
        AiDonor();
        AiRequester();
        ControlChanged();
        MinimumPackage();
        FloatResolution();
        FractionalCashTransfer();
        SharedAidInterval();
        CooldownSlotReservation();
        ApprovedAmount();
        NetworkCommands();
        Snapshot();
        Fingerprint();
        RestoreRejectsBadPapers();
    }

    private static void Assessment()
    {
        var (c, requester, donor, _) = Setup();
        var fingerprint = CampaignFingerprint.Of(c);
        var quote = ForeignAssistance.Assess(c, requester.Id, donor.Id, 100, 7);
        Check(quote.Allowed && quote.PayableAmount <= 1 && quote.PayableAmount > .99999f && c.Relations.Count == 0 && CampaignFingerprint.Of(c) == fingerprint,
            "assessment caps the package to one billion and never creates an otherwise absent relationship");
    }

    private static void Revalidation()
    {
        var (c, requester, donor, _) = Setup();
        var request = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        donor.Treasury = 6.25f;
        var answer = ForeignAssistance.Respond(c, donor.Id, request.RequestId, ForeignAssistanceReply.Accept);
        Check(answer.State == ForeignAssistanceState.Granted && Math.Abs(answer.GrantedAmount - .25f) < .000001f &&
            Math.Abs(donor.Treasury - 6) < .000001f && Math.Abs(requester.Treasury - .5f) < .000001f,
            "acceptance rechecks current cash and leaves the donor's six-billion reserve intact");
    }

    private static void NeedAndFeatures()
    {
        var (c, requester, donor, _) = Setup();
        requester.Treasury = 10;
        Check(!ForeignAssistance.Assess(c, requester.Id, donor.Id, .5f, 7).Allowed &&
            ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7).RequestId == 0 && c.AssistanceRequests.Count == 0,
            "a solvent country cannot manufacture an aid request just because it is at war");
        requester.Treasury = .25f; c.FeatureOverrides["diplomacy"] = false;
        Check(ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7).RequestId == 0 && donor.Treasury == 40,
            "an explicitly disabled diplomacy feature cannot create or pay a request");
    }

    private static void DuplicateAndCooldown()
    {
        var (c, requester, donor, _) = Setup();
        var first = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        var second = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        ForeignAssistance.Respond(c, donor.Id, first.RequestId, ForeignAssistanceReply.Decline);
        var third = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        Check(second.RequestId == 0 && third.RequestId == 0 && c.AssistanceRequests.Count == 1 && donor.Treasury == 40,
            "one pending pair and a directed refusal cooldown block same-day request spam without taking money");
    }

    private static void BoundedInbox()
    {
        var (c, _, donor, _) = Setup(); c.Defines.DealInboxLimit = 32;
        for (var id = 3; id < 20; id++)
        {
            c.Nations.Add(new Nation { Id = id, Population = 100000, Gdp = 100, Treasury = .25f, WeeklyBalance = -1 });
            ForeignAssistance.Submit(c, id, donor.Id, .5f);
        }
        Check(c.AssistanceRequests.Count(r => r.State == ForeignAssistanceState.Pending) == 16 && donor.Treasury == 40,
            "even a larger modded inbox cannot exceed sixteen active money requests");
    }

    private static void SetDay(Campaign c, int day) => c.RestoreRuntimeState(c.Date, day, true,
        c.Random.State, c.Player.Id, c.HumanSeats.ToArray(), c.ActiveSeat, c.NextBattleId, c.MapRevision,
        c.InitialDeploymentCompleted, c.ProvinceController, c.ProvinceOccupation, null, null);

    private static void ExpiredResponse()
    {
        var (c, requester, donor, _) = Setup();
        var request = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        SetDay(c, 21);
        var result = ForeignAssistance.Respond(c, donor.Id, request.RequestId, ForeignAssistanceReply.Accept);
        Check(result.State == ForeignAssistanceState.Expired && c.AssistanceRequests.Single().State == ForeignAssistanceState.Expired &&
            requester.Treasury == .25f && donor.Treasury == 40,
            "an answer on the expiry date closes the paper without payment");
    }

    private static void BoundedHistory()
    {
        var (c, requester, donor, _) = Setup();
        for (var i = 0; i < 40; i++)
        {
            SetDay(c, i * 46);
            var request = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
            ForeignAssistance.Respond(c, donor.Id, request.RequestId, ForeignAssistanceReply.Decline);
        }
        Check(c.AssistanceRequests.Count == 32 && c.AssistanceRequests.Min(r => r.Id) == 9 &&
            c.AssistanceCooldowns.Count == 1 && donor.Treasury == 40,
            "closed request history and directed cooldowns remain bounded across decades of repeated refusals");
    }

    private static void AssistanceFeature()
    {
        var (c, requester, donor, _) = Setup();
        c.Defines.Features.Remove("foreign_assistance");
        Check(c.FeatureEnabled("foreign_assistance"), "older saves retain the new assistance mechanic's builtin default");
        c.FeatureOverrides["foreign_assistance"] = false;
        Check(ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7).RequestId == 0,
            "the explicit foreign-assistance switch disables submission independently of deals");
    }

    private static void LifecycleTick()
    {
        var (c, requester, donor, _) = Setup();
        ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        SetDay(c, 21); ForeignAssistance.Tick(c); ForeignAssistance.Tick(c);
        Check(c.AssistanceRequests.Single().State == ForeignAssistanceState.Expired && donor.Treasury == 40,
            "daily lifecycle expiry is idempotent and does not answer for the human donor");
    }

    private static void AiDonor()
    {
        var (c, requester, donor, _) = Setup(false);
        var refused = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        Check(refused.State == ForeignAssistanceState.Declined && donor.Treasury == 40 && c.AssistanceRequests.Single().ClosedDay == 0,
            "an uninterested government answers a request with a real refusal rather than a pending human paper");
        var (allied, needing, ally, _) = Setup(false);
        CampaignDiplomacy.Get(allied, needing.Id, ally.Id).Stance = DiplomaticStance.Allied;
        var granted = ForeignAssistance.Submit(allied, needing.Id, ally.Id, .5f, 7);
        Check(granted.State == ForeignAssistanceState.Granted && ally.Treasury == 39.5f && needing.Treasury == .75f,
            "a willing solvent ally answers and transfers its real cash immediately");
    }

    private static void AiRequester()
    {
        var (c, requester, donor, _) = Setup();
        var other = new Nation { Id = 3, Population = 100000, Gdp = 100, Treasury = 40, Stability = .8f };
        c.Nations.Add(other);
        CampaignDiplomacy.Get(c, requester.Id, donor.Id).Stance = DiplomaticStance.Allied;
        CampaignDiplomacy.Get(c, requester.Id, other.Id).Stance = DiplomaticStance.Allied;
        var rng = c.Random.State;
        ForeignAssistance.Tick(c); ForeignAssistance.Tick(c);
        Check(c.AssistanceRequests.Count == 1 && c.AssistanceRequests[0].DonorId == donor.Id && donor.Treasury == 40 && c.Random.State == rng,
            "a needy civil-war government selects one best donor with an ID tie-break and leaves human consent and RNG untouched");
        SetDay(c, 14); ForeignAssistance.Tick(c);
        Check(c.AssistanceRequests.Count == 1,
            "an AI requester waits for its outstanding human reply instead of asking every other ally");
    }

    private static void ControlChanged()
    {
        var (c, requester, donor, _) = Setup();
        var request = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        c.ConfigureSinglePlayer(requester.Id);
        var result = ForeignAssistance.Respond(c, donor.Id, request.RequestId, ForeignAssistanceReply.Accept);
        Check(result.State == ForeignAssistanceState.Invalidated && donor.Treasury == 40 &&
            c.AssistanceRequests.Single().State == ForeignAssistanceState.Invalidated,
            "an old human-consent paper cannot become an automatic payout after its donor changes controller");
    }

    private static void MinimumPackage()
    {
        var (c, requester, donor, _) = Setup();
        Check(ForeignAssistance.Assess(c, requester.Id, donor.Id, .01f, 7).Allowed,
            "the advertised ten-million-dollar minimum is not rejected by float-to-double comparison");
    }

    private static void FloatResolution()
    {
        var (c, requester, donor, _) = Setup(); donor.Treasury = 1048576;
        var result = ForeignAssistance.Submit(c, requester.Id, donor.Id, .02f, 7);
        Check(result.RequestId == 0 && requester.Treasury == .25f && donor.Treasury == 1048576,
            "a payment smaller than the donor ledger's float resolution cannot mint free cash");
    }

    private static void SharedAidInterval()
    {
        var (c, requester, donor, _) = Setup();
        new SendAidCommand { IssuerId = donor.Id, TargetId = requester.Id }.Apply(c);
        Check(ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7).RequestId == 0,
            "requester-initiated aid cannot bypass the existing gift interval after a donor gift");
        var (other, client, patron, _) = Setup();
        var request = ForeignAssistance.Submit(other, client.Id, patron.Id, .5f, 7);
        ForeignAssistance.Respond(other, patron.Id, request.RequestId, ForeignAssistanceReply.Accept);
        var gift = new SendAidCommand { IssuerId = patron.Id, TargetId = client.Id }; gift.Apply(other);
        Check(gift.Sent == 0 && patron.Treasury == 39.5f && client.Treasury == .75f,
            "a paid request also sets the shared gift interval without a second same-day package");
    }

    private static void CooldownSlotReservation()
    {
        var (c, requester, donor, enemy) = Setup(); c.Defines.AidRequestCooldownDays = 1;
        var pending = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        SetDay(c, 1); ForeignAssistance.Tick(c);
        enemy.Treasury = 40;
        for (var id = 3; id < 1027; id++)
        {
            c.Nations.Add(new Nation { Id = id, Population = 100000, Gdp = 100, Treasury = .25f, WeeklyBalance = -1 });
            ForeignAssistance.Submit(c, id, enemy.Id, .5f);
        }
        ForeignAssistance.Respond(c, donor.Id, pending.RequestId, ForeignAssistanceReply.Decline);
        Check(c.AssistanceCooldowns.Count == 1024 && c.AssistanceCooldowns.Any(x => x.RequesterId == requester.Id && x.DonorId == donor.Id),
            "a pending paper reserves its cooldown slot even when a mod shortens the request cooldown to one day");
    }

    private static void ApprovedAmount()
    {
        var (c, requester, donor, _) = Setup();
        var request = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        var reply = ForeignAssistance.Respond(c, donor.Id, request.RequestId, ForeignAssistanceReply.Accept, .125f);
        Check(reply.GrantedAmount == .125f && donor.Treasury == 39.875f && requester.Treasury == .375f,
            "a delayed acceptance cannot spend more than the exact package the human approved");
        foreach (var amount in new[] { float.NaN, float.PositiveInfinity, 0f, -1f })
        {
            var (other, client, patron, _) = Setup();
            var paper = ForeignAssistance.Submit(other, client.Id, patron.Id, .5f, 7);
            var denied = ForeignAssistance.Respond(other, patron.Id, paper.RequestId, ForeignAssistanceReply.Accept, amount);
            Check(denied.State == ForeignAssistanceState.Invalidated && patron.Treasury == 40 && client.Treasury == .25f,
                "invalid human approval cap cannot transfer money: " + amount);
        }
    }

    private static void FractionalCashTransfer()
    {
        var (c, requester, donor, _) = Setup(); requester.Treasury = .001f;
        var preview = ForeignAssistance.Assess(c, requester.Id, donor.Id, .05f, 7);
        Check(preview.Allowed && preview.PayableAmount > 0, "a legitimate grant is not rejected merely because the poor country's cash has finer float precision");
        var result = ForeignAssistance.Submit(c, requester.Id, donor.Id, .05f, 7);
        var beforeDonor = donor.Treasury; var beforeRequester = requester.Treasury;
        var response = ForeignAssistance.Respond(c, donor.Id, result.RequestId, ForeignAssistanceReply.Accept, preview.PayableAmount);
        var paid = (double)beforeDonor - donor.Treasury; var received = (double)requester.Treasury - beforeRequester;
        Check(response.State == ForeignAssistanceState.Granted && paid > 0 && received > 0 && paid <= .05f &&
            Math.Abs(paid - received) <= response.GrantedAmount * .00001,
            "both float ledgers move, with conservative bounded rounding and no payment beyond consent");
    }

    private static void NetworkCommands()
    {
        var (c, requester, donor, _) = Setup();
        var ask = new RequestForeignAssistanceCommand { IssuerId = requester.Id, DonorId = donor.Id, Amount = .5f, WarId = 7 };
        Check(NetworkCommandCodec.TryEncode(ask, out var kind, out var json) &&
            NetworkCommandCodec.TryDecode(kind, json, out var decoded) && decoded is RequestForeignAssistanceCommand,
            "the normal command whitelist roundtrips a requester-issued money request");
        NetworkCommandCodec.TryDecode(kind, json, out var request); request.Apply(c);
        Check(c.AssistanceRequests.Count == 1 && c.AssistanceRequests[0].RequesterId == requester.Id && donor.Treasury == 40,
            "the decoded command uses its issuer as requester and cannot spend the addressed donor's cash");
        var reply = new RespondForeignAssistanceCommand { IssuerId = donor.Id, RequestId = c.AssistanceRequests[0].Id,
            Reply = ForeignAssistanceReply.Accept, ApprovedAmount = .125f };
        Check(NetworkCommandCodec.TryEncode(reply, out kind, out json) && NetworkCommandCodec.TryDecode(kind, json, out var answer) &&
            answer is RespondForeignAssistanceCommand { ApprovedAmount: .125f },
            "the wire preserves the explicit human approval cap");
        NetworkCommandCodec.TryDecode(kind, json, out var accepted); accepted.Apply(c);
        Check(donor.Treasury == 39.875f && requester.Treasury == .375f,
            "the decoded donor-authorised response pays only its approved package");
    }

    private static void Snapshot()
    {
        var (c, requester, donor, _) = Setup();
        foreach (var nation in c.Nations) nation.OutputScale = 1;
        SovereignFinance.NormalizeAccounts(c);
        var request = ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        SetDay(c, 5); ForeignAssistance.Tick(c);
        var imported = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "assistance"), "assistance");
        if (!imported.Success) throw new Exception(imported.Warning);
        var clone = imported.Campaign;
        clone.AttachProvinceTopology(Setup().C.ProvinceMap);
        Check(clone.AssistanceRequests.Count == 1 && clone.AssistanceRequests[0].Id == request.RequestId &&
            clone.AssistanceRequests[0].State == ForeignAssistanceState.Pending && clone.AssistanceRequests[0].ExpiresDay == 21 &&
            clone.AssistanceCooldowns.Count == 1 && clone.NextAssistanceRequestId == 2 && clone.AssistanceLastTickDay == 5 &&
            clone.NationById(donor.Id).Treasury == 40 && clone.NationById(requester.Id).Treasury == .25f,
            "the real snapshot restores pending paper, cooldown, next ID and scheduler without paying it");
        c.Defines.AidRequestCooldownDays = 100;
        var changed = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "assistance"), "assistance");
        Check(changed.Success && changed.Campaign.AssistanceCooldowns.Single().ReadyOnDay == 45,
            "loading a pending request preserves its original cooldown when a later mod changes the default");
    }

    private static void Fingerprint()
    {
        var (c, requester, donor, _) = Setup(); var empty = CampaignFingerprint.Of(c);
        ForeignAssistance.Submit(c, requester.Id, donor.Id, .5f, 7);
        var baseline = CampaignFingerprint.Of(c);
        Check(baseline != empty, "a pending assistance paper is authoritative fingerprint state");
        c.AssistanceRequests[0].Reason = "Another presentation language";
        Check(CampaignFingerprint.Of(c) == baseline, "presentation receipt text does not change simulation fingerprint");
        c.AssistanceRequests[0].HumanConsent = false;
        Check(CampaignFingerprint.Of(c) != baseline, "the pending human-consent gate is fingerprinted");
        c.AssistanceRequests[0].HumanConsent = true;
        c.AssistanceCooldowns[0].ReadyOnDay++;
        Check(CampaignFingerprint.Of(c) != baseline, "directed anti-spam timing is fingerprinted");
        c.AssistanceCooldowns[0].ReadyOnDay--;
        c.AssistanceLastTickDay = 0;
        Check(CampaignFingerprint.Of(c) != baseline, "same-day AI scheduler state is fingerprinted");
        c.AssistanceLastTickDay = -1;
        c.Defines.AiAidRequestWillingness += .1f;
        Check(CampaignFingerprint.Of(c) != baseline, "modded assistance weights affecting future decisions are fingerprinted");
        c.Defines.AiAidRequestWillingness -= .1f;
        var relationship = CampaignDiplomacy.Get(c, requester.Id, donor.Id);
        var beforeAid = CampaignFingerprint.Of(c); relationship.AidSentDay = c.ElapsedDays;
        Check(CampaignFingerprint.Of(c) != beforeAid, "the shared gift/request aid payment interval is fingerprinted");
    }

    private static void RestoreRejectsBadPapers()
    {
        var (c, requester, donor, _) = Setup();
        var valid = new ForeignAssistanceRequest { Id = 4, RequesterId = requester.Id, DonorId = donor.Id,
            Amount = .5f, WarId = 7, CreatedDay = 0, ExpiresDay = 21, HumanConsent = true };
        var bad = ForeignAssistance.Copy(valid); bad.Id = 5; bad.Amount = float.NaN;
        var unknown = ForeignAssistance.Copy(valid); unknown.Id = 6; unknown.DonorId = 99;
        var malformed = ForeignAssistance.Copy(valid); malformed.Id = 7; malformed.State = (ForeignAssistanceState)99;
        var horizon = ForeignAssistance.Copy(valid); horizon.Id = 8; horizon.ExpiresDay = int.MaxValue;
        ForeignAssistance.Restore(c, new[] { valid, valid, bad, unknown, malformed, horizon },
            new[] { new ForeignAssistanceCooldown { RequesterId = requester.Id, DonorId = donor.Id, ReadyOnDay = 45 },
                new ForeignAssistanceCooldown { RequesterId = requester.Id, DonorId = donor.Id, ReadyOnDay = 44 },
                new ForeignAssistanceCooldown { RequesterId = 999, DonorId = donor.Id, ReadyOnDay = int.MaxValue } }, -1, int.MaxValue);
        Check(c.AssistanceRequests.Count == 1 && c.AssistanceRequests[0].Id == 4 && c.NextAssistanceRequestId == 5 &&
            c.AssistanceCooldowns.Count == 1 && c.AssistanceCooldowns[0].ReadyOnDay == 45 && c.AssistanceLastTickDay == -1 && donor.Treasury == 40,
            "restore rejects duplicate IDs, unknown actors, invalid enums, NaN, impossible horizons and invalid scheduler state without payout");
    }
}
