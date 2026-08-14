using Xunit;

// The log is process-wide static state, so tests that assert on its contents
// cannot tolerate another test class logging underneath them. The whole suite
// runs in about a second, so serialising it is cheaper than the alternative of
// making every assertion tolerant of unrelated entries.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
