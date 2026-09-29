using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

#nullable enable

namespace Ganss.Xss.Tests;

/// <summary>
/// Tests for the per-call policy snapshot: a single sanitize call must observe exactly one
/// version of the configuration even when another thread mutates the sanitizer mid-call.
/// </summary>
/// <remarks>
/// Part of <see cref="HtmlSanitizerTests"/> so that <see cref="HtmlSanitizerTests.ThreadTest"/>
/// picks these tests up into its concurrency sweep; see the remark on that class.
/// <para>
/// Every assertion message names the stage that produced the value and the input it was
/// produced from, so a regression points at the exact hand-off that broke instead of at a
/// bare mismatch. The inputs are the shortest ones that reach the decision in question -
/// keep them that way when editing: they are the counterexample a failure report shows.
/// </para>
/// </remarks>
public partial class HtmlSanitizerTests
{
    // The mutation is released by a gate inside the call (the parser factory, which runs on
    // entry, after the snapshot is captured), so it lands strictly between "the call started"
    // and "the traversal reads the configuration". Without a snapshot the call would observe
    // the mutated configuration; with one it must observe the version from call entry, and the
    // next call must observe the mutation - proving both consistency per call and freshness
    // across calls (i.e. no stale caching).
    [Fact]
    public void SanitizeObservesSinglePolicyVersionWhileConfigChangesTest()
    {
        const string html = @"<div><iframe src=""https://www.example.com/f.html""></iframe><span title=""t"">y</span></div>";
        // What each policy version does with the input, pinned down so a regression in either
        // direction fails on a fixed expectation rather than on a recomputed one.
        const string expectedBeforeMutation = @"<div><span title=""t"">y</span></div>";
        const string expectedAfterMutation = @"<div><iframe src=""https://www.example.com/f.html""></iframe></div>";

        var sanitizer = new HtmlSanitizer();
        var entered = new ManualResetEventSlim(false);
        var mutated = new ManualResetEventSlim(false);

        // Gate 1: the parser factory runs once per sanitize, on entry. Blocking it until the
        // mutation has landed interleaves the mutation precisely into the call.
        var defaultFactory = sanitizer.HtmlParserFactory;
        sanitizer.HtmlParserFactory = () =>
        {
            entered.Set();
            Assert.True(mutated.Wait(TimeSpan.FromSeconds(10)),
                $"stage=gate1 input={html}: the mutator thread did not complete in time");
            return defaultFactory();
        };

        // Gate 2: the removal events record which policy version the traversal actually applied.
        var removedTags = new List<string>();
        sanitizer.RemovingTag += (s, e) =>
        {
            lock (removedTags) removedTags.Add(e.Tag.NodeName);
        };

        Exception? mutatorError = null;
        var mutator = new Thread(() =>
        {
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)),
                    $"stage=gate1 input={html}: the sanitize call never reached the parser factory");
                sanitizer.AllowedTags.Add("iframe");
                sanitizer.AllowedTags.Remove("span");
                sanitizer.AllowedAttributes.Remove("title");
                mutated.Set();
            }
            catch (Exception ex)
            {
                mutatorError = ex;
                mutated.Set();
            }
        });

        mutator.Start();
        var actualDuringMutation = sanitizer.Sanitize(html);
        mutator.Join(TimeSpan.FromSeconds(10));

        Assert.Null(mutatorError);
        Assert.True(mutated.IsSet, $"stage=gate1 input={html}: mutation did not land during the first call");

        // The call that raced the mutation must be entirely on the old version: iframe removed,
        // span and title kept - not the new version, and not a mix of the two.
        Assert.Equal(expectedBeforeMutation, actualDuringMutation);
        Assert.Equal(["IFRAME"], removedTags.ToArray());

        // The next call starts after the mutation and must observe it in full.
        removedTags.Clear();
        var actualAfterMutation = sanitizer.Sanitize(html);
        Assert.Equal(expectedAfterMutation, actualAfterMutation);
        Assert.Equal(["SPAN"], removedTags.ToArray());
    }

    // Same interleaving as above, but the mutation replaces the RemovingTag handler list
    // mid-call. The most confusable decision point here is the cancel: which handler list gets
    // to cancel which removal. The call must run the handlers captured at entry (cancel
    // included); the replacement takes effect from the next call.
    [Fact]
    public void SanitizeUsesEventHandlersCapturedAtEntryTest()
    {
        const string html = @"<div><script></script><iframe></iframe></div>";
        const string expectedWithFirstHandler = @"<div><script></script></div>";
        const string expectedWithSecondHandler = @"<div><iframe></iframe></div>";

        var sanitizer = new HtmlSanitizer();
        var entered = new ManualResetEventSlim(false);
        var mutated = new ManualResetEventSlim(false);

        var firstHandlerCalls = 0;
        var secondHandlerCalls = 0;
        EventHandler<RemovingTagEventArgs> firstHandler = (s, e) =>
        {
            Interlocked.Increment(ref firstHandlerCalls);
            e.Cancel = e.Tag.NodeName == "SCRIPT";
        };
        EventHandler<RemovingTagEventArgs> secondHandler = (s, e) =>
        {
            Interlocked.Increment(ref secondHandlerCalls);
            e.Cancel = e.Tag.NodeName == "IFRAME";
        };
        sanitizer.RemovingTag += firstHandler;

        var defaultFactory = sanitizer.HtmlParserFactory;
        sanitizer.HtmlParserFactory = () =>
        {
            entered.Set();
            Assert.True(mutated.Wait(TimeSpan.FromSeconds(10)),
                $"stage=gate1 input={html}: the mutator thread did not complete in time");
            return defaultFactory();
        };

        Exception? mutatorError = null;
        var mutator = new Thread(() =>
        {
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)),
                    $"stage=gate1 input={html}: the sanitize call never reached the parser factory");
                sanitizer.RemovingTag -= firstHandler;
                sanitizer.RemovingTag += secondHandler;
                mutated.Set();
            }
            catch (Exception ex)
            {
                mutatorError = ex;
                mutated.Set();
            }
        });

        mutator.Start();
        var actualDuringMutation = sanitizer.Sanitize(html);
        mutator.Join(TimeSpan.FromSeconds(10));

        Assert.Null(mutatorError);

        // The in-flight call keeps the handler list it started with: the first handler's cancel
        // of the script removal applies, the second handler never runs for this call.
        Assert.Equal(expectedWithFirstHandler, actualDuringMutation);
        Assert.True(firstHandlerCalls > 0,
            $"stage=handlers input={html}: the handler attached at call entry did not run");
        Assert.Equal(0, secondHandlerCalls);

        // The replacement is not lost: the next call runs only the second handler.
        var actualAfterMutation = sanitizer.Sanitize(html);
        Assert.Equal(expectedWithSecondHandler, actualAfterMutation);
        Assert.True(secondHandlerCalls > 0,
            $"stage=handlers input={html}: the replacement handler did not run on the next call");
    }

    // The policy snapshot is per call and is never cached, so a handler that has been removed
    // must become collectible once the call that captured it is over. A policy cache that held
    // user delegates would keep the target alive and fail here.
    [Fact]
    public void RemovedEventHandlerIsNotRetainedAfterSanitizeTest()
    {
        var sanitizer = new HtmlSanitizer();
        var weakTarget = AttachHandlerSanitizeAndDetach(sanitizer);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakTarget.TryGetTarget(out _),
            "the sanitizer still references a RemovingTag handler that was removed after the call");
    }

    // Kept out of line so no local of the test method can keep the handler target alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<RemovingTagRecorder> AttachHandlerSanitizeAndDetach(HtmlSanitizer sanitizer)
    {
        var recorder = new RemovingTagRecorder();
        EventHandler<RemovingTagEventArgs> handler = recorder.OnRemovingTag;
        sanitizer.RemovingTag += handler;

        sanitizer.Sanitize("<div><script></script></div>");
        Assert.Equal(1, recorder.Calls);

        sanitizer.RemovingTag -= handler;
        return new WeakReference<RemovingTagRecorder>(recorder);
    }

    private sealed class RemovingTagRecorder
    {
        public int Calls;

        public void OnRemovingTag(object? sender, RemovingTagEventArgs e) => Calls++;
    }

    // Stability under inputs that differ only in order. Written down up front, the only
    // variation allowed anywhere here: the output of two *different* orderings of one sample
    // may differ in exactly the order the input differed in (attribute order and declaration
    // order follow the input). For one fixed input string nothing may vary at all - not the
    // output, not the removal events, not their order - across repeated and concurrent calls.
    [Fact]
    public void SanitizeIsDeterministicForOrderOnlyVariantsTest()
    {
        string[][] sampleGroups =
        [
            // Attribute order variants of one element.
            [
                @"<a href=""https://www.example.com/"" title=""t"">x</a>",
                @"<a title=""t"" href=""https://www.example.com/"">x</a>",
            ],
            // Declaration order variants of one style attribute.
            [
                @"<div style=""color: red; margin: 1px 2px"">y</div>",
                @"<div style=""margin: 1px 2px; color: red"">y</div>",
            ],
            // Order variants of removals: same outcome, same events, only the order differs.
            [
                @"<div><script></script><blink></blink></div>",
                @"<div><blink></blink><script></script></div>",
            ],
        ];

        var sanitizer = new HtmlSanitizer();
        const int sequentialRuns = 20;
        const int numThreads = 8;
        const int iterationsPerThread = 10;

        foreach (var group in sampleGroups)
        {
            foreach (var html in group)
            {
                var expectedEvents = RecordedSanitize(sanitizer, html);
                var expected = expectedEvents.Output;

                for (var i = 1; i < sequentialRuns; i++)
                {
                    var run = RecordedSanitize(sanitizer, html);
                    Assert.True(run.Output == expected && run.Events.SequenceEqual(expectedEvents.Events),
                        $"stage=sequential input={html}: run {i} differs." +
                        $" expected output={expected} events=[{string.Join(", ", expectedEvents.Events)}];" +
                        $" actual output={run.Output} events=[{string.Join(", ", run.Events)}]");
                }

                // Output only here: event handlers live on the shared sanitizer, so a recorder
                // attached by one thread would be captured into another thread's snapshot and
                // cross-record. The event sequence per call is pinned by the sequential phase.
                var concurrent = new ConcurrentBag<string>();
                var threads = Enumerable.Range(0, numThreads).Select(_ => new Thread(() =>
                {
                    for (var i = 0; i < iterationsPerThread; i++)
                        concurrent.Add(sanitizer.Sanitize(html));
                })).ToList();
                foreach (var thread in threads) thread.Start();
                foreach (var thread in threads) thread.Join();

                Assert.All(concurrent, output => Assert.True(output == expected,
                    $"stage=concurrent input={html}: a concurrent run differs." +
                    $" expected output={expected}; actual output={output}"));
            }
        }
    }

    private static (string Output, string[] Events) RecordedSanitize(HtmlSanitizer sanitizer, string html)
    {
        var events = new List<string>();
        void Handler(object? s, RemovingTagEventArgs e) { lock (events) events.Add($"tag:{e.Tag.NodeName}:{e.Reason}"); }
        sanitizer.RemovingTag += Handler;
        try
        {
            return (sanitizer.Sanitize(html), events.ToArray());
        }
        finally
        {
            sanitizer.RemovingTag -= Handler;
        }
    }

    // Pins the DOM and the exact removal event sequence of one fixed policy on one input that
    // exercises every removal stage, so the snapshot change cannot silently reorder or drop a
    // stage. The expected sequence is in pipeline order: disallowed tags first, then per-tag
    // attribute, style and class cleanup in document order, then comments.
    [Fact]
    public void FixedPolicyProducesFixedDomAndRemovalEventOrderTest()
    {
        const string html = @"<div onclick=""f()"" style=""color: red; behavior: x"" class=""a b"">"
            + @"<script></script><!-- c --><img src=""javascript:alert(1)""></div>";

        var sanitizer = new HtmlSanitizer();
        // "class" is not among the default allowed attributes; allowing it is what puts the
        // per-class filtering below on the measured path at all.
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedClasses.Add("a");

        var events = new List<string>();
        sanitizer.RemovingTag += (s, e) => events.Add($"tag:{e.Tag.NodeName}:{e.Reason}");
        sanitizer.RemovingAttribute += (s, e) => events.Add($"attribute:{e.Attribute.Name}:{e.Reason}");
        sanitizer.RemovingStyle += (s, e) => events.Add($"style:{e.Style.Name}:{e.Reason}");
        sanitizer.RemovingCssClass += (s, e) => events.Add($"class:{e.CssClass}:{e.Reason}");
        sanitizer.RemovingComment += (s, e) => events.Add("comment");
        sanitizer.FilterUrl += (s, e) => events.Add($"url:{e.OriginalUrl}");

        var actual = sanitizer.Sanitize(html);

        Assert.Equal(
            @"<div style=""color: rgba(255, 0, 0, 1)"" class=""a""><img></div>",
            actual);

        Assert.Equal(
            [
                "tag:SCRIPT:NotAllowedTag",
                "attribute:onclick:NotAllowedAttribute",
                "style:behavior:NotAllowedStyle",
                "class:b:NotAllowedCssClass",
                "url:javascript:alert(1)",
                "attribute:src:NotAllowedUrlValue",
                "comment",
            ],
            events.ToArray());
    }
}
