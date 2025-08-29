using UnityEngine;
using UnityEngine.UI;
using ChatdollKit.SpeechListener;

namespace ChatdollKit.UI
{
    public class MicrophoneController : MonoBehaviour
    {
        // 既存のフィールド
        [Header("Dependencies")]
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

        // --- new_MicrophoneController.txt 追加: CurrentVolumeDbプロパティ ---
        /// <summary>
        /// 最新のマイク音量 (dB, 0–100) を公開。NoiseMeter などが参照する。
        /// </summary>
        public float CurrentVolumeDb =>
            microphoneManager != null ? microphoneManager.CurrentVolumeDb : -99.9f;
        // --- ここまで ---

        private void Start()
        {
            // 既存の初期化処理
            if (microphoneManager == null)
            {
                microphoneManager = FindObjectOfType<MicrophoneManager>();
                if (microphoneManager == null)
                {
                    Debug.LogWarning("MicrophoneManager is not found in this scene.");
                }
            }

            // --- 追加: スライダー初期値をNoiseGateThresholdDbに合わせる ---
            if (microphoneSlider != null && microphoneManager != null)
            {
                microphoneSlider.value = -microphoneManager.NoiseGateThresholdDb;
            }
            // --- ここまで ---
        }

        private void LateUpdate()
        {
            // --- new_MicrophoneController.txt 追加: エディタ限定デバッグ出力 ---
#if UNITY_EDITOR
            Debug.Log($"CurrentVolumeDb = {microphoneManager.CurrentVolumeDb:F1} dB");
#endif
            // --- ここまで ---

            // --- new_MicrophoneController.txt 追加: スライダーハンドル色変更 ---
            UpdateSliderHandleColor();
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

        /// <summary>
        /// Sensitivity スライダーの OnValueChanged から呼ばれる。
        /// </summary>
        public void UpdateMicrophoneSensitivity()
        {
            if (microphoneManager == null) return;

            microphoneManager.SetNoiseGateThresholdDb(-microphoneSlider.value);

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
