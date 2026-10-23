using System;
using Godot;
using LanguageKit.FSharp;

// Godot source generation stays in C#; F# is an ordinary managed library.
public partial class FSharpBridge : RefCounted
{
    private readonly Counter _counter = new();

    public long WeightedScore(long[] values, long[] weights) => CampaignMath.WeightedScore(values, weights);
    public long Increment() => _counter.Increment();

    public bool VerifyRejectedInput()
    {
        try { CampaignMath.WeightedScore(new long[] { 1 }, Array.Empty<long>()); }
        catch (ArgumentException) { return true; }
        return false;
    }
}
