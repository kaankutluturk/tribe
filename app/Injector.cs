using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tribe.App
{
    // Loads the core into Green Hell's Mono runtime (mono-2.0-bdwgc.dll) using Mono's own embedding API, the way
    // SharpMonoInjector does: each API call runs in a short remote thread whose x64 stub first attaches the thread to
    // the root domain, then calls the export and stores RAX where we can read it back.
    //
    //   image    = mono_image_open_from_data(bytes, len, need_copy 1, &status)
    //   assembly = mono_assembly_load_from_full(image, name, &status, 0)
    //   class    = mono_class_from_name(mono_assembly_get_image(assembly), "Tribe", "Entry")
    //   method   = mono_class_get_method_from_name(class, "Inject", 1)
    //   mono_runtime_invoke(method, null, [mono_string_new(domain, port)], &exception)
    //
    // Entry.Inject runs on that remote thread, so it touches no Unity API; it hooks the next rendered frame instead.
    // Nothing is patched: the only footprint is the loaded assembly and the transient stubs, which are freed.
    sealed class Injector : IDisposable
    {
        const uint Access = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;   // create thread, vm operation/read/write, query information
        const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04, PAGE_EXECUTE_READWRITE = 0x40;
        public const string MonoModule = "mono-2.0-bdwgc.dll";

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr at, IntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr at, IntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr at, byte[] data, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr at, byte[] data, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attrs, IntPtr stack, IntPtr start, IntPtr param, uint flags, out int id);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

        readonly IntPtr process;
        readonly Dictionary<string, IntPtr> exports = new Dictionary<string, IntPtr>();
        readonly List<IntPtr> allocations = new List<IntPtr>();
        IntPtr domain;

        public Injector(Process game)
        {
            IntPtr mono = FindModule(game, MonoModule);
            if (mono == IntPtr.Zero) throw new InvalidOperationException("mono runtime not loaded yet");
            process = OpenProcess(Access, false, game.Id);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot open the game process");
            ReadExports(mono);
        }

        public static IntPtr FindModule(Process p, string name)
        {
            try
            {
                p.Refresh();
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase)) return m.BaseAddress;
            }
            catch (Win32Exception) { }   // module list mid-change while the game starts; try again later
            return IntPtr.Zero;
        }

        /// <summary>Loads the core and calls Tribe.Entry.Inject(arg). Null on success, otherwise what went wrong.</summary>
        public string Inject(byte[] assembly, string name, string arg)
        {
            domain = Call("mono_get_root_domain", false);
            if (domain == IntPtr.Zero) return "mono has no root domain yet";

            IntPtr status = Alloc(8, false);
            IntPtr image = Call("mono_image_open_from_data", true, Write(assembly), (IntPtr)assembly.Length, (IntPtr)1, status);
            if (image == IntPtr.Zero) return "mono could not open the core image (status " + ReadInt(status) + ")";
            IntPtr loaded = Call("mono_assembly_load_from_full", true, image, WriteString(name), status, IntPtr.Zero);
            if (loaded == IntPtr.Zero) return "mono could not load the core assembly (status " + ReadInt(status) + ")";
            IntPtr loadedImage = Call("mono_assembly_get_image", true, loaded);
            IntPtr cls = Call("mono_class_from_name", true, loadedImage, WriteString("Tribe"), WriteString("Entry"));
            if (cls == IntPtr.Zero) return "Tribe.Entry not found in the core";
            IntPtr method = Call("mono_class_get_method_from_name", true, cls, WriteString("Inject"), (IntPtr)1);
            if (method == IntPtr.Zero) return "Tribe.Entry.Inject(string) not found in the core";

            IntPtr text = Call("mono_string_new", true, domain, WriteString(arg));
            IntPtr args = Write(BitConverter.GetBytes(text.ToInt64()));
            IntPtr exception = Alloc(8, false);
            Call("mono_runtime_invoke", true, method, IntPtr.Zero, args, exception);
            IntPtr thrown = ReadPointer(exception);
            if (thrown != IntPtr.Zero) return "Entry.Inject threw: " + Describe(thrown);
            return null;
        }

        string Describe(IntPtr exception)
        {
            try
            {
                IntPtr str = Call("mono_object_to_string", true, exception, IntPtr.Zero);
                IntPtr utf8 = str == IntPtr.Zero ? IntPtr.Zero : Call("mono_string_to_utf8", true, str);
                return utf8 == IntPtr.Zero ? "(no message)" : ReadUtf8(utf8, 2048);
            }
            catch (Exception e) { return "(" + e.Message + ")"; }
        }

        // Runs one export in a remote thread and returns RAX. The stub:
        //   sub rsp,28h; [mov rcx,domain; mov rax,mono_thread_attach; call rax;] mov rcx,a1; mov rdx,a2; mov r8,a3;
        //   mov r9,a4; mov rax,fn; call rax; mov [result],rax; add rsp,28h; ret
        IntPtr Call(string export, bool attach, params IntPtr[] args)
        {
            IntPtr fn;
            if (!exports.TryGetValue(export, out fn)) throw new InvalidOperationException(export + " is not exported by " + MonoModule);
            IntPtr result = Alloc(8, false);
            var code = new List<byte>();
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });
            if (attach)
            {
                Mov(code, 0x48, 0xB9, domain);                            // mov rcx, domain
                Mov(code, 0x48, 0xB8, exports["mono_thread_attach"]);     // mov rax, mono_thread_attach
                code.AddRange(new byte[] { 0xFF, 0xD0 });                 // call rax
            }
            byte[][] regs = { new byte[] { 0x48, 0xB9 }, new byte[] { 0x48, 0xBA }, new byte[] { 0x49, 0xB8 }, new byte[] { 0x49, 0xB9 } };   // rcx rdx r8 r9
            if (args.Length > 4) throw new ArgumentException("at most four arguments");
            for (int i = 0; i < args.Length; i++) Mov(code, regs[i][0], regs[i][1], args[i]);
            Mov(code, 0x48, 0xB8, fn);                                    // mov rax, fn
            code.AddRange(new byte[] { 0xFF, 0xD0 });                     // call rax
            code.AddRange(new byte[] { 0x48, 0xA3 });                     // mov [result], rax
            code.AddRange(BitConverter.GetBytes(result.ToInt64()));
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28, 0xC3 });   // add rsp,28h; ret

            IntPtr stub = Alloc(code.Count, true);
            WriteAt(stub, code.ToArray());
            int id;
            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, IntPtr.Zero, stub, IntPtr.Zero, 0, out id);
            if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread failed");
            try
            {
                // A stub still running after this must not be freed; leaking it beats crashing the game.
                if (WaitForSingleObject(thread, 15000) != 0) { allocations.Remove(stub); throw new TimeoutException(export + " did not return"); }
            }
            finally { CloseHandle(thread); }
            return ReadPointer(result);
        }

        static void Mov(List<byte> code, byte rex, byte op, IntPtr value)
        {
            code.Add(rex); code.Add(op);
            code.AddRange(BitConverter.GetBytes(value.ToInt64()));
        }

        // Export table of the loaded module, read from the game's memory (PE32+: data directory 0 at NT headers + 0x88).
        void ReadExports(IntPtr module)
        {
            long b = module.ToInt64();
            int nt = BitConverter.ToInt32(Read(module, 0x40), 0x3C);
            int dir = BitConverter.ToInt32(Read((IntPtr)(b + nt + 0x88), 4), 0);
            if (dir == 0) throw new InvalidOperationException(MonoModule + " has no export table");
            var ed = Read((IntPtr)(b + dir), 40);
            int count = BitConverter.ToInt32(ed, 0x18), functions = BitConverter.ToInt32(ed, 0x1C), names = BitConverter.ToInt32(ed, 0x20), ordinals = BitConverter.ToInt32(ed, 0x24);
            var nameRvas = Read((IntPtr)(b + names), count * 4);
            var ords = Read((IntPtr)(b + ordinals), count * 2);
            for (int i = 0; i < count; i++)
            {
                string name = ReadAscii((IntPtr)(b + BitConverter.ToInt32(nameRvas, i * 4)), 96);
                if (!name.StartsWith("mono_")) continue;
                int ordinal = BitConverter.ToUInt16(ords, i * 2);
                int rva = BitConverter.ToInt32(Read((IntPtr)(b + functions + ordinal * 4), 4), 0);
                exports[name] = (IntPtr)(b + rva);
            }
            foreach (var needed in new[] { "mono_get_root_domain", "mono_thread_attach", "mono_image_open_from_data", "mono_assembly_load_from_full", "mono_assembly_get_image", "mono_class_from_name", "mono_class_get_method_from_name", "mono_string_new", "mono_runtime_invoke" })
                if (!exports.ContainsKey(needed)) throw new InvalidOperationException(MonoModule + " doesn't export " + needed);
        }

        IntPtr Alloc(int size, bool executable)
        {
            IntPtr p = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)Math.Max(size, 16), MEM_COMMIT | MEM_RESERVE, executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE);
            if (p == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed");
            allocations.Add(p);
            return p;
        }

        IntPtr Write(byte[] data) { IntPtr p = Alloc(data.Length, false); WriteAt(p, data); return p; }
        IntPtr WriteString(string s) { return Write(Encoding.UTF8.GetBytes(s + "\0")); }

        void WriteAt(IntPtr at, byte[] data)
        {
            IntPtr written;
            if (!WriteProcessMemory(process, at, data, (IntPtr)data.Length, out written)) throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory failed");
        }

        byte[] Read(IntPtr at, int size)
        {
            var buf = new byte[size]; IntPtr read;
            if (!ReadProcessMemory(process, at, buf, (IntPtr)size, out read)) throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory failed at 0x" + at.ToInt64().ToString("X"));
            return buf;
        }

        IntPtr ReadPointer(IntPtr at) { return (IntPtr)BitConverter.ToInt64(Read(at, 8), 0); }
        int ReadInt(IntPtr at) { return BitConverter.ToInt32(Read(at, 4), 0); }

        string ReadAscii(IntPtr at, int max)
        {
            var buf = Read(at, max);
            int n = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, n < 0 ? max : n);
        }

        // Reads up to the terminator a page at a time (a long read can run off the end of the allocation).
        string ReadUtf8(IntPtr at, int max)
        {
            var bytes = new List<byte>();
            for (long p = at.ToInt64(); bytes.Count < max; p++)
            {
                byte c = Read((IntPtr)p, 1)[0];
                if (c == 0) break;
                bytes.Add(c);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Dispose()
        {
            foreach (var p in allocations) VirtualFreeEx(process, p, IntPtr.Zero, MEM_RELEASE);
            allocations.Clear();
            if (process != IntPtr.Zero) CloseHandle(process);
        }

        /// <summary>Reads the assembly name directly from an external development core, without copying it.</summary>
        public static string AssemblyNameFromFile(string path)
        {
            try { return System.Reflection.AssemblyName.GetAssemblyName(path).Name; }
            catch { return "Tribe.Core"; }
        }

        /// <summary>Gets the build-generated core assembly name embedded in the single-file release.</summary>
        public static string EmbeddedAssemblyName()
        {
            try
            {
                using (var stream = typeof(Injector).Assembly.GetManifestResourceStream("tribe.core.name"))
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                    return reader.ReadToEnd().Trim();
            }
            catch { return "Tribe.Core"; }
        }
    }
}
