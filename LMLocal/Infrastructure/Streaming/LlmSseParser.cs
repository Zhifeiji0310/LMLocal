using System.Collections.Generic;
using LMLocal.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LMLocal.Infrastructure.Streaming
{
    /// <summary>
    /// Parser for Server-Sent Events (SSE) streams from OpenAI-compatible providers.
    /// Handles JSON tool_calls in delta.tool_calls.
    /// 
    /// Returns derived StreamChunk types:
    /// - TextStreamChunk for content/reasoning/tool arguments
    /// - ToolCallMetadataChunk for tool metadata
    /// - CompletionStreamChunk for final data
    /// </summary>
    internal class LlmSseParser
    {
        private const string DoneMarker = "data: [DONE]";
        private const string DataPrefix = "data: ";

        /// <summary>
        /// Extracts all completed chunks from an SSE line, including buffered XML tool calls.
        /// </summary>
        public List<StreamChunk> ExtractDeltas(string line)
        {
            var chunks = new List<StreamChunk>();

            if (line == DoneMarker)
            {
                chunks.Add(new CompletionStreamChunk(finishReason: "stop"));
                return chunks;
            }

            if (!line.StartsWith(DataPrefix))
            {
                return chunks;
            }

            var dataJson = line.Substring(DataPrefix.Length).Trim();
            if (string.IsNullOrEmpty(dataJson))
            {
                return chunks;
            }

            JObject json;
            try
            {
                json = JObject.Parse(dataJson);
            }
            catch
            {
                return chunks;
            }

            if (json["error"] is JObject err)
            {
                chunks.Add(new ErrorStreamChunk(
                    err["message"]?.ToString() ?? json["message"]?.ToString(),
                    err["type"]?.ToString(),
                    err["code"]?.ToString()));
                return chunks;
            }

            var responseEventType = json["type"]?.ToString();
            if (!string.IsNullOrEmpty(responseEventType))
            {
                return ExtractResponsesDeltas(json, responseEventType);
            }


            var contentChunks = ExtractStreamContents(json);
            if (contentChunks.Count > 0)
            {
                chunks.AddRange(contentChunks);
            }

            if (json["choices"] is JArray choicesToken && choicesToken.Count > 0)
            {
                var firstChoice = choicesToken[0] as JObject;
                var finishReason = firstChoice?["finish_reason"]?.ToString();
                if (!string.IsNullOrEmpty(finishReason))
                {
                    var usage = ExtractUsage(json);
                    chunks.Add(new CompletionStreamChunk(
                        finishReason: finishReason,
                        totalTokens: usage?.TotalTokens,
                        promptTokens: usage?.PromptTokens,
                        completionTokens: usage?.CompletionTokens,
                        reasoningTokens: usage?.ReasoningTokens,
                        cachedTokens: usage?.CachedTokens,
                        systemFingerprint: usage?.SystemFingerprint));
                    return chunks;
                }
            }

            if (chunks.Count == 0)
            {
                if (json["usage"] != null && (!(json["choices"] is JArray choices) || choices.Count == 0))
                {
                    var usageChunk = ExtractUsage(json);
                    if (usageChunk != null)
                    {
                        chunks.Add(usageChunk);
                    }
                    return chunks;
                }
            }

            return chunks;
        }

        private List<StreamChunk> ExtractResponsesDeltas(JObject json, string eventType)
        {
            var chunks = new List<StreamChunk>();
            switch (eventType)
            {
                case "response.output_text.delta":
                    if (!string.IsNullOrEmpty(json["delta"]?.ToString()))
                        chunks.Add(new TextStreamChunk(json["delta"].ToString(), ChunkKind.Content));
                    break;

                case "response.reasoning_text.delta":
                case "response.reasoning_summary_text.delta":
                    if (!string.IsNullOrEmpty(json["delta"]?.ToString()))
                        chunks.Add(new TextStreamChunk(json["delta"].ToString(), ChunkKind.Reasoning));
                    break;

                case "response.output_item.added":
                case "response.output_item.done":
                    var item = json["item"] as JObject;
                    if (item?["type"]?.ToString() == "function_call")
                    {
                        var index = json["output_index"]?.Value<int?>() ?? 0;
                        var callId = item["call_id"]?.ToString() ?? item["id"]?.ToString();
                        var name = item["name"]?.ToString();
                        // The added event contains metadata. The done event can contain the full
                        // arguments, but argument deltas have already been accumulated by then.
                        var initialArguments = eventType == "response.output_item.added"
                            ? item["arguments"]?.ToString()
                            : null;
                        chunks.Add(new ToolCallMetadataChunk(index, callId, name, initialArguments));
                    }
                    break;

                case "response.function_call_arguments.delta":
                    if (!string.IsNullOrEmpty(json["delta"]?.ToString()))
                        chunks.Add(new TextStreamChunk(
                            json["delta"].ToString(),
                            ChunkKind.ToolCallArguments,
                            json["output_index"]?.Value<int?>() ?? 0));
                    break;

                case "response.completed":
                    var response = json["response"] as JObject;
                    var usage = response?["usage"] as JObject;
                    var inputTokens = usage?["input_tokens"]?.Value<int?>();
                    var outputTokens = usage?["output_tokens"]?.Value<int?>();
                    var reasoningTokens = usage?["output_tokens_details"]?["reasoning_tokens"]?.Value<int?>();
                    var cachedTokens = usage?["input_tokens_details"]?["cached_tokens"]?.Value<int?>();
                    var hasToolCalls = response?["output"] is JArray output &&
                        output.OfType<JObject>().Any(x => x["type"]?.ToString() == "function_call");
                    chunks.Add(new CompletionStreamChunk(
                        finishReason: hasToolCalls ? "tool_calls" : "stop",
                        totalTokens: inputTokens.HasValue || outputTokens.HasValue
                            ? (inputTokens ?? 0) + (outputTokens ?? 0)
                            : (int?)null,
                        promptTokens: inputTokens,
                        completionTokens: outputTokens,
                        reasoningTokens: reasoningTokens,
                        cachedTokens: cachedTokens));
                    break;

                case "response.incomplete":
                    chunks.Add(new CompletionStreamChunk(finishReason: "length"));
                    break;

                case "response.failed":
                    var failedError = json["response"]?["error"] ?? json["error"];
                    chunks.Add(new ErrorStreamChunk(
                        failedError?["message"]?.ToString() ?? "The Responses API request failed.",
                        failedError?["type"]?.ToString(),
                        failedError?["code"]?.ToString()));
                    break;

                case "error":
                    var error = json["error"] as JObject ?? json;
                    chunks.Add(new ErrorStreamChunk(
                        error["message"]?.ToString() ?? "The Responses API stream returned an error.",
                        error["type"]?.ToString(),
                        error["code"]?.ToString()));
                    break;
            }

            return chunks;
        }

        private CompletionStreamChunk ExtractUsage(JObject json)
        {
            var usageToken = json["usage"];
            if (usageToken == null || usageToken.Type != JTokenType.Object)
                return null;

            var usage = (JObject)usageToken;

            int? totalTokens = usage["total_tokens"]?.Value<int?>();
            int? promptTokens = usage["prompt_tokens"]?.Value<int?>();
            int? completionTokens = usage["completion_tokens"]?.Value<int?>();

            int? reasoningTokens = null;
            var detailsToken = usage["completion_tokens_details"];
            if (detailsToken?.Type == JTokenType.Object)
            {
                reasoningTokens = detailsToken["reasoning_tokens"]?.Value<int?>();
            }

            int? cachedTokens = null;
            var promptDetails = usage["prompt_tokens_details"];
            if (promptDetails?.Type == JTokenType.Object)
            {
                cachedTokens = promptDetails["cached_tokens"]?.Value<int?>();
            }

            string systemFingerprint = json["system_fingerprint"]?.ToString();

            return new CompletionStreamChunk(
                totalTokens: totalTokens,
                promptTokens: promptTokens,
                completionTokens: completionTokens,
                reasoningTokens: reasoningTokens,
                cachedTokens: cachedTokens,
                systemFingerprint: systemFingerprint);
        }

        /// <summary>
        /// Extracts all completed chunks from JSON, including handling tool call blocks.
        /// </summary>
        private List<StreamChunk> ExtractStreamContents(JObject json)
        {
            var chunks = new List<StreamChunk>();

            if (!(json["choices"] is JArray choices) || choices.Count == 0)
            {
                return chunks;
            }

            var delta = choices[0]?["delta"];
            if (delta == null || delta.Type == JTokenType.Null)
            {
                return chunks;
            }

            if (delta["tool_calls"] is JArray toolCallsArray && toolCallsArray.Count > 0)
            {
                foreach (var toolCallToken in toolCallsArray)
                {
                    if (!(toolCallToken is JObject toolCall)) continue;

                    var index = toolCall["index"]?.Value<int?>();
                    var callId = toolCall["id"]?.ToString();
                    var functionName = toolCall["function"]?["name"]?.ToString();
                    var arguments = toolCall["function"]?["arguments"]?.ToString();

                    if (!string.IsNullOrEmpty(functionName) || !string.IsNullOrEmpty(callId))
                    {
                        var extraContentToken = toolCall["extra_content"];
                        var extraContentJson = extraContentToken != null && extraContentToken.Type != JTokenType.Null
                            ? extraContentToken.ToString(Formatting.None)
                            : null;

                        chunks.Add(new ToolCallMetadataChunk(
                            index ?? 0,
                            callId,
                            functionName,
                            initialArguments: arguments,
                            extraContentJson: extraContentJson));
                    }
                    else if (!string.IsNullOrEmpty(arguments))
                    {
                        chunks.Add(new TextStreamChunk(arguments, ChunkKind.ToolCallArguments, index));
                    }
                }

                return chunks;
            }

            var reasoning = delta["reasoning_content"]?.ToString();
            if (!string.IsNullOrEmpty(reasoning))
            {
                chunks.Add(new TextStreamChunk(reasoning, ChunkKind.Reasoning));
                return chunks;
            }

            var refusal = delta["refusal"]?.ToString();
            if (!string.IsNullOrEmpty(refusal))
            {
                chunks.Add(new TextStreamChunk(refusal, ChunkKind.Content));
            }

            var content = delta["content"]?.ToString();
            if (!string.IsNullOrEmpty(content))
            {
                chunks.Add(new TextStreamChunk(content, ChunkKind.Content));
            }

            return chunks;
        }
    }
}
