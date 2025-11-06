using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using ChatdollKit.Dialog;
using ChatdollKit.Model;

namespace ChatdollKit.Additional
{
    [DisallowMultipleComponent]
    public class IdleCalloutScheduler : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AIAvatar aiAvatar;
        [SerializeField] private DialogProcessor dialogProcessor;
        [SerializeField] private ModelController modelController;

        [Header("Timing (minutes)")]
        [SerializeField, Min(0f)] private float initialDelayMinutes = 60f;
        [SerializeField, Min(0f)] private float repeatIntervalMinutes = 60f;
        [Tooltip("Random offset added to each interval (minutes). X=min, Y=max.")]
        [SerializeField] private Vector2 randomOffsetMinutes = new Vector2(-5f, 5f);
        [Tooltip("Restart the interval timer whenever a user-driven dialog finishes.")]
        [SerializeField] private bool resetAfterInteraction = true;

        [Header("Callouts / Monologues")]
        [TextArea]
        [SerializeField] private List<string> idlePhrases = new List<string>
        {
            "そろそろ誰かとおしゃべりしたいなぁ。",
            "ちょっと暇になっちゃった。話しかけてくれない？",
            "もしもし？聞こえてる？",
            "[face:Joy]ねえねえ、今ならお話できるよ！"
        };

        private float nextTriggerTime;
        private bool calloutInProgress;
        private DialogProcessor.DialogStatus previousDialogStatus = DialogProcessor.DialogStatus.Idling;
        private CancellationTokenSource calloutCts;

        private const string IdleCalloutPayloadKey = "IdleCallout";

        private void Reset()
        {
            aiAvatar = GetComponent<AIAvatar>();
            dialogProcessor = GetComponent<DialogProcessor>();
            modelController = GetComponent<ModelController>();
        }

        private void Awake()
        {
            if (aiAvatar == null)
            {
                aiAvatar = FindFirstObjectByType<AIAvatar>();
            }
            if (dialogProcessor == null && aiAvatar != null)
            {
                dialogProcessor = aiAvatar.DialogProcessor;
            }
            if (modelController == null && aiAvatar != null)
            {
                modelController = aiAvatar.ModelController;
            }

            if (dialogProcessor != null)
            {
                previousDialogStatus = dialogProcessor.Status;
            }
        }

        private void OnEnable()
        {
            ScheduleNextTrigger(true);
        }

        private void OnDisable()
        {
            calloutCts?.Cancel();
            calloutCts?.Dispose();
            calloutCts = null;
            calloutInProgress = false;
        }

        private void Update()
        {
            if (aiAvatar == null)
            {
                aiAvatar = FindFirstObjectByType<AIAvatar>();
            }
            if (dialogProcessor == null && aiAvatar != null && aiAvatar.DialogProcessor != null)
            {
                dialogProcessor = aiAvatar.DialogProcessor;
                previousDialogStatus = dialogProcessor.Status;
            }
            if (modelController == null && aiAvatar != null && aiAvatar.ModelController != null)
            {
                modelController = aiAvatar.ModelController;
            }

            if (aiAvatar == null || dialogProcessor == null || modelController == null)
            {
                return;
            }

            var currentStatus = dialogProcessor.Status;
            if (resetAfterInteraction && currentStatus != previousDialogStatus)
            {
                if (currentStatus == DialogProcessor.DialogStatus.Idling
                    && previousDialogStatus != DialogProcessor.DialogStatus.Idling)
                {
                    ScheduleNextTrigger(false);
                }
            }
            previousDialogStatus = currentStatus;

            if (calloutInProgress)
            {
                return;
            }

            if (idlePhrases.Count == 0 || !isActiveAndEnabled)
            {
                return;
            }

            if (currentStatus != DialogProcessor.DialogStatus.Idling)
            {
                return;
            }

            if (aiAvatar.Mode == AIAvatar.AvatarMode.Disabled)
            {
                return;
            }

            if (modelController.AudioSource != null && modelController.AudioSource.isPlaying)
            {
                return;
            }

            if (Time.time < nextTriggerTime)
            {
                return;
            }

            TriggerCallout().Forget();
        }

        private void ScheduleNextTrigger(bool useInitialDelay)
        {
            var baseMinutes = useInitialDelay ? initialDelayMinutes : repeatIntervalMinutes;
            if (baseMinutes <= 0f)
            {
                // Treat zero or negative as disabled.
                nextTriggerTime = float.PositiveInfinity;
                return;
            }

            float seconds = Mathf.Max(10f, baseMinutes * 60f);
            if (randomOffsetMinutes != Vector2.zero)
            {
                float min = randomOffsetMinutes.x * 60f;
                float max = randomOffsetMinutes.y * 60f;
                if (min > max)
                {
                    (min, max) = (max, min);
                }
                seconds += UnityEngine.Random.Range(min, max);
                if (seconds < 10f)
                {
                    seconds = 10f;
                }
            }

            nextTriggerTime = Time.time + seconds;
        }

        private async UniTaskVoid TriggerCallout()
        {
            if (idlePhrases.Count == 0)
            {
                ScheduleNextTrigger(false);
                return;
            }

            calloutInProgress = true;

            calloutCts?.Cancel();
            calloutCts?.Dispose();
            calloutCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            var token = calloutCts.Token;

            string selected = idlePhrases[UnityEngine.Random.Range(0, idlePhrases.Count)];

            // Suppress the user message window; the avatar speaks proactively.
            var payloads = new Dictionary<string, object>
            {
                { "SuppressUserMessage", true },
                { IdleCalloutPayloadKey, true }
            };

            try
            {
                if (dialogProcessor.OnRequestRecievedAsync != null)
                {
                    await dialogProcessor.OnRequestRecievedAsync(selected, payloads, token);
                }

                if (modelController != null)
                {
                    var request = modelController.ToAnimatedVoiceRequest(selected);
                    request.StartIdlingOnEnd = false;
                    await modelController.AnimatedSay(request, token);
                }

                if (dialogProcessor.OnEndAsync != null)
                {
                    await dialogProcessor.OnEndAsync(false, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation (scene unload, disable, etc.)
            }
            finally
            {
                calloutInProgress = false;
                calloutCts?.Dispose();
                calloutCts = null;
                ScheduleNextTrigger(false);
            }
        }
    }
}
