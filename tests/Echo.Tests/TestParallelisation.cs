using Xunit;

// Several test classes swap process-wide state (chiefly the APPDATA variable, to point
// SettingsStore at a temp folder). xUnit runs test CLASSES in parallel by default, so one
// class could restore APPDATA while another was mid-test and send its writes to the real
// user profile — which is exactly what destroyed a developer's settings.json on 2026-09-18.
// The whole suite runs in about three seconds, so serialising it costs nothing and removes
// the entire class of problem.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
