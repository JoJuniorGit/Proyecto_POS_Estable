using Xunit;

// Disable parallel test execution so UI tests don't compete for window focus or desktop foreground
[assembly: CollectionBehavior(DisableTestParallelization = true)]
