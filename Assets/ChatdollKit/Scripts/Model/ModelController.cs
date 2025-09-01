using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
// --- new_ModelController.txt 追加: FillerVoicePlayer用 ---
using ChatdollKit.Filler;
// --- ここまで ---

namespace ChatdollKit.Model
{
    public enum VoicePrefetchMode
    {
        Parallel,
        Sequential,
        Disabled
    }

    public class ModelController : MonoBehaviour
    {
        // Avator
        [Header("Avatar")]
        public GameObject AvatarModel;

        // Audio
        [Header("Voice")]
        public AudioSource AudioSource;
        public Func<string, Dictionary<string, object>, CancellationToken, UniTask<AudioClip>> SpeechSynthesizerFunc;
        public Action<Voice, CancellationToken> OnSayStart;
        public Action OnSayEnd;
        public VoicePrefetchMode VoicePrefetchMode = VoicePrefetchMode.Parallel;
        private ConcurrentQueue<Voice> voicePrefetchQueue = new ConcurrentQueue<Voice>();
        private CancellationTokenSource voicePrefetchCancellationTokenSource;
        private ILipSyncHelper lipSyncHelper;
        public Action<float[]> HandlePlayingSamples;

        // Prevent self-capture: mute mic while speaking
        [Header("Mic Gating")]
        [SerializeField]
        private bool MuteMicWhileSpeaking = true;
        private ChatdollKit.SpeechListener.MicrophoneManager micManager;
        #if UNITY_WEBGL && !UNITY_EDITOR
        private ChatdollKit.Extension.SileroVAD.SileroVADProcessor sileroVAD;
        #endif

        // --- new_ModelController.txt 追加: FillerVoicePlayer関連 ---
        [Header("Filler Voice")]
        [SerializeField]
        public FillerVoicePlayer fillerVoicePlayer;
        public Action CancelFillerAction;
        // --- ここまで ---

        // Animation
        [Header("Animation")]
        [SerializeField]
        private float IdleAnimationDefaultDuration = 10.0f;
        [SerializeField]
        private string IdleAnimationKey = "BaseParam";
        [SerializeField]
        private int IdleAnimationValue;
        [SerializeField]
        private string layeredAnimationDefaultState = "Default";
        public float AnimationFadeLength = 0.5f;
        private Animator animator;
        private List<Animation> animationQueue = new List<Animation>();
        private float animationStartAt { get; set; }
        private Animation currentAnimation { get; set; }
        private Dictionary<string, List<Animation>> idleAnimations = new Dictionary<string, List<Animation>>();
        private Dictionary<string, List<int>> idleWeightedIndexes = new Dictionary<string, List<int>>();
        private Dictionary<string, FaceExpression> idleFaces = new Dictionary<string, FaceExpression>();
        public string IdlingMode { get; private set; } = "normal";
        public DateTime IdlingModeStartAt { get; private set; } = DateTime.UtcNow;
        private Func<Animation> GetAnimation;
        private Dictionary<string, Animation> registeredAnimations { get; set; } = new Dictionary<string, Animation>();

        // Face Expression
        [Header("Face")]
        public SkinnedMeshRenderer SkinnedMeshRenderer;
        [SerializeField]
        private float defaultFaceExpressionDuration = 7.0f;
        private IFaceExpressionProxy faceExpressionProxy;
        private List<FaceExpression> faceQueue = new List<FaceExpression>();
        private float faceStartAt { get; set; }
        private FaceExpression currentFace { get; set; }
        private IBlink blinker { get; set; }

        // History recorder for debug and test
        public ActionHistoryRecorder History;

        private void Awake()
        {
            GetAnimation = GetIdleAnimation;

            // LipSyncHelper
            lipSyncHelper = gameObject.GetComponent<ILipSyncHelper>();

            // Face expression proxy
            faceExpressionProxy = gameObject.GetComponent<IFaceExpressionProxy>();

            // Blinker
            blinker = gameObject.GetComponent<IBlink>();

            // --- new_ModelController.txt 追加: FillerVoicePlayer自動セット ---
            if (fillerVoicePlayer == null)
            {
                fillerVoicePlayer = GetComponent<FillerVoicePlayer>();
                if (fillerVoicePlayer == null)
                {
                    fillerVoicePlayer = FindObjectOfType<FillerVoicePlayer>();
                }
            }
            // --- ここまで ---

            if (AvatarModel == null)
            {
                enabled = false;
            }
            else
            {
                ActivateAvatar();
            }

            // Cache microphone manager if attached on the same object
            micManager = gameObject.GetComponent<ChatdollKit.SpeechListener.MicrophoneManager>();
            #if UNITY_WEBGL && !UNITY_EDITOR
            sileroVAD = gameObject.GetComponent<ChatdollKit.Extension.SileroVAD.SileroVADProcessor>();
            #endif
        }

        private void Start()
        {
            if (idleAnimations.Count == 0)
            {
                // Set idle animation if not registered
                Debug.LogWarning("Idle animations are not registered. Temprarily use dafault state as idle animation.");
                AddIdleAnimation(new Animation(
                    IdleAnimationKey, IdleAnimationValue, IdleAnimationDefaultDuration
                ));
            }

            // NOTE: Do not start idling here to prevent overwrite the animation that user invokes at Start() in other module.
            // Don't worry, idling will be started at UpdateAnimation() if user doesn't invoke their custom animation.

            StartVoicePrefetchTask().Forget();
        }

        private void Update()
        {
            UpdateAnimation();
            UpdateFace();
        }

        private void LateUpdate()
        {
            // Move to avatar position (because this game object includes AudioSource)
            gameObject.transform.position = AvatarModel.transform.position;
        }

        private void OnDestroy()
        {
            voicePrefetchCancellationTokenSource?.Cancel();
            voicePrefetchCancellationTokenSource?.Dispose();
        }

#region Idling
        // Start idling
        public void StartIdling(bool resetStartTime = true)
        {
            GetAnimation = GetIdleAnimation;
            if (resetStartTime)
            {
                animationStartAt = 0;
            }
        }

        // Stop idling
        public void StopIdling()
        {
            GetAnimation = GetQueuedAnimation;
        }

        // Register idling animation
        public void AddIdleAnimation(Animation animation, int weight = 1, string mode = "normal")
        {
            if (!idleAnimations.ContainsKey(mode))
            {
                idleAnimations.Add(mode, new List<Animation>());
                idleWeightedIndexes.Add(mode, new List<int>());
            }

            idleAnimations[mode].Add(animation);

            // Set weight
            var index = idleAnimations[mode].Count - 1;
            for (var i = 0; i < weight; i++)
            {
                idleWeightedIndexes[mode].Add(index);
            }
        }

        public void AddIdleAnimation(string name, float duration, int weight = 1, string mode = "normal")
        {
            AddIdleAnimation(GetRegisteredAnimation(name, duration), weight, mode);
        }

        // Register idling face expression for mode
        public void AddIdleFace(string mode, string name)
        {
            idleFaces.Add(mode, new FaceExpression(name, float.MaxValue));
        }

        // Change idling mode
        public async UniTask ChangeIdlingModeAsync(string mode = "normal", Func<UniTask> onBeforeChange = null)
        {
            IdlingMode = mode;

            if (onBeforeChange != null)
            {
                await onBeforeChange();
            }

            if (idleFaces.ContainsKey(mode))
            {
                SetFace(new List<FaceExpression>() { idleFaces[mode] });
            }
            else if (mode == "normal")
            {
                SetFace(new List<FaceExpression>() { new FaceExpression("Neutral", 0.0f, string.Empty) });
            }
            IdlingModeStartAt = DateTime.UtcNow;

            StartIdling(true);
        }
#endregion

        // Speak with animation and face expression
        public async UniTask AnimatedSay(AnimatedVoiceRequest request, CancellationToken token)
        {
            // Speak, animate and express face sequentially
            foreach (var animatedVoice in request.AnimatedVoices)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                // Animation
                if (animatedVoice.Animations.Count > 0)
                {
                    Animate(animatedVoice.Animations);
                }

                // Face
                if (animatedVoice.Faces != null && animatedVoice.Faces.Count > 0)
                {
                    SetFace(animatedVoice.Faces);
                }

                // Speech
                if (animatedVoice.Voices.Count > 0)
                {
                    // Wait for the requested voices end
                    await Say(animatedVoice.Voices, token);
                }
            }

            if (request.StartIdlingOnEnd && !token.IsCancellationRequested)
            {
                // Stop running animation not to keep the current state to the next turn (prompting)
                // Do not start idling when cancel requested because the next animated voice request may include animations
                StartIdling();
            }
        }

        public AnimatedVoiceRequest ToAnimatedVoiceRequest(string inputText, string language = null)
        {
            var avreq = new AnimatedVoiceRequest();
            var preGap = 0f;
            var ttsConfig = new TTSConfiguration();
            if (!string.IsNullOrEmpty(language))
            {
                ttsConfig.Params["language"] = language;
            }

            var pattern = @"(\[.*?\])|([^[]+)";
            foreach (Match match in Regex.Matches(inputText, pattern))
            {
                var parsedText = match.Value.Trim();

                if (parsedText.StartsWith("[face:"))
                {
                    var face = parsedText.Substring(6, parsedText.Length - 7);
                    avreq.AddFace(face, duration: defaultFaceExpressionDuration);
                    ttsConfig.Params["style"] = face;
                }
                else if (parsedText.StartsWith("[anim:"))
                {
                    var anim = parsedText.Substring(6, parsedText.Length - 7);
                    if (IsAnimationRegistered(anim))
                    {
                        var a = GetRegisteredAnimation(anim);
                        avreq.AddAnimation(a.ParameterKey, a.ParameterValue, a.Duration, a.LayeredAnimationName, a.LayeredAnimationLayerName);
                    }
                    else
                    {
                        Debug.LogWarning($"Animation {anim} is not registered.");
                    }
                }
                else if (parsedText.StartsWith("[pause:"))
                {
                    var pauseValue = parsedText.Substring(7, parsedText.Length - 8);
                    if (float.TryParse(pauseValue, out float gap))
                    {
                        preGap = gap;
                    }
                }
                else if (parsedText.StartsWith("["))
                {
                    continue;
                }
                else
                {
                    avreq.AddVoice(parsedText, preGap, parsedText.EndsWith("。") ? 0 : 0.3f, ttsConfig: ttsConfig);
                    // Reset preGap. Do not reset ttsConfig to continue the style of voice.
                    preGap = 0f;
                }
            }

            return avreq;
        }

#region Speech
        // Speak
        public async UniTask Say(List<Voice> voices, CancellationToken token)
        {
            // Stop speech
            StopSpeech();

            // Prefetch Web/TTS voice
            if (VoicePrefetchMode != VoicePrefetchMode.Disabled)
            {
                PrefetchVoices(voices, token);
            }

            // Speak sequentially
            isSpeaking = true;

            // Optional: Mute microphone while speaking to avoid capturing own voice
            var shouldGateMic = MuteMicWhileSpeaking && micManager != null;
            if (shouldGateMic)
            {
                micManager.MuteMicrophone(true);
                #if UNITY_WEBGL && !UNITY_EDITOR
                if (sileroVAD != null)
                {
                    sileroVAD.Mute(true);
                }
                #endif
            }
            try
            {
                foreach (var v in voices)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    OnSayStart?.Invoke(v, token);

                    try
                    {
                        // --- new_ModelController.txt 追加: フィラー音声キャンセル ---
                        CancelFillerAction?.Invoke();
                        // --- ここまで ---

                        // --- new_ModelController.txt 追加: フィラー音声再生 ---
                        if (fillerVoicePlayer != null)
                        {
                            fillerVoicePlayer.SetMainVoicePlaying(true);
                            await fillerVoicePlayer.PlayWhileWaitingAsync(
                                SpeechSynthesizerFunc(v.Text, v.TTSConfig?.Params ?? new Dictionary<string, object>(), token),
                                token
                            );
                        }
                        // --- ここまで ---

                        // Download voice from web or TTS service
                        var downloadStartTime = Time.time;
                        AudioClip clip = null;

                        var parameters = v.TTSConfig != null ? v.TTSConfig.Params : new Dictionary<string, object>();
                        clip = await SpeechSynthesizerFunc(v.Text, parameters, token);

                        if (clip != null)
                        {
                            // Wait for PreGap remains after download
                            var preGap = v.PreGap - (Time.time - downloadStartTime);
                            if (preGap > 0)
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        await UniTask.Delay((int)(preGap * 1000), cancellationToken: token);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // OperationCanceledException raises
                                        Debug.Log("Task canceled in waiting PreGap");
                                    }
                                }
                            }
                            History?.Add(v);

                            if (HandlePlayingSamples != null)
                            {
                                // Wait while voice playing with processing LipSync
                                var startTime = Time.realtimeSinceStartup;
                                var bufferSize = clip.channels == 2 ? 2048 : 1024;  // Optimized for 44100Hz / 30FPS
                                var sampleBuffer = new float[bufferSize];
                                var nextPosition = 0;
                                var samples = new float[clip.samples * clip.channels];

                                if (!clip.GetData(samples, 0))
                                {
                                    Debug.LogWarning("Failed to get audio data from clip");
                                }
                                else
                                {
                                    // Play audio
                                    AudioSource.PlayOneShot(clip);

                                    // Process samples by estimating current playing position by time
                                    while (Time.realtimeSinceStartup - startTime < clip.length && !token.IsCancellationRequested)
                                    {
                                        var elapsedTime = Time.realtimeSinceStartup - startTime;
                                        var currentPosition = Mathf.FloorToInt(elapsedTime * clip.frequency) * clip.channels;

                                        while (nextPosition + bufferSize <= currentPosition &&
                                            nextPosition + bufferSize <= samples.Length)
                                        {
                                            System.Array.Copy(samples, nextPosition, sampleBuffer, 0, bufferSize);
                                            HandlePlayingSamples(sampleBuffer);
                                            nextPosition += bufferSize;
                                        }

                                        await UniTask.Delay(33, cancellationToken: token);  // 30FPS
                                    }

                                    // Remaining samples
                                    if (nextPosition < samples.Length)
                                    {
                                        var remaining = samples.Length - nextPosition;
                                        var lastBuffer = new float[remaining];
                                        System.Array.Copy(samples, nextPosition, lastBuffer, 0, remaining);
                                        HandlePlayingSamples(lastBuffer);
                                    }
                                }
                            }
                            else
                            {
                                // Play audio
                                AudioSource.PlayOneShot(clip);

                                // Wait while voice playing
                                while (AudioSource.isPlaying && !token.IsCancellationRequested)
                                {
                                    await UniTask.Delay(33, cancellationToken: token);  // 30FPS
                                }
                            }

                            if (!token.IsCancellationRequested)
                            {
                                try
                                {
                                    // Wait for PostGap
                                    await UniTask.Delay((int)(v.PostGap * 1000), cancellationToken: token);
                                }
                                catch (OperationCanceledException)
                                {
                                    Debug.Log("Task canceled in waiting PostGap");
                                }
                            }

                        }
                    }
                    // --- ここから修正 ---
                    catch (OperationCanceledException)
                    {
                        // キャンセルは正常なので何もしない
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error at Say: {ex.Message}\n{ex.StackTrace}");
                    }
                    // --- ここまで修正 ---
                    finally
                    {
                        // --- new_ModelController.txt 追加: フィラー音声再生終了通知 ---
                        if (fillerVoicePlayer != null)
                        {
                            fillerVoicePlayer.SetMainVoicePlaying(false);
                        }
                        // --- ここまで ---
                        OnSayEnd?.Invoke();
                    }
                }
            }
            finally
            {
                if (shouldGateMic)
                {
                    micManager.MuteMicrophone(false);
#if UNITY_WEBGL && !UNITY_EDITOR
                    if (sileroVAD != null)
                    {
                        sileroVAD.Mute(false);
                    }
#endif
                }
            }

            // Reset viseme
            lipSyncHelper?.ResetViseme();
            isSpeaking = false;
        }

        // Start downloading voices from web/TTS
        public void PrefetchVoices(List<Voice> voices, CancellationToken token)
        {
            foreach (var voice in voices)
            {
                if (VoicePrefetchMode == VoicePrefetchMode.Sequential)
                {
                    voicePrefetchQueue.Enqueue(voice);
                }
                else
                {
                    var parameters = voice.TTSConfig != null ? voice.TTSConfig.Params : new Dictionary<string, object>();
                    SpeechSynthesizerFunc(voice.Text, parameters, token);
                }
            }
        }

        private async UniTaskVoid StartVoicePrefetchTask()
        {
            voicePrefetchCancellationTokenSource = new CancellationTokenSource();
            var token = voicePrefetchCancellationTokenSource.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (VoicePrefetchMode == VoicePrefetchMode.Sequential && voicePrefetchQueue.TryDequeue(out var voice))
                    {
                        var parameters = voice.TTSConfig != null ? voice.TTSConfig.Params : new Dictionary<string, object>();
                        await SpeechSynthesizerFunc(voice.Text, parameters, token);
                    }
                    else
                    {
                        await UniTask.Delay(10, cancellationToken: token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore OperationCanceledException
            }
        }

        // Stop speech
        public void StopSpeech()
        {
            History?.Add("Stop speech");
            AudioSource.Stop();
        }

        // Reset viseme (mouth) to neutral immediately
        public void ResetViseme()
        {
            lipSyncHelper?.ResetViseme();
        }
#endregion


#region Animation
        // Set animations to queue
        public void Animate(List<Animation> animations)
        {
            ResetLayers();
            animationQueue.Clear();
            animationQueue = new List<Animation>(animations);
            animationStartAt = 0;
            GetAnimation = GetQueuedAnimation;
        }

        private void ResetLayers()
        {
            for (var i = 1; i < animator.layerCount; i++)
            {
                animator.CrossFadeInFixedTime(layeredAnimationDefaultState, AnimationFadeLength, i);
            }
        }

        private void UpdateAnimation()
        {
            // This method will be called every frames in `Update()`
            var animationToRun = GetAnimation();
            if (animationToRun == null)
            {
                // Keep listening/speaking/processing pose without switching to normal idle
                if (isListeningMode || isSpeaking || suppressIdleFallback)
                {
                    // 明示アニメが無いフレームでは直前のベース姿勢を維持
                    if (currentAnimation != null)
                    {
                        animator.SetInteger(currentAnimation.ParameterKey, currentAnimation.ParameterValue);
                    }
                    return;
                }

                // Start idling instead when animationToRun is null
                // `resetStartTime = false`: When idling, GetIdleAnimation() returns null if idle animation is not timeout.
                // So, StartIdling() will be called every frame while idle animation is not timeout.
                // If set 0 `animationStartAt` everytime StartIdling() called, idle animation changes every frame.
                // This `false` prevents reset `animationStartAt` to keep running idle animation.
                StartIdling(false);
                return;
            }

            if (currentAnimation == null || animationToRun.Id != currentAnimation.Id)
            {
                // Start new animation
                ResetLayers();
                animator.SetInteger(animationToRun.ParameterKey, animationToRun.ParameterValue);
                // Avoid unnecessary layer resets that can cause visible snaps.
                // Only reset when leaving/entering a state that uses layered additive animation.
                var prevHadLayer = currentAnimation != null && !string.IsNullOrEmpty(currentAnimation.LayeredAnimationName);
                var nextHasLayer = !string.IsNullOrEmpty(animationToRun.LayeredAnimationName);
                if (prevHadLayer || nextHasLayer)
                {
                    ResetLayers();
                }

                // Write base parameter only if it actually changes to prevent redundant transitions
                if (currentAnimation == null
                    || currentAnimation.ParameterKey != animationToRun.ParameterKey
                    || currentAnimation.ParameterValue != animationToRun.ParameterValue)
                {
                    animator.SetInteger(animationToRun.ParameterKey, animationToRun.ParameterValue);
                }
                if (!string.IsNullOrEmpty(animationToRun.LayeredAnimationName))
                {
                    var layerIndex = animator.GetLayerIndex(animationToRun.LayeredAnimationLayerName);
                    animator.CrossFadeInFixedTime(animationToRun.LayeredAnimationName, AnimationFadeLength, layerIndex);
                }
                currentAnimation = animationToRun;
                animationStartAt = Time.realtimeSinceStartup;
            }
        }

        private Animation GetQueuedAnimation()
        {
            if (animationQueue.Count == 0) return default;

            if (animationStartAt > 0 && Time.realtimeSinceStartup - animationStartAt > currentAnimation.Duration)
            {
                // Remove the first animation and reset animationStartAt when:
                // - Not right after the Animate() called (animationStartAt > 0)
                // - Animation is timeout (Time.realtimeSinceStartup - animationStartAt > currentAnimation.Duration)
                animationQueue.RemoveAt(0);
                animationStartAt = 0;
            }

            if (animationQueue.Count == 0) return default;

            return animationQueue.First();
        }

        private Animation GetIdleAnimation()
        {
            if (currentAnimation == null || animationStartAt == 0 || Time.realtimeSinceStartup - animationStartAt > currentAnimation.Duration)
            {
                // Return random idle animation when:
                // - Animation is not running currently (currentAnimation == null)
                // - Right after StartIdling() called (animationStartAt == 0)
                // - Idle animation is timeout (Time.realtimeSinceStartup - animationStartAt > currentAnimation.Duration)
                var i = UnityEngine.Random.Range(0, idleWeightedIndexes[IdlingMode].Count);
                return idleAnimations[IdlingMode][idleWeightedIndexes[IdlingMode][i]];
            }
            else
            {
                return default;
            }
        }

        private bool isListeningMode = false;
        private bool isSpeaking = false;
        private bool suppressIdleFallback = false;
        private Animation listeningIdleAnimation;
        private Animation listeningTriggerAnimation;
        private bool triggerAnimationRequested = false;

        public void SuppressIdleFallback(bool suppress)
        {
            suppressIdleFallback = suppress;
        }

        public void StartListeningIdle(Animation idleAnim)
        {
            isListeningMode = true;
            // Keep idle fallback suppressed outside Idle mode.
            // Do NOT release suppression here to avoid resuming normal idle while Listening.
            listeningIdleAnimation = idleAnim;
            animationStartAt = 0;
            GetAnimation = GetListeningIdleAnimation;
        }

        public void StopListeningIdle()
        {
            isListeningMode = false;
            GetAnimation = GetIdleAnimation;
        }

        public void TriggerListeningAnimation(Animation triggerAnim)
        {
            listeningTriggerAnimation = triggerAnim;
            triggerAnimationRequested = true;
        }

        private Animation GetListeningIdleAnimation()
        {
            if (triggerAnimationRequested && listeningTriggerAnimation != null)
            {
                triggerAnimationRequested = false;
                animationStartAt = 0;
                return listeningTriggerAnimation;
            }

            if (currentAnimation == null || animationStartAt == 0 || Time.realtimeSinceStartup - animationStartAt > listeningIdleAnimation.Duration)
            {
                animationStartAt = Time.realtimeSinceStartup;
                return listeningIdleAnimation;
            }
            else
            {
                return null;
            }
        }

        public void RegisterAnimations(Dictionary<string, Animation> animations)
        {
            foreach (var animation in animations)
            {
                RegisterAnimation(animation.Key, animation.Value);
            }
        }

        public void RegisterAnimation(string name, Animation animation)
        {
            registeredAnimations[name] = animation;
        }

        public Animation GetRegisteredAnimation(string name, float duration = 0.0f, string layeredAnimationName = null, string layeredAnimationLayerName = null)
        {
            return new Animation(
                registeredAnimations[name].ParameterKey,
                registeredAnimations[name].ParameterValue,
                duration == 0.0f ? registeredAnimations[name].Duration : duration,
                layeredAnimationName ?? registeredAnimations[name].LayeredAnimationName,
                layeredAnimationLayerName ?? registeredAnimations[name].LayeredAnimationLayerName
            );
        }

        public bool IsAnimationRegistered(string name)
        {
            return registeredAnimations.ContainsKey(name);
        }

        public string ListRegisteredAnimations(string itemTemplate = null)
        {
            var template = (itemTemplate == null ? "- {0}" : itemTemplate) + "\n";
            var list = "";
            foreach (var item in registeredAnimations.Keys)
            {
                list += string.Format(template, item);
            }
            return list;
        }
#endregion


#region Face Expression
        // Set face expressions
        public void SetFace(List<FaceExpression> faces)
        {
            faceQueue.Clear();
            faceQueue = new List<FaceExpression>(faces);    // Copy faces not to change original list
            faceStartAt = 0;
        }

        private void UpdateFace()
        {
            // This method will be called every frames in `Update()`
            var faceToSet = GetFaceExpression();
            if (faceToSet == null)
            {
                // Set neutral instead when faceToSet is null
                SetFace(new List<FaceExpression>() { new FaceExpression("Neutral", 0.0f, string.Empty) });
                return;
            }

            if (currentFace == null || faceToSet.Name != currentFace.Name || faceToSet.Duration != currentFace.Duration)
            {
                // Set new face
                faceExpressionProxy.SetExpressionSmoothly(faceToSet.Name, 1.0f);
                currentFace = faceToSet;
                faceStartAt = Time.realtimeSinceStartup;
            }
        }

        public FaceExpression GetFaceExpression()
        {
            if (faceQueue.Count == 0) return default;

            if (faceStartAt > 0 && Time.realtimeSinceStartup - faceStartAt > currentFace.Duration)
            {
                // Remove the first face after the duration passed
                faceQueue.RemoveAt(0);
                faceStartAt = 0;
            }

            if (faceQueue.Count == 0) return default;

            return faceQueue.First();
        }
#endregion

#region Avatar
        public void ActivateAvatar(GameObject avatarObject = null, bool configureViseme = false)
        {
            if (avatarObject != null)
            {
                AvatarModel = avatarObject;
            }
            animator = AvatarModel.GetComponent<Animator>();

            // Blink (Blink at first because FaceExpression depends blink)
            blinker.Setup(AvatarModel);
            faceExpressionProxy.Setup(AvatarModel);

            if (configureViseme)
            {
                lipSyncHelper.ConfigureViseme(AvatarModel);
            }

            _ = blinker.StartBlinkAsync();
            currentAnimation = null;  // Set null to newly start idling animation

            AvatarModel.SetActive(true);
            enabled = true;
        }

        public void DeactivateAvatar(Action unloadAction = null)
        {
            if (AvatarModel == null) return;

            enabled = false;
            blinker.StopBlink();

            AvatarModel.SetActive(false);
            unloadAction?.Invoke();
        }
#endregion
    }
}
