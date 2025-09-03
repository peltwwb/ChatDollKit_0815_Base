using UnityEngine;

namespace ChatdollKit.Extension.SileroVAD
{
    internal static class OnnxRuntimePreloadAndroid
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Preload()
        {
            try
            {
                using (var sys = new AndroidJavaClass("java.lang.System"))
                {
                    // Ensure lib is loaded so P/Invoke resolves cleanly
                    sys.CallStatic("loadLibrary", "onnxruntime");
                }
                Debug.Log("[OnnxRuntimePreloadAndroid] onnxruntime loaded");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[OnnxRuntimePreloadAndroid] loadLibrary failed (will rely on Unity resolver): " + e.Message);
            }
        }
#endif
    }
}

