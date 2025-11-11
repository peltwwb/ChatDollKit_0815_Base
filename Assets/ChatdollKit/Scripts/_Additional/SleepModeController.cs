using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ChatdollKit;
using ChatdollKit.Dialog;
using ChatdollKit.Model;

namespace ChatdollKit.Additional
{
    /// <summary>
    /// Drives the dedicated Sleep mode: enters after long idle or sustained click, keeps pose/face,
    /// and darkens configured lights until woken.
    /// </summary>
    [DisallowMultipleComponent]
    public class SleepModeController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AIAvatar aiAvatar;
        [SerializeField] private ModelController modelController;
        [Tooltip("Lights that should dim while the avatar is sleeping.")]
        [SerializeField] private List<Light> dimmableLights = new List<Light>();

        [Header("Sleep Triggers")]
        [Tooltip("Minutes spent Idling before the avatar automatically sleeps. Set 0 to disable.")]
        [SerializeField, Min(0f)] private float idleMinutesUntilSleep = 90f;
        [Tooltip("Continuous left-click duration (seconds) to force sleep while Idling. Set 0 to disable.")]
        [SerializeField, Min(0f)] private float sustainedClickSeconds = 5f;

        [Header("Lighting")]
        [SerializeField, Range(0f, 1f)] private float lightIntensityMultiplier = 0.25f;
        [SerializeField, Min(0f)] private float lightFadeSeconds = 1.5f;

        [Header("Pose / Face")]
        [Tooltip("Registered animation name applied while sleeping. Leave empty to use BaseParam override.")]
        [SerializeField] private string sleepAnimationName = "calm_hands_on_front";
        [SerializeField] private string sleepBaseParamKey = "BaseParam";
        [SerializeField] private int sleepBaseParamValue = 5;
        [Tooltip("Seconds to keep the sleep animation active. 0 means hold indefinitely.")]
        [SerializeField, Min(0f)] private float sleepAnimationDuration = 3600f;
        [SerializeField] private string sleepFaceName = "Blink";
        [Tooltip("Seconds to keep the eyes closed. 0 means hold indefinitely.")]
        [SerializeField, Min(0f)] private float sleepFaceDuration = 3600f;

        private readonly Dictionary<Light, float> originalIntensities = new Dictionary<Light, float>();
        private float idleStartedAt = -1f;
        private float clickStartedAt = -1f;
        private bool lastAppliedSleeping = false;
        private Coroutine activeFade;
        private float sleepFaceLastAppliedAt = -1f;

        private void Reset()
        {
            aiAvatar = GetComponent<AIAvatar>();
            modelController = GetComponent<ModelController>();
        }

        private void Awake()
        {
            ResolveReferences();
            CacheOriginalIntensities();
        }

        private void OnEnable()
        {
            ResolveReferences();
            CacheOriginalIntensities();
        }

        private void OnDisable()
        {
            if (activeFade != null)
            {
                StopCoroutine(activeFade);
                activeFade = null;
            }
            RestoreLightsInstant();
            lastAppliedSleeping = false;
        }

        private void Update()
        {
            if (!ResolveReferences())
            {
                return;
            }

            TrackIdleDuration();
            TrackMouseHold();

            bool sleepingNow = aiAvatar.IsSleeping;
            if (sleepingNow != lastAppliedSleeping)
            {
                lastAppliedSleeping = sleepingNow;
                if (sleepingNow)
                {
                    ApplySleepVisuals();
                }
                else
                {
                    RestoreFromSleep();
                }
            }

            if (sleepingNow)
            {
                MaintainSleepPose();
            }
        }

        private bool ResolveReferences()
        {
            if (aiAvatar == null)
            {
                aiAvatar = GetComponent<AIAvatar>() ?? FindFirstObjectByType<AIAvatar>();
            }

            if (aiAvatar == null)
            {
                return false;
            }

            if (modelController == null)
            {
                modelController = aiAvatar.ModelController ?? GetComponent<ModelController>() ?? FindFirstObjectByType<ModelController>();
            }

            return true;
        }

        private void CacheOriginalIntensities()
        {
            for (int i = dimmableLights.Count - 1; i >= 0; i--)
            {
                if (dimmableLights[i] == null)
                {
                    dimmableLights.RemoveAt(i);
                    continue;
                }

                if (!originalIntensities.ContainsKey(dimmableLights[i]))
                {
                    originalIntensities[dimmableLights[i]] = dimmableLights[i].intensity;
                }
            }
        }

        private void TrackIdleDuration()
        {
            if (aiAvatar.IsSleeping)
            {
                idleStartedAt = -1f;
                return;
            }

            if (idleMinutesUntilSleep <= 0f)
            {
                idleStartedAt = -1f;
                return;
            }

            bool isIdling = aiAvatar.Mode == AIAvatar.AvatarMode.Idle
                && (aiAvatar.DialogProcessor == null || aiAvatar.DialogProcessor.Status == DialogProcessor.DialogStatus.Idling);

            if (!isIdling)
            {
                idleStartedAt = -1f;
                return;
            }

            if (idleStartedAt < 0f)
            {
                idleStartedAt = Time.unscaledTime;
                return;
            }

            float idleSecondsThreshold = Mathf.Max(0f, idleMinutesUntilSleep * 60f);
            if (idleSecondsThreshold <= 0f)
            {
                return;
            }

            if (Time.unscaledTime - idleStartedAt >= idleSecondsThreshold)
            {
                if (aiAvatar.TryEnterSleepMode())
                {
                    idleStartedAt = -1f;
                    clickStartedAt = -1f;
                }
            }
        }

        private void TrackMouseHold()
        {
            if (aiAvatar.IsSleeping || sustainedClickSeconds <= 0f)
            {
                clickStartedAt = -1f;
                return;
            }

            if (aiAvatar.Mode != AIAvatar.AvatarMode.Idle)
            {
                clickStartedAt = -1f;
                return;
            }

            if (Input.GetMouseButton(0))
            {
                if (clickStartedAt < 0f)
                {
                    clickStartedAt = Time.unscaledTime;
                }
                else if (Time.unscaledTime - clickStartedAt >= sustainedClickSeconds)
                {
                    if (aiAvatar.TryEnterSleepMode())
                    {
                        clickStartedAt = -1f;
                        idleStartedAt = -1f;
                    }
                }
            }
            else
            {
                clickStartedAt = -1f;
            }
        }

        private void ApplySleepVisuals()
        {
            ApplySleepPose();
            FadeLights(lightIntensityMultiplier);
        }

        private void RestoreFromSleep()
        {
            FadeLights(1f);
            sleepFaceLastAppliedAt = -1f;

            if (modelController == null)
            {
                return;
            }

            modelController.SuppressIdleFallback(false);
            modelController.StartIdling();
            modelController.SetFace(new List<FaceExpression>()
            {
                new FaceExpression("Neutral", 0.0f, string.Empty)
            });
            modelController.ResetViseme();
        }

        private void ApplySleepPose()
        {
            if (modelController == null)
            {
                return;
            }

            var animation = ResolveSleepAnimation();
            if (animation != null)
            {
                modelController.SuppressIdleFallback(true);
                modelController.StopListeningIdle();
                modelController.Animate(new List<Model.Animation> { animation });
            }

            if (!string.IsNullOrEmpty(sleepFaceName))
            {
                ApplySleepFaceExpression();
            }
        }

        private void MaintainSleepPose()
        {
            MaintainSleepFace();
        }

        private void MaintainSleepFace()
        {
            if (modelController == null || string.IsNullOrEmpty(sleepFaceName))
            {
                return;
            }

            var currentFace = modelController.GetFaceExpression();
            bool hasSleepFace = currentFace != null && !string.IsNullOrEmpty(currentFace.Name) && currentFace.Name == sleepFaceName;
            if (!hasSleepFace)
            {
                ApplySleepFaceExpression();
                return;
            }

            if (sleepFaceDuration > 0f && !float.IsPositiveInfinity(currentFace.Duration))
            {
                float elapsed = Time.unscaledTime - sleepFaceLastAppliedAt;
                if (sleepFaceLastAppliedAt < 0f || elapsed >= Mathf.Max(0.05f, sleepFaceDuration * 0.9f))
                {
                    ApplySleepFaceExpression();
                }
            }
        }

        private void ApplySleepFaceExpression()
        {
            if (modelController == null || string.IsNullOrEmpty(sleepFaceName))
            {
                return;
            }

            float duration = sleepFaceDuration <= 0f ? float.PositiveInfinity : sleepFaceDuration;
            modelController.SetFace(new List<FaceExpression>
            {
                new FaceExpression(sleepFaceName, duration)
            });
            modelController.ResetViseme();
            sleepFaceLastAppliedAt = Time.unscaledTime;
        }

        private Model.Animation ResolveSleepAnimation()
        {
            float duration = sleepAnimationDuration <= 0f ? float.PositiveInfinity : sleepAnimationDuration;

            if (!string.IsNullOrEmpty(sleepAnimationName)
                && modelController != null
                && modelController.IsAnimationRegistered(sleepAnimationName))
            {
                return modelController.GetRegisteredAnimation(sleepAnimationName, duration);
            }

            if (string.IsNullOrEmpty(sleepBaseParamKey))
            {
                return null;
            }

            return new Model.Animation(sleepBaseParamKey, sleepBaseParamValue, duration);
        }

        private void FadeLights(float targetMultiplier)
        {
            if (dimmableLights.Count == 0)
            {
                return;
            }

            CacheOriginalIntensities();

            if (activeFade != null)
            {
                StopCoroutine(activeFade);
            }

            activeFade = StartCoroutine(FadeLightsRoutine(targetMultiplier));
        }

        private IEnumerator FadeLightsRoutine(float targetMultiplier)
        {
            if (lightFadeSeconds <= 0f)
            {
                foreach (var light in dimmableLights)
                {
                    if (light == null) continue;
                    light.intensity = GetOriginalIntensity(light) * targetMultiplier;
                }
                activeFade = null;
                yield break;
            }

            var startIntensities = new Dictionary<Light, float>();
            foreach (var light in dimmableLights)
            {
                if (light == null) continue;
                startIntensities[light] = light.intensity;
            }

            float elapsed = 0f;
            while (elapsed < lightFadeSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / lightFadeSeconds);
                foreach (var kvp in startIntensities)
                {
                    var light = kvp.Key;
                    if (light == null) continue;
                    float target = GetOriginalIntensity(light) * targetMultiplier;
                    light.intensity = Mathf.Lerp(kvp.Value, target, t);
                }
                yield return null;
            }

            foreach (var light in dimmableLights)
            {
                if (light == null) continue;
                light.intensity = GetOriginalIntensity(light) * targetMultiplier;
            }

            activeFade = null;
        }

        private float GetOriginalIntensity(Light light)
        {
            if (light == null)
            {
                return 0f;
            }

            if (!originalIntensities.TryGetValue(light, out float value))
            {
                value = light.intensity;
                originalIntensities[light] = value;
            }

            return value;
        }

        private void RestoreLightsInstant()
        {
            foreach (var kvp in originalIntensities)
            {
                if (kvp.Key == null) continue;
                kvp.Key.intensity = kvp.Value;
            }
        }
    }
}
