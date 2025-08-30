// Assets/ChatdollKit/Extension/SileroVAD/OnnxRuntimePreload.cs
using System;
using System.Runtime.InteropServices;
using UnityEngine;

internal static class OnnxRuntimePreload
{
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
    [DllImport("onnxruntime", EntryPoint = "OrtGetApiBase")]
    private static extern IntPtr OrtGetApiBase();
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Preload()
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        try
        {
            var p = OrtGetApiBase();
            Debug.Log($"[OnnxRuntimePreload] onnxruntime OK. OrtGetApiBase=0x{p.ToInt64():X}");
        }
        catch (DllNotFoundException e)
        {
            Debug.LogWarning("[OnnxRuntimePreload] onnxruntime が見つかりません。"
                + "パッケージの Import 設定（Editor/ARM64）と再起動を確認してください。\n" + e);
        }
        catch (EntryPointNotFoundException e)
        {
            Debug.LogWarning("[OnnxRuntimePreload] onnxruntime は見つかったが OrtGetApiBase が見つかりません。バージョン不整合の可能性。\n" + e);
        }
#endif
    }
}
