using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DealOrForce.Simulation;

/// <summary>Engine-independent sample profiler. Godot's monitors complement, not replace it.</summary>
public static class SimProfiler
{
    public sealed class Sample
    {
        public string Name = "";
        public double LastMilliseconds;
        public double AverageMilliseconds;
        public int Runs;

        /// <summary>
        /// Bytes this pass has asked the allocator for, in total. Wall time on a machine
        /// that is also running an editor is noise; garbage is not, and garbage is what
        /// turns into a stutter every time the collector has to catch up.
        /// </summary>
        public long AllocatedBytes;

        /// <summary>
        /// Total time, so a mean can be taken. The exponential average above is what a live
        /// readout wants; it is useless for comparing two runs, because it depends on which
        /// calls happened last.
        /// </summary>
        public double TotalMilliseconds;

        public double MeanMilliseconds => Runs == 0 ? 0 : TotalMilliseconds / Runs;
    }

    private static readonly object MapsLock = new();
    private static readonly List<Dictionary<string, Sample>> ThreadMaps = new();
    [ThreadStatic] private static Dictionary<string, Sample> _threadSamples;

    /// <summary>
    /// Fast global gate for sample accumulation. True by default for headless tests/benches;
    /// toggled off during live frame gameplay when diagnostics are off so scopes cost ~1 branch
    /// with zero timer/allocation calls or dictionary lookups.
    /// </summary>
    public static volatile bool Enabled = true;

    /// <summary>
    /// Optional diagnostic hook fired with the pass name as each measured scope ends.
    /// Headless isolation probes use it to localise the first stage where two campaigns
    /// in one process stop agreeing; it is null in every shipped run and costs one
    /// delegate check per pass.
    /// </summary>
    public static Action<string> StageProbe;

    private static Dictionary<string, Sample> ThreadSampleMap()
    {
        var map = _threadSamples;
        if (map is not null) return map;
        map = new Dictionary<string, Sample>(256);
        _threadSamples = map;
        lock (MapsLock) ThreadMaps.Add(map);
        return map;
    }

    public static IReadOnlyCollection<Sample> All
    {
        get
        {
            lock (MapsLock)
            {
                if (ThreadMaps.Count == 0) return Array.Empty<Sample>();
                if (ThreadMaps.Count == 1)
                {
                    var only = ThreadMaps[0];
                    var copy = new Sample[only.Count];
                    var idx = 0;
                    foreach (var s in only.Values)
                        copy[idx++] = new Sample
                        {
                            Name = s.Name,
                            Runs = s.Runs,
                            AllocatedBytes = s.AllocatedBytes,
                            TotalMilliseconds = s.TotalMilliseconds,
                            LastMilliseconds = s.LastMilliseconds,
                            AverageMilliseconds = s.AverageMilliseconds
                        };
                    return copy;
                }
                var merged = new Dictionary<string, Sample>();
                for (var ti = 0; ti < ThreadMaps.Count; ti++)
                {
                    foreach (var pair in ThreadMaps[ti])
                    {
                        var src = pair.Value;
                        if (!merged.TryGetValue(pair.Key, out var dst))
                        {
                            merged[pair.Key] = new Sample
                            {
                                Name = src.Name,
                                Runs = src.Runs,
                                AllocatedBytes = src.AllocatedBytes,
                                TotalMilliseconds = src.TotalMilliseconds,
                                LastMilliseconds = src.LastMilliseconds,
                                AverageMilliseconds = src.AverageMilliseconds
                            };
                        }
                        else
                        {
                            dst.Runs += src.Runs;
                            dst.AllocatedBytes += src.AllocatedBytes;
                            dst.TotalMilliseconds += src.TotalMilliseconds;
                            dst.LastMilliseconds = src.LastMilliseconds;
                            dst.AverageMilliseconds = dst.Runs == 0 ? 0 : dst.TotalMilliseconds / dst.Runs;
                        }
                    }
                }
                return new List<Sample>(merged.Values);
            }
        }
    }

    /// <summary>
    /// Forget every measurement.
    ///
    /// For a harness that wants to count one run's passes rather than a session's: how often
    /// a system ran is only an answer if something said when to start counting.
    /// </summary>
    public static void Reset()
    {
        lock (MapsLock)
        {
            for (var i = 0; i < ThreadMaps.Count; i++) ThreadMaps[i].Clear();
        }
    }

    public static Scope Measure(string name) => new(name);

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _started;
        private readonly long _allocated;
        private readonly bool _record;
        private readonly Action<string> _probe;
#if DOF_TRACY
        // The same scope as a Tracy zone (TracyBridge), opened only in a profiling build that
        // was started with DOF_TRACY=1 and has a viewer attached. Ordinary builds do not have
        // this field at all.
        private readonly Tracy.PInvoke.TracyCZoneCtx _zone;
        private readonly bool _traced;
#endif

        public Scope(string name)
        {
            _name = name;
            _record = Enabled;
            _probe = StageProbe;
#if DOF_TRACY
            // Decided once per scope, so a viewer connecting or leaving mid-scope can never
            // produce an end without its beginning.
            _traced = TracyBridge.Live;
            _zone = _traced ? TracyBridge.Begin(name) : default;
#endif
            if (_record
#if DOF_TRACY
                || _traced
#endif
               )
            {
                _allocated = GC.GetAllocatedBytesForCurrentThread();
                _started = Stopwatch.GetTimestamp();
            }
            else
            {
                _allocated = 0;
                _started = 0;
            }
        }

        public void Dispose()
        {
#if DOF_TRACY
            if (_traced)
            {
                var tracyAlloc = GC.GetAllocatedBytesForCurrentThread() - _allocated;
                TracyBridge.End(_zone, tracyAlloc);
            }
#endif
            if (!_record)
            {
                _probe?.Invoke(_name);
                return;
            }
            var elapsed = (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            var map = ThreadSampleMap();
            if (!map.TryGetValue(_name, out var sample))
            {
                sample = new Sample { Name = _name };
                map.Add(_name, sample);
            }

            sample.AllocatedBytes += allocated;
            sample.TotalMilliseconds += elapsed;
            sample.LastMilliseconds = elapsed;
            sample.AverageMilliseconds = sample.Runs == 0
                ? elapsed
                : sample.AverageMilliseconds + (elapsed - sample.AverageMilliseconds) * 0.2;
            sample.Runs++;
            if (DiagnosticLogs.Enabled && elapsed >= DiagnosticLogs.SlowScopeMilliseconds)
                DiagnosticLogs.Write("performance", $"SLOW_SCOPE name={_name} duration_ms={DiagnosticLogs.N(elapsed)} allocated_bytes={allocated}");
            _probe?.Invoke(_name);
        }
    }
}
