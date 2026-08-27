using Lanshu.Presenter.Core.Batch;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class BatchCsvTests
{
    [Fact]
    public void ReadsTheColumnsItKnowsAndIgnoresTheRest()
    {
        var csv = "topic,duration,owner,aspect\nMeasuring outcomes,45,alice,16:9\n";

        var state = BatchQueue.Parse(csv);

        var row = Assert.Single(state.Rows);
        Assert.Equal("Measuring outcomes", row.Topic);
        Assert.Equal(45, row.DurationSeconds);
        Assert.Equal("16:9", row.Aspect);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("title")]
    [InlineData("TOPIC")]
    public void AcceptsTheNamesPeopleActuallyUseForAColumn(string header)
    {
        var state = BatchQueue.Parse($"{header}\nA thing worth explaining\n");

        Assert.Equal("A thing worth explaining", state.Rows[0].Topic);
    }

    [Fact]
    public void KeepsACommaThatSitsInsideAQuotedCell()
    {
        var csv = "topic,duration\n\"Baselines, controls and rules\",60\n";

        var state = BatchQueue.Parse(csv);

        Assert.Equal("Baselines, controls and rules", state.Rows[0].Topic);
        Assert.Equal(60, state.Rows[0].DurationSeconds);
    }

    [Fact]
    public void KeepsANewlineThatSitsInsideAQuotedCell()
    {
        var csv = "topic,duration\n\"First line\nsecond line\",30\n";

        var state = BatchQueue.Parse(csv);

        // A quoted newline is part of the value, not the end of the row.
        Assert.Single(state.Rows);
        Assert.Contains("second line", state.Rows[0].Topic);
    }

    [Fact]
    public void UnescapesADoubledQuote()
    {
        var state = BatchQueue.Parse("topic\n\"She said \"\"measure it\"\" once\"\n");

        Assert.Equal("She said \"measure it\" once", state.Rows[0].Topic);
    }

    [Fact]
    public void SkipsARowWithNothingToSay()
    {
        var state = BatchQueue.Parse("topic,script\n,\nReal topic,\n");

        Assert.Equal("skipped", state.Rows[0].Status);
        Assert.Equal("pending", state.Rows[1].Status);
    }

    [Fact]
    public void AnEmptyOrHeaderOnlyFileYieldsNoRows()
    {
        Assert.Empty(BatchQueue.Parse(string.Empty).Rows);
        Assert.Empty(BatchQueue.Parse("topic,duration\n").Rows);
    }
}

public class BatchResumeTests
{
    private static BatchState Parsed() =>
        BatchQueue.Parse("topic\nOne\nTwo\nThree\n");

    [Fact]
    public void FinishedRowsAreNotMadeAgain()
    {
        var saved = Parsed();
        saved.Rows[0].Status = "done";
        saved.Rows[0].Master = "outputs/one-master.mp4";

        var resumed = BatchQueue.Resume(Parsed(), saved);

        Assert.Equal("done", resumed.Rows[0].Status);
        Assert.Equal("outputs/one-master.mp4", resumed.Rows[0].Master);
        Assert.Equal(2, resumed.Remaining);
    }

    [Fact]
    public void AFailedRowIsRetriedRatherThanCarriedForward()
    {
        var saved = Parsed();
        saved.Rows[1].Status = "failed";
        saved.Rows[1].Detail = "a provider was down";

        var resumed = BatchQueue.Resume(Parsed(), saved);

        // The point of resuming is to finish the batch, and a transient failure deserves another
        // attempt; only genuinely finished rows are skipped.
        Assert.Equal("pending", resumed.Rows[1].Status);
    }

    [Fact]
    public void ARowAddedToTheCsvLaterIsPickedUp()
    {
        var saved = BatchQueue.Parse("topic\nOne\n");
        saved.Rows[0].Status = "done";

        var resumed = BatchQueue.Resume(Parsed(), saved);

        Assert.Equal("done", resumed.Rows[0].Status);
        Assert.Equal("pending", resumed.Rows[2].Status);
    }

    [Fact]
    public void WithNoSavedProgressEverythingIsStillToDo()
    {
        var resumed = BatchQueue.Resume(Parsed(), null);

        Assert.Equal(3, resumed.Remaining);
        Assert.Equal(0, resumed.Done);
    }

    [Fact]
    public void ACorruptStateFileReadsAsNoProgressRatherThanThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lanshu-batch-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");

        try
        {
            // Stranding a batch because its bookkeeping file got truncated would be the worst
            // possible failure mode for an overnight run.
            Assert.Null(BatchQueue.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheSummaryCountsWhatHappened()
    {
        var state = Parsed();
        state.Rows[0].Status = "done";
        state.Rows[1].Status = "failed";
        state.Rows[1].Detail = "no presenter image";

        var markdown = BatchQueue.ToMarkdown(state);

        Assert.Contains("1 done, 1 failed, 2 remaining of 3", markdown);
        Assert.Contains("no presenter image", markdown);
    }
}
