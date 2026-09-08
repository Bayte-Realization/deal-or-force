using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>No Godot print/error calls here: that would recursively enter this logger.</summary>
public partial class GameDiagnosticLogger : Logger
{
    public override void _LogMessage(string message, bool error) => DiagnosticLogs.Write(error ? "errors" : "system", message);
    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        var text = $"ENGINE_{(ErrorType)errorType} file={file}:{line} function={function} code={code} reason={rationale}";
        foreach (var trace in scriptBacktraces) text += "\n" + trace.Format();
        DiagnosticLogs.Write("errors", text);
    }
}
