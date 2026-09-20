using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class PictureTreatmentPlannerTests
{
    private static readonly IReadOnlyCollection<string> CatTokens =
        PictureTreatmentPlanner.TaskTokens("Read the Wikipedia article on cats", "Cat - Wikipedia");

    [Fact]
    public void Off_task_window_gets_the_heaviest_mosaic_everywhere()
    {
        var context = PictureSceneContext.OffTask(1280, 720);

        Assert.Equal(PictureTreatment.Mosaic, PictureTreatmentPlanner.Plan(new PixelRect(400, 200, 300, 200), context));
    }

    [Fact]
    public void Captioned_picture_about_the_task_is_only_softened()
    {
        var lines = new[]
        {
            new ScreenLine("A tabby cat resting on a wall", 420, 420, 260, 18),
            new ScreenLine("Unrelated navigation", 20, 100, 200, 18)
        };
        var context = new PictureSceneContext(true, CatTokens, lines, 1280, 720);

        Assert.Equal(PictureTreatment.Soften, PictureTreatmentPlanner.Plan(new PixelRect(400, 200, 300, 200), context));
    }

    [Fact]
    public void Picture_without_task_vocabulary_nearby_is_pixelated()
    {
        var lines = new[] { new ScreenLine("Photograph of a harbour at dusk", 420, 420, 260, 18) };
        var context = new PictureSceneContext(true, CatTokens, lines, 1280, 720);

        Assert.Equal(PictureTreatment.Pixelate, PictureTreatmentPlanner.Plan(new PixelRect(400, 200, 300, 200), context));
    }

    [Theory]
    [InlineData(200, 150, 728, 90)]
    [InlineData(1100, 150, 160, 600)]
    [InlineData(1050, 300, 300, 250)]
    public void Ad_shaped_pictures_in_rails_or_banners_get_the_mosaic_even_on_task(int x, int y, int width, int height)
    {
        var lines = new[] { new ScreenLine("cat", x, y + height + 10, width, 18) };
        var context = new PictureSceneContext(true, CatTokens, lines, 1280, 720);

        Assert.Equal(PictureTreatment.Mosaic, PictureTreatmentPlanner.Plan(new PixelRect(x, y, width, height), context));
    }

    [Fact]
    public void Cell_counts_decrease_with_heavier_treatment()
    {
        Assert.True(PictureTreatmentPlanner.CellsAcrossShortSide(PictureTreatment.Soften)
            > PictureTreatmentPlanner.CellsAcrossShortSide(PictureTreatment.Pixelate));
        Assert.True(PictureTreatmentPlanner.CellsAcrossShortSide(PictureTreatment.Pixelate)
            > PictureTreatmentPlanner.CellsAcrossShortSide(PictureTreatment.Mosaic));
    }
}
