using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Localization;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Publishing;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class TranslationParsingTests
{
    [Fact]
    public void ReadsBackOneLinePerNumberedInput()
    {
        var reply = "1. Primera linea\n2. Segunda linea\n3. Tercera linea";

        var parsed = ChatTranslator.ParseNumbered(reply, 3);

        Assert.Equal(new[] { "Primera linea", "Segunda linea", "Tercera linea" }, parsed);
    }

    [Fact]
    public void IgnoresAPreambleTheModelAddedAnyway()
    {
        var reply = "Sure, here are the translations:\n\n1. Uno\n2. Dos";

        var parsed = ChatTranslator.ParseNumbered(reply, 2);

        Assert.Equal(new[] { "Uno", "Dos" }, parsed);
    }

    [Fact]
    public void DropsLinesNumberedOutsideTheBatch()
    {
        // A model that invents a line 4 for a batch of 2 must not silently extend the cue list.
        var reply = "1. Uno\n2. Dos\n4. Cuatro";

        var parsed = ChatTranslator.ParseNumbered(reply, 2);

        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void AMissingLineIsReportedAsShortRatherThanPaddedOver()
    {
        // Caption timings belong to the recording, so a missing line has to surface as a count
        // mismatch — padding it would slide every later cue onto the wrong moment.
        var parsed = ChatTranslator.ParseNumbered("1. Uno\n3. Tres", 3);

        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void PromptDemandsTheLineCountAndForbidsMerging()
    {
        var prompt = ChatTranslator.BuildPrompt(new[] { "one", "two" }, "Spanish", "en");

        Assert.Contains("exactly one translated line per numbered input line", prompt);
        Assert.Contains("Never merge two lines or split one", prompt);
        Assert.Contains("1. one", prompt);
        Assert.Contains("2. two", prompt);
    }

    [Theory]
    [InlineData("Spanish", "spanish")]
    [InlineData("Brazilian Portuguese", "brazilian-portuguese")]
    [InlineData("", "translated")]
    public void LanguageNamesSurviveBecomingFilenames(string language, string expected)
    {
        Assert.Equal(expected, SubtitleTranslationService.Slug(language));
    }
}

public class ScriptCloneTests
{
    [Fact]
    public void CloningAScriptLeavesTheOriginalAlone()
    {
        var script = new ScriptDocument { Title = "Original", Language = "en" };
        script.Beats.Add(new ScriptBeat { Role = "hook", Narration = "English words", Keyword = "measure" });

        var clone = script.Clone();
        clone.Language = "es";
        clone.Beats[0].Narration = "Palabras en espanol";

        // The delivered original is still built from the document this was cloned from.
        Assert.Equal("English words", script.Beats[0].Narration);
        Assert.Equal("en", script.Language);
        Assert.Equal("measure", clone.Beats[0].Keyword);
    }
}

public class DubJobTests
{
    private static JobManifest Job()
    {
        var job = new JobManifest { JobId = "demo" };
        job.Creative.Language = "en";
        job.Voice.Provider = "espeak-ng";
        job.Voice.Sections.Add(new VoiceSection { Index = 0, Text = "English words" });
        job.Plan.Chapters.Add(new PlanChapter { Index = 0, Title = "Hook" });
        return job;
    }

    [Fact]
    public void ADubStartsWithNoSpokenSegments()
    {
        // Narration reuse is keyed by segment index. Carrying the original's sections over would
        // hand the dub English audio as "already spoken" for a language it is not in.
        var dub = Job().CloneForDub("Spanish");

        Assert.Empty(dub.Voice.Sections);
        Assert.Equal("Spanish", dub.Creative.Language);
    }

    [Fact]
    public void ADubDoesNotInheritTheOriginalsMeasuredChapters()
    {
        var dub = Job().CloneForDub("Spanish");

        // Every duration changes when the words change, so the chapters are re-measured.
        Assert.Empty(dub.Plan.Chapters);
    }

    [Fact]
    public void CloningForADubDoesNotDisturbTheOriginal()
    {
        var job = Job();

        job.CloneForDub("Spanish");

        Assert.Single(job.Voice.Sections);
        Assert.Equal("en", job.Creative.Language);
    }

    [Fact]
    public void AVariantGetsItsOwnWorkingTreeInsideTheJob()
    {
        var paths = new Lanshu.Presenter.Core.Jobs.JobPaths("/tmp/demo-job");

        var variant = paths.ForVariant("Spanish");

        Assert.Contains("variants", variant.Root);
        Assert.Contains("spanish", variant.Root);
        Assert.NotEqual(paths.AudioReference, variant.AudioReference);
    }
}

public class PublishGateTests
{
    private static PublishPlan Plan() => new()
    {
        Destination = "channel",
        Endpoint = "https://example.invalid/upload",
        Title = "Measuring what changed",
        Visibility = "private",
        VideoPath = "/jobs/demo/outputs/job-master.mp4",
        VideoBytes = 8_000_000,
    };

    [Fact]
    public void TheSamePlanAlwaysFingerprintsTheSame()
    {
        Assert.Equal(PublishService.Fingerprint(Plan()), PublishService.Fingerprint(Plan()));
    }

    [Fact]
    public void ChangingTheVisibilityRetiresAnApproval()
    {
        var approved = PublishService.Fingerprint(Plan());

        var wider = Plan();
        wider.Visibility = "public";

        // Approving a private upload must never authorize a public one.
        Assert.NotEqual(approved, PublishService.Fingerprint(wider));
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Destination")]
    [InlineData("Endpoint")]
    public void ChangingWhatIsSentOrWhereRetiresAnApproval(string field)
    {
        var approved = PublishService.Fingerprint(Plan());

        var changed = Plan();
        switch (field)
        {
            case "Title": changed.Title = "Something else entirely"; break;
            case "Destination": changed.Destination = "a different channel"; break;
            case "Endpoint": changed.Endpoint = "https://elsewhere.invalid/upload"; break;
        }

        Assert.NotEqual(approved, PublishService.Fingerprint(changed));
    }

    [Fact]
    public void ReplacingTheFileRetiresAnApproval()
    {
        var approved = PublishService.Fingerprint(Plan());

        var rerendered = Plan();
        rerendered.VideoBytes = 8_100_000;

        Assert.NotEqual(approved, PublishService.Fingerprint(rerendered));
    }

    [Fact]
    public void MetadataFallsBackToTheBasicsWithNoTemplate()
    {
        var plan = Plan();
        plan.Tags.Add("baseline");

        var metadata = PublishService.BuildMetadata(string.Empty, plan);

        Assert.Contains("\"title\":\"Measuring what changed\"", metadata);
        Assert.Contains("\"visibility\":\"private\"", metadata);
        Assert.Contains("baseline", metadata);
    }

    [Fact]
    public void MetadataTemplatePlaceholdersAreFilledAndEscaped()
    {
        var plan = Plan();
        plan.Title = "A \"quoted\" title";
        plan.Tags.AddRange(new[] { "one", "two" });

        var metadata = PublishService.BuildMetadata(
            "{\"snippet\":{\"title\":\"{{TITLE}}\",\"tags\":[{{TAGS}}]},\"status\":{\"privacyStatus\":\"{{VISIBILITY}}\"}}",
            plan);

        // The result has to stay parseable, which means the title's quotes must be escaped.
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(metadata);
        Assert.Equal("A \"quoted\" title", parsed!["snippet"]!["title"]!.GetValue<string>());
        Assert.Equal("private", parsed["status"]!["privacyStatus"]!.GetValue<string>());
        Assert.Equal(2, parsed["snippet"]!["tags"]!.AsArray().Count);
    }

    [Fact]
    public void TheDefaultVisibilityIsPrivate()
    {
        // The failure mode of a mistake should be an unseen draft, not a publication.
        Assert.Equal("private", new Lanshu.Presenter.Core.Configuration.PublishSettings().DefaultVisibility);
        Assert.False(new Lanshu.Presenter.Core.Configuration.PublishSettings().Enabled);
    }

    [Theory]
    [InlineData("{\"id\":\"abc123\"}", "id", "abc123")]
    [InlineData("{\"data\":{\"video_id\":\"xyz\"}}", "data.video_id", "xyz")]
    [InlineData("not json at all", "id", "")]
    [InlineData("{\"id\":\"abc\"}", "", "")]
    public void TheReplyIdIsReadByPathAndNeverThrows(string body, string path, string expected)
    {
        Assert.Equal(expected, PublishService.ReadPath(body, path));
    }
}

public class LanguageCodeTests
{
    [Theory]
    [InlineData("Spanish", "es")]
    [InlineData("spanish", "es")]
    [InlineData("French", "fr")]
    [InlineData("Brazilian Portuguese", "pt-br")]
    [InlineData("Japanese", "ja")]
    public void MapsALanguageNameToTheCodeAnEngineWants(string name, string expected)
    {
        // espeak-ng exits with "the specified voice does not exist" for -v Spanish, which loses
        // the whole take rather than degrading, so the name has to become a code first.
        Assert.Equal(expected, LanguageCodes.ToCode(name));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("zh_Hans", "zh-hans")]
    public void PassesThroughSomethingThatIsAlreadyACode(string input, string expected)
    {
        // An operator who typed a locale on purpose knows more than the table does.
        Assert.Equal(expected, LanguageCodes.ToCode(input));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Middle High German")]
    public void SaysNothingRatherThanSomethingWrong(string? input)
    {
        // An engine given nothing picks its own default; an engine given a name it does not know
        // fails outright, so an unknown language must produce an empty string.
        Assert.Equal(string.Empty, LanguageCodes.ToCode(input));
    }
}
