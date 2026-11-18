using System;

namespace DealOrForce.Simulation;

/// <summary>
/// Marks a method, property, field, or type invoked out of band by external tooling
/// (for example Godot MCP runtime probes) rather than compiled C# call sites, so static
/// dead-code audits keep it and callers do not break when files are refactored.
/// </summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field |
    AttributeTargets.Class | AttributeTargets.Struct,
    Inherited = false)]
public sealed class ToolHookAttribute : Attribute
{
}
