using Xunit;

// Log is process-wide static state, so tests asserting on it cannot tolerate another
// class logging underneath. The suite runs in a second; serialising is cheaper.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
