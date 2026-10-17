using VoiceTyper.Asr;
using Xunit;

namespace VoiceTyper.Tests;

public class TextPostprocessorTests
{
    [Fact]
    public void StripsRichTags()
    {
        Assert.Equal("你好", TextPostprocessor.Process("<|zh|><|NEUTRAL|><|Speech|><|withitn|>你好"));
    }

    [Fact]
    public void ConvertsSentencePieceMarkerToSpace_ThenCollapsesAroundCjk()
    {
        var result = TextPostprocessor.Process("你好▁world▁再见");
        Assert.Equal("你好world再见", result);
    }

    [Fact]
    public void DropsPureWhitespaceOrPunctuationOnlyResult()
    {
        Assert.Equal("", TextPostprocessor.Process("<|zh|><|NEUTRAL|><|Speech|><|withitn|>。"));
    }

}
