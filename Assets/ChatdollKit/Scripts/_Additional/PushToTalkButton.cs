using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using ChatdollKit;
using ChatdollKit.SpeechListener;

/// <summary>
/// Push-to-talk controller for noisy environments.
/// Press to start recording immediately, release to force-stop and transcribe.
/// Attach this to a UI element that receives pointer down/up (e.g., a Button).
/// </summary>
public class PushToTalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [Header("Targets")]
    [Tooltip("AIAvatar to check current mode (optional). If null, will be found in scene.")]
    [SerializeField] private AIAvatar aiAvatar;
    [Tooltip("SpeechListener used for STT. If null, the first SpeechListenerBase in scene is used.")]
    [SerializeField] private SpeechListenerBase speechListener;

    [Header("Behavior")]
    [Tooltip("Only works while AIAvatar is in Listening mode. If disabled, works anytime.")]
    [SerializeField] private bool onlyWhenListening = true;
    [Tooltip("Silence threshold while holding (sec). Set large to avoid auto-stop by VAD.")]
    [SerializeField] private float holdSilenceThresholdSec = 3600f;
    [Tooltip("Min duration required to emit on release (sec). Set small to accept short utterances.")]
    [SerializeField] private float minDurationOnReleaseSec = 0.1f;
    [Tooltip("Max duration cap for safety (sec). Ignored if segmentation disabled.")]
    [SerializeField] private float maxDurationOnReleaseSec = 600f;

    private bool isHolding = false;

    // Backup original listener settings to restore after release
    private float originalSilenceThreshold;
    private float originalMinRec;
    private float originalMaxRec;
    private bool originalSegmentLongRecordings;
    private bool originalsSaved = false;

    private void Awake()
    {
        if (aiAvatar == null)
        {
            aiAvatar = FindFirstObjectByType<AIAvatar>();
        }
        if (speechListener == null)
        {
            speechListener = FindFirstObjectByType<SpeechListenerBase>();
            if (speechListener == null)
            {
                Debug.LogWarning("PushToTalkButton: SpeechListenerBase not found in scene.");
            }
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (speechListener == null) return;
        if (onlyWhenListening && aiAvatar != null && aiAvatar.Mode != AIAvatar.AvatarMode.Listening)
        {
            return; // Respect Listening-only behavior
        }

        // Save originals once per hold
        if (!originalsSaved)
        {
            originalSilenceThreshold = speechListener.SilenceDurationThreshold;
            originalMinRec = speechListener.MinRecordingDuration;
            originalMaxRec = speechListener.MaxRecordingDuration;
            originalSegmentLongRecordings = speechListener.SegmentLongRecordings;
            originalsSaved = true;
        }

        // Configure for push-to-talk (avoid auto-stop by silence while holding)
        speechListener.SilenceDurationThreshold = holdSilenceThresholdSec;
        speechListener.MinRecordingDuration = Mathf.Max(0.01f, minDurationOnReleaseSec);
        speechListener.MaxRecordingDuration = Mathf.Max(speechListener.MinRecordingDuration + 0.01f, maxDurationOnReleaseSec);
        // Disable segmentation while holding so nothing flushes mid-hold
        speechListener.SegmentLongRecordings = false;

        // Start a fresh session and force recording immediately
        speechListener.StartListening(stopBeforeStart: true);
        ForceStartRecording();

        isHolding = true;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (speechListener == null) return;
        if (!isHolding) return;

        // Restore originals BEFORE we finalize, so the next session started by the listener uses them
        if (originalsSaved)
        {
            speechListener.SilenceDurationThreshold = originalSilenceThreshold;
            speechListener.MinRecordingDuration = originalMinRec;
            speechListener.MaxRecordingDuration = originalMaxRec;
            speechListener.SegmentLongRecordings = originalSegmentLongRecordings;
            originalsSaved = false;
        }

        // Force-stop current recording and trigger transcription
        ForceStopRecordingAndTranscribe();

        isHolding = false;
    }

    // ---- Reflection helpers to control RecordingSession ----
    private object GetCurrentSession()
    {
        if (speechListener == null) return null;
        var slType = typeof(SpeechListenerBase);
        var sessionField = slType.GetField("session", BindingFlags.Instance | BindingFlags.NonPublic);
        if (sessionField == null) return null;
        return sessionField.GetValue(speechListener);
    }

    private void ForceStartRecording()
    {
        try
        {
            var session = GetCurrentSession();
            if (session == null) return;
            var method = session.GetType().GetMethod("StartRecording", BindingFlags.Instance | BindingFlags.NonPublic);
            method?.Invoke(session, null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"PushToTalkButton: Failed to force start recording: {ex.Message}");
        }
    }

    private void ForceStopRecordingAndTranscribe()
    {
        try
        {
            var session = GetCurrentSession();
            if (session == null)
            {
                // As a fallback, stop listener (no transcription when no active session)
                speechListener.StopListening();
                return;
            }

            var stopMethod = session.GetType().GetMethod(
                "StopRecording",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new Type[] { typeof(bool) },
                null);
            if (stopMethod != null)
            {
                // true => invoke OnRecordingComplete callback which triggers STT
                stopMethod.Invoke(session, new object[] { true });
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"PushToTalkButton: Failed to force stop recording: {ex.Message}");
            // Fallback: at least stop current listener session
            try { speechListener.StopListening(); }
            catch { }
        }
    }
}
