using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

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
        [SerializeField] private Text dayHeaderTemplate;
        [SerializeField] private Text entryTemplate;

        [Header("Options")]
        [SerializeField] private bool hideOnStart = true;
        [SerializeField, Tooltip("When true, the newest day is placed at the top of the ScrollView.")]
        private bool newestFirst = true;
        [SerializeField, Tooltip("Automatically refresh the ScrollView contents when new logs arrive.")]
        private bool refreshWhileVisible = true;

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

            if (dayHeaderTemplate != null)
            {
                dayHeaderTemplate.gameObject.SetActive(false);
            }
            if (entryTemplate != null)
            {
                entryTemplate.gameObject.SetActive(false);
            }

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
            if (logManager == null || contentParent == null || dayHeaderTemplate == null || entryTemplate == null)
            {
                Debug.LogWarning("[ConversationLogView] Missing references. Assign manager, content, and templates.");
                return;
            }

            ClearSpawnedItems();

            var snapshot = logManager.GetSnapshot(newestFirst);
            foreach (var day in snapshot)
            {
                var header = Instantiate(dayHeaderTemplate, contentParent);
                header.text = FormatDate(day.Date);
                header.gameObject.SetActive(true);
                spawnedItems.Add(header.gameObject);

                foreach (var turn in day.Turns)
                {
                    var entry = Instantiate(entryTemplate, contentParent);
                    entry.text = FormatTurn(turn);
                    entry.gameObject.SetActive(true);
                    spawnedItems.Add(entry.gameObject);
                }
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

        private static string FormatDate(string dateText)
        {
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("yyyy/MM/dd");
            }
            return dateText;
        }

        private static string FormatTurn(ConversationLogTurnSnapshot turn)
        {
            var timestamp = turn.GetTimestamp().ToLocalTime();
            var label = timestamp.ToString("HH:mm");
            var user = string.IsNullOrWhiteSpace(turn.UserText) ? "-" : turn.UserText;
            var character = string.IsNullOrWhiteSpace(turn.CharacterText) ? "-" : turn.CharacterText;
            return $"[{label}] ユーザー\n{user}\n[{label}] キャラクター\n{character}";
        }
    }
}
