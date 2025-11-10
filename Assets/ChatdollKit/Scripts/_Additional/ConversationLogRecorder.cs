using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using ChatdollKit.Dialog;
using ChatdollKit.LLM;

namespace ChatdollKit.Additional
{
    /// <summary>
    /// Bridges DialogProcessor events to the ConversationLogManager.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConversationLogRecorder : MonoBehaviour
    {
        [SerializeField] private DialogProcessor dialogProcessor;
        [SerializeField] private ConversationLogManager logManager;
        [Tooltip("Treat inputs that start with this prefix as background requests and skip logging.")]
        [SerializeField] private string backgroundRequestPrefix = "$";
        [Tooltip("Skip logging turn when payload contains the IdleCallout marker.")]
        [SerializeField] private bool ignoreIdleCallouts = true;

        private readonly Queue<PendingTurn> pendingTurns = new Queue<PendingTurn>();
        private bool isSubscribed;
        private bool hasInitialized;

        private const string IdleCalloutPayloadKey = "IdleCallout";
        private const string SuppressUserMessageKey = "SuppressUserMessage";

        private void Awake()
        {
            if (dialogProcessor == null)
            {
                dialogProcessor = GetComponent<DialogProcessor>();
            }

            if (logManager == null)
            {
                logManager = GetComponent<ConversationLogManager>() ?? FindFirstObjectByType<ConversationLogManager>();
            }
        }

        private void OnEnable()
        {
            if (hasInitialized)
            {
                Subscribe();
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
            pendingTurns.Clear();
        }

        private void Start()
        {
            hasInitialized = true;
            Subscribe();
        }

        private void Subscribe()
        {
            if (dialogProcessor == null || isSubscribed)
            {
                return;
            }

            dialogProcessor.OnRequestRecievedAsync += HandleRequestAsync;
            dialogProcessor.OnResponseShownAsync += HandleResponseAsync;
            dialogProcessor.OnErrorAsync += HandleErrorAsync;
            isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (dialogProcessor == null || !isSubscribed)
            {
                return;
            }

            dialogProcessor.OnRequestRecievedAsync -= HandleRequestAsync;
            dialogProcessor.OnResponseShownAsync -= HandleResponseAsync;
            dialogProcessor.OnErrorAsync -= HandleErrorAsync;
            isSubscribed = false;
        }

        private UniTask HandleRequestAsync(string text, Dictionary<string, object> payloads, CancellationToken token)
        {
            var shouldRecord = ShouldRecordTurn(text, payloads);
            var preparedText = shouldRecord ? text : string.Empty;

            pendingTurns.Enqueue(new PendingTurn
            {
                ShouldRecord = shouldRecord,
                UserText = preparedText
            });

            return UniTask.CompletedTask;
        }

        private UniTask HandleResponseAsync(string text, Dictionary<string, object> payloads, ILLMSession session, CancellationToken token)
        {
            if (logManager == null)
            {
                Debug.LogWarning("[ConversationLogRecorder] Missing ConversationLogManager reference.");
                return UniTask.CompletedTask;
            }

            PendingTurn pendingTurn = default;
            if (pendingTurns.Count > 0)
            {
                pendingTurn = pendingTurns.Dequeue();
            }

            if (!pendingTurn.ShouldRecord)
            {
                return UniTask.CompletedTask;
            }

            var responseText = session?.StreamBuffer;
            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = session?.CurrentStreamBuffer;
            }

            logManager.LogTurn(pendingTurn.UserText, responseText ?? string.Empty);

            return UniTask.CompletedTask;
        }

        private bool ShouldRecordTurn(string text, Dictionary<string, object> payloads)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(backgroundRequestPrefix) && text.StartsWith(backgroundRequestPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (payloads != null)
            {
                if (payloads.TryGetValue(SuppressUserMessageKey, out var suppressValue)
                    && suppressValue is bool suppress
                    && suppress)
                {
                    return false;
                }

                if (ignoreIdleCallouts &&
                    payloads.TryGetValue(IdleCalloutPayloadKey, out var idleValue) &&
                    idleValue is bool idleFlag &&
                    idleFlag)
                {
                    return false;
                }
            }

            return true;
        }

        private UniTask HandleErrorAsync(string text, Dictionary<string, object> payloads, Exception ex, CancellationToken token)
        {
            if (pendingTurns.Count > 0)
            {
                pendingTurns.Dequeue();
            }

            return UniTask.CompletedTask;
        }

        private struct PendingTurn
        {
            public bool ShouldRecord;
            public string UserText;
        }
    }
}
