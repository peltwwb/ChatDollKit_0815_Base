using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using ChatdollKit.SpeechSynthesizer;

namespace ChatdollKit.Filler
{
    /// <summary>
    /// 指定したテキストからフィラー用の音声クリップを生成するクラス。
    /// </summary>
    public class FillerVoiceGenerator : MonoBehaviour
    {
        [SerializeField] private NijiVoiceSpeechSynthesizer synthesizer;
        [SerializeField] private List<string> fillerTexts = new List<string>();

        /// <summary>
        /// 設定されたテキストからフィラークリップを生成して返す。
        /// </summary>
        public async UniTask<List<AudioClip>> GenerateAsync(CancellationToken token)
        {
            var clips = new List<AudioClip>();
            foreach (var text in fillerTexts)
            {
                var clip = await synthesizer.GetAudioClipAsync(text, new Dictionary<string, object>(), token);
                if (clip != null)
                {
                    clips.Add(clip);
                }
            }
            return clips;
        }
    }
}