using Perianth.Formats.Diagnostics;
using Perianth.Pipeline;
using Xunit;

namespace Perianth.Tests.Pipeline;

/// <summary>
/// Checks the rules between an export's settings, reached the way a window
/// reaches them: by filling in a request, with no command line involved.
/// </summary>
/// <remarks>
/// The point of the rules living beside the request rather than in the argument
/// grammar. A front end that had to restate them would restate them differently,
/// and the refusals are the most useful thing the tool produces.
/// </remarks>
public sealed class ExportRequestTests
{
    private static ExportRequest Minimal() => new()
    {
        Mmb = "model.mmb",
        Cameldata = "model.cameldata",
        Out = "model.glb",
    };

    [Fact]
    public void A_request_naming_only_the_geometry_is_complete()
    {
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal());

        Assert.False(validated.IsRefused, validated.IsRefused ? validated.Refusal.Message : null);
    }

    [Fact]
    public void An_atlas_without_its_state_is_refused_by_name()
    {
        // The refusal names the companion setting rather than the failure, which
        // is what lets a window say which field to fill in next.
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal() with
        {
            SetupAnim = "setup.anim",
            MouthAnim = "mouth.anim",
        });

        Assert.True(validated.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, validated.Refusal.Kind);
        Assert.Contains("--mouth-state", validated.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_outside_its_atlas_vocabulary_is_refused()
    {
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal() with
        {
            SetupAnim = "setup.anim",
            EyebrowsAnim = "eyebrows.anim",
            EyebrowState = 7,
        });

        Assert.True(validated.IsRefused);
        Assert.Contains("1..6", validated.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_facial_atlas_still_needs_a_hierarchy_to_overlay()
    {
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal() with
        {
            PupilsAnim = "pupils.anim",
            PupilState = 3,
        });

        Assert.True(validated.IsRefused);
        Assert.Contains("--setup-anim", validated.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_the_output_over_an_input_is_refused()
    {
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal() with { Out = "model.mmb" });

        Assert.True(validated.IsRefused);
        Assert.Contains("also an input", validated.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_lip_sync_schedule_and_a_fixed_mouth_contradict_each_other()
    {
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal() with
        {
            SetupAnim = "setup.anim",
            MouthAnim = "mouth.anim",
            MouthState = 5,
            LipsyncDatabase = "lipsync.mlipsyncdatabase",
            SpeechId = "17780",
        });

        Assert.True(validated.IsRefused);
        Assert.Contains("--mouth-state", validated.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Materials_without_anywhere_to_read_textures_from_are_refused()
    {
        Result<ExportRequest> validated = ExportRequest.Validate(Minimal() with { Editordata = "model.editordata" });

        Assert.True(validated.IsRefused);
        Assert.Contains("--content-root", validated.Refusal.Message, System.StringComparison.Ordinal);
    }
}
