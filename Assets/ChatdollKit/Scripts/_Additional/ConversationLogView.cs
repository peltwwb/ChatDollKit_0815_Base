using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ChatdollKit.Additional
{
    /// <summary>
    /// Populates a ScrollView with stored conversation logs.
    /// Hook its public methods to UI buttons to toggle the log window.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConversationLogView : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ConversationLogManager logManager;
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private RectTransform contentParent;
        [SerializeField] private TMP_Text dayHeaderTemplate;
        [SerializeField] private TMP_Text userEntryTemplate;
        [SerializeField] private TMP_Text characterEntryTemplate;

        [Header("Options")]
        [SerializeField] private bool hideOnStart = true;
        [SerializeField, Tooltip("When true, the newest day is placed at the top of the ScrollView.")]
        private bool newestFirst = true;
        [SerializeField, Tooltip("Automatically refresh the ScrollView contents when new logs arrive.")]
        private bool refreshWhileVisible = true;
        [SerializeField, Tooltip("チェックを入れると保存済みログを削除し、完了後オフに戻ります。")]
        private bool clearLogsRequest;

        private readonly List<GameObject> spawnedItems = new List<GameObject>();

        private void Awake()
        {
            if (logManager == null)
            {
                logManager = GetComponent<ConversationLogManager>() ?? FindFirstObjectByType<ConversationLogManager>();
            }

            if (panelRoot == null)
            {
                panelRoot = gameObject;
            }

            DisableTemplate(dayHeaderTemplate);
            DisableTemplate(userEntryTemplate);
            DisableTemplate(characterEntryTemplate);

            if (logManager != null)
            {
                logManager.LogsUpdated += HandleLogsUpdated;
            }

            if (hideOnStart && panelRoot != null)
            {
                panelRoot.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            if (logManager != null)
            {
                logManager.LogsUpdated -= HandleLogsUpdated;
            }
        }

        public void Toggle()
        {
            if (panelRoot == null)
            {
                return;
            }

            var targetState = !panelRoot.activeSelf;
            panelRoot.SetActive(targetState);
            if (targetState)
            {
                Refresh();
            }
        }

        public void Show()
        {
            if (panelRoot == null)
            {
                return;
            }

            panelRoot.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            panelRoot?.SetActive(false);
        }

        public void Refresh()
        {
            if (!HasValidTemplates())
            {
                Debug.LogWarning("[ConversationLogView] Missing references. Assign manager, content, and templates.");
                return;
            }

            ClearSpawnedItems();

            var snapshot = logManager.GetSnapshot(newestFirst);
            foreach (var day in snapshot)
            {
                SpawnHeader(day);
                SpawnTurns(day);
            }

            if (scrollRect != null)
            {
                // Scroll to top when newest logs are at the top, otherwise bottom.
                scrollRect.verticalNormalizedPosition = newestFirst ? 1f : 0f;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(contentParent);
        }

        private void HandleLogsUpdated()
        {
            if (!refreshWhileVisible)
            {
                return;
            }

            if (panelRoot != null && panelRoot.activeInHierarchy)
            {
                Refresh();
            }
        }

        private void ClearSpawnedItems()
        {
            for (int i = 0; i < spawnedItems.Count; i++)
            {
                if (spawnedItems[i] != null)
                {
                    Destroy(spawnedItems[i]);
                }
            }
            spawnedItems.Clear();
        }

        private bool HasValidTemplates()
        {
            return logManager != null
                && contentParent != null
                && dayHeaderTemplate != null
                && userEntryTemplate != null
                && characterEntryTemplate != null;
        }

        private static string FormatDate(string dateText)
        {
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("yyyy/MM/dd");
            }
            return dateText;
        }

        private static string FormatUserTurn(ConversationLogTurnSnapshot turn)
        {
            var timestamp = turn.GetTimestamp().ToLocalTime();
            var label = timestamp.ToString("HH:mm");
            var user = string.IsNullOrWhiteSpace(turn.UserText) ? "-" : turn.UserText;
            return $"[{label}] ユーザー\n{user}";
        }

        private static string FormatCharacterTurn(ConversationLogTurnSnapshot turn)
        {
            var timestamp = turn.GetTimestamp().ToLocalTime();
            var label = timestamp.ToString("HH:mm");
            var character = string.IsNullOrWhiteSpace(turn.CharacterText) ? "-" : turn.CharacterText;
            return $"[{label}] キャラクター\n{character}";
        }

        private void SpawnHeader(ConversationLogDaySnapshot day)
        {
            var header = Instantiate(dayHeaderTemplate, contentParent);
            header.text = FormatDate(day.Date);
            header.alignment = TextAlignmentOptions.Left;
            header.gameObject.SetActive(true);
            spawnedItems.Add(header.gameObject);
        }

        private void SpawnTurns(ConversationLogDaySnapshot day)
        {
            foreach (var turn in day.Turns)
            {
                if (!string.IsNullOrWhiteSpace(turn.UserText))
                {
                    var userEntry = Instantiate(userEntryTemplate, contentParent);
                    userEntry.text = FormatUserTurn(turn);
                    userEntry.alignment = TextAlignmentOptions.TopLeft;
                    userEntry.gameObject.SetActive(true);
                    spawnedItems.Add(userEntry.gameObject);
                }

                if (!string.IsNullOrWhiteSpace(turn.CharacterText))
                {
                    var characterEntry = Instantiate(characterEntryTemplate, contentParent);
                    characterEntry.text = FormatCharacterTurn(turn);
                    characterEntry.alignment = TextAlignmentOptions.TopRight;
                    characterEntry.gameObject.SetActive(true);
                    spawnedItems.Add(characterEntry.gameObject);
                }
            }
        }

        private void DisableTemplate(TMP_Text template)
        {
            if (template != null)
            {
                template.gameObject.SetActive(false);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!clearLogsRequest)
            {
                return;
            }

            clearLogsRequest = false;

            if (logManager == null)
            {
                logManager = GetComponent<ConversationLogManager>() ?? FindFirstObjectByType<ConversationLogManager>();
            }

            if (logManager == null)
            {
                return;
            }

            logManager.ClearLogs();
            if (Application.isPlaying)
            {
                Refresh();
            }
        }
#endif
    }
}
