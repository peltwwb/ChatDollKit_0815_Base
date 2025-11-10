// WebSearchTool.cs
using System;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using ChatdollKit.LLM;

namespace ChatdollKit.Examples.ChatGPT
{
    /// <summary>
    /// OpenAI Responses API の web_search ツールを直接呼び出し、
    /// 要約テキストを ChatDollKit に返す ToolBase 実装。
    /// ネイティブプラットフォーム専用（WebGL は CORS のため不可）。
    /// </summary>
    public class WebSearchTool : ToolBase
    {
        // ───────── OpenAI 設定 ─────────
        [Header("OpenAI")]
        [Tooltip("ビルドに直書きは推奨しません。起動時入力→安全保管を推奨。")]
        [SerializeField] private string openAIApiKey = "";
        [SerializeField] private string model = "gpt-4o";   // 軽量版なら gpt-4o-mini
        [SerializeField] private int    timeoutSec = 30;

        // ───────── Function Calling 用メタ ─────────
        [Header("Tool spec")]
        public string FunctionName = "web_search";
        public string FunctionDescription =
            "不確実・最新情報を含む質問に対しウェブ検索を行い、要約を返す。";

        public override ILLMTool GetToolSpec()
        {
            var func = new LLMTool(FunctionName, FunctionDescription);
            func.AddProperty("query",  new Dictionary<string, object> { { "type", "string" } });
            func.AddProperty("locale", new Dictionary<string, object> { { "type", "string" } });
            return func;   // AddRequired は存在しないので ExecuteFunction 側で必須チェック
        }

        // ───── ① 追加：Inspector から切り替えられるフラグ ─────
        [Header("Debug")]
        [SerializeField] private bool logRawResponse = true;


        // ───────── リクエスト / レスポンス型定義 (必要分だけ) ─────────
        [Serializable] class ReqMessage { public string role; public object[] content; }
        [Serializable] class InputItem
        {
            // Responses API expects the literal value "input_text" here
            public string type = "input_text";
            public string text;
        }
        [Serializable] class ResponsesReq
        {
            public string model;
            public object[] input;
            public object[] tools;
            public string tool_choice = "auto";
        }

        [Serializable] class OpenAIResp
        {
            public string id;
            public string @object;
            public OutputEntry[] output;
        }
        [Serializable] class OutputEntry
        {
            public string id;
            public string type;          // "message" or "web_search_call"
            public string status;
            public object action;        // present on web_search_call
            // For type=="message", content array is here, not nested
            public object[] content;
        }
        [Serializable] class OutputItem
        {
            public string type;   // e.g. "output_text"
            public string text;
        }

        // ───────── 本体 ─────────
        protected override async UniTask<ToolResponse> ExecuteFunction(
            string argumentsJsonString, CancellationToken token)
        {
            // 1) 引数パース & バリデーション
            var args   = JsonConvert.DeserializeObject<Dictionary<string, string>>(argumentsJsonString ?? "{}");
            var query  = args != null && args.TryGetValue("query",  out var q) ? q : null;
            var locale = args != null && args.TryGetValue("locale", out var l) ? l : "ja-JP";
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResponse("{\"error\":\"query is required\"}");
            if (string.IsNullOrEmpty(openAIApiKey))
                return new ToolResponse("{\"error\":\"OPENAI_API_KEY not set\"}");

            // 2) OpenAI Responses API リクエスト組み立て
            const string sysPrompt =
                "あなたは事実に厳密なアシスタントです。不明点や最新情報が含まれる場合は "
              + "web_search ツールを使い、簡潔な要約のみを返します。憶測で断定しません。"
              + "会話文の中で使用するので、箇条書きではなく、自然な文章で返してください。";

            var body = new ResponsesReq
            {
                model = model,
                input = new object[]
                {
                    new ReqMessage {
                        role = "system",
                        content = new object[] { new InputItem{ text = sysPrompt } }
                    },
                    new ReqMessage {
                        role = "user",
                        content = new object[] { new InputItem{ text = query } }
                    }
                },
                tools = new object[] { new Dictionary<string, object>{{"type","web_search"}} }
                // tool_choice は既定 "auto"
            };
            var jsonBody = JsonConvert.SerializeObject(body);
            if (logRawResponse)
            {
                Debug.Log($"[WebSearchTool] Request body ▼\n{jsonBody}");
            }

            using var req = new UnityWebRequest("https://api.openai.com/v1/responses", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",  "application/json");
            req.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
            req.timeout = timeoutSec;

            // 3) 送信
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                token.ThrowIfCancellationRequested();
                await UniTask.Yield();
            }

            var raw = req.downloadHandler.text;

            if (logRawResponse)
            {
                var preview = raw.Length > 2048 ? raw.Substring(0, 2048) + " …(truncated)" : raw;
                Debug.Log($"[WebSearchTool] Raw OpenAI response ▼\n{preview}");
            }

            if (string.IsNullOrEmpty(raw))
            {
                return new ToolResponse("{\"error\":\"empty response\"}");
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                var errMsg = req.error ?? $"HTTP {req.responseCode}";
                var errJson = req.downloadHandler != null ? req.downloadHandler.text : "(no body)";
                Debug.LogError($"[WebSearchTool] HTTP error: {errMsg}\nResponse body:\n{errJson}");
                return new ToolResponse(JsonConvert.SerializeObject(new { error = errMsg }));
            }

            // 4) レスポンス解析
            OpenAIResp respObj;
            try { respObj = JsonConvert.DeserializeObject<OpenAIResp>(raw); }
            catch { return new ToolResponse("{\"error\":\"json parse error\"}"); }

            string text = null;

            if (respObj?.output != null)
            {
                foreach (var entry in respObj.output)
                {
                    if (entry.type == "message" && entry.content != null)
                    {
                        foreach (var itm in entry.content)
                        {
                            try
                            {
                                var outItem = JsonConvert.DeserializeObject<OutputItem>(itm.ToString());
                                if (outItem != null && outItem.type == "output_text")
                                {
                                    text = outItem.text;
                                    break;
                                }
                            }
                            catch { /* ignore */ }
                        }
                    }
                    if (text != null) break;
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                text = "（検索結果なし）";

            // 5) ChatDollKit へ返却
            var payload = new { answer = text.Trim(), locale };
            return new ToolResponse(JsonConvert.SerializeObject(payload));
        }

        // 追加：実行時に API キーを注入したいとき用
        public void SetOpenAIKeyAtRuntime(string key) => openAIApiKey = key;
    }
}
