using Perianth.Core.Audio;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Audio;

/// <summary>
/// The decoder's guards that do not need the external tool to run: a named
/// executable that is absent refuses as a request problem, not a fault.
/// </summary>
public sealed class VgmstreamDecoderTests
{
    [Fact]
    public void A_named_executable_that_does_not_exist_is_unsupported()
    {
        WemSelection wem = new("/tmp/whatever.wem", "english(us)");

        Result<AudioInfo> result = VgmstreamDecoder.Decode(wem, executable: "/definitely/not/a/real/vgmstream-cli");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }
}
