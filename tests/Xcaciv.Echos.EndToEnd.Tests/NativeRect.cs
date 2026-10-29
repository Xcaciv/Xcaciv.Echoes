using System.Runtime.InteropServices;

namespace Xcaciv.Echos.EndToEnd.Tests;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
