using UnityEngine;

namespace ChatdollKit.Additional
{
    /// <summary>
    /// Enables one of the supplied magic-circle effects based on the avatar state:
    /// Idle → White, Listening → Blue, Conversation → Green.
    /// </summary>
    [DisallowMultipleComponent]
    public class MagicCircleStateIndicator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AIAvatar aiAvatar;

        [Header("Magic Circle Objects")]
        [Tooltip("Shown while AIAvatar is Idle (e.g. FX_White_Flipbook).")]
        [SerializeField] private GameObject idleCircle;
        [Tooltip("Shown while AIAvatar is Listening (e.g. FX_Blue_Flipbook).")]
        [SerializeField] private GameObject listeningCircle;
        [Tooltip("Shown while AIAvatar is in Conversation mode (e.g. FX_Green_Flipbook).")]
        [SerializeField] private GameObject conversationCircle;

        private AIAvatar.AvatarMode? lastAppliedMode;

        private void Reset()
        {
            aiAvatar = GetComponent<AIAvatar>();
        }

        private void Awake()
        {
            ResolveAvatarReference();
            ApplyCurrentMode(force: true);
        }

        private void OnDisable()
        {
            SetActive(idleCircle, false);
            SetActive(listeningCircle, false);
            SetActive(conversationCircle, false);
            lastAppliedMode = null;
        }

        private void Update()
        {
            if (!ResolveAvatarReference())
            {
                return;
            }

            ApplyCurrentMode(force: false);
        }

        private bool ResolveAvatarReference()
        {
            if (aiAvatar != null)
            {
                return true;
            }

            aiAvatar = GetComponent<AIAvatar>();
            if (aiAvatar == null)
            {
                aiAvatar = FindFirstObjectByType<AIAvatar>();
            }

            return aiAvatar != null;
        }

        private void ApplyCurrentMode(bool force)
        {
            var mode = aiAvatar != null ? aiAvatar.Mode : AIAvatar.AvatarMode.Disabled;
            if (!force && lastAppliedMode.HasValue && lastAppliedMode.Value == mode)
            {
                return;
            }

            lastAppliedMode = mode;

            bool showIdle = mode == AIAvatar.AvatarMode.Idle;
            bool showListening = mode == AIAvatar.AvatarMode.Listening;
            bool showConversation = mode == AIAvatar.AvatarMode.Conversation && !showListening;

            SetActive(idleCircle, showIdle);
            SetActive(listeningCircle, showListening);
            SetActive(conversationCircle, showConversation);

            if (!showIdle && !showListening && !showConversation)
            {
                SetActive(idleCircle, false);
                SetActive(listeningCircle, false);
                SetActive(conversationCircle, false);
            }
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target == null || target.activeSelf == active)
            {
                return;
            }

            target.SetActive(active);
        }
    }
}
