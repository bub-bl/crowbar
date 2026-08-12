using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// The IL fast path (the analog of s&amp;box's IL hotload): when only method
/// bodies changed, patches the live assembly's method entry to jump to the
/// reloaded assembly's method, so existing instances keep their identity AND
/// run the new code — no assembly swap, no heap walk, no instance upgrade.
///
/// The patch writes a 12-byte x64 detour (<c>mov rax, imm64; jmp rax</c>) at
/// the start of the JIT-compiled method, which requires the method's native
/// body to be at least 12 bytes (checked via the JIT header word preceding
/// the code). Any failure — non-x64, tiered compilation enabled, method too
/// small, abstract/generic — returns false and the caller falls back to a
/// full reload.
/// </summary>
internal static class MethodBodyPatcher
{
    private const int DetourLength = 12;
    private const uint PageExecuteReadWrite = 0x40;

    public static bool TryPatch(MethodBase oldMethod, MethodBase newMethod, out string? error)
    {
        error = null;
        if (IntPtr.Size != 8 || !OperatingSystem.IsWindows())
        {
            error = "the IL fast path requires x64 Windows";
            return false;
        }

        // Tiered compilation replaces Tier-0 code with a promoted Tier-1 body,
        // which would silently discard the detour. Crowbar disables tiering
        // (runtimeconfig TieredCompilation=false); refuse otherwise. The switch
        // value is the "enabled" flag (absent → enabled by default).
        var tieringEnabled = !AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out var enabled) || enabled;
        if (tieringEnabled)
        {
            error = "tiered compilation must be disabled for IL patching";
            return false;
        }

        if (oldMethod.IsAbstract || oldMethod.IsGenericMethodDefinition || oldMethod.ContainsGenericParameters)
        {
            error = $"'{oldMethod.Name}' is abstract or generic";
            return false;
        }

        try
        {
            RuntimeHelpers.PrepareMethod(oldMethod.MethodHandle);
            RuntimeHelpers.PrepareMethod(newMethod.MethodHandle);

            var codeStart = oldMethod.MethodHandle.GetFunctionPointer();
            // The JIT writes the native code size in the low 24 bits of the
            // header word that precedes the code.
            var header = Marshal.ReadInt32(codeStart - 4);
            var codeSize = header & 0xFFFFFF;
            if (codeSize < DetourLength)
            {
                error = $"'{oldMethod.Name}' native body too small ({codeSize} bytes)";
                return false;
            }

            var target = newMethod.MethodHandle.GetFunctionPointer();

            // Ensure the page is writable before writing the detour, then
            // restore the original protection and flush the instruction cache.
            if (!VirtualProtect(codeStart, (nuint)DetourLength, PageExecuteReadWrite, out var oldProtection))
            {
                error = "code page is not writable";
                return false;
            }

            try
            {
                unsafe
                {
                    var p = (byte*)codeStart.ToPointer();
                    p[0] = 0x48;               // REX.W
                    p[1] = 0xB8;               // mov rax, imm64
                    *(long*)(p + 2) = target.ToInt64();
                    p[10] = 0xFF;              // jmp rax
                    p[11] = 0xE0;
                }
            }
            finally
            {
                VirtualProtect(codeStart, (nuint)DetourLength, oldProtection, out _);
            }

            FlushInstructionCache((nint)(-1), codeStart, (nuint)DetourLength);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern void FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);
}
