param([Parameter(Mandatory)][int]$EditorProcessId, [int]$ThreadId = 0)
$ErrorActionPreference = 'Stop'
$target = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $EditorProcessId)
if (-not $target -or $target.Name -notmatch '^Godot.*\.exe$' -or $target.CommandLine -notmatch '--editor') {
    throw 'The requested process is not a running Godot editor.'
}
if ([IntPtr]::Size -ne 8) { throw 'This local diagnostic supports Windows x64 only.' }
# Samples one verified editor thread (the main thread by default), just for GetThreadContext
# and pinning a matching directory handle, then resumed in finally BEFORE symbol lookup.
# No debugger attach,
# injection, process termination, dump of private memory, or symbol-server download.
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class DofEditorNativeStack {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenThread(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetThreadContext(IntPtr thread,IntPtr context);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(IntPtr source,IntPtr handle,IntPtr target,out IntPtr duplicate,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(IntPtr handle,StringBuilder path,uint size,uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] bytes,UIntPtr length,out UIntPtr read);
    [DllImport("dbghelp.dll", CharSet=CharSet.Ansi, SetLastError=true)] static extern bool SymInitialize(IntPtr process,string path,bool invade);
    [DllImport("dbghelp.dll")] static extern bool SymCleanup(IntPtr process);
    [DllImport("dbghelp.dll")] static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll")] static extern bool SymFromAddr(IntPtr process,ulong address,out ulong displacement,IntPtr symbol);
    [DllImport("dbghelp.dll")] static extern IntPtr SymFunctionTableAccess64(IntPtr process,ulong address);
    [DllImport("dbghelp.dll")] static extern ulong SymGetModuleBase64(IntPtr process,ulong address);
    delegate IntPtr FunctionTable(IntPtr process,ulong address);
    delegate ulong ModuleBase(IntPtr process,ulong address);
    [DllImport("dbghelp.dll")] static extern bool StackWalk64(uint machine,IntPtr process,IntPtr thread,IntPtr frame,IntPtr context,
        IntPtr read,FunctionTable functionTable,ModuleBase moduleBase,IntPtr translate);
    static ulong U64(IntPtr address,int offset) { return unchecked((ulong)Marshal.ReadInt64(address,offset)); }
    static byte[] Read(IntPtr process,ulong address,int length) {
        var bytes=new byte[length]; UIntPtr read;
        return ReadProcessMemory(process,new IntPtr(unchecked((long)address)),bytes,(UIntPtr)length,out read) && read.ToUInt64()==(ulong)length ? bytes : null;
    }
    static string OpenFileName(IntPtr process,ulong attributes) {
        var data=Read(process,attributes,48);
        if(data==null || BitConverter.ToInt32(data,0)!=48) return "unavailable";
        var name=Read(process,BitConverter.ToUInt64(data,16),16);
        if(name==null) return "unavailable";
        int length=BitConverter.ToUInt16(name,0);
        if(length<2 || length>4096 || (length&1)!=0) return "unavailable";
        var text=Read(process,BitConverter.ToUInt64(name,8),length);
        return text==null ? "unavailable" : Encoding.Unicode.GetString(text);
    }
    static string DirectoryName(IntPtr copy) {
        if(copy==IntPtr.Zero) return "unavailable";
        var path=new StringBuilder(4096);
        uint length=GetFinalPathNameByHandle(copy,path,4096,8);
        return length>0 && length<4096 ? path.ToString() : "unavailable";
    }
    public static string Sample(uint processId,uint threadId) {
        var process=OpenProcess(0x450,false,processId); var thread=OpenThread(0x4a,false,threadId);
        if (process==IntPtr.Zero || thread==IntPtr.Zero) {
            if(process!=IntPtr.Zero) CloseHandle(process); if(thread!=IntPtr.Zero) CloseHandle(thread);
            throw new InvalidOperationException("Could not open the editor for a read-only stack sample.");
        }
        var raw=Marshal.AllocHGlobal(1248); var context=new IntPtr((raw.ToInt64()+15)&~15L);
        var frame=Marshal.AllocHGlobal(1024); var symbol=Marshal.AllocHGlobal(1120); bool initialized=false;
        IntPtr directoryCopy=IntPtr.Zero;
        try {
            // Resolve local syscall RVAs before suspending; no path/symbol I/O
            // while the target thread is paused. DuplicateHandle pins only our
            // diagnostic copy against the target closing its original on resume.
            var localNtdll=GetModuleHandle("ntdll.dll");
            ulong targetNtdll=0;
            foreach(ProcessModule module in Process.GetProcessById((int)processId).Modules)
                if(module.ModuleName.Equals("ntdll.dll",StringComparison.OrdinalIgnoreCase))
                    targetNtdll=unchecked((ulong)module.BaseAddress.ToInt64());
            var queryEntries=new ulong[2]; int queryIndex=0;
            foreach(var name in new[]{"NtQueryDirectoryFile","NtQueryDirectoryFileEx"}) {
                var address=GetProcAddress(localNtdll,name);
                queryEntries[queryIndex++]=address==IntPtr.Zero || targetNtdll==0 ? 0 :
                    targetNtdll+unchecked((ulong)(address.ToInt64()-localNtdll.ToInt64()));
            }
            Marshal.Copy(new byte[1232],0,context,1232); Marshal.WriteInt32(context,48,0x100003);
            if(SuspendThread(thread)==uint.MaxValue) throw new InvalidOperationException("Could not sample editor thread.");
            try {
                if(!GetThreadContext(thread,context)) throw new InvalidOperationException("GetThreadContext failed.");
                ulong pc=U64(context,248);
                foreach(ulong entry in queryEntries) if(entry!=0 && pc>=entry && pc<entry+32) {
                    DuplicateHandle(process,new IntPtr(unchecked((long)U64(context,200))),new IntPtr(-1),out directoryCopy,0,false,2);
                    break;
                }
            }
            finally { if(ResumeThread(thread)==uint.MaxValue) throw new InvalidOperationException("ResumeThread failed."); }
            // Local module exports only; do not follow embedded PDB/server paths.
            SymSetOptions(0x4|0x2|0x200|0x80|0x80000);
            initialized=SymInitialize(process,"",true);
            if(!initialized) throw new InvalidOperationException("Local symbol initialization failed.");
            Marshal.Copy(new byte[1024],0,frame,1024);
            Marshal.WriteInt64(frame,0,unchecked((long)U64(context,248))); Marshal.WriteInt32(frame,12,3);
            Marshal.WriteInt64(frame,32,unchecked((long)U64(context,160))); Marshal.WriteInt32(frame,44,3);
            Marshal.WriteInt64(frame,48,unchecked((long)U64(context,152))); Marshal.WriteInt32(frame,60,3);
            var modules=Process.GetProcessById((int)processId).Modules;
            var result=new StringBuilder("process="+processId+" thread="+threadId+"\n"); ulong previous=0;
            var ntdll=GetModuleHandle("ntdll.dll");
            foreach(var name in new[]{"NtOpenFile","NtWaitForSingleObject","NtWaitForMultipleObjects","NtWaitForAlertByThreadId","NtAlertThreadByThreadId"}) {
                var address=GetProcAddress(ntdll,name);
                if(address!=IntPtr.Zero) result.AppendLine("local_export="+name+" rva=0x"+(address.ToInt64()-ntdll.ToInt64()).ToString("x"));
            }
            // Registers only have these meanings at the matching syscall.
            // A write/import wait must not be mislabeled as a directory scan.
            Marshal.WriteInt32(symbol,0,88); Marshal.WriteInt32(symbol,80,1024); ulong leafDisplacement;
            string nativeLeaf="";
            if(SymFromAddr(process,U64(context,248),out leafDisplacement,symbol)) {
                int length=Math.Min(1024,Math.Max(0,Marshal.ReadInt32(symbol,76)));
                nativeLeaf=Marshal.PtrToStringAnsi(IntPtr.Add(symbol,84),length) ?? "";
            }
            if(nativeLeaf=="NtOpenFile" || nativeLeaf=="NtCreateFile")
                result.AppendLine("open_file_argument="+OpenFileName(process,U64(context,184)));
            if(nativeLeaf=="NtQueryDirectoryFile" || nativeLeaf=="NtQueryDirectoryFileEx")
                result.AppendLine("directory_argument="+DirectoryName(directoryCopy));
            // Not a backtrace: bounded return-address candidates help locate a
            // native extension when stripped executable unwind data is unavailable.
            var candidates=Read(process,U64(context,152),8192); int emitted=0;
            if(candidates!=null) for(int offset=0;offset<candidates.Length && emitted<24;offset+=8) {
                ulong candidate=BitConverter.ToUInt64(candidates,offset);
                foreach(ProcessModule module in modules) {
                    if(!module.ModuleName.StartsWith("~lib",StringComparison.OrdinalIgnoreCase)) continue;
                    ulong start=unchecked((ulong)module.BaseAddress.ToInt64());
                    if(candidate>=start && candidate<start+(ulong)module.ModuleMemorySize) {
                        result.AppendLine("stack_extension_candidate="+module.ModuleName+"+0x"+(candidate-start).ToString("x")); emitted++; break;
                    }
                }
            }
            for(int i=0;i<32;i++) {
                ulong pc=U64(frame,0); if(pc==0 || pc==previous) break; previous=pc;
                string location="0x"+pc.ToString("x");
                foreach(ProcessModule module in modules) {
                    ulong start=unchecked((ulong)module.BaseAddress.ToInt64());
                    if(pc>=start && pc<start+(ulong)module.ModuleMemorySize) { location=module.ModuleName+"+0x"+(pc-start).ToString("x"); break; }
                }
                Marshal.WriteInt32(symbol,0,88); Marshal.WriteInt32(symbol,80,1024); ulong displacement;
                if(SymFromAddr(process,pc,out displacement,symbol)) {
                    int length=Math.Min(1024,Math.Max(0,Marshal.ReadInt32(symbol,76)));
                    location+="!"+Marshal.PtrToStringAnsi(IntPtr.Add(symbol,84),length)+"+0x"+displacement.ToString("x");
                }
                result.AppendLine(location);
                if(i==0) {
                    var stack=Read(process,U64(context,152),8);
                    if(stack!=null) {
                        ulong caller=BitConverter.ToUInt64(stack,0);
                        foreach(ProcessModule module in modules) {
                            ulong start=unchecked((ulong)module.BaseAddress.ToInt64());
                            if(caller>=start && caller<start+(ulong)module.ModuleMemorySize) result.AppendLine("leaf_return="+module.ModuleName+"+0x"+(caller-start).ToString("x"));
                        }
                    }
                }
                if(!StackWalk64(0x8664,process,thread,frame,context,IntPtr.Zero,SymFunctionTableAccess64,SymGetModuleBase64,IntPtr.Zero)) break;
            }
            return result.ToString();
        } finally {
            if(directoryCopy!=IntPtr.Zero) CloseHandle(directoryCopy);
            if(initialized) SymCleanup(process); Marshal.FreeHGlobal(symbol); Marshal.FreeHGlobal(frame); Marshal.FreeHGlobal(raw);
            CloseHandle(thread); CloseHandle(process);
        }
    }
}
'@
$editor = Get-Process -Id $EditorProcessId
$sampleThread = if ($ThreadId -eq 0) {
    $editor.Threads | Sort-Object StartTime | Select-Object -First 1
} else {
    $editor.Threads | Where-Object Id -EQ $ThreadId | Select-Object -First 1
}
if (-not $sampleThread) { throw 'Requested thread does not belong to this editor.' }
[DofEditorNativeStack]::Sample([uint32]$EditorProcessId,[uint32]$sampleThread.Id)
