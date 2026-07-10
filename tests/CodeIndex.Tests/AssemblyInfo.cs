using Xunit;

// GeneratedFileClassifier is process-wide mutable config (UseGlobs), so a test that overrides the globs would
// otherwise race any parallel test that reads them. The suite is tiny (sub-second), so run it serially for
// deterministic isolation rather than sprinkling fragile [Collection] annotations across every glob-dependent class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
