using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>
/// One external process per package (Go, Rust, or any executable that reads one JSON request
/// from stdin and writes one JSON response). Protocol 1. The process is not a lockstep peer
/// and is refused when more than one human is seated. A timeout, a bad exit, or a response
/// the whitelist rejects disables this worker for the rest of the campaign so it cannot
/// stall every following day.
///
/// The request is a snapshot. The response may only queue commands named in <see cref="Capabilities"/>,
/// plus <c>study_doctrine</c> and <c>propose_diplomatic_act</c> which the parser also accepts.
/// Unknown command types fail the whole batch. Nothing is enqueued until the whole batch is valid.
/// This does not grant a second economy or a second battle. It queues the same commands the UI uses.
/// </summary>
public sealed class WorkerModBackend
{
    /// <summary>Bumped only when the request or response shape changes in a way old workers cannot ignore.</summary>
    public const int Protocol = 1;

    /// <summary>
    /// Advertised on the request. <see cref="AcceptResponse"/> also accepts <c>study_doctrine</c>
    /// and <c>propose_diplomatic_act</c>. A type that neither this list nor that parser names fails the batch.
    /// </summary>
    public static IReadOnlyList<string> Capabilities { get; } = Array.AsReadOnly(new[] {
        "set_feature", "improve_relations", "reaffirm_alliance", "enact_policy", "manage_debt", "course_shock" });
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly Action<string> _log;
    private readonly int _timeoutMs;
    public int IntervalDays { get; }
    public bool Disabled { get; private set; }
    private int _lastDay = -1;

    public WorkerModBackend(string executable, IEnumerable<string> arguments, int timeoutMs, int intervalDays, Action<string> log)
    {
        _executable = Path.GetFullPath(executable);
        _arguments = arguments?.ToArray() ?? Array.Empty<string>();
        _timeoutMs = Math.Clamp(timeoutMs, 50, 15000);
        IntervalDays = Math.Clamp(intervalDays, 1, 365);
        _log = log;
    }

    /// <summary>
    /// Run the process for <c>load</c>, <c>campaign_created</c>, or <c>day_advanced</c>.
    /// Day hooks fire only every <see cref="IntervalDays"/>, and never twice for the same day.
    /// The response is queued, not applied, inside this call.
    /// </summary>
    public void Hook(Campaign campaign, string hook)
    {
        if (Disabled) return;
        if (hook == "day_advanced" && (campaign.ElapsedDays == _lastDay || campaign.ElapsedDays % IntervalDays != 0)) return;
        _lastDay = campaign.ElapsedDays;
        try
        {
            // External processes are not lockstep peers. No local worker side effects in multiplayer.
            if (campaign.HumanSeats.Count > 1) throw new InvalidOperationException("Worker mods are single-player only in protocol v1.");
            var request = BuildRequest(campaign, hook);
            var response = InvokeAsync(request).GetAwaiter().GetResult();
            AcceptResponse(campaign, response, _log);
        }
        catch (Exception error)
        {
            Disabled = true; // A broken mod cannot hang or flood logs every simulation day.
            _log?.Invoke("Worker disabled: " + error.Message);
        }
    }

    /// <summary>
    /// One JSON object: protocol, event, day, seed, capabilities, nations ordered by id, features.
    /// Nation <c>id</c> is not on this snapshot; workers address countries by tag.
    /// Old workers may ignore new fields. Do not remove a field they already read.
    /// </summary>
    public static string BuildRequest(Campaign c, string hook) => JsonSerializer.Serialize(new {
        protocol = Protocol, @event = hook, day = c.ElapsedDays, seed = c.Seed,
        capabilities = Capabilities,
        nations = c.Nations.OrderBy(n => n.Id).Select(n => new {
            tag = n.Tag, name = n.Name, population = n.Population, stability = n.Stability,
            treasury = n.Treasury, public_debt = n.PublicDebt, debt_arrears = n.DebtArrears,
            payment_arrears = n.PaymentArrears, credit_confidence = n.CreditConfidence,
            debt_condition = n.DebtCondition.ToString(),
            course = n.NationalCourse.Dominant, course_initialized = n.NationalCourse.Initialized,
            inertia = n.NationalCourse.Entrenchment, shock = n.NationalCourse.Shock,
            convictions = n.NationalCourse.Convictions.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
            laws = n.Laws.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new {
                slot = p.Key, id = p.Value.Id, target_tag = p.Value.TargetTag, region = p.Value.Region }),
            promise = n.Agenda?.Id ?? "", promise_completed = n.Agenda?.Completed ?? false,
            promise_succeeded = n.Agenda?.Succeeded ?? false
        }),
        features = MechanicFeatures.BuiltIns.OrderBy(id => id, StringComparer.Ordinal).ToDictionary(id => id, c.FeatureEnabled)
    });

    private async Task<string> InvokeAsync(string request)
    {
        if (request.Length > 2 * 1024 * 1024) throw new InvalidDataException("Worker request exceeds the size limit.");
        using var deadline = new CancellationTokenSource(_timeoutMs);
        using var process = new Process { StartInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_executable),
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }};
        foreach (var argument in _arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Worker did not start.");
        try
        {
            var output = ReadBounded(process.StandardOutput, 256 * 1024, deadline.Token);
            var errors = ReadBounded(process.StandardError, 16 * 1024, deadline.Token);
            await process.StandardInput.WriteAsync(request.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            // WhenAll waits for *every* task even after a bounded reader rejects
            // output. A child still writing into that abandoned pipe then blocks,
            // and a size-limit violation needlessly waits for the entire timeout.
            // Observe each completion immediately so the existing catch kills our
            // child as soon as either stream or the process fails.
            var pending = new List<Task> { output, errors, process.WaitForExitAsync(deadline.Token) };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                await finished;
                pending.Remove(finished);
            }
            if (process.ExitCode != 0) throw new InvalidDataException($"Worker exit {process.ExitCode}: {await errors}");
            return await output;
        }
        catch
        {
            deadline.Cancel();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + count > maximum) throw new InvalidDataException("Worker output exceeds the size limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    /// <summary>
    /// Validate the complete batch before queueing anything. No arbitrary reflection or RPC.
    /// Issuer and target are tags. The command stores <c>Nation.Id</c>, the list index, only after the tag resolves.
    /// A single bad command rejects the batch, so a worker cannot apply half a turn.
    /// </summary>
    public static void AcceptResponse(Campaign c, string response, Action<string> log)
    {
        if (c.HumanSeats.Count > 1) throw new InvalidOperationException("Worker commands are single-player only.");
        if (response.Length > 256 * 1024) throw new InvalidDataException("Oversized response.");
        using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!root.TryGetProperty("protocol", out var version) || version.GetInt32() != Protocol)
            throw new InvalidDataException("Unsupported worker protocol.");
        var pending = new List<ICampaignCommand>(); var messages = new List<string>();
        if (root.TryGetProperty("logs", out var logs))
        {
            if (logs.ValueKind != JsonValueKind.Array || logs.GetArrayLength() > 32) throw new InvalidDataException("Invalid logs.");
            foreach (var item in logs.EnumerateArray())
            {
                var message = item.GetString();
                if (message is null || message.Length > 1024) throw new InvalidDataException("Invalid log message.");
                messages.Add(message);
            }
        }
        if (root.TryGetProperty("commands", out var commands))
        {
            if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() > 32) throw new InvalidDataException("Invalid command batch.");
            foreach (var item in commands.EnumerateArray())
            {
                var kind = item.GetProperty("type").GetString();
                if (kind == "set_feature")
                {
                    var id = item.GetProperty("feature").GetString();
                    if (!MechanicFeatures.ValidId(id)) throw new InvalidDataException("Invalid feature id.");
                    pending.Add(new WorkerFeatureCommand { Feature = id, Enabled = item.GetProperty("enabled").GetBoolean() });
                    continue;
                }
                var issuer = c.NationByTag(item.GetProperty("issuer").GetString());
                if (issuer is null) throw new InvalidDataException("Invalid issuer country.");
                if (kind == "enact_policy")
                {
                    var id = item.GetProperty("policy").GetString();
                    var promise = item.TryGetProperty("promise", out var promiseValue) && promiseValue.GetBoolean();
                    var targetTag = item.TryGetProperty("target", out var targetValue) ? targetValue.GetString() ?? "" : "";
                    var region = item.TryGetProperty("region", out var regionValue) ? regionValue.GetInt32() : 0;
                    if (PoliticalPolicyCatalog.Find(id, promise) is null || region < 0 ||
                        (targetTag.Length > 0 && c.NationByTag(targetTag) is null))
                        throw new InvalidDataException("Invalid policy, region or target.");
                    pending.Add(new AdoptPoliticalPolicyCommand {
                        IssuerId = issuer.Id, PolicyId = id, TargetTag = targetTag, Region = region, Agenda = promise });
                    continue;
                }
                if (kind == "manage_debt")
                {
                    var action = item.GetProperty("action").GetString();
                    var amount = item.TryGetProperty("amount", out var amountValue) ? amountValue.GetDouble() : 0;
                    if (action is not ("borrow" or "repay" or "restructure") || !double.IsFinite(amount) ||
                        amount < 0 || amount > float.MaxValue || (action is "borrow" or "repay" && amount <= 0))
                        throw new InvalidDataException("Invalid debt action or amount.");
                    pending.Add(new ManageDebtCommand { IssuerId = issuer.Id, Action = action, Amount = (float)amount });
                    continue;
                }
                if (kind == "course_shock")
                {
                    var strength = item.GetProperty("strength").GetDouble();
                    var reason = item.GetProperty("reason").GetString();
                    if (!double.IsFinite(strength) || strength <= 0 || strength > 1 || reason is null || reason.Length > 256)
                        throw new InvalidDataException("Invalid national-course shock.");
                    pending.Add(new ModCourseShockCommand { IssuerId = issuer.Id, Strength = (float)strength, Reason = reason });
                    continue;
                }
                if (kind == "study_doctrine")
                {
                    var branchId = item.GetProperty("branch").GetString() ?? "";
                    var nodeId = item.GetProperty("node").GetString() ?? "";
                    var rush = item.TryGetProperty("rush", out var rushVal) && rushVal.GetBoolean();
                    var branch = TacticalDoctrineCatalog.Find(branchId);
                    if (branch is null || branch.Find(nodeId) is null)
                        throw new InvalidDataException("Invalid doctrine branch or node.");
                    pending.Add(new StudyDoctrineCommand { IssuerId = issuer.Id, BranchId = branchId, NodeId = nodeId, Rush = rush });
                    continue;
                }
                if (kind == "propose_diplomatic_act")
                {
                    var actTarget = c.NationByTag(item.GetProperty("target").GetString());
                    var actRaw = item.GetProperty("act").GetString() ?? "";
                    if (actTarget is null || issuer.Id == actTarget.Id ||
                        !Enum.TryParse<DiplomaticAct>(actRaw.Replace("_", "", StringComparison.Ordinal), true, out var act))
                        throw new InvalidDataException("Invalid diplomatic act or target.");
                    pending.Add(new DiplomaticActCommand {
                        IssuerId = issuer.Id, TargetId = actTarget.Id, Act = act, Step = DiplomaticActStep.Propose });
                    continue;
                }
                var target = c.NationByTag(item.GetProperty("target").GetString());
                if (issuer is null || target is null || issuer.Id == target.Id) throw new InvalidDataException("Invalid country pair.");
                pending.Add(kind switch
                {
                    "improve_relations" => new ImproveRelationsCommand { IssuerId = issuer.Id, TargetId = target.Id },
                    "reaffirm_alliance" => new ReaffirmAllianceCommand { IssuerId = issuer.Id, TargetId = target.Id,
                        ProgramId = item.TryGetProperty("program", out var program) ? program.GetString() ?? "coordination" : "coordination",
                        Cancel = item.TryGetProperty("cancel", out var cancel) && cancel.GetBoolean() },
                    _ => throw new InvalidDataException("Unknown worker command: " + kind)
                });
            }
        }
        foreach (var command in pending) c.Commands.Enqueue(command);
        foreach (var message in messages) log?.Invoke(message);
    }
}

/// <summary>
/// The worker's <c>set_feature</c> command. Apply still asks <see cref="MechanicFeatures.Allows"/>
/// and still refuses a multiplayer campaign. Turning a switch off does not delete the content it gated.
/// </summary>
public sealed class WorkerFeatureCommand : ICampaignCommand
{
    public int IssuerId { get; init; } = -1;
    public string Feature { get; init; } = "";
    public bool Enabled { get; init; }
    public void Apply(Campaign c)
    {
        if (c.HumanSeats.Count <= 1 && MechanicFeatures.Allows(c, this)) c.SetFeature(Feature, Enabled);
    }
}
