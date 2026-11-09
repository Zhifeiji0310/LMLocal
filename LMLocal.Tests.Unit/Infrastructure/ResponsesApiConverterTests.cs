using System.Collections.Generic;
using LMLocal.Infrastructure.LlmApi.Converter;
using LMLocal.Infrastructure.LlmApi.Requests;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LMLocal.Tests.Unit
{
    [TestFixture]
    public class ResponsesApiConverterTests
    {
        [Test]
        public void ToRequestJson_MapsMessagesToolsAndToolResults()
        {
            var request = new SendChatRequest
            {
                Model = "gpt-6.1",
                Stream = true,
                MaxCompletionTokens = 2048,
                ReasoningEffort = "high",
                Messages = new List<Message>
                {
                    new Message { Role = "user", Content = "Inspect this code." },
                    new Message
                    {
                        Role = "assistant",
                        ToolCalls = new List<ToolCall>
                        {
                            new ToolCall
                            {
                                Id = "call_123",
                                Function = new FunctionCallDetails
                                {
                                    Name = "read_file",
                                    Arguments = "{\"path\":\"Program.cs\"}"
                                }
                            }
                        }
                    },
                    new Message { Role = "tool", ToolCallId = "call_123", Content = "file content" }
                },
                Tools = new List<OpenAiToolDefinition>
                {
                    new OpenAiToolDefinition
                    {
                        Function = new FunctionDefinition
                        {
                            Name = "read_file",
                            Parameters = new FunctionParameters
                            {
                                Type = "object",
                                Properties = new Dictionary<string, object>(),
                                Required = new List<string>()
                            }
                        }
                    }
                }
            };

            var json = JObject.Parse(ResponsesApiConverter.ToRequestJson(request));
            var input = (JArray)json["input"];

            Assert.That(json["model"].Value<string>(), Is.EqualTo("gpt-6.1"));
            Assert.That(json["store"].Value<bool>(), Is.False);
            Assert.That(json["stream"].Value<bool>(), Is.True);
            Assert.That(json["max_output_tokens"].Value<int>(), Is.EqualTo(2048));
            Assert.That(json["reasoning"]["effort"].Value<string>(), Is.EqualTo("high"));
            Assert.That(input[0]["role"].Value<string>(), Is.EqualTo("user"));
            Assert.That(input[1]["type"].Value<string>(), Is.EqualTo("function_call"));
            Assert.That(input[1]["call_id"].Value<string>(), Is.EqualTo("call_123"));
            Assert.That(input[2]["type"].Value<string>(), Is.EqualTo("function_call_output"));
            Assert.That(input[2]["call_id"].Value<string>(), Is.EqualTo("call_123"));
            Assert.That(json["tools"][0]["name"].Value<string>(), Is.EqualTo("read_file"));
            Assert.That(json["tools"][0]["type"].Value<string>(), Is.EqualTo("function"));
        }

        [Test]
        public void ToChatCompletionJson_MapsTextAndFunctionCalls()
        {
            const string response = "{\"id\":\"resp_1\",\"model\":\"gpt-6.1\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Ready\"}]},{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"Program.cs\\\"}\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";

            var json = JObject.Parse(ResponsesApiConverter.ToChatCompletionJson(response));
            var choice = json["choices"][0];
            Assert.That(choice["message"]["content"].Value<string>(), Is.EqualTo("Ready"));
            Assert.That(choice["message"]["tool_calls"][0]["id"].Value<string>(), Is.EqualTo("call_1"));
            Assert.That(choice["message"]["tool_calls"][0]["function"]["name"].Value<string>(), Is.EqualTo("read_file"));
            Assert.That(choice["finish_reason"].Value<string>(), Is.EqualTo("tool_calls"));
            Assert.That(json["usage"]["total_tokens"].Value<int>(), Is.EqualTo(15));
        }
    }
}
