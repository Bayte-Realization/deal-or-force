using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    /// <summary>
    /// Advance one whole campaign day, whatever the step length is.
    ///
    /// <see cref="Tick"/> advances a STEP, and a step has not been a day since the step
    /// became a setting: at the shipped hour it takes twenty-four of them. Anything that
    /// means "a day" - a harness measuring a day's cost, a test that wants a year of world -
    /// has to say so, or it silently runs a twenty-fourth of what it thinks and every
    /// duration it checks becomes a different claim.
    ///
    /// Bounded, because a paused campaign never advances and a step longer than a day
    /// overshoots on purpose: a ten-day step crosses ten days and this returns after one
    /// call, which is the honest answer to "advance a day" at that setting.
    /// </summary>
    public void TickDay()
    {
        if (IsPaused || SimulationFault.Length > 0) return;
        var until = ElapsedDays + 1;
        for (var steps = 0; ElapsedDays < until && steps < 240; steps++) Tick();
    }

    /// <summary>One step, all of it now. A step begun by StepFor and not finished is finished first.</summary>
    public void Tick()
    {
        FinishStep();
        StepFor(double.PositiveInfinity);
    }

    private IEnumerator<int> _step;
    private bool _runningStages;
    private long _stepStartedHours;

    /// <summary>
    /// When the stage now running was resumed (a Stopwatch timestamp). See <see cref="StageOverdue"/>.
    /// Of the thread, not of the campaign: stages run on the thread that steps, one at a time,
    /// and a loop that asks has no campaign variable it can be sure of. Not saved.
    /// </summary>
    [ThreadStatic] private static long _stageStarted;
    [ThreadStatic] private static long _stepDeadline;
    [ThreadStatic] private static bool _unboundedStep;

    internal static bool IsUnboundedStep => _unboundedStep;

    /// <summary>
    /// How long one stage may run before a loop that asks <see cref="StageOverdue"/> hands the
    /// frame back. Not a define and not saved: it decides how many frames carry a step, never
    /// what the step does.
    /// </summary>
    public static double StageSliceMilliseconds = 2.5;

    /// <summary>
    /// True when the stage now running has had its slice: a staged loop asks this between two
    /// of its items and yields when it is true. Then the slice starts again, so a loop walked by
    /// a plain foreach (a system's own Tick) also yields only every few milliseconds.
    ///
    /// A yield is not part of the world. The step runs the same items in the same order whether
    /// it yields between every one of them or never - nothing else in the simulation runs at a
    /// yield, and commands wait for the next step - so a loop may yield by the clock, which a
    /// world must never depend on, and two runs that yielded in different places are still the
    /// same world. That is what lets this replace the counts ("every 32 countries") that were
    /// tuned for a world of 260 countries and turned into 40-millisecond stages in a century-old
    /// save. Counted yields stay where they are; this is the ceiling over them.
    /// </summary>
    [ThreadStatic] private static bool _heavyNext;

    /// <summary>
    /// When the frame that is stepping began (a Stopwatch timestamp), set by SimulationFrameClock
    /// for the length of one frame and zero otherwise; a frame can make several StepFor calls,
    /// and the headroom below is the frame's, not one call's. Not saved.
    /// </summary>
    [ThreadStatic] public static long FrameStarted;

    /// <summary>How much of a frame may already be spent before a heavy piece is sent to the next one.</summary>
    public static double HeavyHeadroomMilliseconds = 2.0;

    /// <summary>
    /// Said by a staged loop just before it yields ahead of a piece it expects to be long and
    /// cannot cut (one country's war council, one country's new name): a bounded smooth step
    /// then ends the frame here, unless the frame is still fresh, so the piece starts the next
    /// frame. Like a yield, it changes when the frame ends and nothing the step does.
    /// </summary>
    public static void HeavyNext() => _heavyNext = true;

    public static bool StageOverdue()
    {
        if (_unboundedStep) return false;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_stepDeadline != 0 && now >= _stepDeadline)
        {
            _stageStarted = now;
            return true;
        }
        if ((now - _stageStarted) * 1000.0 < StageSliceMilliseconds * System.Diagnostics.Stopwatch.Frequency) return false;
        _stageStarted = now;
        return true;
    }

    /// <summary>Whether a step has been begun by StepFor and is not finished yet.</summary>
    public bool MidStep => _step is not null;

    /// <summary>A complete step, after its iterator is cleared; saves and external orders share this boundary.</summary>
    public event Action<Campaign> StepCompleted;

    /// <summary>
    /// Begin a step, or carry on with the one begun, for at most this many milliseconds of
    /// work; true once it is finished. This is the smoothing: the day's passes and the week's
    /// economy are spread over as many frames as they need, in the order they always run, and
    /// the clock does not move on until the step is done - the same world, without the frame
    /// that used to stop for a third of a second every simulated week.
    /// </summary>
    /// <param name="heavyStageMilliseconds">
    /// When the call is bounded and this ceiling is finite, a stage whose own time reaches
    /// it ends the call. Later stages wait for the next call. They run in the same order
    /// when it comes. The default, positive infinity, leaves the budget as the only stop.
    /// An unbounded step finishes either way: a replay that asks for the whole day still gets it.
    /// </param>
    public bool StepFor(double milliseconds, double heavyStageMilliseconds = double.PositiveInfinity)
    {
        if (_step is null)
        {
            _stepStartedHours = ElapsedHours;
            _step = TickStages().GetEnumerator();
        }
        return RunStages(_step, milliseconds, heavyStageMilliseconds);
    }

    /// <summary>Finish a step in progress, so a save, a snapshot or a command never sees half of one.</summary>
    public void FinishStep()
    {
        // TickStages applies its initial orders while MoveNext is executing. That is
        // already a stable boundary, and reentering the same iterator would discard it.
        if (_step is not null && !_runningStages) RunStages(_step, double.PositiveInfinity, double.PositiveInfinity);
    }

    private bool RunStages(IEnumerator<int> stages, double milliseconds, double heavyStageMilliseconds)
    {
        // A whole-step caller has no deadline. Bounded frames use a monotonic
        // timestamp, not a heap-allocated Stopwatch on every iterator resume.
        // A finite heavy ceiling ends the call after a stage that reached it, so the
        // frame that asked for one heavy stage does not keep starting the next ones
        // until the budget is gone. The ceiling is ignored for an unbounded step.
        var bounded = !double.IsPositiveInfinity(milliseconds);
        var heavyBounded = bounded && heavyStageMilliseconds >= 0 && !double.IsInfinity(heavyStageMilliseconds);
        var started = bounded ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        var freqMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        var stepDeadline = bounded && milliseconds > 0.0 ? started + (long)(milliseconds * freqMs) : 0L;
        var stepPreEntryCutoff = bounded && milliseconds > 0.0
            ? started + (long)(Math.Max(0.0, milliseconds - Math.Min(0.85, milliseconds * 0.25)) * freqMs)
            : 0L;
        var ranAny = false;
        var finished = false;
        var startedHours = _stepStartedHours;
        var prevDeadline = _stepDeadline;
        var prevUnbounded = _unboundedStep;
        _stepDeadline = stepDeadline;
        _unboundedStep = !bounded;
        _runningStages = true;
        try
        {
            while (true)
            {
                if (ranAny && bounded)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (stepPreEntryCutoff != 0 && now >= stepPreEntryCutoff) break;
                    if (heavyBounded && _heavyNext &&
                        System.Diagnostics.Stopwatch.GetElapsedTime(FrameStarted != 0 ? FrameStarted : started).TotalMilliseconds >=
                        HeavyHeadroomMilliseconds)
                        break;
                }
                ranAny = true;
                var stageStarted = heavyBounded ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                _stageStarted = heavyBounded ? stageStarted : System.Diagnostics.Stopwatch.GetTimestamp();
                _heavyNext = false;
                if (!stages.MoveNext()) { finished = true; break; }
                // An unbounded step never takes the heavy break. The ceiling is a second
                // stop only for a call that asked for one.
                if (bounded && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds) break;
                if (heavyBounded &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds >= heavyStageMilliseconds)
                    break;
                // The stage just finished said the next one is a single piece that cannot be cut
                // (HeavyNext). If this frame has already spent its headroom, that piece waits for
                // the next frame and starts it, instead of landing on top of the stages that
                // filled this one: the frame then costs the piece, not the piece plus a slice.
                if (heavyBounded && _heavyNext &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(FrameStarted != 0 ? FrameStarted : started).TotalMilliseconds >=
                    HeavyHeadroomMilliseconds)
                    break;
            }
        }
        catch (Exception ex)
        {
            finished = true;
            CrashEnvelope.Dump(this, ex);
            throw;
        }
        finally
        {
            _stepDeadline = prevDeadline;
            _unboundedStep = prevUnbounded;
            _runningStages = false;
            if (finished)
            {
                if (ReferenceEquals(stages, _step)) _step = null;
                stages.Dispose();
            }
        }
        // Notify only after clearing the iterator. A hook may issue commands or save;
        // neither should recurse into the day whose last stage just returned.
        if (finished && ElapsedHours != startedHours && !DeferMainThreadNotifications) StepCompleted?.Invoke(this);
        // A step that crossed midnight closes a simulation day on Tracy's timeline. Marked
        // here, once the step is whole, rather than inside the day loop: a frame that ended
        // halfway through a bounded StepFor would cut a day in two.
        if (finished && ElapsedHours / 24 != startedHours / 24) TracyBridge.SimulationDay();
        return finished;
    }

    private IEnumerable<int> TickStages()
    {
        // =====================================================================================
        // THE RHYTHM OF THE SIMULATION - the contract every system here obeys.
        //
        // A step is whatever the player or a bench asks for: six hours, one hour, three days. The
        // whole architecture is a promise that the answer does not depend on which of those it was.
        //
        //   PASSES PER STEP (this half of the method). Marching, supply, organisation, the fighting
        //   itself and the building of things accumulate at a rate per day, so they are handed the
        //   fraction of a day the step covers, and they run once per step whatever the step is. That
        //   is why a six-hour tick moves an army a quarter of the way instead of not at all.
        //
        //   PASSES PER DAY (the loop further down, marked "THE DAY, IN ORDER"). A decision happens
        //   on a day: an event either fires today or it does not, the economy clears on the week,
        //   history takes one sample. These run once for every whole day the step crossed, which is
        //   what stops them from running four times on an hourly clock and once on a six-hour one.
        //
        //   PASSES ON THEIR OWN CLOCK. Inside the day a system may gate itself: `OnItsDay` spreads
        //   the countries of the world across a cycle so that a monthly decision does not land on
        //   the same day for everybody, and `% 7` / `% 30` gates keep weekly and monthly work off
        //   the daily path. A gate is not an optimisation detail, it is part of this contract: a
        //   system that loses its gate runs thirty times more often than it was tuned for.
        //
        // Orders are applied at the top of the step, before anything reads the world, so a player's
        // command and a computer's are read at exactly the same point of the clock.
        // =====================================================================================
        if (IsPaused || SimulationFault.Length > 0) yield break;
        // A world under the ice or the static (WorldCurses): the clock goes on and nothing
        // else does - no system runs and no order is carried out, the player's included.
        if (WorldCurses.Frozen(this))
        {
            Commands.Discard();
            ElapsedHours += TickHours;
            Date.Advance(TickHours);
            SimulationTickClockSeq++;
            StepOpensNewDay = false;
            DiagnosticLogs.SetDay(ElapsedDays);
            yield break;
        }
        DiagnosticLogs.SetDay(ElapsedDays);
        if (DiagnosticLogs.Enabled) DiagnosticLogs.Write("simulation", $"DAY_BEGIN date={Date} pending_commands={Commands.Count}");

        // Three parts named apart: a quarter of a millisecond every hour was billed to "orders"
        // in a world where nobody was giving any, and the table could not say which part it was.
        using (SimProfiler.Measure("step:orders"))
        {
            using (SimProfiler.Measure("  orders:apply")) Commands.ApplyAll(this);
            using (SimProfiler.Measure("  orders:unlocks")) EvaluateFeatureUnlocks();

            // The long-range reach index, rebuilt fresh from the orders the step has just
            // applied: the fighting below asks it who reaches into each battle, and a client
            // restored from a snapshot must hold the same answer a host does at the same hour.
            using (SimProfiler.Measure("  orders:support")) SupportCore.PrepareStep(this);
        }

        // What the step covers, and how many whole days it crosses. A day boundary is what
        // the day pass is for; the hours are what everything that accumulates gets.
        var hours = TickHours;
        var beforeHours = ElapsedHours;
        var before = ElapsedDays;
        var endHours = beforeHours + hours;
        var endDays = (int)(endHours / 24);
        var daysCrossed = endDays - before;
        var tailHours = 0;
        if (daysCrossed <= 1)
        {
            ElapsedHours = endHours;
            Date.Advance(hours);
        }
        else
        {
            // Advance only to the first crossed day boundary before combat and day 0 so multi-day
            // steps (e.g. 48h/72h) execute each crossed day on its own sequential calendar day
            // instead of skipping odd days and firing even-day gates twice.
            var firstHours = (int)((before + 1L) * 24L - beforeHours);
            ElapsedHours += firstHours;
            Date.Advance(firstHours);
            tailHours = (int)(endHours - ((before + (long)daysCrossed) * 24L));
        }
        SimulationTickClockSeq++;
        // Read by the handful of per-step passes that are really once-a-day decisions. See
        // StepOpensNewDay for why they cannot simply be moved into the day loop below: they
        // have to run before the fighting, not after it.
        StepOpensNewDay = daysCrossed > 0;
        DiagnosticLogs.SetDay(ElapsedDays);
        // The weird route walks every formation towards the water before anything else moves.
        if (Curse.Length > 0) WorldCurses.March(this);

        // Each pass is measured so both the in-game console (`sim`) and the headless
        // bench can say which system a slow day was actually spent in, rather than
        // leaving "the simulation is slow" as the whole diagnosis.
        //
        // These accumulate: marching, supply, organisation, the fighting itself and the
        // building of things all happen at a rate per day, and a step that covers a
        // fraction of a day gives them that fraction. They run once per step whatever the
        // step is, which is what lets a six-hour tick move an army a quarter of the way
        // instead of not at all.
        if (StepOpensNewDay)
        {
            yield return 0;
            using (var militaryAccess = MilitaryAccess.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("military access")) more = militaryAccess.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var expeditions = Expeditions.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("expeditions")) more = expeditions.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var borderShape = BorderShape.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("border shape")) more = borderShape.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var civilWarFronts = CivilWarFronts.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("civil war fronts")) more = civilWarFronts.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var movementMergers = MovementMergers.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("movement mergers")) more = movementMergers.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        }
        using (var diplomaticActs = ModSimulationPasses.RunStaged(this, SimulationPass.DiplomaticActs, DiplomaticActs.TickStages).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("diplomatic acts")) more = diplomaticActs.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        yield return 0;
        using (var logistics = ModSimulationPasses.RunStaged(this, SimulationPass.Logistics, LogisticsSystem.TickStages).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("logistics")) more = logistics.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        // A frame may stop here and between the fleets and the fighting: the same passes in the
        // same order, but the step's heaviest pair - supply and the battles with their staff
        // work, thirty milliseconds together - no longer has to land in one frame.
        yield return 0;
        using (var military = MilitaryOperations.TickStages(this).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("military")) more = military.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        yield return 0;
        if (StepOpensNewDay)
        {
            using (var admiralty = AiAdmiralty.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("naval"))
                    using (SimProfiler.Measure("  naval:admiralty")) more = admiralty.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        }
        using (var navalOps = NavalOperations.TickStages(this).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("naval"))
                using (SimProfiler.Measure("  naval:operations")) more = navalOps.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        // Formations crossing the sea by one order: march, board, sail, land, march on.
        using (SimProfiler.Measure("naval"))
        using (SimProfiler.Measure("  naval:sealift")) SeaLift.Tick(this);
        // Those aboard are where their ships are.
        NavalInvasion.CarryAlong(this);
        yield return 0;
        // Staged, not measured as one block: the pass yields between its own sections and
        // between wars, so wrapping the enumeration in a single Measure would bill the
        // suspended frames to "warfare". Every section inside keeps its own measure.
        foreach (var _ in ModSimulationPasses.RunStaged(this, SimulationPass.Warfare, Warfare.TickStages))
            yield return 0;
        // Right after the fighting: the step's broken formations are removed and the
        // countries that lost them start replacing them.
        using (var  attrition = Attrition.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("attrition")) more = attrition.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        if (StepOpensNewDay)
        {
            // A pocket the computer is in is a pocket it tries to leave.
            using (var pocketBreakout = PocketBreakout.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("pocket breakout")) more = pocketBreakout.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // And a pocket whose stores are gone, with nobody coming, gives up (StrandedGarrisons).
            using (var pocketSurrender = StrandedGarrisons.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("pocket surrender")) more = pocketSurrender.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // The ordinary staff is conservative by design. A war for existence is the one
            // exception: mend a hole with a reserve, take an empty enemy province, or queue an
            // emergency formation - all through the same commands and the same manpower/debt
            // rules every player uses.
            using (var warEmergency = WarEmergency.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("war emergency")) more = warEmergency.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        }
        // What the services learned from that fighting, and what the army is reading: articles of
        // the war doctrine are studied by the day, and they are shares of the same arithmetic.
        using (var militaryExperience = MilitaryExperience.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("military experience")) more = militaryExperience.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        using (var tacticalDoctrines = TacticalDoctrines.TickStages(this).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("tactical doctrines")) more = tacticalDoctrines.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        using (var strategicDoctrines = StrategicDoctrines.TickStages(this).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("strategic doctrines")) more = strategicDoctrines.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        using (SimProfiler.Measure("construction")) ModSimulationPasses.Run(this, SimulationPass.Construction, ConstructionSystem.Tick);
        yield return 0;

        // And these happen on a day rather than during one. An event either fires today or
        // it does not; the economy clears on the week; history takes one sample. Running
        // them once for each whole day the step crossed is what keeps the answer the same
        // whether the campaign is stepping six hours at a time or three days.
        for (var day = 0; day < daysCrossed; day++)
        {
            if (day > 0)
            {
                ElapsedHours += 24;
                Date.Advance(24);
                DiagnosticLogs.SetDay(ElapsedDays);
            }
            // =====================================================================================
            // THE DAY, IN ORDER - read this before adding anything below.
            //
            // Sixty passes run here, every whole day the step crosses. They are grouped by what
            // they read, and the grouping is the reason for the order:
            //
            //   1. A country's own numbers ..... recruitment, contractors, stockpiles, support,
            //                                    militia, modifiers
            //   2. What people think ........... social memory, opinion
            //   3. Treaties and courts ......... alliances, guarantees and agreements, gestures,
            //                                    land arrangements, treasury dynamics
            //   4. Our hand abroad ............. parallel administration, internal migration
            //   5. Sanity ...................... numeric integrity (reports; never repairs)
            //   6. What the war costs .......... war damage, war weariness, civil-war reserves,
            //                                    crisis ladder, DEFCON
            //   7. Power inside ................ factions, defections, desertion, coups
            //   8. The world's furniture ....... proxy wars, organizations and their life, deals,
            //                                    ultimatums, foreign assistance
            //   9. Movements and our kin ....... movement methods, kin statecraft
            //  10. Long arcs ................... research, shipbuilding, statecraft, subjects,
            //                                    consolidation, culture union, corridors, world war,
            //                                    intel, covert operations, addresses
            //  11. Record and story ............ historical timeline, events, history sample
            //  12. The map changes ............. secession, union, demography, extinction, resistance,
            //                                    civil-war outbreak, names, governance, rulers,
            //                                    crises, mood, promises, goals, identity, naming,
            //                                    colonisation, submission, occupations, lost lands,
            //                                    capital
            //
            // Three sentences for why: numbers before opinions, opinions before treaties, treaties
            // before the map; a war's cost is counted before anything is decided about it; and the
            // map is redrawn last, because every pass above was worked out on the map that is still
            // standing.
            //
            // ADDING SOMETHING? Put it in the group whose state it reads, and put it here only if it
            // is a daily decision - the passes before this loop scale with the hours in the step and
            // must not. A pass that writes anything must also reach the save (SaveSystem) and, when
            // it changes what the world *is*, the fingerprint (CampaignFingerprint); otherwise a
            // loaded campaign and the bench disagree and nothing says so.
            //
            // A phase that is heavy belongs behind a day-cycle gate (`MovementRules.OnItsDay`) or
            // in stages (`DemographicCycle.Stages`), never dropped in raw: at the hourly step the
            // profiler shows a single system eating the frame, and this list is where a reader looks
            // first.
            // =====================================================================================
            using (var recruitment = RecruitmentSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("recruitment")) more = recruitment.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var contractors = Contractors.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("contractors")) more = contractors.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var stockpiles = StrategicStockpiles.TickDayStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("stockpiles")) more = stockpiles.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var supportCore = SupportCore.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("support")) more = supportCore.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var militia = MilitiaCore.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("militia")) more = militia.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var modifiers = ModifierSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("modifiers")) more = modifiers.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var socialMemory = SocialMemory.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("social memory")) more = socialMemory.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var opinions = OpinionSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("opinion")) more = opinions.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var reviews = AllianceMaintenance.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("alliances"))
                    using (SimProfiler.Measure("  alliance:maintenance")) more = reviews.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var shifts = AllyRivalShift.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("alliances"))
                    using (SimProfiler.Measure("  alliance:rivalry")) more = shifts.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Offers and requests that nobody answered stop occupying the tie. Both are state
            // that travels in a save, so both have to expire somewhere.
            using (var guarantees = Guarantee.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("diplomatic_offers")) more = guarantees.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var agreements = AgreementSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("diplomatic_offers")) more = agreements.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Visits, shows of force, mediation and loans (DiplomaticGestures): the instalments weekly, the computer's choices monthly.
            using (var gestures = DiplomaticGestures.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("diplomatic_gestures")) more = gestures.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var gestureAi = DiplomaticGestures.AiTickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("diplomatic_gestures")) more = gestureAi.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Rents, terms, the troops that stay and the owners who answer them (LandArrangements).
            using (var landarrangements = LandArrangementSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("land_arrangements")) more = landarrangements.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Why money does not pile up: erosion, the pressure to spend, the credit cycle, the bills nobody budgets for.
            using (var treasury = TreasuryDynamics.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("treasury_dynamics")) more = treasury.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Somebody else's paperwork inside somebody else's region: what it takes, what it
            // costs, and whether the host has noticed (ParallelAdministration).
            using (var paralleladministration = ParallelAdministration.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("parallel administration")) more = paralleladministration.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Votes under the troops in regions held a year or more (HeldRegions).
            HeldRegions.AiMonth(this);
            yield return 0;
            // A government relieves the commander who keeps losing (Generals).
            Generals.AiMonth(this);
            yield return 0;
            // And strikes off the army groups nobody serves in any more (Generals.Tidy).
            using (SimProfiler.Measure("army groups")) Generals.Tidy(this);
            yield return 0;
            foreach (var _ in EconomySystem.TickStages(this)) yield return 0;
            // In stages, like the economy before it (BrainDrain.Stages).
            using (var drain = BrainDrain.Stages(this).GetEnumerator())
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("brain drain")) more = drain.MoveNext();
                    yield return 0;
                    if (!more) break;
                }
            // And inside each country: from the regions that are worse off to the ones that are
            // better, and away from the fighting, carrying who they are.
            using (var internalmigration = InternalMigration.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("internal migration")) more = internalmigration.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // Before political actions read thresholds: comparisons with NaN can otherwise
            // authorise every uprising. Do not let one bad economy contaminate the map.
            string invalid = null;
            if (ElapsedDays % 7 == 0)
            {
                using (var integrity = NumericIntegrity.FirstLiveIssueStages(this, found => invalid = found).GetEnumerator())
                {
                    while (true)
                    {
                        bool more;
                        using (SimProfiler.Measure("integrity")) more = integrity.MoveNext();
                        if (!more) break;
                        yield return 0;
                    }
                }
            }
            if (invalid is not null)
            {
                BlockSimulation(invalid);
                throw new CampaignIntegrityException(invalid);
            }
            yield return 0;
            using (var warDamage = WarDamage.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("war damage")) more = warDamage.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var warWeariness = WarWeariness.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("war weariness")) more = warWeariness.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var civilWar = CivilWarPersistence.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("civil war reserves")) more = civilWar.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // Coalition members that want out of a war, the other side buying them out, leaders paying them to stay.
            using (var separatePeace = SeparatePeace.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("separate peace")) more = separatePeace.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            foreach (var _ in Escalation.TickStages(this)) yield return 0;
            using (var crisisLadder = CrisisLadder.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("crisis ladder")) more = crisisLadder.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var defcon = DefconSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("defcon system")) more = defcon.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var factions = Factions.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("factions")) more = factions.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // A formation that has been cut off or is standing among its own people can
            // change sides. Monthly, deterministic, and only after the fighting has moved.
            using (var defections = Defections.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("defections")) more = defections.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var reclamation = MainlandReclamation.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("mainland reclamation")) more = reclamation.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var desertion = Desertion.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("desertion")) more = desertion.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // Fighters of a rising who scattered after a lost battle: weekly, they drift home
            // or take the field again where nobody is standing (Partisans).
            using (var partisans = Partisans.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("partisans")) more = partisans.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var coups = Coups.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("coups")) more = coups.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var proxywars = ProxyWars.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("proxy wars")) more = proxywars.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var organizations = InternationalOrganizations.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("organizations")) more = organizations.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            foreach (var _ in OrganizationDynamics.TickStages(this)) yield return 0;
            yield return 0;
            using (var deals = DealNegotiations.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("deal inbox")) more = deals.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var ultimatumdesk = Ultimatum.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("ultimatum desk")) more = ultimatumdesk.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var foreignassistance = ForeignAssistance.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("foreign assistance")) more = foreignassistance.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            foreach (var _ in MovementRules.TickStages(this)) yield return 0;
            foreach (var _ in MovementAi.TickStages(this)) yield return 0;
            using (var movMethods = MovementMethods.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("movements"))
                    using (SimProfiler.Measure("  mov:methods"))
                        more = movMethods.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // A country's own hand among its own people abroad (KinStatecraft): organisers and
            // volunteers across a border, monthly and quiet, where the demography gives it a
            // people to work. It happens after the movements have acted, because what it may do
            // about a rising is join one that already exists.
            foreach (var _ in KinStatecraft.TickStages(this)) yield return 0;
            yield return 0;
            foreach (var _ in ResearchSystem.TickStages(this)) yield return 0;
            yield return 0;
            using (var shipbuilding = Shipbuilding.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("shipbuilding")) more = shipbuilding.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var statecraftRules = Statecraft.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("statecraft rules")) more = statecraftRules.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var subjects = SubjectSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("subjects")) more = subjects.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var borderWarm = BorderShape.EnsureStages(this).GetEnumerator()) while (borderWarm.MoveNext()) { }
            foreach (var _ in AiStatecraft.TickStages(this)) yield return 0;
            using (var consolidation = Consolidation.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("consolidation")) more = consolidation.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var cultureunion = CultureUnion.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("culture union")) more = cultureunion.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var corridors = Corridors.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("corridors")) more = corridors.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var borderdiplomacy = BorderDiplomacy.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("border diplomacy")) more = borderdiplomacy.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var worldwar = WorldWar.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("world war")) more = worldwar.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var intelNetworks = IntelNetworks.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("intel networks")) more = intelNetworks.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var covertOps = CovertOperations.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("covert operations")) more = covertOps.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var addresses = AddressToNation.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("addresses")) more = addresses.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            if (FeatureEnabled("movements") && MechanicFeatures.SystemEnabled(this, nameof(MovementPower)) && MovementRules.NeedsPeopleIndex(this))
            {
                var dueForPower = false;
                for (var ni = 0; ni < Nations.Count; ni++)
                {
                    var m = Nations[ni];
                    if (m.Movement is { Dissolved: false } && MovementRules.OnItsDay(this, m.Id, 7))
                    { dueForPower = true; break; }
                }
                if (dueForPower)
                    foreach (var _ in MovementRules.EnsurePeopleIndexedStages(this, insideMovRules: false))
                        yield return 0;
            }
            foreach (var _ in MovementPower.TickStages(this)) yield return 0;
            yield return 0;
            using (var historicalEvents = HistoricalTimeline.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("historical events")) more = historicalEvents.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var events = EventSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("events")) more = events.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (SimProfiler.Measure("history")) History.Capture(this);
            yield return 0;

            // Whether any part of any country has finished deciding it is not part of it.
            // Last in the day, because it redraws the map the rest of the day was worked
            // out on; it keeps its own weekly schedule, because it also has to clear its
            // report list on the days it does nothing.
            using (var secession = SecessionEvents.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("secession")) more = secession.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        // A proclaimed state is tested once a day, not once: history does not stop at the moment a
        // country is proclaimed (UnionCohesion).
        using (var unionCohesion = UnionCohesion.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("union")) more = unionCohesion.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        // Age structure and the cultures that migration moves: the demographic cycle reads the births
        // and deaths the household review has just counted, and aggregates the country's shape so a
        // tooltip or a modifier source reads one number (DemographicCycle).
        // In stages, so the week's heaviest pass does not land in one frame (DemographicCycle.Stages).
        using (var demography = DemographicCycle.Stages(this).GetEnumerator())
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("demography")) more = demography.MoveNext();
                if (!more) break;
                yield return 0;
            }
        if (ElapsedDays > 0 && ElapsedDays % Math.Max(1, Defines.EconomyTickDays) == 0 &&
            (Defines.StaggerWeeklyEconomy || Defines.EconomyWeekStaggered))
        {
            EconomySystem.CompactWorldEmptiedGroups(this);
        }
        // A country that is running out of people is counted here, straight after the cycle that
        // counts them. What becomes of its empty ground is a later decision about inheritance,
        // deliberately not this line (DEMOGRAPHIC_EXTINCTION_DESIGN_RU.md).
        using (var extinction = DemographicExtinction.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("extinction")) more = extinction.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        // What holding somebody else's ground costs: once a week, an occupation nobody is
        // standing in slips (OccupationResistance, gated, off by default like extinction).
        using (var resistance = OccupationResistance.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("resistance")) more = resistance.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // And whether any country has lost its government's grip on part of itself.
            using (var civilWarOutbreak = CivilWarOutbreak.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("civil war outbreak")) more = civilWarOutbreak.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // And whether a state failing on several fronts at once is coming apart, its capital
            // overthrowing its government, or a piece of a state that came apart going home
            // (StateFailure). After the single outbreak, so the two read the same day's world.
            using (var stateFailure = StateFailure.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("state failure")) more = stateFailure.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (SimProfiler.Measure("country names")) CountryNames.Remember(this);

            // And whether any country has stopped being what it was. After secession
            // because a state that has just lost a province is a state in exactly the
            // condition this reads.
            using (var governanceEvents = GovernanceEvents.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("governance")) more = governanceEvents.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            using (var rulers = Rulers.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("rulers")) more = rulers.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (var crises = DomesticCrises.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("domestic crises")) more = crises.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // And whether the country still agrees with the government that holds it. Derived
            // from what the residents have and believe, never a bonus or penalty per tag.
            using (var politicalMood = PoliticalMood.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("political mood")) more = politicalMood.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;

            // And whether a promise made in public has come true or missed its date. Checked
            // continuously, so early fulfilment is credited the day it happens; a government
            // with no promises out costs nothing to ask.
            using (var govPromises = GovernmentPromises.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("government promises")) more = govPromises.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            // And whether a government the player does not run says something in public about
            // its own crisis: the same bet on the same terms, taken by everyone else.
            using (var aiPromises = AiPromises.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("ai promises")) more = aiPromises.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;

            // And what the player's own pinned goals say now that the day's facts are in.
            // Private bookkeeping: a goal met changes nothing in the world but the record.
            using (var strategicGoals = StrategicGoals.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("strategic goals")) more = strategicGoals.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;
            using (SimProfiler.Measure("grand projects")) global::DealOrForce.Simulation.GrandProjects.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("world situations")) global::DealOrForce.Simulation.WorldSituations.Tick(this);
            yield return 0;
            using (var healthDisasters = global::DealOrForce.Simulation.DiseasesAndDisasters.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("diseases & disasters")) more = healthDisasters.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }

            // And first sight of identities: a country that declares nothing yet declares
            // what it already is - its own plurality, silently and at nobody's cost.
            StateIdentity.EnsureDeclared(this);
            // And what a founded state has decided to call itself, once it has stood long
            // enough to have decided anything.
            using (var nationNaming = NationNaming.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("naming")) more = nationNaming.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;

            // Settlers on land nobody owns, and whether anybody new sets out for it.
            using (var colonisation = ColonisationCore.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("colonisation")) more = colonisation.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }

            // And whether any country nobody plays asks a smaller one to answer to it.
            using (var submission = SubmissionSystem.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("submission")) more = submission.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }

            // Ground held by a country nobody is at war with goes home: whatever ended the
            // war today - a peace, a submission, a subjugation - the troops do not stay.
            // Ground held by arrangement first - a lease, troops in without a war - so the
            // occupations that go home below are only the ones nothing holds there.
            if (FeatureEnabled("land_arrangements"))
            {
                using (SimProfiler.Measure("land arrangements")) LandArrangementSystem.Enforce(this);
                yield return 0;
            }
            using (var occupations = Occupations.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("occupations")) more = occupations.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;

            // And whether a country played by somebody has ground it lost that it could take back.
            using (var lostLands = LostLands.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("lost lands")) more = lostLands.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            // And what a generation under one flag makes of a region: home, or a memory (HomelandIntegration).
            using (var homelands = HomelandIntegration.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("homelands")) more = homelands.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
            yield return 0;

            // Pieces of one region that have come back under one flag become one region again
            // today, not on the next load. Only an annexation used to ask; ground that changed
            // hands any other way stayed two regions in the running game and one in its save,
            // so saving and loading changed the world - and a player joining a network game,
            // who is sent exactly that save, started in a different world from the host's.
            using (SimProfiler.Measure("reunion")) StatePartition.Reunify(this);
            yield return 0;
            // The relations of countries with nobody left go to sleep; a restored country's wake.
            using (SimProfiler.Measure("relation dormancy")) RelationDormancy.Sweep(this);
            yield return 0;
            // And every fourth week the long dead are retired and what they left behind is struck (LongCampaign).
            using (SimProfiler.Measure("long campaign")) LongCampaign.Sweep(this);
            yield return 0;

            // And last, where each government now sits: a region ceded today takes its
            // capital with it, and the country moves to what it has left.
            using (var capitalCore = CapitalCore.TickStages(this).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("capital")) more = capitalCore.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }
        }
        if (tailHours > 0)
        {
            ElapsedHours += tailHours;
            Date.Advance(tailHours);
        }
        CampaignIntel.InvalidateActors(this);
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation", $"DAY_END date={Date} player={Player?.Tag} divisions={Armies.Count} fleets={Fleets.Count} wars={Wars.Count(w => !w.Ended)} battles={Battles.Count(b => !b.Ended)} training={Recruitment.Count} construction={Construction.Count} treasury={DiagnosticLogs.N(Player?.Treasury ?? 0)} gdp={DiagnosticLogs.N(Player?.Gdp ?? 0)} map_revision={MapRevision}");

        // The interface redraws what changed when it next draws (FlushInterfaceChanges), not
        // once a step. At the hourly step a frame at top speed runs several steps, and every
        // one of them redrew the whole interface - the top bar, the side panel, two hundred
        // and sixty names on the map - to show a state the player saw for no frame at all.
        //
        // What cannot wait for the frame is whatever stops the clock: an event or an offer
        // put to a player is answered by the interface pausing, and the step after it would
        // be a step too many.
        if (SomethingAwaitsAPlayer() && !DeferMainThreadNotifications) Changed?.Invoke();
        else InterfaceDirty = true;
    }

    internal bool HasPendingPlayerEvent() => SomethingAwaitsAPlayer();

    private bool SomethingAwaitsAPlayer()
    {
        if (HumanSeats.Count == 0) return false;
        foreach (var pending in PendingEvents)
            if (HumanSeats.Contains(pending.NationId)) return true;
        // A peace offer no longer stops the clock: it arrives as a notification the player
        // opens when they choose (PeaceOfferUI), so it waits for the frame like anything else.
        // Counting it here redrew the whole interface every step for the sixty days it stood.
        return false;
    }
}
