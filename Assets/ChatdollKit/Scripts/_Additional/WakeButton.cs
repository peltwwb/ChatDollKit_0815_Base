using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using ChatdollKit.Dialog;
using ChatdollKit.IO;

public class WakeButton : MonoBehaviour
{
    public DialogProcessor DialogProcessor;
    [Header("Optional microphone components to disable during dialog")]
    [SerializeField] private Behaviour[] micComponents;      // e.g., KeywordDetector, VoiceRecorder
    [SerializeField] private UnityEngine.UI.Button wakeButton;
    private bool[] micPrevStates;

    public async void OnWakeButton()
    {
        if (DialogProcessor == null) { return; }
        if (DialogProcessor.Status != DialogProcessor.DialogStatus.Idling) { return; }

        // Prevent double‑click
        if (wakeButton != null) { wakeButton.interactable = false; }

        // Disable mic components while the dialog is running
        if (micComponents != null && micComponents.Length > 0)
        {
            micPrevStates = new bool[micComponents.Length];
            for (int i = 0; i < micComponents.Length; i++)
            {
                if (micComponents[i] != null)
                {
                    micPrevStates[i] = micComponents[i].enabled;
                    micComponents[i].enabled = false;
                }
            }
        }

        // Start dialog and wait until it completes
        // 初回のWake（ボタン押下）ではユーザー側メッセージ表示を抑止
        var payloads = new Dictionary<string, object>
        {
            { "SuppressUserMessage", true },
            { "IsWakeword", true },
        };
        await DialogProcessor.StartDialogAsync(
            "こんにちは",
            payloads,
            true);

        // Restore mic component states
        if (micComponents != null && micComponents.Length > 0)
        {
            for (int i = 0; i < micComponents.Length; i++)
            {
                if (micComponents[i] != null)
                {
                    micComponents[i].enabled = micPrevStates[i];
                }
            }
        }

        if (wakeButton != null) { wakeButton.interactable = true; }
    }

}
