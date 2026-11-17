using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobLiveOutputTests
{
	[Fact]
	public void Tail_NumbersLinesInOrderAndKeepsTheNewest()
	{
		var tail = new JobLiveOutputTail(maxLines: 3);
		for (var i = 1; i <= 5; i++)
		{
			tail.Append($"line {i}", isError: i == 4);
		}

		var output = tail.GetSince(0);

		Assert.Equal(5, output.LatestSequence);
		Assert.Equal([3L, 4L, 5L], output.Lines.Select(line => line.Sequence));
		Assert.Equal("line 4", output.Lines[1].Content);
		Assert.True(output.Lines[1].IsError);
		Assert.Equal([5L], tail.GetSince(4).Lines.Select(line => line.Sequence));
		Assert.Empty(tail.GetSince(5).Lines);
	}

	[Fact]
	public void Tail_SendsEveryLineWhenAskedPastItsLatest()
	{
		// The asker holds an earlier run's lines: the job restarted and numbering began again.
		var tail = new JobLiveOutputTail();
		tail.Append("first", isError: false);
		tail.Append("second", isError: false);

		var output = tail.GetSince(400);

		Assert.Equal(2, output.LatestSequence);
		Assert.Equal(["first", "second"], output.Lines.Select(line => line.Content));
	}

	[Fact]
	public async Task Tail_BroadcastsLinesInTheOrderTheyWereWritten()
	{
		var tail = new JobLiveOutputTail();
		var sent = new List<long>();
		var random = new Random(7);

		for (var i = 0; i < 40; i++)
		{
			var delay = random.Next(0, 4);
			tail.Append($"line {i}", isError: false, async line =>
			{
				await Task.Delay(delay);
				lock (sent)
				{
					sent.Add(line.Sequence);
				}
			});
		}

		// A failing send doesn't hold up the lines after it
		tail.Append("boom", isError: false, _ => throw new InvalidOperationException());
		tail.Append("after", isError: false, line =>
		{
			lock (sent)
			{
				sent.Add(line.Sequence);
			}
			return Task.CompletedTask;
		});

		await tail.Broadcasts.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i).Append(42L), sent);
	}

	[Fact]
	public void Buffer_AppendsLinesInSequenceAndDropsRepeats()
	{
		var buffer = new JobLiveOutputBuffer();

		Assert.True(buffer.Add(Line(1, "one")));
		Assert.True(buffer.Add(Line(2, "two")));
		Assert.True(buffer.Add(Line(2, "two")));
		Assert.True(buffer.Add(Line(3, "three", isError: true)));

		var lines = buffer.ToList();
		Assert.Equal(["one", "two", "three"], lines.Select(line => line.Content));
		Assert.True(lines[2].IsError);
		Assert.Equal(3, buffer.LastSequence);
	}

	[Fact]
	public void Buffer_HoldsLinesAfterAGapUntilTheMissingOnesAreFetched()
	{
		var buffer = new JobLiveOutputBuffer();
		buffer.Add(Line(1, "one"));

		// Lines 2 and 3 were sent while the phone was asleep
		Assert.False(buffer.Add(Line(4, "four")));
		Assert.False(buffer.Add(Line(5, "five")));
		Assert.Equal(["one"], buffer.ToList().Select(line => line.Content));

		var (afterSequence, generation) = buffer.BeginCatchUp();
		Assert.Equal(1, afterSequence);
		var isComplete = buffer.ApplyCatchUp(Output(4, Line(2, "two"), Line(3, "three"), Line(4, "four")), afterSequence, generation);

		Assert.True(isComplete);
		Assert.Equal(["one", "two", "three", "four", "five"], buffer.ToList().Select(line => line.Content));
		Assert.Equal(5, buffer.LastSequence);
	}

	[Fact]
	public void Buffer_PageOpenedMidRunShowsTheSameLinesAsOneOpenSinceTheStart()
	{
		var tail = new JobLiveOutputTail();
		var watching = new JobLiveOutputBuffer();
		for (var i = 1; i <= 30; i++)
		{
			watching.Add(tail.Append($"line {i}", isError: false));
		}

		// Saved output on a running job is only its newest part
		var joining = new JobLiveOutputBuffer();
		joining.LoadSavedOutput("line 29\nline 30\n");
		var (afterSequence, generation) = joining.BeginCatchUp();
		Assert.Equal(0, afterSequence);
		Assert.True(joining.ApplyCatchUp(tail.GetSince(afterSequence), afterSequence, generation));

		var next = tail.Append("line 31", isError: false);
		watching.Add(next);
		joining.Add(next);

		Assert.Equal(
			watching.ToList().Select(line => (line.Content, line.Timestamp)),
			joining.ToList().Select(line => (line.Content, line.Timestamp)));
		Assert.Equal(31, joining.ToList().Count);
	}

	[Fact]
	public void Buffer_LinesBeforeTheFirstFetchWaitForIt()
	{
		var buffer = new JobLiveOutputBuffer();

		Assert.False(buffer.Add(Line(7, "seven")));
		Assert.True(buffer.IsEmpty);

		var (afterSequence, generation) = buffer.BeginCatchUp();
		buffer.ApplyCatchUp(Output(6, Line(5, "five"), Line(6, "six")), afterSequence, generation);

		Assert.Equal(["five", "six", "seven"], buffer.ToList().Select(line => line.Content));
	}

	[Fact]
	public void Buffer_KeepsLinesThatArrivedWhileTheFirstFetchWasOut()
	{
		var buffer = new JobLiveOutputBuffer();
		var (afterSequence, generation) = buffer.BeginCatchUp();
		for (var i = 1; i <= 5; i++)
		{
			buffer.Add(Line(i, $"line {i}"));
		}

		Assert.True(buffer.ApplyCatchUp(Output(3, Line(1, "line 1"), Line(2, "line 2"), Line(3, "line 3")), afterSequence, generation));

		Assert.Equal(Enumerable.Range(1, 5).Select(i => $"line {i}"), buffer.ToList().Select(line => line.Content));
	}

	[Fact]
	public void Buffer_KeepsLinesThatArrivedWhileACatchUpWasOut()
	{
		var buffer = new JobLiveOutputBuffer();
		buffer.Add(Line(1, "line 1"));
		buffer.Add(Line(2, "line 2"));
		Assert.False(buffer.Add(Line(4, "line 4")));
		var (afterSequence, generation) = buffer.BeginCatchUp();

		// The missing line turns up over the live connection before the reply does
		buffer.Add(Line(3, "line 3"));
		buffer.Add(Line(5, "line 5"));
		Assert.True(buffer.ApplyCatchUp(Output(4, Line(3, "line 3"), Line(4, "line 4")), afterSequence, generation));

		Assert.Equal(Enumerable.Range(1, 5).Select(i => $"line {i}"), buffer.ToList().Select(line => line.Content));
	}

	[Fact]
	public void Buffer_LineOneStartsANewRun()
	{
		var buffer = new JobLiveOutputBuffer();
		buffer.Add(Line(1, "old one"));
		buffer.Add(Line(2, "old two"));
		var (afterSequence, generation) = buffer.BeginCatchUp();

		Assert.True(buffer.Add(Line(1, "new one")));
		Assert.Equal(["new one"], buffer.ToList().Select(line => line.Content));

		// A reply to a fetch made during the old run is ignored
		buffer.ApplyCatchUp(Output(3, Line(3, "old three")), afterSequence, generation);
		Assert.Equal(["new one"], buffer.ToList().Select(line => line.Content));
	}

	[Fact]
	public void Buffer_ServerWithFewerLinesStartsOver()
	{
		var buffer = new JobLiveOutputBuffer();
		for (var i = 1; i <= 3; i++)
		{
			buffer.Add(Line(i, $"old {i}"));
		}

		// The service restarted and the job resumed in a new run, which has written 2 lines
		var (afterSequence, generation) = buffer.BeginCatchUp();
		buffer.ApplyCatchUp(Output(2, Line(1, "new 1"), Line(2, "new 2")), afterSequence, generation);

		Assert.Equal(["new 1", "new 2"], buffer.ToList().Select(line => line.Content));
		Assert.Equal(2, buffer.LastSequence);
	}

	[Fact]
	public void Buffer_WithoutALiveCopyShowsWhatArrived()
	{
		var buffer = new JobLiveOutputBuffer();
		buffer.LoadSavedOutput("saved\n");
		Assert.False(buffer.Add(Line(8, "eight")));

		var (afterSequence, generation) = buffer.BeginCatchUp();
		Assert.True(buffer.ApplyCatchUp(null, afterSequence, generation));

		Assert.Equal(["saved", "eight"], buffer.ToList().Select(line => line.Content));
		Assert.True(buffer.Add(Line(9, "nine")));
	}

	[Fact]
	public void Buffer_ClearHidesShownLinesWithoutFetchingThemAgain()
	{
		var buffer = new JobLiveOutputBuffer();
		buffer.Add(Line(1, "one"));
		buffer.Add(Line(2, "two"));

		buffer.Clear();
		buffer.Add(Line(3, "three"));

		Assert.Equal(["three"], buffer.ToList().Select(line => line.Content));
		Assert.Equal(3, buffer.BeginCatchUp().AfterSequence);
	}

	[Fact]
	public void Buffer_KeepsTheNewestLinesUpToItsLimit()
	{
		var buffer = new JobLiveOutputBuffer(maxLines: 2);
		for (var i = 1; i <= 4; i++)
		{
			buffer.Add(Line(i, $"line {i}"));
		}

		Assert.Equal(["line 3", "line 4"], buffer.ToList().Select(line => line.Content));
	}

	private static JobOutputLine Line(long sequence, string content, bool isError = false)
		=> new()
		{
			Sequence = sequence,
			Content = content,
			IsError = isError,
			Timestamp = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc).AddSeconds(sequence)
		};

	private static JobLiveOutput Output(long latestSequence, params JobOutputLine[] lines)
		=> new() { LatestSequence = latestSequence, Lines = lines.ToList() };
}
