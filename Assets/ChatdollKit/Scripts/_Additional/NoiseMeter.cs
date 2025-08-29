using UnityEngine;
using UnityEngine.UI;

namespace ChatdollKit.UI
{
    /// <summary>
    /// マイク入力レベルを右 → 左に表示するメーター
    /// ハンドル（しきい値スライダー）と同じ 0〜100 スケールを想定。
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class NoiseMeter : MonoBehaviour
    {
        // ──────── 依存関係 ────────
        [Header("Dependencies")]
        [SerializeField] private MicrophoneController micController;

        // ──────── キャリブレーション ────────
        [Header("Calibration (dB)")]
        [Tooltip("部屋のノイズフロア付近 (小さい値ほど感度高)")]
        [SerializeField] private float minDb = -80f;
        [Tooltip("ほぼクリップ付近 (大きい値ほど感度低)")]
        [SerializeField] private float maxDb = -10f;

        // ──────── スムージング ────────
        [Header("Smoothing")]
        [Tooltip("音が上がった時に追従する速さ")]
        [Range(1f, 120f)]
        [SerializeField] private float attackSpeed = 80f;

        [Tooltip("音が下がった時に戻る速さ")]
        [Range(1f, 120f)]
        [SerializeField] private float releaseSpeed = 10f;

        // ──────── 内部変数 ────────
        private Image meterImage;

        // ──────────────────────────────────────────────
        private void Awake()
        {
            // Image 取得
            meterImage = GetComponent<Image>();

            // 無地スプライト（端までベタ塗り）を自動生成
            if (meterImage.sprite == null)
            {
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                meterImage.sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, 1, 1),
                    Vector2.one * 0.5f
                );
            }

            // Image 設定
            meterImage.type       = Image.Type.Filled;
            meterImage.fillMethod = Image.FillMethod.Horizontal;
            meterImage.fillOrigin = (int)Image.OriginHorizontal.Right;
            meterImage.fillAmount = 0f; // 初期化

            // RectTransform を stretch（幅＝親の幅）＋右端ピボットに
            var rt = (RectTransform)transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(1f, 0.5f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private void Update()
        {
            if (micController == null) return;

            // dB → 0–1 に線形マッピング
            float target = Mathf.InverseLerp(minDb, maxDb, micController.CurrentVolumeDb);

            // 攻め／戻しで速度を切り替え
            float speed  = (target > meterImage.fillAmount) ? attackSpeed : releaseSpeed;

            // 補間して反映
            meterImage.fillAmount = Mathf.Lerp(
                meterImage.fillAmount,
                target,
                Time.deltaTime * speed
            );

#if UNITY_EDITOR
            // デバッグ：dB と fillAmount を表示
            Debug.Log($"dB={micController.CurrentVolumeDb:F1}, fill={meterImage.fillAmount:F2}");
#endif
        }
    }
}
