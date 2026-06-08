using System.Reflection;
using System.Runtime.InteropServices;

namespace CanMessager.Hal.Vector;

/// <summary>
/// vxlapi64.dll 동적 로더. 표준 경로에 없으면 Vector 설치 폴더에서 찾아 로드한다.
/// 어셈블리 전역 DllImport 리졸버를 (최초 1회) 등록한다.
/// </summary>
internal static class VectorNative
{
    private static int _registered;

    /// <summary>vxlapi P/Invoke 전에 호출 — 리졸버를 1회만 등록한다.</summary>
    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        try { NativeLibrary.SetDllImportResolver(typeof(VectorNative).Assembly, Resolve); }
        catch { /* 이미 등록됨 등 무시 */ }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.StartsWith("vxlapi", StringComparison.OrdinalIgnoreCase))
            return nint.Zero;

        if (NativeLibrary.TryLoad(libraryName, out var h)) return h;
        foreach (var candidate in CandidatePaths())
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out h))
                return h;
        return nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        var subs = new[]
        {
            @"Vector Platform Manager\VtpDrivers\Common\vxlapi64.dll",
            @"Vector vFlash 5\Bin\vxlapi64.dll",
            @"Vector vFlash 4\Bin\vxlapi64.dll",
            @"Vector CANape 16\Exec\Drivers\Common\vxlapi64.dll"
        };
        foreach (var r in roots)
            foreach (var s in subs)
                yield return Path.Combine(r, s);
    }
}
