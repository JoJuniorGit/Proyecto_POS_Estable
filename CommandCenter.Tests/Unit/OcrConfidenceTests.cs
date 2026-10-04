using Core.Common;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class OcrConfidenceTests
{
    [Fact]
    public void Bands_AreOrderedAndUnresolvedIsZero()
    {
        Assert.Equal(0m, OcrConfidence.Unresolved);
        Assert.True(OcrConfidence.Unresolved < OcrConfidence.RedBelow);
        Assert.True(OcrConfidence.RedBelow < OcrConfidence.YellowBelow);
    }
}
