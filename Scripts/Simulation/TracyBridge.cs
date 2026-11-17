using System;
using System.Diagnostics;
#if DOF_TRACY
using System.Collections.Generic;
using System.Runtime.InteropServices;
using bottlenoselabs.C2CS.Runtime;
using Tracy;
#endif

namespace DealOrForce.Simulation;

/// <summary>
/// The one door to Tracy (https://github.com/wolfpld/tracy), the frame profiler.
///
/// SimProfiler already knows the name of every stage and adds up its time; what it cannot say
/// is what happened inside one particular day - which stage ran long on the day the game
/// stuttered, and what ran around it. Tracy draws exactly that, as a timeline. So nothing new
/// is measured here: every SimProfiler.Measure scope becomes a Tracy zone with the same name,
/// carrying the bytes it allocated as its value, and two frame marks cut the timeline into
/// simulation days and interface frames.
///
/// Compiled in only with `-p:DofTracy=true` (Scripts/Simulation/StructuredData.Dependencies.props);
/// in any other build the frame methods below are [Conditional] and their calls disappear at
/// compile time, and SimProfiler carries no Tracy code at all. In a Tracy build nothing is
/// sent unless the process was started with DOF_TRACY=1, and the native client is not even
/// loaded until then, because loading it is what opens its port. Even then a zone is only
/// emitted while a viewer is connected (see Live).
///
/// Read-only by construction: it observes clocks and allocation counters and never touches the
/// campaign, so it cannot move a lockstep world off its fingerprint.
/// </summary>
public static class TracyBridge
{
#if DOF_TRACY
    /// <summary>Read once: switching a profiler on halfway through a run would leave zones without a start.</summary>
    public static readonly bool On = Environment.GetEnvironmentVariable("DOF_TRACY") == "1";

    // Tracy identifies a zone's place by the address of a source-location record that must live
    // as long as the process. One record per distinct stage name, allocated the first time the
    // name is seen and never freed: there are a few hundred names, a few kilobytes in all.
    private static readonly Dictionary<string, nint> Locations = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    // Frame and plot names are compared by pointer on Tracy's side, so each must be the same
    // native string for the whole run, not a fresh copy per call.
    private static readonly CString DayFrame = CString.FromString("sim day");
    private static readonly CString HeapPlot = CString.FromString("managed heap MB");
    private static readonly CString AllocatedPlot = CString.FromString("allocated per day MB");
    private static readonly CString CollectionsPlot = CString.FromString("gen2 collections");
    private static readonly CString File = CString.FromString("SimProfiler.Measure");
    private static long _allocatedAtLastDay = -1;

    /// <summary>
    /// Whether to emit at all: switched on AND a viewer or tracy-capture is connected.
    ///
    /// Without a viewer the native client keeps every event in memory until one arrives, and
    /// a run that emits tens of millions of zones that way ends in an access violation inside
    /// the client (measured on 17 November 2026: 26 million zones with nobody listening failed
    /// every time; the same run with tracy-capture attached never did). So nothing is recorded
    /// until somebody is listening - the behaviour Tracy calls "on demand", which the packaged
    /// client was not built with. Checked per zone: SimBench --profiler-overhead measured a scope
    /// at 44 ns with this check and no viewer, against 41-44 ns in a build without Tracy.
    /// </summary>
    internal static bool Live => On && PInvoke.TracyConnected() != 0;

    internal static unsafe PInvoke.TracyCZoneCtx Begin(string name)
    {
        nint location;
        lock (Gate)
        {
            if (!Locations.TryGetValue(name, out location))
            {
                var record = (PInvoke.TracySourceLocationData*)NativeMemory.AllocZeroed((nuint)sizeof(PInvoke.TracySourceLocationData));
                var text = CString.FromString(name);
                record->_Name = text;
                record->_Function = text;
                record->_File = File;
                location = (nint)record;
                Locations.Add(name, location);
            }
        }
        return PInvoke.TracyEmitZoneBegin((PInvoke.TracySourceLocationData*)location, 1);
    }

    /// <summary>The zone's value is what it allocated: garbage is what turns into a pause later.</summary>
    internal static void End(PInvoke.TracyCZoneCtx zone, long allocatedBytes)
    {
        PInvoke.TracyEmitZoneValue(zone, (ulong)Math.Max(0, allocatedBytes));
        PInvoke.TracyEmitZoneEnd(zone);
    }
#endif

    /// <summary>
    /// A simulation day is complete. Marks the "sim day" frame and samples the heap, so a long
    /// day on the timeline can be laid against the memory that was growing under it.
    /// </summary>
    [Conditional("DOF_TRACY")]
    public static void SimulationDay()
    {
#if DOF_TRACY
        if (!Live) return;
        PInvoke.TracyEmitFrameMark(DayFrame);
        PInvoke.TracyEmitPlot(HeapPlot, GC.GetTotalMemory(false) / 1_048_576.0);
        var allocated = GC.GetTotalAllocatedBytes(false);
        if (_allocatedAtLastDay >= 0)
            PInvoke.TracyEmitPlot(AllocatedPlot, (allocated - _allocatedAtLastDay) / 1_048_576.0);
        _allocatedAtLastDay = allocated;
        PInvoke.TracyEmitPlotInt(CollectionsPlot, GC.CollectionCount(2));
#endif
    }

    /// <summary>An interface frame is complete: Tracy's main frame, the one its frame graph shows.</summary>
    [Conditional("DOF_TRACY")]
    public static void InterfaceFrame()
    {
#if DOF_TRACY
        if (Live) PInvoke.TracyEmitFrameMark(default);
#endif
    }
}
