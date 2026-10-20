using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

/// <summary>
/// Tools-only experimental host, independent of Godot and of mod workers.
/// The primary library has an explicit absolute path; OS dependency resolution still applies.
/// No pointers retained by the kernel; no ownership of world state.
/// Managed exceptions cannot contain a native access violation, abort or infinite loop.
/// </summary>
internal sealed class NativeOwnershipKernel : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Components(
        IntPtr offsets, uint offsetsCount, IntPtr neighbors, uint neighborCount,
        IntPtr owners, uint vertices, IntPtr water, uint waterCount,
        IntPtr labels, uint labelsCapacity, IntPtr members, uint membersCapacity,
        IntPtr componentOffsets, uint componentOffsetsCapacity,
        IntPtr componentCount, IntPtr memberCount);

    private readonly object _gate = new();
    private IntPtr _library;
    private readonly Components _compute;
    internal bool IsEnabled { get; private set; } = true;
    internal string Diagnostic { get; private set; } = "native ownership ABI v1";

    private NativeOwnershipKernel(IntPtr library, Components compute)
    { _library = library; _compute = compute; }

    internal static bool TryOpen(string absoluteDirectory, out NativeOwnershipKernel kernel, out string reason)
    {
        kernel = null;
        reason = "an explicit absolute kernel directory is required";
        if (string.IsNullOrWhiteSpace(absoluteDirectory) || !Path.IsPathFullyQualified(absoluteDirectory)) return false;
        var fileName = OperatingSystem.IsWindows() ? "dof_graph_kernel.dll" :
            OperatingSystem.IsLinux() ? "libdof_graph_kernel.so" :
            OperatingSystem.IsMacOS() ? "libdof_graph_kernel.dylib" : null;
        if (fileName is null) { reason = "unsupported native platform"; return false; }
        var handle = IntPtr.Zero;
        try
        {
            var path = Path.GetFullPath(Path.Combine(absoluteDirectory, fileName));
            if (!File.Exists(path)) { reason = "kernel file not found: " + path; return false; }
            handle = NativeLibrary.Load(path);
            var abi = Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(handle, "dof_graph_abi_version"));
            if (abi() != 1) { reason = "ownership kernel ABI mismatch"; return false; }
            var compute = Marshal.GetDelegateForFunctionPointer<Components>(NativeLibrary.GetExport(handle, "dof_owner_components_v1"));
            kernel = new NativeOwnershipKernel(handle, compute);
            handle = IntPtr.Zero; // The instance keeps the handle until every call has finished.
            reason = "";
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or
            ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reason = error.GetType().Name + ": " + error.Message;
            return false;
        }
        finally { if (handle != IntPtr.Zero) NativeLibrary.Free(handle); }
    }

    internal bool TryCompute(OwnershipComponentGraph graph, ushort[] owners, byte[] water, OwnershipComponentBatch result)
    {
        graph.ValidateInputs(owners, water);
        lock (_gate)
        {
            if (!IsEnabled || _library == IntPtr.Zero) return false;
            var pins = new GCHandle[8];
            try
            {
                var counts = new int[2];
                Array[] arrays = { graph.Offsets, graph.Neighbors, owners, water, result.Labels, result.MemberOrder, result.ComponentOffsets, counts };
                for (var index = 0; index < pins.Length; index++) pins[index] = GCHandle.Alloc(arrays[index], GCHandleType.Pinned);
                var countPointer = pins[7].AddrOfPinnedObject();
                var status = _compute(pins[0].AddrOfPinnedObject(), (uint)graph.Offsets.Length,
                    pins[1].AddrOfPinnedObject(), (uint)graph.Neighbors.Length,
                    pins[2].AddrOfPinnedObject(), (uint)owners.Length, pins[3].AddrOfPinnedObject(), (uint)water.Length,
                    pins[4].AddrOfPinnedObject(), (uint)result.Labels.Length,
                    pins[5].AddrOfPinnedObject(), (uint)result.MemberOrder.Length,
                    pins[6].AddrOfPinnedObject(), (uint)result.ComponentOffsets.Length,
                    countPointer, IntPtr.Add(countPointer, sizeof(int)));
                if (status != 0) return Disable("native status " + status);
                result.ComponentCount = counts[0];
                result.MemberCount = counts[1];
                if (!result.IsStructurallyValid(owners, water)) return Disable("invalid complete native batch");
                return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or MarshalDirectiveException or OverflowException)
            { return Disable(error.GetType().Name); }
            finally
            {
                foreach (var pin in pins) if (pin.IsAllocated) pin.Free();
            }
        }
    }

    private bool Disable(string reason)
    {
        IsEnabled = false;
        Diagnostic = "managed fallback (native disabled: " + reason + ")";
        return false;
    }

    internal void RejectHostInput(string reason)
    {
        lock (_gate) Disable(reason);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            IsEnabled = false;
            if (_library == IntPtr.Zero) return;
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
            Diagnostic = "managed fallback (native disposed)";
        }
    }
}
