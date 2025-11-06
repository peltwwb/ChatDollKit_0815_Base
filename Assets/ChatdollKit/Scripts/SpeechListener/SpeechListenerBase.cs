using System;
using System.Text;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using System.Collections.Generic;

namespace ChatdollKit.SpeechListener
{
    public class SpeechListenerBase : MonoBehaviour, ISpeechListener
    {
        [Header("Voice Recorder Settings")]
        public string Name;
        public float SilenceDurationThreshold = 0.3f;
        public float MinRecordingDuration = 0.5f;
        public float MaxRecordingDuration = 3.0f;
        public float MaxPrerollDuration = 0.2f;
        public List<Func<float[], float, bool>> DetectVoiceFunctions;
        public Func<float[], float, bool> DetectVoiceFunc
        {
            set { DetectVoiceFunctions = new List<Func<float[], float, bool>>(){value};}
        }
        public string Language = "ja-JP";
        public List<string> AlternativeLanguages;
        public bool AutoStart = true;
        public bool PrintResult = false;
        public int TargetSampleRate = 0;
        public bool IsRecording { get; private set; }
        public bool IsVoiceDetected { get; private set; }

        public Func<string, UniTask> OnRecognized { get; set; }
        public Action OnTranscriptionStarted { get; set; }

        protected MicrophoneManager microphoneManager;
        private RecordingSession session;
        private CancellationTokenSource cancellationTokenSource;

        [Header("Voice Detection Tuning")]
        [Tooltip("Ratio of samples that must exceed the noise gate to mark as voiced (0.0 - 1.0). Higher values make the VAD less sensitive to noise.")]
        [Range(0.0f, 1.0f)]
        [SerializeField]
        private float volumeDetectionRequiredRatio = 0.05f;
        [Tooltip("Extra headroom (dB) added on top of the noise gate threshold for peak checks.")]
        [SerializeField]
        private float volumeDetectionHeadroomDb = 6.0f;
        [Tooltip("Minimum RMS headroom (dB) over the gate to classify as voiced when peak ratio is not met.")]
        [SerializeField]
        private float volumeDetectionRmsHeadroomDb = 3.0f;

        [Header("Segmentation")]
        [Tooltip("If true, long utterances are split into chunks around MaxRecordingDuration. If false, a single long clip is emitted per turn.")]
        public bool SegmentLongRecordings = false;
        [Tooltip("Overlap duration between segments when segmentation is enabled.")]
        public float SegmentOverlap = 0.1f;
        [Tooltip("Minimum duration (sec) to emit a segmented chunk. Used when segmentation is enabled.")]
        public float MinSegmentDuration = 0.3f;

        protected virtual void Start()
        {
            microphoneManager = gameObject.GetComponent<MicrophoneManager>();

            if (AutoStart)
            {
                StartListening();
            }
        }

        protected virtual void Update()
        {
            IsRecording = session != null && session.IsRecording;
            IsVoiceDetected = session != null && !session.IsSilent;
        }

        public void StartListening(bool stopBeforeStart = false)
        {
            if (microphoneManager == null) return;

            if (stopBeforeStart)
            {
                StopListening();
            }

            if (session != null) return;

            cancellationTokenSource = new CancellationTokenSource();

            var maxPrerollSamples = (int)(microphoneManager.SampleRate * MaxPrerollDuration);

            session = new RecordingSession(
                name: Name,
                silenceDurationThreshold: SilenceDurationThreshold,
                minRecordingDuration: MinRecordingDuration,
                maxRecordingDuration: MaxRecordingDuration,
                maxPrerollSamples: maxPrerollSamples,
                onRecordingComplete: async (samples) => await HandleRecordingCompleteAsync(samples, cancellationTokenSource.Token),
                detectVoiceFunctions: DetectVoiceFunctions ?? new() { IsVoiceDetectedByVolume },
                sampleRate: microphoneManager.SampleRate
            );

            // Configure segmentation behavior
            session.EnableMaxDurationSegmentation = SegmentLongRecordings;
            session.SegmentOverlapDuration = SegmentOverlap;
            session.MinSegmentDuration = MinSegmentDuration;

            microphoneManager.StartRecordingSession(session);
        }

        public bool IsVoiceDetectedByVolume(float[] samples, float linearThreshold)
        {
            if (samples == null || samples.Length == 0)
            {
                return false;
            }

            var peakThreshold = linearThreshold * Mathf.Pow(10.0f, volumeDetectionHeadroomDb / 20.0f);
            var requiredCount = Mathf.Clamp(Mathf.RoundToInt(samples.Length * volumeDetectionRequiredRatio), 1, samples.Length);

            var aboveCount = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                if (Mathf.Abs(samples[i]) >= peakThreshold)
                {
                    aboveCount++;
                    if (aboveCount >= requiredCount)
                    {
                        return true;
                    }
                }
            }

            // Fallback to RMS check to handle softer speech that lasts long enough
            double sum = 0.0;
            for (var i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            var rms = Mathf.Sqrt((float)(sum / samples.Length));
            var rmsThreshold = linearThreshold * Mathf.Pow(10.0f, volumeDetectionRmsHeadroomDb / 20.0f);

            return rms >= rmsThreshold;
        }

        public void StopListening()
        {
            if (session == null) return;

            microphoneManager.StopRecordingSession(session);
            cancellationTokenSource.Dispose();
            cancellationTokenSource = null;
            session = null;
        }

        public void ChangeSessionConfig(float silenceDurationThreshold = float.MinValue, float minRecordingDuration = float.MinValue, float maxRecordingDuration = float.MinValue)
        {
            if (silenceDurationThreshold > float.MinValue) SilenceDurationThreshold = silenceDurationThreshold;
            if (minRecordingDuration > float.MinValue) MinRecordingDuration = minRecordingDuration;
            if (maxRecordingDuration > float.MinValue) MaxRecordingDuration = maxRecordingDuration;
            // Only restart listening when the session has actually ended
            // (e.g., stopped by silence). When segmentation is enabled,
            // OnRecordingComplete can be invoked while still recording.
            if (session == null || !session.IsRecording)
            {
                StartListening(true);
            }
        }

        private float[] Resample(float[] samples, int originalRate, int targetRate)
        {
            if (originalRate == targetRate) return samples;

            int dstLength = Mathf.CeilToInt(samples.Length * (targetRate / (float)originalRate));
            var dst = new float[dstLength];
            float ratio = samples.Length / (float)dstLength;

            for (int i = 0; i < dstLength; i++)
            {
                float srcIndex = i * ratio;
                int i0 = Mathf.FloorToInt(srcIndex);
                int i1 = Mathf.Min(i0 + 1, samples.Length - 1);
                float t = srcIndex - i0;
                dst[i] = Mathf.Lerp(samples[i0], samples[i1], t);
            }
            return dst;
        }

        protected async UniTask HandleRecordingCompleteAsync(float[] samples, CancellationToken token)
        {
            try
            {
                float[] samplesToTranscript;
                int sampleRate;
                if (TargetSampleRate > 0 && microphoneManager.SampleRate > TargetSampleRate)
                {
                    sampleRate = TargetSampleRate;
                    samplesToTranscript = Resample(samples, microphoneManager.SampleRate, TargetSampleRate);
                }
                else
                {
                    sampleRate = microphoneManager.SampleRate;
                    samplesToTranscript = samples;
                }

                OnTranscriptionStarted?.Invoke();
                var text = await ProcessTranscriptionAsync(samplesToTranscript, sampleRate, token);
                if (PrintResult)
                {
                    Debug.Log($"Speech recognized: {text} ({Name})");
                }

                if (OnRecognized != null)
                {
                    await OnRecognized(text);
                }
            }
            catch (OperationCanceledException)
            {
                Debug.Log($"Transcription canceled ({Name})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error at HandleRecordingCompleteAsync ({Name}): {ex.Message}\n{ex.StackTrace}");
            }

            StartListening(true);
        }

    #pragma warning disable CS1998
        protected virtual async UniTask<string> ProcessTranscriptionAsync(float[] samples, int sampleRate, CancellationToken token)
        {
            throw new NotImplementedException($"ProcessTranscriptionAsync for {Name} is not implemented");
        }
    #pragma warning restore CS1998

        protected static byte[] SampleToPCM(float[] samplingData, int frequency, int channels)
        {
            var headerLength = 44;
            var pcm = new byte[samplingData.Length * channels * 2 + headerLength];

            // Set header
            SetWaveHeader(pcm, channels, frequency);

            for (var i = 0; i < samplingData.Length; i++)
            {
                // float to 16bit int to bytes
                Array.Copy(BitConverter.GetBytes((short)(samplingData[i] * 32767)), 0, pcm, i * 2 + headerLength, 2);
            }

            return pcm;
        }

        protected static void SetWaveHeader(byte[] pcm, int channels, int frequency)
        {
            Array.Copy(Encoding.ASCII.GetBytes("RIFF"), 0, pcm, 0, 4);
            Array.Copy(BitConverter.GetBytes((UInt32)(pcm.Length - 8)), 0, pcm, 4, 4);
            Array.Copy(Encoding.ASCII.GetBytes("WAVE"), 0, pcm, 8, 4);
            Array.Copy(Encoding.ASCII.GetBytes("fmt "), 0, pcm, 12, 4);
            Array.Copy(BitConverter.GetBytes(16), 0, pcm, 16, 4);
            Array.Copy(BitConverter.GetBytes((UInt16)1), 0, pcm, 20, 2);
            Array.Copy(BitConverter.GetBytes((UInt16)channels), 0, pcm, 22, 2);
            Array.Copy(BitConverter.GetBytes((UInt32)frequency), 0, pcm, 24, 4);
            Array.Copy(BitConverter.GetBytes((UInt32)frequency * 2), 0, pcm, 28, 4);
            Array.Copy(BitConverter.GetBytes((UInt16)2), 0, pcm, 32, 2);
            Array.Copy(BitConverter.GetBytes((UInt16)16), 0, pcm, 34, 2);
            Array.Copy(Encoding.ASCII.GetBytes("data"), 0, pcm, 36, 4);
            Array.Copy(BitConverter.GetBytes((UInt32)(pcm.Length - 44)), 0, pcm, 40, 4);
        }

        protected virtual void OnDestroy()
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                microphoneManager.StopRecordingSession(session);
            }
        }
    }
}
