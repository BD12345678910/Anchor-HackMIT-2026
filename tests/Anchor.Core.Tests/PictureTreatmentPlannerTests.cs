using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class PictureTreatmentPlannerTests
{
    private static readonly IReadOnlyCollection<string> CatTokens =
        PictureTreatmentPlanner.TaskTokens("Read the Wikipedia article on cats", "Cat - Wikipedia");

    private static PictureGradingRequest Request(params PictureDescriptor[] pictures) =>
        new("Read the Wikipedia article on cats", "Read the Behaviour section", "chrome", "Cat - Wikipedia", "Cats are small carnivorous mammals", pictures);

    [Fact]
    public void Off_task_window_gets_the_heaviest_mosaic_whatever_the_verdict()
    {
        var context = PictureSceneContext.OffTask(1280, 720);
        var picture = PictureTreatmentPlanner.Describe(new PixelRect(400, 200, 300, 200), context);

        Assert.Equal(PictureTreatment.Mosaic, PictureTreatmentPlanner.Plan(picture, context, PictureRelevance.Illustrates));
        Assert.Equal(PictureTreatment.Mosaic, PictureTreatmentPlanner.Plan(picture, context, null));
    }

    [Fact]
    public void Descriptor_carries_the_caption_and_keys_on_its_text()
    {
        var lines = new[]
        {
            new ScreenLine("A tabby cat resting on a wall", 420, 420, 260, 18),
            new ScreenLine("Unrelated navigation", 20, 100, 200, 18)
        };
        var context = new PictureSceneContext(true, CatTokens, lines, 1280, 720);

        var picture = PictureTreatmentPlanner.Describe(new PixelRect(400, 200, 300, 200), context);
        var again = PictureTreatmentPlanner.Describe(new PixelRect(402, 205, 300, 200), context);

        Assert.Equal("A tabby cat resting on a wall", picture.NearbyText);
        Assert.False(picture.AdShaped);
        Assert.Equal(picture.Key, again.Key);
    }

    [Fact]
    public void Verdicts_map_to_treatments_and_pending_keeps_pictures_readable()
    {
        var context = new PictureSceneContext(true, CatTokens, [], 1280, 720);
        var picture = PictureTreatmentPlanner.Describe(new PixelRect(400, 200, 300, 200), context);

        Assert.Equal(PictureTreatment.Soften, PictureTreatmentPlanner.Plan(picture, context, PictureRelevance.Illustrates));
        Assert.Equal(PictureTreatment.Pixelate, PictureTreatmentPlanner.Plan(picture, context, PictureRelevance.Unrelated));
        Assert.Equal(PictureTreatment.Mosaic, PictureTreatmentPlanner.Plan(picture, context, PictureRelevance.Bait));
        Assert.Equal(PictureTreatment.Soften, PictureTreatmentPlanner.Plan(picture, context, null));
    }

    [Theory]
    [InlineData(200, 150, 728, 90)]
    [InlineData(1100, 150, 160, 600)]
    [InlineData(1050, 300, 300, 250)]
    public void Ad_shaped_pictures_are_flagged_and_mosaicked_while_pending(int x, int y, int width, int height)
    {
        var context = new PictureSceneContext(true, CatTokens, [], 1280, 720);
        var picture = PictureTreatmentPlanner.Describe(new PixelRect(x, y, width, height), context);

        Assert.True(picture.AdShaped);
        Assert.Equal(PictureTreatment.Mosaic, PictureTreatmentPlanner.Plan(picture, context, null));
    }

    [Fact]
    public void Local_grade_uses_task_vocabulary_and_ad_shape()
    {
        var cat = new PictureDescriptor("cat", "A tabby cat resting on a wall", false, 300, 200);
        var harbour = new PictureDescriptor("harbour", "Photograph of a harbour at dusk", false, 300, 200);
        var banner = new PictureDescriptor("banner", "cat food sale", true, 728, 90);
        var uncaptioned = new PictureDescriptor("plain", "", false, 300, 200);

        var grading = PictureTreatmentPlanner.LocalGrade(Request(cat, harbour, banner, uncaptioned));

        Assert.True(grading.IsFallback);
        Assert.Equal(PictureRelevance.Illustrates, grading.Verdicts["cat"]);
        Assert.Equal(PictureRelevance.Unrelated, grading.Verdicts["harbour"]);
        Assert.Equal(PictureRelevance.Bait, grading.Verdicts["banner"]);
        Assert.Equal(PictureRelevance.Illustrates, grading.Verdicts["plain"]);
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
