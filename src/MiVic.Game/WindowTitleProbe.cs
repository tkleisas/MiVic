using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace MiVic.Game;

/// <summary>
/// Reads the window title back out of SDL.
/// <para>
/// The OS title bar is drawn by Windows, not by the game, so a wrong title can
/// come from the managed string, from MonoGame's UTF-8 marshalling, from SDL's
/// UTF-8 to UTF-16 conversion, or from the system font. Reading the string SDL
/// stored splits the chain in half: if SDL holds correct Greek, the game's side
/// is fine and the problem is downstream.
/// </para>
/// </summary>
public static class WindowTitleProbe
{
    /// <summary>Returns the title SDL holds for the window, or null if it cannot be read.</summary>
    public static string? ReadFromSdl(GameWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        nint handle = GetWindowHandle(window);

        if (handle == 0)
        {
            return null;
        }

        foreach (string candidate in CandidateLibraryPaths())
        {
            if (!File.Exists(candidate) || !NativeLibrary.TryLoad(candidate, out nint module))
            {
                continue;
            }

            if (!NativeLibrary.TryGetExport(module, "SDL_GetWindowTitle", out nint function))
            {
                continue;
            }

            unsafe
            {
                var getTitle = (delegate* unmanaged[Cdecl]<nint, nint>)function;
                nint pointer = getTitle(handle);

                if (pointer == 0)
                {
                    return null;
                }

                return Marshal.PtrToStringUTF8(pointer);
            }
        }

        return null;
    }

    /// <summary>MonoGame keeps the native window handle in a private field.</summary>
    private static nint GetWindowHandle(GameWindow window)
    {
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (string name in (string[])["_handle", "handle"])
        {
            FieldInfo? field = window.GetType().GetField(name, Flags);

            if (field?.GetValue(window) is nint handle && handle != 0)
            {
                return handle;
            }
        }

        return 0;
    }

    private static IEnumerable<string> CandidateLibraryPaths()
    {
        string root = AppContext.BaseDirectory;

        yield return Path.Combine(root, "runtimes", "win-x64", "native", "SDL2.dll");
        yield return Path.Combine(root, "runtimes", "win-x86", "native", "SDL2.dll");
        yield return Path.Combine(root, "runtimes", "win-arm64", "native", "SDL2.dll");
        yield return Path.Combine(root, "SDL2.dll");
        yield return "SDL2.dll";
    }
}
