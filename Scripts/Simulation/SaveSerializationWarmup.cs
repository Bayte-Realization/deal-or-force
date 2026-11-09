using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DealOrForce.Simulation;

public static partial class SaveSystem
{
    private static readonly object SerializationWarmupGate = new();
    private static bool _serializationWarmed;

    /// <summary>
    /// Learn the persistence schema before play, not on the first autosave's frame.
    /// Only type contracts and the resolver's fresh defaults are visited: no campaign,
    /// catalogue enumeration, string-table context or mutable save graph crosses threads.
    /// The caller may run this on a worker while the loading screen keeps drawing.
    /// Actual capture/serialization still runs at the campaign's safe boundary.
    /// </summary>
    public static void WarmSerializationContracts()
    {
        lock (SerializationWarmupGate)
        {
            if (_serializationWarmed) return;
            // SimProfiler's scope table belongs to the simulation thread. Do not
            // add a worker scope to that mutable dictionary during map loading.
            var started = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            WarmContracts(JsonOptions);
            WarmContracts(TextOptions);
            _serializationWarmed = true;
            DiagnosticLogs.Write("performance", $"SAVE_CONTRACTS duration_ms={DiagnosticLogs.N(Stopwatch.GetElapsedTime(started).TotalMilliseconds)} allocated_bytes={GC.GetAllocatedBytesForCurrentThread() - allocated} thread={Environment.CurrentManagedThreadId}");
        }
    }

    private static void WarmContracts(JsonSerializerOptions options)
    {
        options.MakeReadOnly();
        var visited = new HashSet<Type>();
        Visit(typeof(CampaignSave));

        void Visit(Type type)
        {
            if (!visited.Add(type)) return;
            var contract = options.GetTypeInfo(type);
            foreach (var member in contract.Properties)
                if (member.Get is not null || member.Set is not null) Visit(member.PropertyType);
            if (type.IsArray) Visit(type.GetElementType());
            else if (contract.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
                foreach (var argument in type.GenericTypeArguments) Visit(argument);
        }
    }
}
