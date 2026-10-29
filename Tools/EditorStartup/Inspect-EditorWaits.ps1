param([Parameter(Mandatory)][int]$EditorProcessId)
$ErrorActionPreference = 'Stop'
$target = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $EditorProcessId)
if (-not $target -or $target.Name -notmatch '^Godot.*\.exe$' -or $target.CommandLine -notmatch '--editor') {
    throw 'The requested process is not a running Godot editor.'
}
# Read-only OS wait-chain inspection. Does not attach a debugger, suspend threads,
# alter privileges, write process memory, or close any application.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DofEditorWaits {
    [DllImport("advapi32.dll", SetLastError=true)] static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
    [DllImport("advapi32.dll")] static extern void CloseThreadWaitChainSession(IntPtr session);
    [DllImport("advapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetThreadWaitChain(IntPtr session, UIntPtr context, uint flags, uint thread, ref uint count, IntPtr nodes, [MarshalAs(UnmanagedType.Bool)] out bool cycle);
    public static string Read(uint thread) {
        var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero) return "thread=" + thread + " session_error=" + Marshal.GetLastWin32Error();
        var buffer = Marshal.AllocHGlobal(280 * 16);
        try {
            uint count=16; bool cycle;
            if (!GetThreadWaitChain(session, UIntPtr.Zero, 5, thread, ref count, buffer, out cycle))
                return "thread=" + thread + " query_error=" + Marshal.GetLastWin32Error();
            var result="thread=" + thread + " cycle=" + cycle + " nodes=" + count;
            for (int i=0;i<count && i<16;i++) {
                var node=IntPtr.Add(buffer,280*i);
                int kind=Marshal.ReadInt32(node), state=Marshal.ReadInt32(node,4);
                result += " | kind=" + kind + " state=" + state;
                if (kind==8) result += " process=" + Marshal.ReadInt32(node,8) + " thread=" + Marshal.ReadInt32(node,12);
                else result += " name=" + Marshal.PtrToStringUni(IntPtr.Add(node,8),128).TrimEnd('\0');
            }
            return result;
        } finally { Marshal.FreeHGlobal(buffer); CloseThreadWaitChainSession(session); }
    }
}
'@
$process = Get-Process -Id $EditorProcessId
$process.Threads | Sort-Object TotalProcessorTime -Descending | Select-Object -First 8 | ForEach-Object { [DofEditorWaits]::Read([uint32]$_.Id) }
