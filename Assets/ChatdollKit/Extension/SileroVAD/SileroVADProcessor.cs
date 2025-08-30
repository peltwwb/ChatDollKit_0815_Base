using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine.Networking;
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#else
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
# endif

namespace ChatdollKit.Extension.SileroVAD
{
    public class SileroVADProcessor : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "IsVoiceDetected")]
        private static extern int IsVoiceDetectedJS();

        [DllImport("__Internal")]
        private static extern float GetVoiceProbability();

        [DllImport("__Internal")]
        private static extern void SetVADThreshold(float threshold);

        private float lastThreshold;
        public bool IsMuted { get; private set; } = false;
#else
        [Header("Input Settings")]
        [Tooltip("Source audio sample rate. Set from your mic/listener. If 0, treated as 16000.")]
        [SerializeField]
        private int sourceSampleRate = 16000;

        [Header("VAD Model Settings")]
        [Tooltip("The filename of the ONNX model in the StreamingAssets folder.")]
        [SerializeField]
        private string onnxModelName = "silero_vad.onnx";

        // The chunk size for SireloVAD is 512.
        private int sampleSize = 512;

        // The sampling rate for SileroVAD is 16000.
        private long modelSamplingRate = 16000;
        private InferenceSession session;
        private float[] state = new float[256];
        private readonly List<float> audioBuffer = new List<float>();

        // Resampler state (source -> 16kHz)
        private readonly List<float> resampleCarry = new List<float>(2);
        private double resampleNextPos = 0.0; // position in source-sample units for next output sample
        private long resampleSourceCount = 0; // total source samples processed so far
#endif
        [Header("VAD Detection Settings")]
        [Tooltip("Confidence threshold (WebGL only). For native, use Trigger/Release.")]
        [SerializeField, Range(0f, 1f)]
        private float threshold = 0.5f;

        [Tooltip("Start speech when probability >= this value for N frames (native only).")]
        [SerializeField, Range(0f, 1f)]
        private float triggerThreshold = 0.35f;

        [Tooltip("End speech when probability <= this value for M frames (native only).")]
        [SerializeField, Range(0f, 1f)]
        private float releaseThreshold = 0.20f;

        [Tooltip("Min consecutive frames to confirm speech start (native only).")]
        [SerializeField, Min(1)]
        private int minSpeechFrames = 3;

        [Tooltip("Min consecutive frames to confirm speech end (native only).")]
        [SerializeField, Min(1)]
        private int minSilenceFrames = 10;

        [Tooltip("The speech probability from the most recent inference.")]
        [SerializeField]
        private float lastProbability = 0f;

        private bool speaking = false;
        private int speechStreak = 0;
        private int silenceStreak = 0;

        // Return behavior tuning
        [Tooltip("If any frame in current batch is voiced, return true (prevents truncation on small end silences).")]
        [SerializeField]
        private bool returnAnySpeakingInBatch = true;

        [Tooltip("Extra chunks to keep returning true after last speech frame (batch-level hangover).")]
        [SerializeField, Min(0)]
        private int returnHangoverChunks = 3;
        private int hangoverChunksRemaining = 0;

        public bool IsVoiceDetected { get { return speaking; } }

#if UNITY_WEBGL && !UNITY_EDITOR
        private void Start()
        {
            lastThreshold = threshold;
            SetVADThreshold(threshold);
        }
#endif
        public void Initialize()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // Do nothing on WebGL runtime
#else
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                string modelPath = Path.Combine(Application.persistentDataPath, onnxModelName);
                using (UnityWebRequest www = UnityWebRequest.Get(Path.Combine(Application.streamingAssetsPath, onnxModelName)))
                {
                    www.SendWebRequest();
                    while (!www.isDone) { }
                    
                    if (www.result == UnityWebRequest.Result.Success)
                    {
                        File.WriteAllBytes(modelPath, www.downloadHandler.data);
                        Debug.Log($"Successfully copied {onnxModelName} to persistent storage");
                    }
                    else
                    {
                        throw new Exception($"Failed to load {onnxModelName} from StreamingAssets: {www.error}");
                    }
                }
#else
                string modelPath = Path.Combine(Application.streamingAssetsPath, onnxModelName);
#endif
                session = new InferenceSession(modelPath, new SessionOptions());
                ResetStates();
                Debug.Log($"VAD Initialized. Expecting {modelSamplingRate}Hz audio. Processing chunk size: {sampleSize}.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"VAD initialization failed: {ex.Message}\n{ex.StackTrace}");
            }
#endif
        }

        public bool IsVoiced(float[] newSamples, float param)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsMuted) return false;

            if (Mathf.Abs(threshold - lastThreshold) > 0.01f)
            {
                SetVADThreshold(threshold);
                lastThreshold = threshold;
            }

            lastProbability = GetVoiceProbability();

            return IsVoiceDetectedJS() == 1;
#else
            if (session == null || newSamples == null) return false;

            // If no samples are provided (e.g., mic muted or output playing),
            // do not keep returning true via hangover. Treat as silence.
            if (newSamples.Length == 0)
            {
                hangoverChunksRemaining = 0;
                return false; // no input -> treat as silence
            }

            // Heuristically treat the 2nd parameter as source sample rate if plausible
            if (param >= 1000f && param <= 192000f)
            {
                int sr = (int)param;
                if (sr != sourceSampleRate)
                {
                    SetSourceSampleRate(sr);
                }
            }

            // Push samples with resampling if needed
            PushSamplesToModelRate(newSamples);

            if (audioBuffer.Count < sampleSize)
            {
                // Not enough for inference yet; keep previous decision with hangover
                if (returnAnySpeakingInBatch)
                {
                    if (hangoverChunksRemaining > 0)
                    {
                        hangoverChunksRemaining = Mathf.Max(0, hangoverChunksRemaining - 1);
                        return true;
                    }
                    return speaking;
                }
                else
                {
                    return speaking;
                }
            }

            bool anySpeakingThisBatch = false;
            bool speakingBeganThisBatch = false;
            bool wasSpeaking = speaking;
            int processedChunks = 0;
            while (audioBuffer.Count >= sampleSize)
            {
                var chunkToProcess = new float[sampleSize];
                audioBuffer.CopyTo(0, chunkToProcess, 0, sampleSize);
                audioBuffer.RemoveRange(0, sampleSize);
                processedChunks++;

                // Update probability from model
                RunInference(chunkToProcess);

                // Hysteresis with hold timers
                // Optional amplitude gate using linear noise threshold (param when < 1.0)
                bool lowAmp = false;
                if (!speaking && param > 0f && param < 1.0f)
                {
                    float maxAbs = 0f;
                    for (int i = 0; i < chunkToProcess.Length; i++)
                    {
                        float a = Mathf.Abs(chunkToProcess[i]);
                        if (a > maxAbs) maxAbs = a;
                    }
                    lowAmp = maxAbs < param;
                }

                if (!lowAmp && lastProbability >= triggerThreshold)
                {
                    speechStreak++;
                    silenceStreak = 0;
                    if (!speaking && speechStreak >= minSpeechFrames)
                    {
                        speaking = true;
                        speakingBeganThisBatch = true;
                    }
                    // Consider as activity only when we already started speaking or have a streak
                    // This avoids pre-speech spikes from noise triggering activity.
                    if (speaking || speechStreak > 1)
                    {
                        anySpeakingThisBatch = true;
                    }
                }
                else if (lastProbability <= releaseThreshold)
                {
                    silenceStreak++;
                    speechStreak = 0;
                    if (speaking && silenceStreak >= minSilenceFrames)
                    {
                        speaking = false;
                        // Reset model states only when speech ends to keep context
                        ResetStates();
                    }
                }
                else
                {
                    // Between thresholds: do not accumulate either streak
                    speechStreak = 0;
                    silenceStreak = 0;
                }
            }
            // Hangover handling: keep returning true briefly after speech in this batch
            if (anySpeakingThisBatch)
            {
                hangoverChunksRemaining = Mathf.Max(hangoverChunksRemaining, returnHangoverChunks);
            }
            else if (hangoverChunksRemaining > 0)
            {
                // Decrease hangover per call, not per processed chunk, to keep stable timing
                hangoverChunksRemaining = Mathf.Max(0, hangoverChunksRemaining - 1);
            }

            if (returnAnySpeakingInBatch)
            {
                // Only report activity if actually speaking, or just began speaking,
                // or we are within hangover after having spoken.
                return speaking || speakingBeganThisBatch || (wasSpeaking && hangoverChunksRemaining > 0);
            }
            else
            {
                return speaking;
            }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        public void Mute(bool mute)
        {
            IsMuted = mute;
        }
#else
        private bool RunInference(float[] audioSamples)
        {
            try
            {
                var inputShape = new int[] { 1, sampleSize };
                var srShape = new int[] { 1 };
                var stateShape = new int[] { 2, 1, 128 };

                var inputTensor = new DenseTensor<float>(audioSamples, inputShape);
                var srTensor = new DenseTensor<long>(new long[] { this.modelSamplingRate }, srShape);
                var stateTensor = new DenseTensor<float>(this.state, stateShape);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", inputTensor),
                    NamedOnnxValue.CreateFromTensor("sr", srTensor),
                    NamedOnnxValue.CreateFromTensor("state", stateTensor)
                };

                using (var results = session.Run(inputs))
                {
                    var outputValue = results.FirstOrDefault(v => v.Name == "output");
                    var stateNValue = results.FirstOrDefault(v => v.Name == "stateN");

                    if (outputValue != null)
                    {
                        lastProbability = outputValue.AsTensor<float>().ToArray()[0];
                        if (stateNValue != null)
                        {
                            stateNValue.AsTensor<float>().ToArray().CopyTo(this.state, 0);
                        }
                        return lastProbability > threshold;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"VAD processing error: {ex.Message}\n{ex.StackTrace}");
            }
            return false;
        }

        public void ResetStates()
        {
            System.Array.Clear(this.state, 0, this.state.Length);
        }

        void OnDestroy()
        {
            session?.Dispose();
        }
#endif

#if !UNITY_WEBGL || UNITY_EDITOR
        // Allow external components to set/override the input sample rate
        public void SetSourceSampleRate(int sr)
        {
            sourceSampleRate = sr <= 0 ? (int)modelSamplingRate : sr;
        }

        private void PushSamplesToModelRate(float[] src)
        {
            int srcRate = sourceSampleRate <= 0 ? (int)modelSamplingRate : sourceSampleRate;
            if (src == null || src.Length == 0)
            {
                return;
            }

            if (srcRate == modelSamplingRate)
            {
                audioBuffer.AddRange(src);
                return;
            }

            // Build working buffer = carry + src
            var working = new List<float>(resampleCarry.Count + src.Length);
            if (resampleCarry.Count > 0) working.AddRange(resampleCarry);
            working.AddRange(src);

            double step = (double)srcRate / (double)modelSamplingRate; // source samples per 1 target sample
            double baseIndex = (double)(resampleSourceCount - resampleCarry.Count); // source index of working[0]

            int maxIdxForInterp = working.Count - 1; // need i and i+1
            // Generate as many target samples as possible
            while (resampleNextPos <= baseIndex + maxIdxForInterp - 0.9999999) // ensure i+1 exists
            {
                double pos = resampleNextPos - baseIndex;
                int i = (int)System.Math.Floor(pos);
                double frac = pos - i;
                float s0 = working[i];
                float s1 = working[i + 1];
                float y = (float)(s0 + (s1 - s0) * frac);
                audioBuffer.Add(y);
                resampleNextPos += step;
            }

            // Update counters and carry last source sample for next call
            resampleSourceCount += src.Length;
            resampleCarry.Clear();
            if (working.Count > 0)
            {
                resampleCarry.Add(working[working.Count - 1]);
            }
        }
#endif
    }
}
