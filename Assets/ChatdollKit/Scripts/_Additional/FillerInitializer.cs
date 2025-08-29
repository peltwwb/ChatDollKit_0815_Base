using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace ChatdollKit.Filler
{
    /// <summary>
    /// アプリ起動時にフィラー音声を生成しプレイヤーに設定する初期化スクリプト。
    /// </summary>
    public class FillerInitializer : MonoBehaviour
    {
        [SerializeField] private FillerVoiceGenerator generator;
        [SerializeField] private FillerVoicePlayer player;

        private async void Start()
        {
            CancellationToken token = this.GetCancellationTokenOnDestroy();
            var clips = await generator.GenerateAsync(token);
            player.SetFillerClips(clips);
        }
    }
}