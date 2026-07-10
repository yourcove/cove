using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class ConfigurationDefaultsTests
{
    [Fact]
    public void ImageCardFitDefaultsToContainWhileVideoPreviewFitRemainsCover()
    {
        var configuration = new CoveConfiguration();
        var dto = new UiConfigDto();

        Assert.Equal("contain", configuration.Ui.ImageObjectFit);
        Assert.Equal("cover", configuration.Ui.VideoObjectFit);
        Assert.Equal("contain", dto.ImageObjectFit);
        Assert.Equal("cover", dto.VideoObjectFit);
    }
}
