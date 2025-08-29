using UnityEngine;
using UnityEngine.UI;
using ChatdollKit.SpeechListener;

namespace ChatdollKit.UI
{
    public class MicrophoneController : MonoBehaviour
    {
        // --- new_MicrophoneController.txt 追加: Inspector用ヘッダー ---
        [Header("Dependencies")]
        // --- ここまで ---
        [SerializeField]
        private MicrophoneManager microphoneManager;

        [Header("UI – Sensitivity Slider")]
        [SerializeField]
        private Slider microphoneSlider;
        [SerializeField]
        private Image sliderHandleImage;
        [SerializeField]
        private Color32 voiceDetectedColor = new Color32(0, 204, 0, 255);
        [SerializeField]
        private Color32 voiceNotDetectedColor = new Color32(255, 255, 255, 255);
        [SerializeField]
        private GameObject volumePanel;
        [SerializeField]
        private Text volumeText;

        private float volumePanelHideTimer = 0.0f;
        private float volumeUpdateInterval = 0.33f;
        private float volumeUpdateTimer = 0.0f;
        private float previousVolume = -99.9f;

        // --- new_MicrophoneController.txt 追加: CurrentVolumeDbプロパティ ---
        /// <summary>
        /// 最新のマイク音量 (dB, 0–100) を公開。NoiseMeter などが参照する。
        /// </summary>
        public float CurrentVolumeDb =>
            microphoneManager != null ? microphoneManager.CurrentVolumeDb : -99.9f;
        // --- ここまで ---

        private void Start()
        {
            if (microphoneManager == null)
            {
                microphoneManager = FindFirstObjectByType<MicrophoneManager>();
                if (microphoneManager == null)
                {
                    Debug.LogWarning("MicrophoneManager is not found in this scene.");
                }
            }

            microphoneSlider.value = -1 * microphoneManager.NoiseGateThresholdDb;
        }

        private void LateUpdate()
        {
            if (volumePanel.activeSelf)
            {
                volumePanelHideTimer += Time.deltaTime;
                if (volumePanelHideTimer >= 5.0f)
                {
                    volumePanel.SetActive(false);
                }
            }

            volumeUpdateTimer += Time.deltaTime;
            if (volumeUpdateTimer >= volumeUpdateInterval)
            {
                if (microphoneManager.IsMuted)
                {
                    volumeText.text = $"Muted";
                }
                else
                {
                    var volumeToShow = microphoneManager.CurrentVolumeDb > -99.9f ? microphoneManager.CurrentVolumeDb : previousVolume;
                    volumeText.text = $"{volumeToShow:f1} / {-1 * microphoneSlider.value:f1} db";
                }
                volumeUpdateTimer = 0.0f;
            }
            if (microphoneManager.CurrentVolumeDb > -99.9f)
            {
                previousVolume = microphoneManager.CurrentVolumeDb;
            }

            // --- new_MicrophoneController.txt 追加: スライダーハンドル色変更のメソッド化 ---
            UpdateSliderHandleColor();
            // --- ここまで ---

            // --- new_MicrophoneController.txt 追加: エディタ限定デバッグ出力 ---
#if UNITY_EDITOR
            Debug.Log($"CurrentVolumeDb = {microphoneManager.CurrentVolumeDb:F1} dB");
#endif
            // --- ここまで ---
        }

        // --- new_MicrophoneController.txt 追加: スライダーハンドル色変更メソッド ---
        private void UpdateSliderHandleColor()
        {
            if (microphoneManager == null) return;

            sliderHandleImage.color =
                microphoneManager.CurrentVolumeDb > microphoneManager.NoiseGateThresholdDb
                    ? voiceDetectedColor
                    : voiceNotDetectedColor;
        }
        // --- ここまで ---

        public void UpdateMicrophoneSensitivity()
        {
            volumePanel.SetActive(true);
            volumePanelHideTimer = 0.0f;

            microphoneManager.SetNoiseGateThresholdDb(-1 * microphoneSlider.value);
            if (microphoneSlider.value == 0 && !microphoneManager.IsMuted)
            {
                microphoneManager.MuteMicrophone(true);
            }
            else if (microphoneSlider.value > 0 && microphoneManager.IsMuted)
            {
                microphoneManager.MuteMicrophone(false);
            }
        }
    }
}
