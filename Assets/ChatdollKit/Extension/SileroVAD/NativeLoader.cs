// Assets/Scripts/NativeLoader.cs
using System;
using System.Runtime.InteropServices;

public static class NativeLoader
{
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
    const int RTLD_NOW = 2;
    [DllImport("libSystem.B.dylib")]
    static extern IntPtr dlopen(string path, int mode);
    [DllImport("libSystem.B.dylib")]
    static extern IntPtr dlsym(IntPtr handle, string symbol);
    [DllImport("libSystem.B.dylib")]
    static extern IntPtr dlerror();

    public static bool Preload(string fullPath)
    {
        var h = dlopen(fullPath, RTLD_NOW);
        return h != IntPtr.Zero && dlsym(h, "OrtGetApiBase") != IntPtr.Zero;
    }
#else
    public static bool Preload(string _) => true; // 他プラットフォームは不要
#endif
}
