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

        [Header("ChatdollKit components")]
        public ModelController ModelController;
        public DialogProcessor DialogProcessor;
        public LLMContentProcessor LLMContentProcessor;
        public IMicrophoneManager MicrophoneManager;
        public ISpeechListener SpeechListener;
        public MessageWindowBase UserMessageWindow;
        public MessageWindowBase CharacterMessageWindow;
 
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

        private void Awake()
        {
            // Get ChatdollKit components
            MicrophoneManager = MicrophoneManager ?? gameObject.GetComponent<IMicrophoneManager>();
            ModelController = ModelController ?? gameObject.GetComponent<ModelController>();
            DialogProcessor = DialogProcessor ?? gameObject.GetComponent<DialogProcessor>();
            LLMContentProcessor = LLMContentProcessor ?? gameObject.GetComponent<LLMContentProcessor>();
            SpeechListener = gameObject.GetComponent<ISpeechListener>();

            // Setup MicrophoneManager
            MicrophoneManager.SetNoiseGateThresholdDb(VoiceRecognitionThresholdDB);

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
                        UserMessageWindow?.Show("聞いています");
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

                // Ensure a base speaking pose when no [anim:] tag is provided
                // Constructed by Animator parameter key/value (numeric), not by registered name
                if (ModelController != null && !string.IsNullOrEmpty(speakingBaseParamKey))
                {
                    foreach (var frame in avreq.AnimatedVoices)
                    {
                        if (frame.Animations == null || frame.Animations.Count == 0)
                        {
                            frame.AddAnimation(
                                speakingBaseParamKey,
                                speakingBaseParamValue,
                                speakingBaseDuration,
                                null,
                                null
                            );
                        }
                    }
                }
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

            // User message window (Listening... previous style)
            if (DialogProcessor.Status == DialogProcessor.DialogStatus.Idling)
            {
                if (Mode == AvatarMode.Listening)
                {
                    // Entered Listening or returned to Idling -> show label
                    if (Mode != previousMode || DialogProcessor.Status != previousDialogStatus)
                    {
                        UserMessageWindow?.Show("聞いています");
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

            // Speech listener config
            if (Mode != previousMode)
            {
                // Start/Stop listening base pose
                if (Mode == AvatarMode.Listening)
                {
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

            // Cancel Word
            else if (!string.IsNullOrEmpty(ExtractCancelWord(text)))
            {
                await StopChatAsync();
            }

            // --- new_AIAvatar.txt 追加: ExitWord対応 ---
            // Exit Word
            else if (!string.IsNullOrEmpty(ExtractExitWord(text)))
            {
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }
            // --- ここまで ---

            // Interupt Word
            else if (!string.IsNullOrEmpty(ExtractInterruptWord(text)))
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
