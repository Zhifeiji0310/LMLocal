using System;
using System.Collections.Generic;
using System.Linq;
using LMLocal.Infrastructure.LlmApi.Requests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LMLocal.Infrastructure.LlmApi.Converter
{
    /// <summary>
    /// Converts the existing internal Chat Completions request/response model to and from
    /// the OpenAI Responses API wire format. The rest of the chat orchestration can therefore
    /// continue to use its established SendChatResponse and tool-call models.
    /// </summary>
    internal static class ResponsesApiConverter
    {
        public static string ToRequestJson(SendChatRequest request)
        {
            var input = new JArray();

            foreach (var message in request.Messages ?? new List<Message>())
            {
                if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    input.Add(new JObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.ToolCallId,
                        ["output"] = ContentToString(message.Content)
                    });
                    continue;
                }

                var content = message.Content == null ? null : JToken.FromObject(message.Content);
                if (content != null && content.Type != JTokenType.Null)
                {
                    input.Add(new JObject
                    {
                        ["type"] = "message",
                        ["role"] = message.Role,
                        ["content"] = ConvertMessageContent(content)
                    });
                }

                if (message.ToolCalls != null)
                {
                    foreach (var call in message.ToolCalls)
                    {
                        if (call?.Function == null) continue;
                        input.Add(new JObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id,
                            ["name"] = call.Function.Name,
                            ["arguments"] = call.Function.Arguments ?? "{}"
                        });
                    }
                }
            }

            var body = new JObject
            {
                ["model"] = request.Model,
                ["input"] = input,
                ["store"] = false
            };

            if (request.Stream.HasValue) body["stream"] = request.Stream.Value;
            if (request.MaxCompletionTokens.HasValue) body["max_output_tokens"] = request.MaxCompletionTokens.Value;
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
                body["reasoning"] = new JObject { ["effort"] = request.ReasoningEffort };

            // Sampling parameters are not accepted by all reasoning models.
            if (string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
                if (request.TopP.HasValue) body["top_p"] = request.TopP.Value;
            }

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = new JArray();
                foreach (var tool in request.Tools)
                {
                    if (tool?.Function == null) continue;
                    var function = new JObject
                    {
                        ["type"] = "function",
                        ["name"] = tool.Function.Name,
                        ["strict"] = false
                    };
                    if (!string.IsNullOrWhiteSpace(tool.Function.Description))
                        function["description"] = tool.Function.Description;
                    if (tool.Function.Parameters != null)
                        function["parameters"] = JObject.FromObject(tool.Function.Parameters);
                    tools.Add(function);
                }
                if (tools.Count > 0)
                {
                    body["tools"] = tools;
                    body["tool_choice"] = "auto";
                    body["parallel_tool_calls"] = request.ParallelToolCalls ?? true;
                }
            }

            return body.ToString(Formatting.None);
        }

        public static string ToChatCompletionJson(string responseJson)
        {
            var response = JObject.Parse(responseJson);
            var toolCalls = new JArray();
            var text = new System.Text.StringBuilder();
            var output = response["output"] as JArray;

            if (output != null)
            {
                foreach (var item in output.OfType<JObject>())
                {
                    var type = item["type"]?.ToString();
                    if (type == "message")
                    {
                        var contents = item["content"] as JArray;
                        if (contents == null) continue;
                        foreach (var part in contents.OfType<JObject>())
                        {
                            if (part["type"]?.ToString() == "output_text")
                                text.Append(part["text"]?.ToString());
                            else if (part["type"]?.ToString() == "refusal")
                                text.Append(part["refusal"]?.ToString());
                        }
                    }
                    else if (type == "function_call")
                    {
                        toolCalls.Add(new JObject
                        {
                            ["id"] = item["call_id"] ?? item["id"],
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = item["name"],
                                ["arguments"] = item["arguments"] ?? "{}"
                            }
                        });
                    }
                }
            }

            var usage = response["usage"] as JObject;
            var promptTokens = usage?["input_tokens"]?.Value<int?>();
            var completionTokens = usage?["output_tokens"]?.Value<int?>();

            var message = new JObject
            {
                ["role"] = "assistant",
                ["content"] = text.Length == 0 ? JValue.CreateNull() : new JValue(text.ToString())
            };
            if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;

            var completion = new JObject
            {
                ["id"] = response["id"],
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = response["model"],
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = toolCalls.Count > 0 ? "tool_calls" : "stop"
                })
            };

            if (usage != null)
            {
                var mappedUsage = new JObject();
                if (promptTokens.HasValue) mappedUsage["prompt_tokens"] = promptTokens.Value;
                if (completionTokens.HasValue) mappedUsage["completion_tokens"] = completionTokens.Value;
                if (promptTokens.HasValue || completionTokens.HasValue)
                    mappedUsage["total_tokens"] = (promptTokens ?? 0) + (completionTokens ?? 0);

                var details = usage["output_tokens_details"] as JObject;
                if (details?["reasoning_tokens"] != null)
                    mappedUsage["completion_tokens_details"] = new JObject { ["reasoning_tokens"] = details["reasoning_tokens"] };
                var inputDetails = usage["input_tokens_details"] as JObject;
                if (inputDetails?["cached_tokens"] != null)
                    mappedUsage["prompt_tokens_details"] = new JObject { ["cached_tokens"] = inputDetails["cached_tokens"] };
                completion["usage"] = mappedUsage;
            }

            return completion.ToString(Formatting.None);
        }

        private static JToken ConvertMessageContent(JToken content)
        {
            if (!(content is JArray parts)) return content;

            var converted = new JArray();
            foreach (var part in parts)
            {
                if (part is JObject obj && obj["type"]?.ToString() == "image_url")
                {
                    var imageUrl = obj["image_url"] is JObject imageObject
                        ? imageObject["url"]?.ToString()
                        : obj["image_url"]?.ToString();
                    converted.Add(new JObject { ["type"] = "input_image", ["image_url"] = imageUrl });
                }
                else if (part is JObject textPart && textPart["type"]?.ToString() == "text")
                {
                    converted.Add(new JObject { ["type"] = "input_text", ["text"] = textPart["text"] });
                }
                else
                {
                    converted.Add(part.DeepClone());
                }
            }
            return converted;
        }

        private static string ContentToString(object content)
        {
            if (content == null) return string.Empty;
            if (content is string text) return text;
            return JToken.FromObject(content).ToString(Formatting.None);
        }
    }
}
