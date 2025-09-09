// FileSearchTool.cs
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
    /// OpenAI Responses API の file_search ツールを直接呼び出し、
    /// ベクターストア上のPDF等を参照して要約を返す ToolBase 実装。
    /// ネイティブプラットフォーム専用（WebGL は CORS のため不可）。
    /// </summary>
    public class FileSearchTool : ToolBase
    {
        // ───────── OpenAI 設定 ─────────
        [Header("OpenAI")]
        [Tooltip("ビルドに直書きせず、起動時入力や安全保管から注入してください。")]
        [SerializeField] private string openAIApiKey = "";
        [SerializeField] private string model = "gpt-4o";     // 軽量なら gpt-4o-mini
        [SerializeField] private int    timeoutSec = 45;

        // ───────── File Search 対象の Vector Store ─────────
        [Header("File Search")]
        [Tooltip("File Search が参照する Vector Store ID 群（vs_...）。複数指定可。")]
        [SerializeField] private List<string> vectorStoreIds = new List<string>();

        // ───────── Function Calling 用メタ ─────────
        [Header("Tool spec")]
        public string FunctionName = "file_search_answer";
        public string FunctionDescription =
            "指定のVector Storeにアップロード済みのファイル（PDF等）をFile Searchで検索し、質問に答える。";

        public override ILLMTool GetToolSpec()
        {
            var func = new LLMTool(FunctionName, FunctionDescription);
            func.AddProperty("query",  new Dictionary<string, object> { { "type", "string" } }); // 必須扱いはExecute側で
            func.AddProperty("locale", new Dictionary<string, object> { { "type", "string" } }); // 任意
            return func;
        }

        [Header("Debug")]
        [SerializeField] private bool logRequestBody = false;
        [SerializeField] private bool logRawResponse = true;

        // ───────── リクエスト / レスポンス型（必要最小限） ─────────
        [Serializable] class ReqMessage { public string role; public object[] content; }
        [Serializable] class InputItem { public string type = "input_text"; public string text; }

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
            public string type;    // "message" / "file_search_call" など
            public string status;
            public object action;
            public object[] content; // type=="message" の本文
        }
        [Serializable] class OutputItem
        {
            public string type;  // "output_text" など
            public string text;
        }

        // ───────── 本体 ─────────
        protected override async UniTask<ToolResponse> ExecuteFunction(
            string argumentsJsonString, CancellationToken token)
        {
            // 0) 前提チェック
            if (string.IsNullOrEmpty(openAIApiKey))
                return new ToolResponse("{\"error\":\"OPENAI_API_KEY not set\"}");
            if (vectorStoreIds == null || vectorStoreIds.Count == 0)
                return new ToolResponse("{\"error\":\"vector_store_ids not set\"}");

            // 1) 引数
            var args   = JsonConvert.DeserializeObject<Dictionary<string, string>>(argumentsJsonString ?? "{}");
            var query  = args != null && args.TryGetValue("query",  out var q) ? q : null;
            var locale = args != null && args.TryGetValue("locale", out var l) ? l : "ja-JP";
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResponse("{\"error\":\"query is required\"}");

            // 2) システムプロンプト
            const string sysPrompt =
                "あなたは厳密な根拠を示すアシスタントです。まず file_search ツールで参照資料を検索し、"
              + "根拠に基づいて簡潔に回答してください。出典が曖昧な場合は不確実と明示します。";

            // 3) Responses API ボディ作成（tools に file_search + vector_store_ids）
            var tools = new object[]
            {
                new Dictionary<string, object> {
                    { "type", "file_search" },
                    { "vector_store_ids", vectorStoreIds.ToArray() }
                }
            };

            var body = new ResponsesReq
            {
                model = model,
                input = new object[]
                {
                    new ReqMessage {
                        role = "system",
                        content = new object[] { new InputItem { text = sysPrompt } }
                    },
                    new ReqMessage {
                        role = "user",
                        content = new object[] { new InputItem { text = query } }
                    }
                },
                tools = tools,
                tool_choice = "auto"
            };

            var jsonBody = JsonConvert.SerializeObject(body);
            if (logRequestBody) Debug.Log($"[FileSearchTool] Request body ▼\n{jsonBody}");

            // 4) 送信
            using var req = new UnityWebRequest("https://api.openai.com/v1/responses", "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",  "application/json");
            req.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
            req.timeout = timeoutSec;

            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                token.ThrowIfCancellationRequested();
                await UniTask.Yield();
            }

            var raw = req.downloadHandler?.text ?? "";
            if (logRawResponse)
            {
                var preview = raw.Length > 4000 ? raw.Substring(0, 4000) + " …(truncated)" : raw;
                Debug.Log($"[FileSearchTool] Raw OpenAI response ▼\n{preview}");
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                var errMsg  = req.error ?? $"HTTP {req.responseCode}";
                var errJson = req.downloadHandler != null ? req.downloadHandler.text : "(no body)";
                Debug.LogError($"[FileSearchTool] HTTP error: {errMsg}\nResponse body:\n{errJson}");
                return new ToolResponse(JsonConvert.SerializeObject(new { error = errMsg }));
            }
            if (string.IsNullOrEmpty(raw))
                return new ToolResponse("{\"error\":\"empty response\"}");

            // 5) 応答解析（output[].type=='message' の content[].type=='output_text' を拾う）
            string text = null;
            try
            {
                var resp = JsonConvert.DeserializeObject<OpenAIResp>(raw);
                if (resp?.output != null)
                {
                    foreach (var entry in resp.output)
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
            }
            catch
            {
                return new ToolResponse("{\"error\":\"json parse error\"}");
            }

            if (string.IsNullOrWhiteSpace(text))
                text = "（該当資料から有用な回答が得られませんでした）";

            var payload = new { answer = text.Trim(), locale };
            return new ToolResponse(JsonConvert.SerializeObject(payload));
        }

        // ランタイム注入用
        public void SetOpenAIKeyAtRuntime(string key) => openAIApiKey = key;

        public void SetVectorStoreIds(params string[] ids)
        {
            vectorStoreIds = new List<string>(ids ?? Array.Empty<string>());
        }

        // ───────── 参考：PDFアップロード→Vector Storeに追加する簡易ユーティリティ ─────────
        // 注意: 初回のインデックス作成は非同期で進むため、実運用ではポーリング等で完了待ちを推奨。
        public async UniTask<(bool ok, string fileId, string error)> UploadAndAttachFile(string vectorStoreId, string localPath, CancellationToken token)
        {
            if (string.IsNullOrEmpty(openAIApiKey)) return (false, null, "OPENAI_API_KEY not set");
            if (string.IsNullOrEmpty(vectorStoreId)) return (false, null, "vector_store_id is empty");
            if (!System.IO.File.Exists(localPath)) return (false, null, $"file not found: {localPath}");

            // a) /v1/files へ multipart/form-data でアップロード（purpose=assistants）
            var form = new WWWForm();
            byte[] data = System.IO.File.ReadAllBytes(localPath);
            form.AddBinaryData("file", data, System.IO.Path.GetFileName(localPath), "application/pdf");
            form.AddField("purpose", "assistants");

            using (var up = UnityWebRequest.Post("https://api.openai.com/v1/files", form))
            {
                up.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
                up.timeout = timeoutSec;

                var send = up.SendWebRequest();
                while (!send.isDone)
                {
                    token.ThrowIfCancellationRequested();
                    await UniTask.Yield();
                }

                if (up.result != UnityWebRequest.Result.Success)
                {
                    return (false, null, $"upload error: {up.error} / body: {up.downloadHandler?.text}");
                }

                // 返ってくる JSON から file_id を取る
                string fileId;
                try
                {
                    var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(up.downloadHandler.text);
                    fileId = obj != null && obj.TryGetValue("id", out var v) ? v.ToString() : null;
                }
                catch { return (false, null, "upload: json parse error"); }

                if (string.IsNullOrEmpty(fileId)) return (false, null, "upload: file_id not found");

                // b) /v1/vector_stores/{id}/files にアタッチ
                var attachBody = JsonConvert.SerializeObject(new { file_id = fileId });
                using var attach = new UnityWebRequest($"https://api.openai.com/v1/vector_stores/{vectorStoreId}/files", "POST");
                attach.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(attachBody));
                attach.downloadHandler = new DownloadHandlerBuffer();
                attach.SetRequestHeader("Content-Type", "application/json");
                attach.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
                attach.timeout = timeoutSec;

                var op = attach.SendWebRequest();
                while (!op.isDone)
                {
                    token.ThrowIfCancellationRequested();
                    await UniTask.Yield();
                }

                if (attach.result != UnityWebRequest.Result.Success)
                {
                    return (false, null, $"attach error: {attach.error} / body: {attach.downloadHandler?.text}");
                }

                return (true, fileId, null);
            }
        }
    }
}
