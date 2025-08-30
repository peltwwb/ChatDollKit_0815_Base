using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChatdollKit.SpeechListener
{
    public class RecordingSession
    {
        public string Name;
        public float SilenceDurationThreshold;
        public float MinRecordingDuration;
        public float MaxRecordingDuration;
        public System.Action<float[]> OnRecordingComplete;
        public List<Func<float[], float, bool>> DetectVoiceFunctions;
        public int SampleRate;
        // Use to reject near-silent segments when segmenting by max duration
        private float currentLinearNoiseGateThreshold;
        [Tooltip("Minimum duration (sec) to emit a segmented chunk. If 0, uses MinRecordingDuration.")]
        public float MinSegmentDuration = 0f;
        public bool EnableMaxDurationSegmentation = true;
        public float SegmentOverlapDuration = 0.1f; // seconds

        private List<float> recordedSamples = new List<float>();
        private float[] prerollBuffer;
        private int maxPrerollSamples;
        private int prerollIndex = 0;
        private int prerollCount = 0;
        private bool prerollConsumed = false;
        public bool IsRecording { get; private set; }
        public bool IsSilent { get; private set; }
        private bool isCompleted = false;
        private float silenceDuration = 0.0f;
        private float recordingStartTime;

        public RecordingSession(string name, float silenceDurationThreshold, float minRecordingDuration, float maxRecordingDuration, int maxPrerollSamples, System.Action<float[]> onRecordingComplete, List<Func<float[], float, bool>> detectVoiceFunctions, int sampleRate)
        {
            Name = name;
            SilenceDurationThreshold = silenceDurationThreshold;
            MinRecordingDuration = minRecordingDuration;
            MaxRecordingDuration = maxRecordingDuration;
            this.maxPrerollSamples = maxPrerollSamples;
            prerollBuffer = new float[maxPrerollSamples];
            OnRecordingComplete = onRecordingComplete;
            DetectVoiceFunctions = detectVoiceFunctions;
            SampleRate = sampleRate;
        }

        public void ProcessSamples(float[] samples, float linearNoiseGateThreshold)
        {
            // Update the latest linear threshold for silence checks in segmentation
            currentLinearNoiseGateThreshold = linearNoiseGateThreshold;
            if (isCompleted)
            {
                return; // Do not process completed session
            }

            // Check voice activity (OR semantics): if any function detects voice, it's not silent
            IsSilent = true;
            foreach (var f in DetectVoiceFunctions)
            {
                if (f(samples, linearNoiseGateThreshold))
                {
                    IsSilent = false;
                    break;
                }
            }

            if (!IsRecording && !IsSilent)
            {
                StartRecording();
            }

            if (IsRecording)
            {
                if (IsSilent)
                {
                    silenceDuration += Time.deltaTime;
                    if (silenceDuration >= SilenceDurationThreshold)
                    {
                        StopRecording();
                    }
                }
                else
                {
                    silenceDuration = 0.0f;
                }

                recordedSamples.AddRange(samples);

                if (EnableMaxDurationSegmentation && (Time.time - recordingStartTime > MaxRecordingDuration))
                {
                    FlushSegment();
                }
            }
            else
            {
                // Add samples to circular buffer
                foreach (var sample in samples)
                {
                    prerollBuffer[prerollIndex] = sample;
                    prerollIndex = (prerollIndex + 1) % maxPrerollSamples;
                    if (prerollCount < maxPrerollSamples)
                        prerollCount++;
                }
            }
        }

        private void StartRecording()
        {
            if (IsRecording || isCompleted)
            {
                return; // Do not start recording when session is already started or completed
            }

            IsRecording = true;
            silenceDuration = 0.0f;
            recordingStartTime = Time.time;
            recordedSamples.Clear();
            prerollConsumed = false;
        }

        private void StopRecording(bool invokeCallback = true)
        {
            if (!IsRecording || isCompleted)
            {
                return; // Do not stop recording when session is not started yet or already completed
            }

            IsRecording = false;

            if (invokeCallback)
            {
                var recordingDuration = Time.time - recordingStartTime - silenceDuration;
                // If segmentation is disabled, accept long recordings beyond MaxRecordingDuration
                bool withinMax = EnableMaxDurationSegmentation ? (recordingDuration <= MaxRecordingDuration) : true;
                if (recordingDuration >= MinRecordingDuration && withinMax)
                {
                    isCompleted = true; // Set isCompleted=true only when the length is valid
                    var combinedSamples = GetCombinedSamples(includePreroll: !prerollConsumed);
                    prerollConsumed = true;
                    OnRecordingComplete?.Invoke(combinedSamples);
                }
            }

            recordedSamples.Clear();
            prerollIndex = 0;
            prerollCount = 0;
        }
        
        private void FlushSegment()
        {
            if (!IsRecording || recordedSamples.Count == 0) return;

            // Build segment samples: include preroll only for the very first flush after start
            var segmentSamples = GetCombinedSamples(includePreroll: !prerollConsumed);
            prerollConsumed = true;

            // Guard: reject too-short or near-silent segments
            var segDur = segmentSamples.Length / (float)SampleRate;
            var minSeg = MinSegmentDuration > 0f ? MinSegmentDuration : MinRecordingDuration;
            if (segDur >= minSeg && !IsNearSilent(segmentSamples, currentLinearNoiseGateThreshold))
            {
                OnRecordingComplete?.Invoke(segmentSamples);
            }

            // Overlap handling: keep last tail for continuity
            int overlapSamples = Mathf.Max(0, Mathf.RoundToInt(SegmentOverlapDuration * SampleRate));
            int keep = Mathf.Min(overlapSamples, recordedSamples.Count);
            if (keep > 0)
            {
                var tail = new List<float>(keep);
                for (int i = recordedSamples.Count - keep; i < recordedSamples.Count; i++) tail.Add(recordedSamples[i]);
                recordedSamples.Clear();
                recordedSamples.AddRange(tail);
                recordingStartTime = Time.time - (keep / (float)SampleRate);
            }
            else
            {
                recordedSamples.Clear();
                recordingStartTime = Time.time;
            }

            // Keep recording, reset silence accumulator to avoid immediate stop
            silenceDuration = 0.0f;
        }

        private static bool IsNearSilent(float[] samples, float linearThreshold)
        {
            if (samples == null || samples.Length == 0) return true;
            // Compute RMS and compare to threshold (scaled down a bit to allow for natural variation)
            double sum = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                sum += s * s;
            }
            var rms = Mathf.Sqrt((float)(sum / samples.Length));
            // If linearThreshold is 0 (disabled), never treat as silent here
            if (linearThreshold <= 0f) return false;
            // Slightly below the gate to avoid borderline drops
            return rms < (linearThreshold * 0.8f);
        }

        private float[] GetCombinedSamples(bool includePreroll)
        {
            if (!includePreroll)
            {
                return recordedSamples.ToArray();
            }

            var prerollArray = new float[prerollCount];
            var startIndex = prerollCount < maxPrerollSamples ? 0 : prerollIndex;

            for (int i = 0; i < prerollCount; i++)
            {
                prerollArray[i] = prerollBuffer[(startIndex + i) % maxPrerollSamples];
            }

            var combinedSamples = new float[prerollCount + recordedSamples.Count];
            Array.Copy(prerollArray, 0, combinedSamples, 0, prerollCount);
            recordedSamples.CopyTo(combinedSamples, prerollCount);

            return combinedSamples;
        }
    }
}
