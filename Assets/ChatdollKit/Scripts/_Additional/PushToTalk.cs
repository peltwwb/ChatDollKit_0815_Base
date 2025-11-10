using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ChatdollKit;
using ChatdollKit.SpeechListener;

/// <summary>
/// Push-to-talk controller for noisy environments.
/// Click to start recording immediately, click again (or wait for timeout) to force-stop and transcribe.
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
    [Tooltip("Auto stop duration for toggle mode (sec). Set <= 0 to disable auto stop.")]
    [SerializeField] private float autoStopDurationSec = 120f;

    private bool isRecording = false;
    private float recordingStartTime = 0f;

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
        ToggleRecording();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        // Toggle handled on pointer down. Pointer up is ignored intentionally.
    }

    private void Update()
    {
        HandleScreenClickDown();
        HandleAutoStop();
    }

    private void HandleScreenClickDown()
    {
        if (!Input.GetMouseButtonDown(0)) return;
        if (IsPointerOverBlockedUI()) return;

        ToggleRecording();
    }

    private void HandleAutoStop()
    {
        if (!isRecording) return;
        if (autoStopDurationSec <= 0f) return;
        if (Time.time - recordingStartTime < autoStopDurationSec) return;

        StopRecording();
    }

    private bool CanStartRecording()
    {
        if (speechListener == null) return false;
        if (onlyWhenListening && aiAvatar != null && aiAvatar.Mode != AIAvatar.AvatarMode.Listening)
        {
            return false; // Respect Listening-only behavior
        }

        return true;
    }

    private void ToggleRecording()
    {
        if (isRecording)
        {
            StopRecording();
            return;
        }

        if (!CanStartRecording())
        {
            return;
        }

        StartRecording();
    }

    private void StartRecording()
    {
        if (speechListener == null) return;

        // Save originals once per session toggle
        if (!originalsSaved)
        {
            originalSilenceThreshold = speechListener.SilenceDurationThreshold;
            originalMinRec = speechListener.MinRecordingDuration;
            originalMaxRec = speechListener.MaxRecordingDuration;
            originalSegmentLongRecordings = speechListener.SegmentLongRecordings;
            originalsSaved = true;
        }

        // Configure for push-to-talk (avoid auto-stop by silence while active)
        speechListener.SilenceDurationThreshold = holdSilenceThresholdSec;
        speechListener.MinRecordingDuration = Mathf.Max(0.01f, minDurationOnReleaseSec);
        speechListener.MaxRecordingDuration = Mathf.Max(speechListener.MinRecordingDuration + 0.01f, maxDurationOnReleaseSec);
        // Disable segmentation while active so nothing flushes mid-session
        speechListener.SegmentLongRecordings = false;

        // Start a fresh session and force recording immediately
        speechListener.StartListening(stopBeforeStart: true);
        ForceStartRecording();

        isRecording = true;
        recordingStartTime = Time.time;

        if (aiAvatar != null)
        {
            aiAvatar.SetPushToTalkActive(true);
        }
    }

    private void StopRecording()
    {
        if (!isRecording)
        {
            return;
        }

        if (aiAvatar != null)
        {
            aiAvatar.SetPushToTalkActive(false);
        }

        if (speechListener == null)
        {
            isRecording = false;
            recordingStartTime = 0f;
            return;
        }

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

        isRecording = false;
        recordingStartTime = 0f;
    }

    private bool IsPointerOverBlockedUI()
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            return false;
        }

        var pointerData = new PointerEventData(eventSystem) { position = Input.mousePosition };
        List<RaycastResult> raycastResults = new List<RaycastResult>();
        eventSystem.RaycastAll(pointerData, raycastResults);
        for (int i = 0; i < raycastResults.Count; i++)
        {
            var target = raycastResults[i].gameObject;
            if (target == null)
            {
                continue;
            }

            if (target.GetComponent<Button>() != null)
            {
                return true;
            }
        }

        return false;
    }

    private void OnDisable()
    {
        if (isRecording)
        {
            StopRecording();
        }
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
