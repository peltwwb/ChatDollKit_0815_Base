using System;
using System.Collections.Generic;
using UnityEngine;
using ChatdollKit.SpeechListener;
using ChatdollKit.Extension.SileroVAD;

namespace ChatdollKit.Demo
{
    public class SileroVADInitializer : MonoBehaviour
    {
        [SerializeField] private SpeechListenerBase speechListener;
        [SerializeField] private SileroVADProcessor sileroVAD;
        [SerializeField] private bool combineWithVolumeVAD = false;

        private void Awake()
        {
            try
            {
                if (speechListener == null)
                    speechListener = GetComponent<SpeechListenerBase>() ?? FindFirstObjectByType<SpeechListenerBase>();

                if (speechListener == null)
                {
                    Debug.LogWarning("[SileroVAD] SpeechListenerBase not found.");
                    return;
                }

                if (sileroVAD == null)
                    sileroVAD = speechListener.GetComponent<SileroVADProcessor>() ?? GetComponent<SileroVADProcessor>();

                if (sileroVAD == null)
                {
                    Debug.LogWarning("[SileroVAD] SileroVADProcessor not found.");
                    return;
                }



                // ② 初期化
                sileroVAD.Initialize();

                // ③ ハンドラ差し替え
                if (combineWithVolumeVAD)
                {
                    speechListener.DetectVoiceFunctions = new List<Func<float[], float, bool>>
                    {
                        sileroVAD.IsVoiced, speechListener.IsVoiceDetectedByVolume
                    };
                }
                else
                {
                    speechListener.DetectVoiceFunc = sileroVAD.IsVoiced;
                }

                Debug.Log("[SileroVAD] Initialized.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SileroVAD] Init failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
