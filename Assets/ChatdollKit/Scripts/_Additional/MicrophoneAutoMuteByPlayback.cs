using UnityEngine;

namespace ChatdollKit.SpeechListener
{
    // Mutes microphone while specified AudioSources are actively playing above a threshold.
    // Attach this to the same GameObject that has MicrophoneManager, and assign AI voice AudioSources.
    public class MicrophoneAutoMuteByPlayback : MonoBehaviour
    {
        [Tooltip("AudioSources that play the avatar's synthesized voice")]
        public AudioSource[] OutputSources;

        [Header("Detection Settings")]
        [Tooltip("dB threshold for output activity (RMS). Typical: -40 to -30 dB")] 
        public float OutputThresholdDb = -35f;

        [Tooltip("Samples to analyze per frame (larger = smoother, more CPU)")]
        public int SampleCount = 256;

        [Tooltip("Seconds output must exceed threshold before muting mic")]
        public float EngageDelay = 0.05f;

        [Tooltip("Seconds output must stay below threshold before unmuting mic")]
        public float ReleaseDelay = 0.25f;

        [Header("Debug")]
        public bool IsOutputActive;
        public bool IsMicMuted;

        private MicrophoneManager mic;
        private float activeTimer;
        private float inactiveTimer;
        private float[] scratch;

        private void Awake()
        {
            mic = GetComponent<MicrophoneManager>();
            if (mic == null)
            {
                Debug.LogWarning("MicrophoneAutoMuteByPlayback requires MicrophoneManager on the same GameObject");
            }
            scratch = new float[Mathf.Max(64, SampleCount)];
        }

        private void Update()
        {
            if (OutputSources == null || OutputSources.Length == 0 || mic == null)
            {
                return;
            }

            // Detect output activity
            bool active = false;
            int count = Mathf.Clamp(SampleCount, 64, 4096);
            if (scratch == null || scratch.Length != count) scratch = new float[count];

            for (int s = 0; s < OutputSources.Length; s++)
            {
                var src = OutputSources[s];
                if (src == null || !src.isPlaying) continue;

                try
                {
                    src.GetOutputData(scratch, 0);
                    float sum = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        float v = scratch[i];
                        sum += v * v;
                    }
                    float rms = Mathf.Sqrt(sum / count);
                    float db = 20f * Mathf.Log10(Mathf.Max(rms, 1e-7f));
                    if (db >= OutputThresholdDb)
                    {
                        active = true;
                        break;
                    }
                }
                catch (System.Exception)
                {
                    // Some platforms may not support GetOutputData reliably per source.
                    // Fall back to isPlaying as a heuristic.
                    active = true;
                    break;
                }
            }

            IsOutputActive = active;

            // Timers and state transitions with hysteresis
            if (active)
            {
                activeTimer += Time.deltaTime;
                inactiveTimer = 0f;
                if (!IsMicMuted && activeTimer >= EngageDelay)
                {
                    mic.MuteMicrophone(true);
                    IsMicMuted = true;
                }
            }
            else
            {
                inactiveTimer += Time.deltaTime;
                activeTimer = 0f;
                if (IsMicMuted && inactiveTimer >= ReleaseDelay)
                {
                    mic.MuteMicrophone(false);
                    IsMicMuted = false;
                }
            }
        }
    }
}

