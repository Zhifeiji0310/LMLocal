using LMLocal.Core.Models;
using LMLocal.Infrastructure.Streaming;
using NUnit.Framework;

namespace LMLocal.Tests.Unit
{
    [TestFixture]
    public class ResponsesApiSseParserTests
    {
        private LlmSseParser _parser;

        [SetUp]
        public void SetUp()
        {
            _parser = new LlmSseParser();
        }

        [Test]
        public void ExtractDeltas_ParsesResponsesTextDelta()
        {
            var chunks = _parser.ExtractDeltas("data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}");
            Assert.That(chunks, Has.Count.EqualTo(1));
            Assert.That(chunks[0], Is.TypeOf<TextStreamChunk>());
            Assert.That(((TextStreamChunk)chunks[0]).Text, Is.EqualTo("hello"));
            Assert.That(chunks[0].Kind, Is.EqualTo(ChunkKind.Content));
        }

        [Test]
        public void ExtractDeltas_ParsesResponsesFunctionCallMetadataAndArguments()
        {
            var metadata = _parser.ExtractDeltas("data: {\"type\":\"response.output_item.added\",\"output_index\":2,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"read_file\",\"arguments\":\"\"}}");
            var delta = _parser.ExtractDeltas("data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":2,\"delta\":\"{\\\"path\\\":\\\"a.cs\\\"}\"}");

            Assert.That(metadata[0], Is.TypeOf<ToolCallMetadataChunk>());
            var tool = (ToolCallMetadataChunk)metadata[0];
            Assert.That(tool.Index, Is.EqualTo(2));
            Assert.That(tool.CallId, Is.EqualTo("call_1"));
            Assert.That(tool.FunctionName, Is.EqualTo("read_file"));
            Assert.That(delta[0], Is.TypeOf<TextStreamChunk>());
            Assert.That(((TextStreamChunk)delta[0]).ToolCallIndex, Is.EqualTo(2));
        }

        [Test]
        public void ExtractDeltas_ParsesResponsesCompletedUsage()
        {
            var chunks = _parser.ExtractDeltas("data: {\"type\":\"response.completed\",\"response\":{\"output\":[],\"usage\":{\"input_tokens\":12,\"output_tokens\":8}}}");
            Assert.That(chunks[0], Is.TypeOf<CompletionStreamChunk>());
            var completion = (CompletionStreamChunk)chunks[0];
            Assert.That(completion.TotalTokens, Is.EqualTo(20));
            Assert.That(completion.PromptTokens, Is.EqualTo(12));
            Assert.That(completion.CompletionTokens, Is.EqualTo(8));
        }
    }
}
