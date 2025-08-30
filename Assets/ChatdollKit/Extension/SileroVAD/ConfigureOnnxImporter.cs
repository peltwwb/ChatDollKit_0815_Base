#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class ConfigureOnnxImporter
{
    const string Arm64Path = "Packages/com.github.asus4.onnxruntime/Plugins/macOS/arm64/libonnxruntime.dylib";
    const string X64Path   = "Packages/com.github.asus4.onnxruntime/Plugins/macOS/x64/libonnxruntime.dylib";

    [MenuItem("Tools/ONNX Runtime/Configure macOS plugins")]
    public static void Run()
    {
        Configure(Arm64Path, editor:true,  cpu:"ARM64");   // Editor/Standalone は arm64 を使う
        Configure(X64Path,   editor:false, cpu:"x86_64");  // 必要なら editor:true にしても、CPU フィルタで無視されます
        AssetDatabase.SaveAssets();
        Debug.Log("ONNX Runtime macOS plugins configured. Please restart the Unity Editor.");
    }

    static void Configure(string path, bool editor, string cpu)
    {
        var imp = AssetImporter.GetAtPath(path) as PluginImporter;
        if (imp == null) { Debug.LogWarning("Plugin not found: " + path); return; }

        imp.SetCompatibleWithAnyPlatform(false);

        // Editor
        imp.SetCompatibleWithEditor(editor);
        imp.SetEditorData("OS",  "OSX");
        imp.SetEditorData("CPU", cpu);

        // Standalone macOS
        imp.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
        imp.SetPlatformData(BuildTarget.StandaloneOSX, "OS",  "OSX");
        imp.SetPlatformData(BuildTarget.StandaloneOSX, "CPU", cpu);

        imp.SaveAndReimport();
    }
}
#endif
