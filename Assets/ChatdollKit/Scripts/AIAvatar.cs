using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;
// --- new_AIAvatar.txt 追加: UnityEditor参照 ---
#if UNITY_EDITOR
using UnityEditor;
#endif
// --- ここまで ---
using Cysharp.Threading.Tasks;
using ChatdollKit.Dialog;
using ChatdollKit.LLM;
using ChatdollKit.Model;
using ChatdollKit.SpeechListener;
using ChatdollKit.SpeechSynthesizer;

namespace ChatdollKit
{
    public class AIAvatar : MonoBehaviour
    {
        public static string VERSION = "0.8.15";

        [Header("Avatar lifecycle settings")]
        [SerializeField]
        private float conversationTimeout = 10.0f;
        [SerializeField]
        private float idleTimeout = 60.0f;
        private float modeTimer = 60.0f;
        public enum AvatarMode
        {
            Disabled,
            Sleep,
            Idle,
            Conversation,
            Listening,
        }
        public AvatarMode Mode { get; private set; } = AvatarMode.Idle;
        private AvatarMode previousMode = AvatarMode.Idle;

        [Header("SpeechListener settings")]
        public float VoiceRecognitionThresholdDB = -50.0f;
        public float VoiceRecognitionRaisedThresholdDB = -15.0f;

        [SerializeField]
        private float conversationSilenceDurationThreshold = 0.4f;
        [SerializeField]
        private float conversationMinRecordingDuration = 0.3f;
        [SerializeField]
        private float conversationMaxRecordingDuration = 10.0f;
        [SerializeField]
        private float idleSilenceDurationThreshold = 0.3f;
        [SerializeField]
        private float idleMinRecordingDuration = 0.2f;
        [SerializeField]
        private float idleMaxRecordingDuration = 3.0f;
        [SerializeField]
        private bool showMessageWindowOnWake = true;

        public enum MicrophoneMuteStrategy
        {
            None,
            Threshold,
            Mute,
            StopDevice,
            StopListener
        }
        public MicrophoneMuteStrategy MicrophoneMuteBy = MicrophoneMuteStrategy.Mute;

        [Header("Conversation settings")]
        public List<WordWithAllowance> WakeWords;
        public List<string> CancelWords;
        public List<string> ExitWords;
        public List<WordWithAllowance> InterruptWords;
        public List<string> IgnoreWords = new List<string>() { "。", "、", "？", "！" };
        public int WakeLength;
        public string BackgroundRequestPrefix = "$";
        [SerializeField]
        [Tooltip("Animator int parameter name for speaking base pose (e.g. 'BaseParam')")]
        private string speakingBaseParamKey = "BaseParam";
        [SerializeField]
        [Tooltip("Animator int value for speaking base pose")]
        private int speakingBaseParamValue = 0;
        [SerializeField]
        [Tooltip("Duration for speaking base pose per frame (seconds)")]
        private float speakingBaseDuration = 3600f;
        [Header("Listening base settings")]
        [SerializeField]
        [Tooltip("Animator int parameter name for listening base pose (e.g. 'BaseParam')")]
        private string listeningBaseParamKey = "BaseParam";
        [SerializeField]
        [Tooltip("Animator int value for listening base pose")]
        private int listeningBaseParamValue = 0;
        [SerializeField]
        [Tooltip("Duration for a single loop of listening base animation (seconds)")]
        private float listeningBaseDuration = 60f;

        [Header("Listening Nod")]
        [SerializeField]
        [Tooltip("Enable nodding once when a valid utterance is recorded in Listening state")]
        private bool enableListeningNodOnUtterance = true;
        public enum ListeningNodTiming
        {
            OnVoiceStart,
            OnVoiceEnd
        }
        [SerializeField]
        [Tooltip("When to nod: at voice detection start or after utterance end")]
        private ListeningNodTiming listeningNodTiming = ListeningNodTiming.OnVoiceStart;
        [SerializeField]
        [Tooltip("Additive animation state name used for the nod (Animator layer state)")]
        private string listeningNodAdditiveName = "AGIA_Layer_nodding_once_01";
        [SerializeField]
        [Tooltip("Animator layer name for the additive nod animation")]
        private string listeningNodAdditiveLayer = "Additive Layer";
        [SerializeField]
        [Tooltip("Duration of the base pose while overlaying the nod additive animation (seconds)")]
        [Range(0.1f, 10f)]
        private float listeningNodTriggerDuration = 2.0f;
        [SerializeField]
        [Tooltip("Delay after voice detection start to nod (sec) when timing is OnVoiceStart")]
        [Range(0.0f, 2.0f)]
        private float listeningNodStartDelaySec = 0.0f;
        [SerializeField]
        [Tooltip("Interval to repeatedly nod while voice is detected (seconds). Set > 0 to enable.")]
        [Range(0.0f, 10f)]
        private float listeningNodRepeatIntervalSec = 2.0f;
        [SerializeField]
        [Tooltip("Minimum total recording duration to accept an utterance for nod (sec)")]
        [Range(0.05f, 5f)]
        private float listeningNodRequiredRecordingDurationSec = 0.3f;
        [SerializeField]
        [Tooltip("Required duration the input stays above volume threshold during the recording (sec)")]
        [Range(0.01f, 3f)]
        private float listeningNodRequiredLoudDurationSec = 0.15f;
        [SerializeField]
        [Tooltip("Offset to mic noise-gate threshold used for loudness check (dB). 0 means same as recognition threshold.")]
        private float listeningNodVolumeThresholdDbOffset = 0.0f;
        [SerializeField]
        [Tooltip("Guard time after character speech ends before accepting nod triggers (sec)")]
        [Range(0.0f, 1.0f)]
        private float listeningNodPostSpeechGuardSec = 0.2f;

        // Internals for nod detection
        private ChatdollKit.SpeechListener.MicrophoneManager micForReading; // optional scene mic for CurrentVolumeDb
        private IMicrophoneManager microphoneControl; // concrete mic control target accepting strategy operations
        private bool microphoneMutedByAvatar = false;
        private bool speechListenerSuspendedByAvatar = false;
        private float defaultNoiseGateThresholdDb;
        private bool listeningPrevRecording = false;
        private float listeningRecStartedAt = -1f;
        private float listeningRecTotalAccum = 0f;
        private float listeningRecLoudAccum = 0f;
        private bool listeningNodPending = false;
        private bool listeningNodTriggeredForThisRec = false;
        // Track last nod timing while repeating
        private float listeningNodLastFiredAt = -1f;
        private AudioMixer characterAudioMixer;
        [SerializeField]
        private string characterVolumeParameter = "CharacterVolume";
        [SerializeField]
        private float maxCharacterVolumeDb = 0.0f;
        public float MaxCharacterVolumeDb
        {
            get { return maxCharacterVolumeDb; }
            set { SetCharacterMaxVolumeDb(value); }
        }
        [SerializeField]
        private float characterVolumeSmoothTime = 0.2f;
        [SerializeField]
        private float characterVolumeChangeDelay = 0.2f;
        private float targetCharacterVolumeDb;
        private float currentCharacterVolumeDb;
        private float currentCharacterVelocity;
        private bool isPreviousRecording = false;
        private float recordingStartTime = 0f;
        private bool isVolumeChangePending = false;
        private bool isCharacterMuted;
        public bool IsCharacterMuted
        {
            get { return isCharacterMuted; }
            set { MuteCharacter(value); }
        }
        // Track when character speech just ended to guard nod immediately after
        private bool previousCharacterSpeaking = false;
        private float lastCharacterSpeakingEndedAt = -100f;

        [Header("ChatdollKit components")]
        public ModelController ModelController;
        public DialogProcessor DialogProcessor;
        public LLMContentProcessor LLMContentProcessor;
        public IMicrophoneManager MicrophoneManager;
        public ISpeechListener SpeechListener;
        public MessageWindowBase UserMessageWindow;
        public MessageWindowBase CharacterMessageWindow;
        
        // Exposed: whether currently in Listening mode and judged silent
        public bool IsListeningSilent { get; private set; } = false;

        private enum ListeningVoiceMessageState
        {
            None,
            NoInput,
            Inputting,
            InputReceived
        }

        private ListeningVoiceMessageState listeningVoiceMessageState = ListeningVoiceMessageState.None;
        private bool listeningMessagePrevRecording = false;
        private bool listeningMessageHadVoiceInRecording = false;
        private bool listeningMessageRecognizedInRecording = false;
        private bool listeningMessageSuppressedUntilNextRecording = false;
        // Ensure processing animation plays once per recording turn
        private bool processingPresentationShownForCurrentRecording = false;
        private bool pushToTalkActive = false;
        [Header("Listening Message Display")]
        [SerializeField]
        [Tooltip("Minimum seconds to keep 'input received' status visible before showing transcript. Set 0 to skip.")]
        private float listeningStatusMinDisplaySeconds = 0.5f;
        [SerializeField]
        [Tooltip("Minimum seconds to keep recognized transcript visible before starting AI response. Set 0 to skip.")]
        private float listeningTranscriptMinDisplaySeconds = 0.5f;
        private float listeningMessageStateChangedAt = -100f;
        [Header("Listening Silent Blink")]
        [SerializeField]
        private string listeningSilentBlinkFaceName = "Blink";
        [SerializeField]
        private float listeningSilentBlinkOnsetDelay = 0.7f; // seconds of continuous silence before closing eyes
        [SerializeField]
        private float listeningSilentBlinkMaxDuration = 1.5f; // maximum eyes-closed duration per silent segment
        [SerializeField]
        private float listeningSilentInitialNoBlinkWindow = 5.0f; // do not Blink in first N sec after entering Listening
        [SerializeField]
        [Tooltip("Min continuous recording time to 'confirm' non-silent and allow blink (sec)")]
        [Range(0.0f, 2.0f)]
        private float listeningBlinkGateRequiredRecordingDurationSec = 0.2f;
        [SerializeField]
        [Tooltip("Min time above volume threshold during that recording to 'confirm' non-silent (sec)")]
        [Range(0.0f, 2.0f)]
        private float listeningBlinkGateRequiredLoudDurationSec = 0.1f;
        private bool appliedListeningBlink = false;
        private bool detectedVoiceSinceListeningStart = false;
        private float listeningSilentStartedAt = -1f;
        private float listeningEnteredAt = -100f;
        // Internals for blink gate confirmation
        private bool listeningBlinkGatePrevRecording = false;
        private float listeningBlinkGateRecStartedAt = -1f;
        private float listeningBlinkGateLoudAccumSec = 0f;

        [Header("Post Speech Guard")]
        [SerializeField]
        [Tooltip("Ignore recognition for a short time after character speech ends to avoid self-echo.")]
        private bool enablePostSpeechRecognitionGuard = true;
        [SerializeField]
        [Tooltip("Seconds to ignore recognition after speech ends when guard is enabled.")]
        [Range(0.0f, 2.0f)]
        private float postSpeechRecognitionGuardSec = 0.5f;
 
        [Header("Error")]
        [SerializeField]
        private string ErrorVoice;
        [SerializeField]
        private string ErrorFace;
        [SerializeField]
        private string ErrorAnimationParamKey;
        [SerializeField]
        private int ErrorAnimationParamValue;

        private DialogProcessor.DialogStatus previousDialogStatus = DialogProcessor.DialogStatus.Idling;
        public Func<Dictionary<string, object>> GetPayloads { get; set; }
        public Func<string, UniTask> OnWakeAsync { get; set; }
        public List<ProcessingPresentation> ProcessingPresentations = new List<ProcessingPresentation>();

        // --- new_AIAvatar.txt 追加: フィラー音声再生用 ---
        private CancellationTokenSource fillerCts;
        // --- ここまで ---

        // 応答開始までprocessingAnimationを維持するためのフラグ
        private bool speakingBaseAppliedThisTurn = false;

        private void Awake()
        {
            // Get ChatdollKit components
            MicrophoneManager = MicrophoneManager ?? gameObject.GetComponent<IMicrophoneManager>();
            ModelController = ModelController ?? gameObject.GetComponent<ModelController>();
            DialogProcessor = DialogProcessor ?? gameObject.GetComponent<DialogProcessor>();
            LLMContentProcessor = LLMContentProcessor ?? gameObject.GetComponent<LLMContentProcessor>();
            SpeechListener = gameObject.GetComponent<ISpeechListener>();

            // Resolve scene microphone manager (for CurrentVolumeDb and threshold)
            if (MicrophoneManager is SpeechListener.MicrophoneManager mmSelf)
            {
                micForReading = mmSelf;
            }
            else
            {
                micForReading = FindFirstObjectByType<SpeechListener.MicrophoneManager>();
            }

            microphoneControl = (IMicrophoneManager)(micForReading ?? MicrophoneManager);
            defaultNoiseGateThresholdDb = VoiceRecognitionThresholdDB;
            if (micForReading != null)
            {
                defaultNoiseGateThresholdDb = micForReading.NoiseGateThresholdDb;
            }

            // Setup MicrophoneManager
            microphoneControl?.SetNoiseGateThresholdDb(VoiceRecognitionThresholdDB);

            // Ensure self-capture protection: route the avatar's AudioSource to the mic manager
            // so it can auto-mute mic while the character is speaking.
            if (micForReading != null)
            {
                if (micForReading.OutputAudioSource == null)
                {
                    micForReading.OutputAudioSource = (ModelController != null && ModelController.AudioSource != null)
                        ? ModelController.AudioSource
                        : GetComponent<AudioSource>();
                }
                // Keep auto-mute enabled by default to avoid picking up own TTS
                micForReading.AutoMuteWhileAudioPlaying = true;
            }

            // Setup ModelController
            ModelController.OnSayStart = async (voice, token) =>
            {
                // Ensure microphone is muted while character speaks even if dialog bypassed the request hook
                ApplyMicrophoneMuteState(true);

                if (!string.IsNullOrEmpty(voice.Text))
                {
                    if (CharacterMessageWindow != null)
                    {
                        if (voice.PreGap > 0)
                        {
                            await UniTask.Delay((int)(voice.PreGap * 1000));
                        }
                        _ = CharacterMessageWindow.ShowMessageAsync(voice.Text, token);
                    }
                }

                // 音声再生開始タイミングで初回のみ「発話ベース姿勢」を適用
                // これにより、応答開始まではprocessingAnimationが維持される
                if (!speakingBaseAppliedThisTurn && ModelController != null && !string.IsNullOrEmpty(speakingBaseParamKey))
                {
                    if (!ModelController.ShouldSkipSpeakingBasePose)
                    {
                        var baseAnim = new Model.Animation(
                            speakingBaseParamKey,
                            speakingBaseParamValue,
                            speakingBaseDuration
                        );
                        ModelController.Animate(new List<Model.Animation> { baseAnim });
                        speakingBaseAppliedThisTurn = true;
                    }
                }
            };
            ModelController.OnSayEnd = () =>
            {
                CharacterMessageWindow?.Hide();
            };

            // Setup DialogProcessor
            var neutralFaceRequest = new List<FaceExpression>() { new FaceExpression("Neutral") };
            DialogProcessor.OnRequestRecievedAsync = async (text, payloads, token) =>
            {
                // Exit Listening and enter Conversation immediately when a request arrives
                // so that Main stops the listening idle loop before processingAnimation starts
                Debug.Log("[AIAvatar] OnRequestRecievedAsync -> Conversation");
                Mode = AvatarMode.Conversation;
                modeTimer = conversationTimeout;

                // 新しいターンの開始時に、発話ベース姿勢の適用フラグをリセット
                speakingBaseAppliedThisTurn = false;

                // Control microphone at first before AI's speech
                ApplyMicrophoneMuteState(true);

                // Processing開始中のプレゼンテーション
                EnsureProcessingPresentationForCurrentRecording();

                // Show user message
                if (UserMessageWindow != null && !string.IsNullOrEmpty(text))
                {
                    // Suppress explicitly when requested (e.g., wake button initial input)
                    if (payloads != null && payloads.ContainsKey("SuppressUserMessage")
                        && payloads["SuppressUserMessage"] is bool && (bool)payloads["SuppressUserMessage"])
                    {
                        // Don't show user message this time
                    }
                    else if (!showMessageWindowOnWake && payloads != null && payloads.ContainsKey("IsWakeword") && (bool)payloads["IsWakeword"])
                    {
                        // Don't show message window on wakeword when disabled by setting
                    }
                    else if (text.StartsWith(BackgroundRequestPrefix))
                    {
                        // Don't show message window when text starts with BackgroundRequestPrefix (default: $)
                    }
                    else
                    {
                        await UserMessageWindow.ShowMessageAsync(text, token);
                    }
                }

                // Restore face to neutral
                ModelController.SetFace(neutralFaceRequest);
            };

#pragma warning disable CS1998
            DialogProcessor.OnEndAsync = async (endConversation, token) =>
            {
                // Control microphone after response / error shown
                ApplyMicrophoneMuteState(false);

                Debug.Log($"[AIAvatar] OnEndAsync endConversation={endConversation}");
                if (!token.IsCancellationRequested)
                {
                    if (endConversation)
                    {
                        // Conversation fully ended: return to Idle
                        Mode = AvatarMode.Idle;
                        modeTimer = idleTimeout;
                        ModelController.StartIdling();
                        ModelController.SuppressIdleFallback(false); // Idleへ戻るので抑止解除
                        UserMessageWindow?.Hide();
                        await ModelController.ChangeIdlingModeAsync("normal");
                        processingPresentationShownForCurrentRecording = false;
                    }
                    else
                    {
                        // Normal turn end: go to Listening and wait for next input
                        // Apply Listening base immediately to avoid any leftover prompt animation flashing
                        if (ModelController != null && !string.IsNullOrEmpty(listeningBaseParamKey))
                        {
                            var immediateListeningBase = new Model.Animation(listeningBaseParamKey, listeningBaseParamValue, listeningBaseDuration);
                            // Clear queued prompt animations and write base now
                            ModelController.ApplyBaseImmediately(immediateListeningBase, clearQueued: true, resetAdditiveLayers: false);
                        }

                        Mode = AvatarMode.Listening;
                        modeTimer = idleTimeout;
                        processingPresentationShownForCurrentRecording = false;
                        // Do not switch idling mode here to avoid double control with Main.cs
                        listeningMessagePrevRecording = false;
                        listeningMessageHadVoiceInRecording = false;
                        listeningMessageRecognizedInRecording = false;
                        listeningMessageSuppressedUntilNextRecording = false;
                        SetListeningMessage(ListeningVoiceMessageState.NoInput, true);
                        // Listening開始時にModelController側で抑止解除される
                    }
                }
            };

            DialogProcessor.OnStopAsync = async (forSuccessiveDialog) =>
            {
                // Stop speaking immediately
                ModelController.StopSpeech();
                ApplyMicrophoneMuteState(false);

                Debug.Log($"[AIAvatar] OnStopAsync forSuccessiveDialog={forSuccessiveDialog}");
                // Return to Idle when no successive dialogs are allocated
                if (!forSuccessiveDialog)
                {
                    Mode = AvatarMode.Idle;
                    modeTimer = idleTimeout;
                    ModelController.StartIdling();
                    ModelController.SuppressIdleFallback(false); // Idleへ戻るので抑止解除
                    UserMessageWindow?.Hide();
                    await ModelController.ChangeIdlingModeAsync("normal");
                    processingPresentationShownForCurrentRecording = false;
                }
            };
#pragma warning restore CS1998

            DialogProcessor.OnErrorAsync = OnErrorAsyncDefault;

            // Setup LLM ContentProcessor
            LLMContentProcessor.HandleSplittedText = (contentItem) =>
            {
                // Convert to AnimatedVoiceRequest
                var avreq = ModelController.ToAnimatedVoiceRequest(contentItem.Text, contentItem.Language);
                // Avoid switching back to Idle between streamed chunks
                avreq.StartIdlingOnEnd = false;
                if (contentItem.IsFirstItem)
                {
                    if (avreq.AnimatedVoices[0].Faces.Count == 0)
                    {
                        // Reset face expression at the beginning of animated voice
                        avreq.AddFace("Neutral");
                    }
                }

                // ベースの発話姿勢はOnSayStartで初回のみ適用するため、
                // ここでは自動注入しない（processingAnimationを応答開始まで維持）
                contentItem.Data = avreq;
            };

#pragma warning disable CS1998
            LLMContentProcessor.ProcessContentItemAsync = async (contentItem, token) =>
            {
                if (contentItem.Data is AnimatedVoiceRequest avreq)
                {
                    // Prefetch the voice from TTS service
                    foreach (var av in avreq.AnimatedVoices)
                    {
                        foreach (var v in av.Voices)
                        {
                            if (v.Text.Trim() == string.Empty) continue;

                            ModelController.PrefetchVoices(new List<Voice>(){new Voice(
                                v.Text, 0.0f, 0.0f, v.TTSConfig, true, string.Empty
                            )}, token);
                        }
                    }
                }
            };
#pragma warning restore CS1998

            LLMContentProcessor.ShowContentItemAsync = async (contentItem, cancellationToken) =>
            {
                if (contentItem.Data is AnimatedVoiceRequest avreq)
                {
                    await ModelController.AnimatedSay(avreq, cancellationToken);
                }
            };

            // Setup SpeechListner
            SpeechListener.OnTranscriptionStarted = OnSpeechListenerTranscriptionStarted;
            SpeechListener.OnRecognized = OnSpeechListenerRecognized;
            SpeechListener.ChangeSessionConfig(
                silenceDurationThreshold: idleSilenceDurationThreshold,
                minRecordingDuration: idleMinRecordingDuration,
                maxRecordingDuration: idleMaxRecordingDuration
            );

            // Setup SpeechSynthesizer
            foreach (var speechSynthesizer in gameObject.GetComponents<ISpeechSynthesizer>())
            {
                if (speechSynthesizer.IsEnabled)
                {
                    ModelController.SpeechSynthesizerFunc = speechSynthesizer.GetAudioClipAsync;
                    break;
                }
            }

            // Character speech volume
            if (ModelController.AudioSource.outputAudioMixerGroup != null)
            {
                characterAudioMixer = ModelController.AudioSource.outputAudioMixerGroup.audioMixer;
            }

            // --- new_AIAvatar.txt 追加: フィラー音声再生用キャンセル ---
            if (ModelController != null)
            {
                ModelController.CancelFillerAction = () => fillerCts?.Cancel();
            }
            // --- ここまで ---
        }

        private void Update()
        {
            UpdateMode();
            UpdateCharacterVolume();

            if (DialogProcessor.Status == DialogProcessor.DialogStatus.Processing
                && previousDialogStatus != DialogProcessor.DialogStatus.Processing)
            {
                // New processing turn started; ensure processing presentation plays
                speakingBaseAppliedThisTurn = false;
                EnsureProcessingPresentationForCurrentRecording();
            }

            // Listening + Silent indicator via Blink face
            // Use SpeechListenerBase to read voice activity; fall back to false when unavailable
            bool nowSilent = false;
            bool voiceDetectedNow = false;
            if (SpeechListener is SpeechListenerBase slBase)
            {
                voiceDetectedNow = slBase.IsVoiceDetected;
                nowSilent = !voiceDetectedNow;
            }
            // Avoid applying listening blink while character is speaking
            bool characterSpeaking = ModelController != null && ModelController.AudioSource != null && ModelController.AudioSource.isPlaying;
            // Track transition from speaking -> not speaking to apply a short guard for nods
            if (!characterSpeaking && previousCharacterSpeaking)
            {
                lastCharacterSpeakingEndedAt = Time.time;
            }
            previousCharacterSpeaking = characterSpeaking;
            // Only open Blink gate after a confirmed non-silent segment since entering Listening
            if (Mode == AvatarMode.Listening)
            {
                bool recNowGate = SpeechListener != null && SpeechListener.IsRecording;
                if (recNowGate)
                {
                    if (!listeningBlinkGatePrevRecording)
                    {
                        // Recording just started for gate check
                        listeningBlinkGateRecStartedAt = Time.time;
                        listeningBlinkGateLoudAccumSec = 0f;
                    }
                    // Accumulate time while actually voiced
                    if (voiceDetectedNow)
                    {
                        listeningBlinkGateLoudAccumSec += Time.deltaTime;
                    }
                    // Open gate when both total recording and voiced durations exceed thresholds
                    if (!detectedVoiceSinceListeningStart
                        && (Time.time - listeningBlinkGateRecStartedAt) >= listeningBlinkGateRequiredRecordingDurationSec
                        && listeningBlinkGateLoudAccumSec >= listeningBlinkGateRequiredLoudDurationSec)
                    {
                        detectedVoiceSinceListeningStart = true;
                    }
                }
                else if (listeningBlinkGatePrevRecording)
                {
                    // Recording ended without opening gate; reset accumulators for next try
                    listeningBlinkGateRecStartedAt = -1f;
                    listeningBlinkGateLoudAccumSec = 0f;
                }
                listeningBlinkGatePrevRecording = recNowGate;
            }
            bool withinInitialNoBlink = (Mode == AvatarMode.Listening) && (Time.time - listeningEnteredAt < listeningSilentInitialNoBlinkWindow);
            IsListeningSilent = (Mode == AvatarMode.Listening) && nowSilent && !characterSpeaking && detectedVoiceSinceListeningStart && !withinInitialNoBlink;

            // Manage persistent Blink with onset delay
            if (IsListeningSilent)
            {
                if (listeningSilentStartedAt < 0f)
                {
                    listeningSilentStartedAt = Time.time; // start timing continuous silence
                }
                if (!appliedListeningBlink && (Time.time - listeningSilentStartedAt) >= listeningSilentBlinkOnsetDelay)
                {
                    // Apply Blink (close eyes) for up to MaxDuration, then return to Neutral automatically
                    ModelController?.SetFace(new List<FaceExpression>()
                    {
                        new FaceExpression(listeningSilentBlinkFaceName, listeningSilentBlinkMaxDuration),
                        new FaceExpression("Neutral", 0.0f, string.Empty)
                    });
                    // Also reset viseme to prevent mouth movement while eyes are closed
                    ModelController?.ResetViseme();
                    appliedListeningBlink = true;
                }
                else if (appliedListeningBlink)
                {
                    // Keep resetting viseme during the closed-eyes window to suppress mouth twitch
                    ModelController?.ResetViseme();
                }
            }
            else
            {
                listeningSilentStartedAt = -1f;
                if (appliedListeningBlink)
                {
                    // Open eyes when leaving silence or other conditions break the silent state
                    ModelController?.SetFace(new List<FaceExpression>() { new FaceExpression("Neutral", 0.0f, string.Empty) });
                    appliedListeningBlink = false;
                }
            }

            // User message window (Listening status indicator)
            if (DialogProcessor.Status == DialogProcessor.DialogStatus.Idling && Mode == AvatarMode.Listening)
            {
                bool recordingNowForMessage = SpeechListener != null && SpeechListener.IsRecording;
                bool voiceActiveForMessage = voiceDetectedNow;

                if (!(SpeechListener is SpeechListenerBase) && recordingNowForMessage)
                {
                    voiceActiveForMessage = true;
                }

                bool newRecordingStarted = recordingNowForMessage && !listeningMessagePrevRecording;
                if (newRecordingStarted)
                {
                    listeningMessageHadVoiceInRecording = false;
                    listeningMessageRecognizedInRecording = false;
                    listeningMessageSuppressedUntilNextRecording = false;
                    processingPresentationShownForCurrentRecording = false;
                }

                if (!listeningMessageSuppressedUntilNextRecording)
                {
                    if (recordingNowForMessage)
                    {
                        if (voiceActiveForMessage)
                        {
                            listeningMessageHadVoiceInRecording = true;
                        }

                        if (listeningMessageRecognizedInRecording)
                        {
                            SetListeningMessage(ListeningVoiceMessageState.InputReceived);
                        }
                        else
                        {
                            SetListeningMessage(listeningMessageHadVoiceInRecording
                                ? ListeningVoiceMessageState.Inputting
                                : ListeningVoiceMessageState.NoInput);
                        }
                    }
                    else
                    {
                        if (listeningMessagePrevRecording)
                        {
                            if (listeningMessageRecognizedInRecording)
                            {
                                SetListeningMessage(ListeningVoiceMessageState.InputReceived);
                            }
                            else
                            {
                                SetListeningMessage(ListeningVoiceMessageState.NoInput);
                            }
                            listeningMessageHadVoiceInRecording = false;
                            listeningMessageRecognizedInRecording = false;
                        }
                        else if (listeningVoiceMessageState == ListeningVoiceMessageState.None)
                        {
                            SetListeningMessage(ListeningVoiceMessageState.NoInput);
                        }
                    }
                }

                listeningMessagePrevRecording = recordingNowForMessage;
            }
            else
            {
                if (!listeningMessageSuppressedUntilNextRecording && listeningVoiceMessageState != ListeningVoiceMessageState.None)
                {
                    SetListeningMessage(ListeningVoiceMessageState.None);
                }
                listeningMessagePrevRecording = false;
                listeningMessageHadVoiceInRecording = false;
                listeningMessageRecognizedInRecording = false;
                listeningMessageSuppressedUntilNextRecording = false;
                processingPresentationShownForCurrentRecording = false;
            }

            // Listening nod trigger based on recorded utterance
            if (enableListeningNodOnUtterance && Mode == AvatarMode.Listening && SpeechListener != null)
            {
                bool recNow = SpeechListener.IsRecording;
                // Guard against nodding from own TTS
                bool characterSpeakingNow = ModelController != null && ModelController.AudioSource != null && ModelController.AudioSource.isPlaying;
                // Accept nod only if some time has passed after character finished speaking
                bool passedPostSpeechGuard = (Time.time - lastCharacterSpeakingEndedAt) >= listeningNodPostSpeechGuardSec;

                if (recNow)
                {
                    if (!listeningPrevRecording)
                    {
                        // Recording started
                        listeningRecStartedAt = Time.time;
                        listeningRecTotalAccum = 0f;
                        listeningRecLoudAccum = 0f;
                        listeningNodTriggeredForThisRec = false;
                        // Reset repeat timing at the start of a recording
                        listeningNodLastFiredAt = -1f;
                        // Prepare start-timing nod only when repeat is disabled
                        listeningNodPending = (listeningNodRepeatIntervalSec <= 0f && listeningNodTiming == ListeningNodTiming.OnVoiceStart);
                    }

                    // Accumulate durations while recording
                    var dt = Time.deltaTime;
                    listeningRecTotalAccum += dt;
                    // Accumulate loud duration only when voice is actually detected (by VAD/volume)
                    if (voiceDetectedNow)
                    {
                        listeningRecLoudAccum += dt;
                    }

                    // Repeat nod while voice is detected (every listeningNodRepeatIntervalSec)
                    if (listeningNodRepeatIntervalSec > 0f
                        && voiceDetectedNow
                        && !characterSpeakingNow
                        && passedPostSpeechGuard)
                    {
                        // First nod after start delay and loudness gate
                        if (listeningNodLastFiredAt < 0f)
                        {
                            if (listeningRecLoudAccum >= listeningNodRequiredLoudDurationSec
                                && (Time.time - listeningRecStartedAt) >= listeningNodStartDelaySec)
                            {
                                if (ModelController != null
                                    && !string.IsNullOrEmpty(listeningNodAdditiveName)
                                    && !string.IsNullOrEmpty(listeningNodAdditiveLayer))
                                {
                                    var triggerAnim = new Model.Animation(
                                        listeningBaseParamKey,
                                        listeningBaseParamValue,
                                        listeningNodTriggerDuration,
                                        listeningNodAdditiveName,
                                        listeningNodAdditiveLayer
                                    );
                                    ModelController.TriggerListeningAnimation(triggerAnim);
                                    listeningNodLastFiredAt = Time.time;
                                    listeningNodTriggeredForThisRec = true;
                                    listeningNodPending = false; // ensure one-shot path is disabled once repeat starts
                                }
                            }
                        }
                        // Subsequent nods at the configured interval
                        else if ((Time.time - listeningNodLastFiredAt) >= listeningNodRepeatIntervalSec)
                        {
                            if (ModelController != null
                                && !string.IsNullOrEmpty(listeningNodAdditiveName)
                                && !string.IsNullOrEmpty(listeningNodAdditiveLayer))
                            {
                                var triggerAnim = new Model.Animation(
                                    listeningBaseParamKey,
                                    listeningBaseParamValue,
                                    listeningNodTriggerDuration,
                                    listeningNodAdditiveName,
                                    listeningNodAdditiveLayer
                                );
                                ModelController.TriggerListeningAnimation(triggerAnim);
                                listeningNodLastFiredAt = Time.time;
                            }
                        }
                    }

                    // If nod is pending for start timing (only when repeat disabled), and delay has passed, fire once
                    if (listeningNodRepeatIntervalSec <= 0f
                        && listeningNodPending
                        && listeningNodTiming == ListeningNodTiming.OnVoiceStart
                        && !characterSpeakingNow
                        && passedPostSpeechGuard
                        && listeningRecLoudAccum >= listeningNodRequiredLoudDurationSec
                        && (Time.time - listeningRecStartedAt) >= listeningNodStartDelaySec)
                    {
                        if (ModelController != null
                            && !string.IsNullOrEmpty(listeningNodAdditiveName)
                            && !string.IsNullOrEmpty(listeningNodAdditiveLayer))
                        {
                            var triggerAnim = new Model.Animation(
                                listeningBaseParamKey,
                                listeningBaseParamValue,
                                listeningNodTriggerDuration,
                                listeningNodAdditiveName,
                                listeningNodAdditiveLayer
                            );
                            ModelController.TriggerListeningAnimation(triggerAnim);
                            listeningNodPending = false;
                            listeningNodTriggeredForThisRec = true;
                        }
                    }
                }
                else if (listeningPrevRecording)
                {
                    // Recording just ended: decide and possibly nod
                    var total = listeningRecTotalAccum;
                    var loud = listeningRecLoudAccum;

                    // Reset accumulators for next turn
                    listeningRecStartedAt = -1f;
                    listeningRecTotalAccum = 0f;
                    listeningRecLoudAccum = 0f;

                    // Conditions: within Listening, not currently speaking, recorded long enough and loud long enough
                    // Only when repeat is disabled; repeat stops when detection ends
                    if (listeningNodRepeatIntervalSec <= 0f
                        && listeningNodTiming == ListeningNodTiming.OnVoiceEnd
                        && !listeningNodTriggeredForThisRec
                        && !characterSpeakingNow
                        && passedPostSpeechGuard
                        && total >= listeningNodRequiredRecordingDurationSec
                        && loud >= listeningNodRequiredLoudDurationSec
                        && ModelController != null
                        && !string.IsNullOrEmpty(listeningNodAdditiveName)
                        && !string.IsNullOrEmpty(listeningNodAdditiveLayer))
                    {
                        var triggerAnim = new Model.Animation(
                            listeningBaseParamKey,
                            listeningBaseParamValue,
                            listeningNodTriggerDuration,
                            listeningNodAdditiveName,
                            listeningNodAdditiveLayer
                        );
                        ModelController.TriggerListeningAnimation(triggerAnim);
                        listeningNodTriggeredForThisRec = true;
                    }
                    listeningNodPending = false;
                    listeningNodLastFiredAt = -1f; // reset repeat timer when recording ends
                }

                listeningPrevRecording = recNow;
            }

            // Speech listener config
            if (Mode != previousMode)
            {
                Debug.Log($"[AIAvatar] Mode change {previousMode} -> {Mode}");
                // Non-idle modes must keep idle fallback suppressed to avoid flashing the idle pose
                ModelController?.SuppressIdleFallback(Mode != AvatarMode.Idle);

                // Reset Blink gating when entering Listening
                if (Mode == AvatarMode.Listening)
                {
                    // Start each Listening with eyes open (no Blink)
                    detectedVoiceSinceListeningStart = false;
                    appliedListeningBlink = false;
                    IsListeningSilent = false;
                    listeningSilentStartedAt = -1f;
                    listeningEnteredAt = Time.time;
                    listeningBlinkGatePrevRecording = false;
                    listeningBlinkGateRecStartedAt = -1f;
                    listeningBlinkGateLoudAccumSec = 0f;
                    ModelController?.SetFace(new List<FaceExpression>() { new FaceExpression("Neutral", 0.0f, string.Empty) });

                    // Reset nod detection accumulators per listening session
                    // Always start from non-recording for nod detection to avoid false end triggers
                    listeningPrevRecording = false;
                    listeningRecStartedAt = -1f;
                    listeningRecTotalAccum = 0f;
                    listeningRecLoudAccum = 0f;
                    listeningNodPending = false;
                    listeningNodTriggeredForThisRec = false;
                    listeningNodLastFiredAt = -1f;

                    listeningMessagePrevRecording = false;
                    listeningMessageHadVoiceInRecording = false;
                    listeningMessageRecognizedInRecording = false;
                    listeningMessageSuppressedUntilNextRecording = false;
                    if (DialogProcessor.Status == DialogProcessor.DialogStatus.Idling)
                    {
                        SetListeningMessage(ListeningVoiceMessageState.NoInput, true);
                    }
                    else
                    {
                        SetListeningMessage(ListeningVoiceMessageState.None, true);
                    }
                }
                // Start/Stop listening base pose
                if (Mode == AvatarMode.Listening)
                {
                    if (ModelController != null && !string.IsNullOrEmpty(listeningBaseParamKey))
                    {
                        var idleAnim = new Model.Animation(listeningBaseParamKey, listeningBaseParamValue, listeningBaseDuration);
                        ModelController.StartListeningIdle(idleAnim);
                    }
                    Debug.Log("[AIAvatar] Enter Listening");
                }
                else if (previousMode == AvatarMode.Listening)
                {
                    ModelController?.StopListeningIdle();
                    Debug.Log("[AIAvatar] Exit Listening");
                }

                if (Mode == AvatarMode.Conversation)
                {
                    SpeechListener.ChangeSessionConfig(
                        silenceDurationThreshold: conversationSilenceDurationThreshold,
                        minRecordingDuration: conversationMinRecordingDuration,
                        maxRecordingDuration: conversationMaxRecordingDuration
                    );
                }
                else
                {
                    SpeechListener.ChangeSessionConfig(
                        silenceDurationThreshold: idleSilenceDurationThreshold,
                        minRecordingDuration: idleMinRecordingDuration,
                        maxRecordingDuration: idleMaxRecordingDuration
                    );
                }

                // Recover normal idling pool whenever coming back to Idle
                if (Mode == AvatarMode.Idle)
                {
                    _ = ModelController.ChangeIdlingModeAsync("normal");
                }
            }

            previousDialogStatus = DialogProcessor.Status;
            previousMode = Mode;
        }

        private void SetListeningMessage(ListeningVoiceMessageState state, bool force = false)
        {
            if (!force && listeningVoiceMessageState == state)
            {
                return;
            }

            listeningVoiceMessageState = state;
            listeningMessageStateChangedAt = Time.unscaledTime;

            ApplyListeningMessageDisplay(state);
        }

        private void ApplyListeningMessageDisplay(ListeningVoiceMessageState state)
        {
            if (listeningMessageSuppressedUntilNextRecording && state != ListeningVoiceMessageState.None)
            {
                return;
            }

            var prefix = pushToTalkActive ? "【確実に聞いています】" : "【聞いています】";

            switch (state)
            {
                case ListeningVoiceMessageState.None:
                    UserMessageWindow?.Hide();
                    break;
                case ListeningVoiceMessageState.NoInput:
                    UserMessageWindow?.Show($"{prefix}\n入力待機中・・・");
                    break;
                case ListeningVoiceMessageState.Inputting:
                    UserMessageWindow?.Show($"{prefix}\n音声入力中・・・");
                    break;
                case ListeningVoiceMessageState.InputReceived:
                    UserMessageWindow?.Show($"{prefix}\n文字起こし中・・・");
                    break;
            }
        }

        private void UpdateMode()
        {
            if (DialogProcessor.Status != DialogProcessor.DialogStatus.Idling
                && DialogProcessor.Status != DialogProcessor.DialogStatus.Error)
            {
                Mode = AvatarMode.Conversation;
                modeTimer = conversationTimeout;
                return;
            }

            if (Mode == AvatarMode.Sleep)
            {
                return;
            }

            modeTimer -= Time.deltaTime;
            if (modeTimer > 0)
            {
                return;
            }

            if (Mode == AvatarMode.Conversation)
            {
                // After conversation timer, fall back to Idle
                Mode = AvatarMode.Idle;
                modeTimer = idleTimeout;
            }
            else if (Mode == AvatarMode.Listening)
            {
                // After listening timer, fall back to Idle (then Idle may go to Sleep later)
                Mode = AvatarMode.Idle;
                modeTimer = idleTimeout;
            }
            else if (Mode == AvatarMode.Idle)
            {
                // Sleep はまだ実装しないため、Idle のままタイマーをリセット
                modeTimer = idleTimeout;
            }
        }

        private void UpdateCharacterVolume()
        {
            if (characterAudioMixer == null || isCharacterMuted) return;

            // Handle recording state changes
            if (SpeechListener.IsRecording && !isPreviousRecording)
            {
                // Recording just started - mark the time and set pending flag
                recordingStartTime = Time.time;
                isVolumeChangePending = true;
            }
            else if (!SpeechListener.IsRecording && isPreviousRecording)
            {
                // Recording stopped - immediately restore volume
                targetCharacterVolumeDb = maxCharacterVolumeDb; // Unmute
                isVolumeChangePending = false;
            }
            
            // Check if we should start muting after delay
            if (isVolumeChangePending && SpeechListener.IsRecording)
            {
                if (Time.time - recordingStartTime >= characterVolumeChangeDelay)
                {
                    // Delay has passed and still recording - start muting
                    targetCharacterVolumeDb = -80.0f;   // Mute
                    isVolumeChangePending = false;
                }
            }
            
            isPreviousRecording = SpeechListener.IsRecording;

            // Smoothing
            currentCharacterVolumeDb = Mathf.SmoothDamp(
                currentCharacterVolumeDb,
                targetCharacterVolumeDb,
                ref currentCharacterVelocity,
                characterVolumeSmoothTime
            );

            // Apply to mixer
            characterAudioMixer.SetFloat(characterVolumeParameter, currentCharacterVolumeDb);
        }

        private void MuteCharacter(bool mute)
        {
            if (characterAudioMixer == null) return;

            currentCharacterVelocity = 0.0f;
            // Update currentCharacterVolumeDb and targetCharacterVolumeDb to stop smoothing
            currentCharacterVolumeDb = mute ? -80.0f : maxCharacterVolumeDb;
            targetCharacterVolumeDb = currentCharacterVolumeDb;
            characterAudioMixer.SetFloat(characterVolumeParameter, currentCharacterVolumeDb);
            isCharacterMuted = mute;
        }

        private void SetCharacterMaxVolumeDb(float volumeDb)
        {
            if (characterAudioMixer == null) return;

            maxCharacterVolumeDb = volumeDb > 0 ? 0.0f : volumeDb < -80.0f ? -80.0f : volumeDb;
            // Update currentCharacterVolumeDb and targetCharacterVolumeDb to stop smoothing
            currentCharacterVolumeDb = maxCharacterVolumeDb;
            targetCharacterVolumeDb = currentCharacterVolumeDb;
            characterAudioMixer.SetFloat(characterVolumeParameter, currentCharacterVolumeDb);
        }

        private string ExtractWakeWord(string text)
        {
            var textLower = text.ToLower();
            foreach (var iw in IgnoreWords)
            {
                textLower = textLower.Replace(iw.ToLower(), string.Empty);
            }

            foreach (var ww in WakeWords)
            {
                var wwText = ww.Text.ToLower();
                if (textLower.Contains(wwText))
                {
                    var prefix = textLower.Substring(0, textLower.IndexOf(wwText));
                    var suffix = textLower.Substring(textLower.IndexOf(wwText) + wwText.Length);

                    if (prefix.Length <= ww.PrefixAllowance && suffix.Length <= ww.SuffixAllowance)
                    {
                        return text;
                    }
                }
            }

            if (WakeLength > 0)
            {
                if (textLower.Length >= WakeLength)
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private string ExtractCancelWord(string text)
        {
            var textLower = text.ToLower().Trim();
            foreach (var iw in IgnoreWords)
            {
                textLower = textLower.Replace(iw.ToLower(), string.Empty);
            }

            foreach (var cw in CancelWords)
            {
                if (textLower == cw.ToLower())
                {
                    return cw;
                }
            }

            return string.Empty;
        }

        private string ExtractInterruptWord(string text)
        {
            var textLower = text.ToLower();
            foreach (var iw in IgnoreWords)
            {
                textLower = textLower.Replace(iw.ToLower(), string.Empty);
            }

            foreach (var w in InterruptWords)
            {
                var itrwText = w.Text.ToLower();
                if (textLower.Contains(itrwText))
                {
                    var prefix = textLower.Substring(0, textLower.IndexOf(itrwText));
                    var suffix = textLower.Substring(textLower.IndexOf(itrwText) + itrwText.Length);

                    if (prefix.Length <= w.PrefixAllowance && suffix.Length <= w.SuffixAllowance)
                    {
                        return text;
                    }
                }
            }

            return string.Empty;
        }

        // --- new_AIAvatar.txt 追加: ExitWord抽出 ---
        private string ExtractExitWord(string text)
        {
            var textLower = text.ToLower().Trim();
            foreach (var iw in IgnoreWords)
            {
                textLower = textLower.Replace(iw.ToLower(), string.Empty);
            }

            if (ExitWords != null)
            {
                foreach (var ew in ExitWords)
                {
                    if (textLower == ew.ToLower())
                    {
                        return ew;
                    }
                }
            }

            return string.Empty;
        }
        // --- ここまで ---

        public void Chat(string text = null, Dictionary<string, object> payloads = null)
        {
            if (string.IsNullOrEmpty(text.Trim()))
            {
                if (WakeWords.Count > 0)
                {
                    text = WakeWords[0].Text;
                }
                else
                {
                    Debug.LogWarning("Can't start chat without request text");
                    return;
                }
            }

            _ = DialogProcessor.StartDialogAsync(text, payloads);
        }

        public void StopChat(bool continueDialog = false)
        {
            _ = StopChatAsync();
        }

        public async UniTask StopChatAsync(bool continueDialog = false)
        {
            if (continueDialog)
            {
                // Just stop current AI's turn
                await DialogProcessor.StopDialog();
            }
            else
            {
                // Stop AI's turn and wait for idling status
                await DialogProcessor.StopDialog(waitForIdling: true);
                // Change AvatarMode to Idle not to show user message window
                Mode = AvatarMode.Idle;
                modeTimer = idleTimeout;
            }

            ApplyMicrophoneMuteState(false);
            processingPresentationShownForCurrentRecording = false;
        }

        public void AddProcessingPresentaion(List<Model.Animation> animations, List<FaceExpression> faces)
        {
            ProcessingPresentations.Add(new ProcessingPresentation()
            {
                Animations = animations,
                Faces = faces
            });
        }

        private void ShowProcessingPresentation()
        {
            if (ModelController == null)
            {
                return;
            }

            // Ensure idle fallback does not override processing pose
            ModelController.SuppressIdleFallback(true);

            if (ProcessingPresentations.Count == 0)
            {
                return;
            }

            var animAndFace = ProcessingPresentations[UnityEngine.Random.Range(0, ProcessingPresentations.Count)];

            ModelController.StopIdling();
            ModelController.StopListeningIdle();

            if (animAndFace?.Animations != null && animAndFace.Animations.Count > 0)
            {
                ModelController.Animate(animAndFace.Animations);
            }

            var faces = animAndFace?.Faces ?? new List<FaceExpression>();
            ModelController.SetFace(faces);
        }

        private void EnsureProcessingPresentationForCurrentRecording()
        {
            if (processingPresentationShownForCurrentRecording)
            {
                return;
            }

            if (ModelController == null || ProcessingPresentations.Count == 0)
            {
                return;
            }

            ShowProcessingPresentation();
            processingPresentationShownForCurrentRecording = true;
        }

        private void ApplyMicrophoneMuteState(bool mute, bool force = false)
        {
            var control = microphoneControl ?? MicrophoneManager;
            var previousState = microphoneMutedByAvatar;
            var stateChanged = previousState != mute;

            if (!stateChanged && !force)
            {
                if (!mute && MicrophoneMuteBy == MicrophoneMuteStrategy.Threshold && micForReading != null)
                {
                    defaultNoiseGateThresholdDb = micForReading.NoiseGateThresholdDb;
                }
                return;
            }

            if (mute && MicrophoneMuteBy == MicrophoneMuteStrategy.Threshold)
            {
                if (micForReading != null)
                {
                    defaultNoiseGateThresholdDb = micForReading.NoiseGateThresholdDb;
                }
                else if (control != null)
                {
                    defaultNoiseGateThresholdDb = VoiceRecognitionThresholdDB;
                }
            }

            microphoneMutedByAvatar = mute;

            switch (MicrophoneMuteBy)
            {
                case MicrophoneMuteStrategy.StopDevice:
                    if (mute)
                    {
                        control?.StopMicrophone();
                    }
                    else
                    {
                        control?.StartMicrophone();
                    }
                    break;
                case MicrophoneMuteStrategy.StopListener:
                    // handled in UpdateSpeechListenerSuspension
                    break;
                case MicrophoneMuteStrategy.Mute:
                    control?.MuteMicrophone(mute);
                    break;
                case MicrophoneMuteStrategy.Threshold:
                    if (mute)
                    {
                        control?.SetNoiseGateThresholdDb(VoiceRecognitionRaisedThresholdDB);
                    }
                    else
                    {
                        control?.SetNoiseGateThresholdDb(defaultNoiseGateThresholdDb);
                        if (micForReading != null)
                        {
                            defaultNoiseGateThresholdDb = micForReading.NoiseGateThresholdDb;
                        }
                    }
                    break;
                case MicrophoneMuteStrategy.None:
                default:
                    break;
            }

            UpdateSpeechListenerSuspension(mute, force || stateChanged);

            if (mute)
            {
                ResetListeningVoiceDetectionState();
            }

            if (!mute && MicrophoneMuteBy != MicrophoneMuteStrategy.Threshold && micForReading != null)
            {
                defaultNoiseGateThresholdDb = micForReading.NoiseGateThresholdDb;
            }
        }

        private void ResetListeningVoiceDetectionState()
        {
            listeningPrevRecording = false;
            listeningRecStartedAt = -1f;
            listeningRecTotalAccum = 0f;
            listeningRecLoudAccum = 0f;
            listeningNodPending = false;
            listeningNodTriggeredForThisRec = false;
            listeningNodLastFiredAt = -1f;
        }

        private void UpdateSpeechListenerSuspension(bool mute, bool allowForce)
        {
            if (SpeechListener == null) return;

            if (mute)
            {
                if (!speechListenerSuspendedByAvatar || allowForce)
                {
                    SpeechListener.StopListening();
                    speechListenerSuspendedByAvatar = true;
                }
            }
            else
            {
                if (speechListenerSuspendedByAvatar || allowForce)
                {
                    SpeechListener.StartListening(true);
                    speechListenerSuspendedByAvatar = false;
                }
            }
        }

        private async UniTask OnErrorAsyncDefault(string text, Dictionary<string, object> payloads, Exception ex, CancellationToken token)
        {
            var errorAnimatedVoiceRequest = new AnimatedVoiceRequest();

            if (!string.IsNullOrEmpty(ErrorVoice))
            {
                errorAnimatedVoiceRequest.AddVoice(ErrorVoice);
            }
            if (!string.IsNullOrEmpty(ErrorFace))
            {
                errorAnimatedVoiceRequest.AddFace(ErrorFace, 5.0f);
            }
            if (!string.IsNullOrEmpty(ErrorAnimationParamKey))
            {
                errorAnimatedVoiceRequest.AddAnimation(ErrorAnimationParamKey, ErrorAnimationParamValue, 5.0f);
            }

            await ModelController.AnimatedSay(errorAnimatedVoiceRequest, token);

            ApplyMicrophoneMuteState(false);
        }

        private void OnSpeechListenerTranscriptionStarted()
        {
            UniTask.Void(async () =>
            {
                await UniTask.SwitchToMainThread();
                EnsureProcessingPresentationForCurrentRecording();
            });
        }

        private async UniTask OnSpeechListenerRecognized(string text)
        {
            if (string.IsNullOrEmpty(text) || Mode == AvatarMode.Disabled) return;

            var isListeningMode = Mode == AvatarMode.Listening;
            var isWakeListeningMode = Mode == AvatarMode.Idle || Mode == AvatarMode.Sleep;

            if (!isListeningMode && !isWakeListeningMode)
            {
                // Ignore recognition during Conversation/Sleep transitions
                return;
            }

            if (isWakeListeningMode)
            {
                var wakeWord = ExtractWakeWord(text);
                if (!string.IsNullOrEmpty(wakeWord))
                {
                    if (OnWakeAsync != null)
                    {
                        await OnWakeAsync(text);
                    }
                    var payloads = GetPayloads?.Invoke();
                    if (payloads == null || payloads.Count == 0)
                    {
                        payloads = new Dictionary<string, object>();
                    }
                    payloads["IsWakeword"] = true;
                    EnsureProcessingPresentationForCurrentRecording();
                    _ = DialogProcessor.StartDialogAsync(text, payloads: payloads);
                }
                return;
            }

            // Listening mode only beyond this point

            if (enablePostSpeechRecognitionGuard)
            {
                var elapsed = Time.time - lastCharacterSpeakingEndedAt;
                if (elapsed >= 0f && elapsed < postSpeechRecognitionGuardSec)
                {
                    return;
                }
            }

            bool characterSpeaking = ModelController != null && ModelController.AudioSource != null && ModelController.AudioSource.isPlaying;

            var cancelWord = ExtractCancelWord(text);
            var exitWord = ExtractExitWord(text);
            var interruptWord = ExtractInterruptWord(text);

            if (characterSpeaking && string.IsNullOrEmpty(cancelWord) && string.IsNullOrEmpty(exitWord) && string.IsNullOrEmpty(interruptWord))
            {
                return;
            }

            listeningMessageRecognizedInRecording = true;

            float transcriptShownAt = Time.unscaledTime;
            bool transcriptDisplayed = false;

            if (DialogProcessor.Status == DialogProcessor.DialogStatus.Idling && Mode == AvatarMode.Listening)
            {
                SetListeningMessage(ListeningVoiceMessageState.InputReceived, true);
                if (UserMessageWindow != null)
                {
                    listeningMessageSuppressedUntilNextRecording = true;

                    var statusMin = Mathf.Max(0f, listeningStatusMinDisplaySeconds);
                    if (statusMin > 0f)
                    {
                        var elapsedStatus = Time.unscaledTime - listeningMessageStateChangedAt;
                        var waitStatus = statusMin - elapsedStatus;
                        if (waitStatus > 0f)
                        {
                            await UniTask.Delay(TimeSpan.FromSeconds(waitStatus), cancellationToken: CancellationToken.None);
                        }
                    }

                    await UserMessageWindow.ShowMessageAsync(text, CancellationToken.None);
                    transcriptShownAt = Time.unscaledTime;
                    transcriptDisplayed = true;
                }
            }

            if (!string.IsNullOrEmpty(cancelWord))
            {
                await StopChatAsync();
                return;
            }

            if (!string.IsNullOrEmpty(exitWord))
            {
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
                return;
            }

            if (!string.IsNullOrEmpty(interruptWord))
            {
                await StopChatAsync(continueDialog: true);
                return;
            }

            if (transcriptDisplayed)
            {
                var transcriptMin = Mathf.Max(0f, listeningTranscriptMinDisplaySeconds);
                if (transcriptMin > 0f)
                {
                    var elapsedTranscript = Time.unscaledTime - transcriptShownAt;
                    var waitTranscript = transcriptMin - elapsedTranscript;
                    if (waitTranscript > 0f)
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(waitTranscript), cancellationToken: CancellationToken.None);
                    }
                }
            }

            if (Mode != AvatarMode.Conversation)
            {
                Mode = AvatarMode.Conversation;
            }
            modeTimer = conversationTimeout;
            speakingBaseAppliedThisTurn = false;
            ApplyMicrophoneMuteState(true);
            EnsureProcessingPresentationForCurrentRecording();

            // Conversation request while listening
            fillerCts?.Cancel();
            fillerCts = new CancellationTokenSource();

            var dialogTask = DialogProcessor.StartDialogAsync(text, payloads: GetPayloads?.Invoke()).AttachExternalCancellation(fillerCts.Token);
            var dialogTaskWithClip = dialogTask.ContinueWith(() => (AudioClip)null);

            if (ModelController != null && ModelController.fillerVoicePlayer != null)
            {
                await ModelController.fillerVoicePlayer.PlayWhileWaitingAsync(dialogTaskWithClip, fillerCts.Token);
            }
            else
            {
                await dialogTask;
            }
        }

        public void SetPushToTalkActive(bool active)
        {
            if (pushToTalkActive == active)
            {
                return;
            }

            pushToTalkActive = active;

            if (listeningMessageSuppressedUntilNextRecording)
            {
                return;
            }

            if (Mode == AvatarMode.Listening
                && DialogProcessor.Status == DialogProcessor.DialogStatus.Idling
                && listeningVoiceMessageState != ListeningVoiceMessageState.None)
            {
                ApplyListeningMessageDisplay(listeningVoiceMessageState);
            }
        }

        public void ChangeSpeechListener(ISpeechListener speechListener)
        {
            SpeechListener.StopListening();
            SpeechListener = speechListener;
            SpeechListener.OnRecognized = OnSpeechListenerRecognized;
            SpeechListener.ChangeSessionConfig(
                silenceDurationThreshold: idleSilenceDurationThreshold,
                minRecordingDuration: idleMinRecordingDuration,
                maxRecordingDuration: idleMaxRecordingDuration
            );

            // Refresh microphone references based on the new listener
            if (speechListener is Component listenerComponent)
            {
                var attachedMic = listenerComponent.GetComponent<SpeechListener.MicrophoneManager>();
                if (attachedMic != null)
                {
                    micForReading = attachedMic;
                    microphoneControl = attachedMic;
                }
            }

            if (micForReading == null)
            {
                micForReading = FindFirstObjectByType<SpeechListener.MicrophoneManager>();
                microphoneControl = (IMicrophoneManager)(micForReading ?? MicrophoneManager);
            }

            if (micForReading != null)
            {
                if (micForReading.OutputAudioSource == null)
                {
                    micForReading.OutputAudioSource = (ModelController != null && ModelController.AudioSource != null)
                        ? ModelController.AudioSource
                        : GetComponent<AudioSource>();
                }
                micForReading.AutoMuteWhileAudioPlaying = true;
                defaultNoiseGateThresholdDb = micForReading.NoiseGateThresholdDb;
            }

            // Re-apply current mute state to the refreshed microphone wiring
            var wasMuted = microphoneMutedByAvatar;
            speechListenerSuspendedByAvatar = false;
            ApplyMicrophoneMuteState(wasMuted, force: true);
        }

        public class ProcessingPresentation
        {
            public List<Model.Animation> Animations { get; set; } = new List<Model.Animation>();
            public List<FaceExpression> Faces { get; set; }
        }

        [Serializable]
        public class WordWithAllowance
        {
            public string Text;
            public int PrefixAllowance = 4;
            public int SuffixAllowance = 4;
        }
    }
}
