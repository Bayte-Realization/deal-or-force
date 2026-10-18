using System;
using DealOrForce.Tools;

static class ArmyOrderObservationTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }

    public static void Run()
    {
        var marching = new ArmyOrderSample(1, 2, 10, .5f);
        Check(ArmyOrderSample.Compare(marching, marching) == ArmyOrderChange.None, "unchanged march is neither arrival nor retarget");
        Check(ArmyOrderSample.Compare(marching, new(2, 2, 10, 0)) == ArmyOrderChange.Arrived, "arrival counts even when the order retains its goal");
        Check(ArmyOrderSample.Compare(marching, new(2, 0, 11, 0)) == ArmyOrderChange.Arrived, "cleared order still counts a real arrival");
        Check(ArmyOrderSample.Compare(marching, new(2, 3, 11, 0)) == ArmyOrderChange.Arrived, "new task issued on arrival is not a retarget of an unfinished march");
        Check(ArmyOrderSample.Compare(new(2, 2, 10, 0), new(3, 4, 11, 0)) == ArmyOrderChange.None, "departure after an already completed task is not a retarget");
        Check(ArmyOrderSample.Compare(marching, new(1, 3, 11, 0)) == ArmyOrderChange.Retargeted, "unfinished march redirected to another goal counts a retarget");
        Check(ArmyOrderSample.Compare(marching, new(3, 4, 11, 0)) == ArmyOrderChange.Retargeted, "moving without reaching the old goal does not conceal a retarget");
        Check(ArmyOrderSample.Compare(new(1, 0, 10, 0), new(1, 3, 11, 0)) == ArmyOrderChange.None, "a first task is not a fictional arrival");
        Check(ArmyOrderSample.Compare(marching, new(1, 0, 11, 0)) == ArmyOrderChange.None, "cancellation is not a destination arrival");
        Check(ArmyOrderSample.Compare(marching, new(1, 2, 11, .1f)) == ArmyOrderChange.ProgressReset, "same-goal lost progress is reported independently");
        Check(ArmyOrderSample.Compare(marching, new(1, 2, 11, .6f)) == ArmyOrderChange.None, "refreshed same-goal order preserving progress is not a reset");
        Check(ArmyOrderSample.Compare(new(0, 0, 0, 0), new(0, 0, 1, 0)) == ArmyOrderChange.None, "unset positions and goals cannot create activity");
    }
}
