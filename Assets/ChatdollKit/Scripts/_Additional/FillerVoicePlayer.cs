using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace ChatdollKit.Filler
{
    /// <summary>
    /// 応答待機中にフィラーを再生し、完了後に本番音声を返すプレイヤー。
    /// </summary>
    public class FillerVoicePlayer : MonoBehaviour
    {
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private List<AudioClip> fillerClips = new List<AudioClip>();
        [SerializeField] private float fillerInterval = 5f;
        private System.Random random = new System.Random();
        private bool isMainVoicePlaying = false;

        private void Awake()
        {
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
            }
        }

        public void SetFillerClips(List<AudioClip> clips)
        {
            fillerClips = clips;
        }

        public void SetFillerInterval(float interval)
        {
            fillerInterval = interval;
        }

        public void SetMainVoicePlaying(bool playing)
        {
            isMainVoicePlaying = playing;
            if (playing && audioSource.isPlaying)
            {
                audioSource.Stop();
            }
        }

        /// <summary>
        /// 応答生成が一定時間を超える場合にフィラーを再生し、終了後に応答クリップを返す。
        /// </summary>
        public async UniTask<AudioClip> PlayWhileWaitingAsync(UniTask<AudioClip> responseTask, CancellationToken token)
        {
            Debug.Log("PlayWhileWaitingAsync called");

            // 本番音声再生中ならフィラー再生しない
            if (isMainVoicePlaying)
            {
                Debug.Log("Main voice is playing, skip filler.");
                return await responseTask;
            }

            if (fillerClips.Count == 0)
            {
                Debug.Log("No filler clips");
                return await responseTask;
            }

            var preservedResponse = responseTask.Preserve();
            var preservedTask = preservedResponse.AsTask();

            // まず最初のfillerIntervalだけ待機
            var waitTask = UniTask.Delay((int)(fillerInterval * 1000), cancellationToken: token).AsTask();
            var completed = await Task.WhenAny(preservedTask, waitTask);

            if (completed == preservedTask)
            {
                return await preservedResponse;
            }

            Debug.Log("Filler loop start");

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // 本番音声再生中になったら即終了
                if (isMainVoicePlaying)
                {
                    Debug.Log("Main voice started during filler loop, stop filler.");
                    if (audioSource.isPlaying)
                    {
                        audioSource.Stop();
                    }
                    return await preservedResponse;
                }

                // フィラー再生
                var fillerClip = fillerClips[random.Next(fillerClips.Count)];
                audioSource.clip = fillerClip;
                audioSource.Play();
                Debug.Log($"[FillerVoicePlayer] フィラー再生: {fillerClip.name}");

                // フィラー再生終了 or 応答完了 or 本番音声再生開始まで待つ
                var playTask = UniTask.WaitUntil(() => !audioSource.isPlaying, PlayerLoopTiming.Update, token).AsTask();
                while (true)
                {
                    completed = await Task.WhenAny(preservedTask, playTask, UniTask.WaitUntil(() => isMainVoicePlaying, PlayerLoopTiming.Update, token).AsTask());
                    if (completed == preservedTask)
                    {
                        await UniTask.Delay(1000, cancellationToken: token);
                        audioSource.Stop();
                        return await preservedResponse;
                    }
                    else if (completed == playTask)
                    {
                        break; // フィラー再生終了
                    }
                    else
                    {
                        // 本番音声再生開始
                        audioSource.Stop();
                        return await preservedResponse;
                    }
                }

                // フィラー再生終了後、fillerInterval待つ or 応答完了 or 本番音声再生開始まで待つ
                waitTask = UniTask.Delay((int)(fillerInterval * 1000), cancellationToken: token).AsTask();
                while (true)
                {
                    completed = await Task.WhenAny(preservedTask, waitTask, UniTask.WaitUntil(() => isMainVoicePlaying, PlayerLoopTiming.Update, token).AsTask());
                    if (completed == preservedTask)
                    {
                        await UniTask.Delay(1000, cancellationToken: token);
                        audioSource.Stop();
                        return await preservedResponse;
                    }
                    else if (completed == waitTask)
                    {
                        break; // インターバル経過
                    }
                    else
                    {
                        // 本番音声再生開始
                        audioSource.Stop();
                        return await preservedResponse;
                    }
                }
            }
        }
    }
}
