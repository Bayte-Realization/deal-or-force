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

/// <summary>Trusted, stateless workers. This limits the protocol, not OS permissions.</summary>
public sealed class WorkerModBackend
{
    public const int Protocol = 1;
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
        _timeoutMs = Math.Clamp(timeoutMs, 50, 5000);
        IntervalDays = Math.Clamp(intervalDays, 1, 365);
        _log = log;
    }

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

    /// <summary>Additive v1 snapshot: old templates may ignore new fields.</summary>
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
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new InvalidDataException($"Worker exit {process.ExitCode}: {await errors}");
            return await output;
        }
        catch
        {
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

    /// <summary>Validate the complete batch before queueing anything. No arbitrary reflection/RPC.</summary>
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
