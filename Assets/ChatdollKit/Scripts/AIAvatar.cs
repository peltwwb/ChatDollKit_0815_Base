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
        private bool listeningPrevRecording = false;
        private float listeningRecStartedAt = -1f;
        private float listeningRecTotalAccum = 0f;
        private float listeningRecLoudAccum = 0f;
        private bool listeningNodPending = false;
        private bool listeningNodTriggeredForThisRec = false;
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

        // Silero VAD via reflection (no hard assembly dependency)
        private Component sileroVADComponent;
        private System.Type sileroVADType;
        private System.Reflection.MethodInfo sileroInitializeMethod;
        private System.Reflection.MethodInfo sileroSetSourceSampleRateMethod;
        private System.Reflection.MethodInfo sileroIsVoicedMethod;

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
        [Header("Listening Silent Blink")]
        [SerializeField]
        private string listeningSilentBlinkFaceName = "Blink";
        [SerializeField]
        private float listeningSilentBlinkOnsetDelay = 0.7f; // seconds of continuous silence before closing eyes
        [SerializeField]
        private float listeningSilentBlinkMaxDuration = 1.5f; // maximum eyes-closed duration per silent segment
        [SerializeField]
        private float listeningSilentInitialNoBlinkWindow = 5.0f; // do not Blink in first N sec after entering Listening
        private bool appliedListeningBlink = false;
        private bool detectedVoiceSinceListeningStart = false;
        private float listeningSilentStartedAt = -1f;
        private float listeningEnteredAt = -100f;
 
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

            // Setup MicrophoneManager
            MicrophoneManager.SetNoiseGateThresholdDb(VoiceRecognitionThresholdDB);

            // Ensure self-capture protection: route the avatar's AudioSource to the mic manager
            // so it can auto-mute mic while the character is speaking.
            if (MicrophoneManager is SpeechListener.MicrophoneManager mm)
            {
                if (mm.OutputAudioSource == null)
                {
                    mm.OutputAudioSource = (ModelController != null && ModelController.AudioSource != null)
                        ? ModelController.AudioSource
                        : GetComponent<AudioSource>();
                }
                // Keep auto-mute enabled by default to avoid picking up own TTS
                mm.AutoMuteWhileAudioPlaying = true;
            }

            // Resolve Silero VAD (by name to avoid asmdef dependency)
            sileroVADComponent = GetComponent("SileroVADProcessor") as Component;
            if (sileroVADComponent == null)
            {
                // Search in scene
                foreach (var c in FindObjectsOfType<Component>())
                {
                    if (c != null && c.GetType().Name == "SileroVADProcessor")
                    {
                        sileroVADComponent = c;
                        break;
                    }
                }
            }
            if (sileroVADComponent != null)
            {
                sileroVADType = sileroVADComponent.GetType();
                sileroInitializeMethod = sileroVADType.GetMethod("Initialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                sileroSetSourceSampleRateMethod = sileroVADType.GetMethod("SetSourceSampleRate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                sileroIsVoicedMethod = sileroVADType.GetMethod("IsVoiced", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            }

            // Setup ModelController
            ModelController.OnSayStart = async (voice, token) =>
            {
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
                    var baseAnim = new Model.Animation(
                        speakingBaseParamKey,
                        speakingBaseParamValue,
                        speakingBaseDuration
                    );
                    ModelController.Animate(new List<Model.Animation> { baseAnim });
                    speakingBaseAppliedThisTurn = true;
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
                Mode = AvatarMode.Conversation;
                modeTimer = conversationTimeout;

                // 新しいターンの開始時に、発話ベース姿勢の適用フラグをリセット
                speakingBaseAppliedThisTurn = false;

                // Control microphone at first before AI's speech
                if (MicrophoneMuteBy == MicrophoneMuteStrategy.StopDevice)
                {
                    MicrophoneManager.StopMicrophone();
                }
                else if (MicrophoneMuteBy == MicrophoneMuteStrategy.StopListener)
                {
                    SpeechListener.StopListening();
                }
                else if (MicrophoneMuteBy == MicrophoneMuteStrategy.Mute)
                {
                    MicrophoneManager.MuteMicrophone(true);
                }
                else if (MicrophoneMuteBy == MicrophoneMuteStrategy.Threshold)
                {
                    MicrophoneManager.SetNoiseGateThresholdDb(VoiceRecognitionRaisedThresholdDB);
                }

                // Processing開始中はIdleフォールバックを必ず抑止
                ModelController.SuppressIdleFallback(true);

                // Presentation
                if (ProcessingPresentations.Count > 0)
                {
                    var animAndFace = ProcessingPresentations[UnityEngine.Random.Range(0, ProcessingPresentations.Count)];
                    ModelController.StopIdling();
                    ModelController.Animate(animAndFace.Animations);
                    ModelController.SetFace(animAndFace.Faces);
                }

                // Show user message
                if (UserMessageWindow != null && !string.IsNullOrEmpty(text))
                {
                    if (!showMessageWindowOnWake && payloads != null && payloads.ContainsKey("IsWakeword") && (bool)payloads["IsWakeword"])
                    {
                        // Don't show message window on wakeword
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
                if (MicrophoneMuteBy == MicrophoneMuteStrategy.StopDevice)
                {
                    MicrophoneManager.StartMicrophone();
                }
                else if (MicrophoneMuteBy == MicrophoneMuteStrategy.StopListener)
                {
                    SpeechListener.StartListening();
                }
                else if (MicrophoneMuteBy == MicrophoneMuteStrategy.Mute)
                {
                    MicrophoneManager.MuteMicrophone(false);
                }
                else if (MicrophoneMuteBy == MicrophoneMuteStrategy.Threshold)
                {
                    MicrophoneManager.SetNoiseGateThresholdDb(VoiceRecognitionThresholdDB);
                }

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
                    }
                    else
                    {
                        // Normal turn end: go to Listening and wait for next input
                        Mode = AvatarMode.Listening;
                        modeTimer = idleTimeout;
                        // Do not switch idling mode here to avoid double control with Main.cs
                        UserMessageWindow?.Show("【聞いています】");
                        // Listening開始時にModelController側で抑止解除される
                    }
                }
            };

            DialogProcessor.OnStopAsync = async (forSuccessiveDialog) =>
            {
                // Stop speaking immediately
                ModelController.StopSpeech();

                // Return to Idle when no successive dialogs are allocated
                if (!forSuccessiveDialog)
                {
                    Mode = AvatarMode.Idle;
                    modeTimer = idleTimeout;
                    ModelController.StartIdling();
                    ModelController.SuppressIdleFallback(false); // Idleへ戻るので抑止解除
                    UserMessageWindow?.Hide();
                    await ModelController.ChangeIdlingModeAsync("normal");
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
            SpeechListener.OnRecognized = OnSpeechListenerRecognized;
            SpeechListener.ChangeSessionConfig(
                silenceDurationThreshold: idleSilenceDurationThreshold,
                minRecordingDuration: idleMinRecordingDuration,
                maxRecordingDuration: idleMaxRecordingDuration
            );

            // Plug Silero VAD into speech detection if available
            if (SpeechListener is SpeechListenerBase slBase && sileroVADComponent != null && sileroIsVoicedMethod != null)
            {
                try
                {
                    if (sileroInitializeMethod != null)
                    {
                        sileroInitializeMethod.Invoke(sileroVADComponent, null);
                    }
                    if (micForReading != null && sileroSetSourceSampleRateMethod != null)
                    {
                        sileroSetSourceSampleRateMethod.Invoke(sileroVADComponent, new object[] { micForReading.SampleRate });
                    }
                }
                catch { /* ignore */ }

                // Build delegate that forwards to SileroVADProcessor.IsVoiced(samples, lin)
                slBase.DetectVoiceFunctions = new List<Func<float[], float, bool>>()
                {
                    (samples, lin) =>
                    {
                        try
                        {
                            var res = sileroIsVoicedMethod.Invoke(sileroVADComponent, new object[] { samples, lin });
                            return res is bool b && b;
                        }
                        catch { return false; }
                    }
                };
            }

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
            // Only start using Blink after the first voice detection since entering Listening
            if (Mode == AvatarMode.Listening && voiceDetectedNow)
            {
                detectedVoiceSinceListeningStart = true;
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

            // User message window (Listening... previous style)
            if (DialogProcessor.Status == DialogProcessor.DialogStatus.Idling)
            {
                if (Mode == AvatarMode.Listening)
                {
                    // Entered Listening or returned to Idling -> show label
                    if (Mode != previousMode || DialogProcessor.Status != previousDialogStatus)
                    {
                        UserMessageWindow?.Show("【聞いています】");
                    }
                }
                else
                {
                    // Left Listening -> hide label
                    if (previousMode == AvatarMode.Listening)
                    {
                        UserMessageWindow?.Hide();
                    }
                }
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
                        // Prepare start-timing nod if configured
                        listeningNodPending = (listeningNodTiming == ListeningNodTiming.OnVoiceStart);
                    }

                    // Accumulate durations while recording
                    var dt = Time.deltaTime;
                    listeningRecTotalAccum += dt;
                    // Accumulate loud duration only when voice is actually detected (by VAD/volume)
                    if (voiceDetectedNow)
                    {
                        listeningRecLoudAccum += dt;
                    }

                    // If nod is pending for start timing, and delay has passed, fire once
                    if (listeningNodPending
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
                    if (listeningNodTiming == ListeningNodTiming.OnVoiceEnd
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
                }

                listeningPrevRecording = recNow;
            }

            // Speech listener config
            if (Mode != previousMode)
            {
                // Reset Blink gating when entering Listening
                if (Mode == AvatarMode.Listening)
                {
                    // Start each Listening with eyes open (no Blink)
                    detectedVoiceSinceListeningStart = false;
                    appliedListeningBlink = false;
                    IsListeningSilent = false;
                    listeningSilentStartedAt = -1f;
                    listeningEnteredAt = Time.time;
                    ModelController?.SetFace(new List<FaceExpression>() { new FaceExpression("Neutral", 0.0f, string.Empty) });

                    // Reset nod detection accumulators per listening session
                    // Always start from non-recording for nod detection to avoid false end triggers
                    listeningPrevRecording = false;
                    listeningRecStartedAt = -1f;
                    listeningRecTotalAccum = 0f;
                    listeningRecLoudAccum = 0f;
                    listeningNodPending = false;
                    listeningNodTriggeredForThisRec = false;
                }
                // Start/Stop listening base pose
                if (Mode == AvatarMode.Listening)
                {
                    // Ensure idle fallback is suppressed while in Listening
                    ModelController?.SuppressIdleFallback(true);
                    if (ModelController != null && !string.IsNullOrEmpty(listeningBaseParamKey))
                    {
                        var idleAnim = new Model.Animation(listeningBaseParamKey, listeningBaseParamValue, listeningBaseDuration);
                        ModelController.StartListeningIdle(idleAnim);
                    }
                }
                else if (previousMode == AvatarMode.Listening)
                {
                    ModelController?.StopListeningIdle();
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
                // After idle timer, go to Sleep
                Mode = AvatarMode.Sleep;
                modeTimer = 0.0f;
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
        }

        public void AddProcessingPresentaion(List<Model.Animation> animations, List<FaceExpression> faces)
        {
            ProcessingPresentations.Add(new ProcessingPresentation()
            {
                Animations = animations,
                Faces = faces
            });
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
        }

        private async UniTask OnSpeechListenerRecognized(string text)
        {
            if (string.IsNullOrEmpty(text) || Mode == AvatarMode.Disabled) return;

            // Detect speaking state of the character (avoid echo loops)
            bool characterSpeaking = ModelController != null && ModelController.AudioSource != null && ModelController.AudioSource.isPlaying;

            // Pre-extract control words once
            var cancelWord = ExtractCancelWord(text);
            var exitWord = ExtractExitWord(text);
            var interruptWord = ExtractInterruptWord(text);

            // If the character is currently speaking, ignore non-control utterances to avoid feedback loops
            if (characterSpeaking && string.IsNullOrEmpty(cancelWord) && string.IsNullOrEmpty(exitWord) && string.IsNullOrEmpty(interruptWord))
            {
                return;
            }

            // Cancel Word
            if (!string.IsNullOrEmpty(cancelWord))
            {
                await StopChatAsync();
            }

            // --- new_AIAvatar.txt 追加: ExitWord対応 ---
            // Exit Word
            else if (!string.IsNullOrEmpty(exitWord))
            {
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }
            // --- ここまで ---

            // Interupt Word
            else if (!string.IsNullOrEmpty(interruptWord))
            {
                await StopChatAsync(continueDialog: true);
            }

            // Conversation request (Priority is higher than wake word)
            else if (Mode >= AvatarMode.Conversation)
            {
                // --- new_AIAvatar.txt 追加: フィラー音声再生 ---
                fillerCts?.Cancel(); // 前回のフィラー再生を必ず止める
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
                // --- ここまで ---
            }

            // Wake Word
            else if (!string.IsNullOrEmpty(ExtractWakeWord(text)))
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
                _ = DialogProcessor.StartDialogAsync(text, payloads: payloads);
            }
        }

        public void ChangeSpeechListener(ISpeechListener speechListener)
        {
            SpeechListener.StopListening();
            SpeechListener = speechListener;
            SpeechListener.OnRecognized = OnSpeechListenerRecognized;

            // Rewire VAD detection to new listener if possible
            if (SpeechListener is SpeechListenerBase slBase && sileroVADComponent != null && sileroIsVoicedMethod != null)
            {
                slBase.DetectVoiceFunctions = new List<Func<float[], float, bool>>()
                {
                    (samples, lin) =>
                    {
                        try
                        {
                            var res = sileroIsVoicedMethod.Invoke(sileroVADComponent, new object[] { samples, lin });
                            return res is bool b && b;
                        }
                        catch { return false; }
                    }
                };
            }
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
