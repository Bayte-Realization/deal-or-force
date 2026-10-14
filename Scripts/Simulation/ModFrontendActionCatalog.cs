using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DealOrForce.Modding;

public sealed class ModFrontendActionDeclaration
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
}

public sealed record ModFrontendActionDescriptor(string FullId, string ModId, string Id, string Caption);

/// <summary>Descriptor-only discovery and explicit, package-owned runtime dispatch.</summary>
public sealed class ModFrontendActionCatalog
{
    public const int ActionsPerPackage = 32;
    public const int TotalActions = 512;
    private readonly List<ModFrontendActionDescriptor> _actions = new();
    private readonly Dictionary<string, (Registration Owner, Func<string> Handler)> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private bool _running;
    public IReadOnlyList<ModFrontendActionDescriptor> Actions => _actions.AsReadOnly();
    public void ResetDeclarations(IEnumerable<(string ModId, IEnumerable<ModFrontendActionDeclaration> Actions)> packages,
        Action<string, string> error = null)
    {
        StopRuntime();
        _actions.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packages is null) return;
        foreach (var package in packages)
        {
            if (_actions.Count >= TotalActions) { error?.Invoke(package.ModId, "frontend_actions: catalogue action limit reached."); break; }
            if (!ValidId(package.ModId, 128)) { error?.Invoke(package.ModId, "frontend_actions: invalid package identifier."); continue; }
            if (package.Actions is null) continue;
            var inspected = 0;
            foreach (var action in package.Actions)
            {
                if (inspected++ >= ActionsPerPackage || _actions.Count >= TotalActions)
                { error?.Invoke(package.ModId, "frontend_actions: action limit reached."); break; }
                var caption = action?.Caption?.Trim();
                if (action is null || !ValidId(action.Id, 64) || string.IsNullOrEmpty(caption) || caption.Length > 160 ||
                    ContainsControl(caption))
                { error?.Invoke(package.ModId, "frontend_actions: invalid local identifier or caption."); continue; }
                var fullId = package.ModId + ":" + action.Id;
                if (!seen.Add(fullId)) { error?.Invoke(package.ModId, "frontend_actions: duplicate local identifier."); continue; }
                _actions.Add(new(fullId, package.ModId, action.Id, caption));
            }
        }
    }
    private static bool ValidId(string id, int maximum)
    {
        if (string.IsNullOrEmpty(id) || id.Length > maximum) return false;
        foreach (var c in id)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }
    private static bool ContainsControl(string caption)
    { foreach (var c in caption) if (char.IsControl(c)) return true; return false; }
    public void BeginRuntime() { StopRuntime(); _running = true; }
    public void StopRuntime() { _running = false; _generation++; _handlers.Clear(); }
    public Registration RegisterFor(string modId) => new(this, modId, _generation);
    public bool TryRun(string fullId, out string note)
    {
        if (!_running || string.IsNullOrEmpty(fullId) || !_handlers.TryGetValue(fullId, out var loaded))
        { note = "Mod action is unavailable."; return false; }
        try { note = loaded.Handler() ?? ""; return true; }
        catch (Exception) { note = "Mod action failed."; return false; }
    }
    public sealed class Registration : IDisposable
    {
        private readonly ModFrontendActionCatalog _catalog;
        private readonly string _modId;
        private readonly long _generation;
        private bool _disposed;
        internal Registration(ModFrontendActionCatalog catalog, string modId, long generation)
        { _catalog = catalog; _modId = modId; _generation = generation; }
        public bool Register(string localId, Func<string> handler)
        {
            if (_disposed || !_catalog._running || _generation != _catalog._generation || handler is null) return false;
            foreach (var descriptor in _catalog._actions)
                if (string.Equals(descriptor.ModId, _modId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(descriptor.Id, localId, StringComparison.OrdinalIgnoreCase))
                    return _catalog._handlers.TryAdd(descriptor.FullId, (this, handler));
            return false;
        }
        public void Dispose()
        {
            _disposed = true;
            var owned = new List<string>();
            foreach (var entry in _catalog._handlers)
                if (ReferenceEquals(entry.Value.Owner, this)) owned.Add(entry.Key);
            foreach (var id in owned) _catalog._handlers.Remove(id);
        }
    }
}
