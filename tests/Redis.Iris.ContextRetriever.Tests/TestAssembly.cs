using Xunit;

// Meter/Activity listeners observe process-wide events. Lifetime tests exercise their own concurrency.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
