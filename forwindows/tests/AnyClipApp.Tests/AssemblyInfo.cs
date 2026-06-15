using Xunit;

// These tests mutate global OS state (HKCU registry keys, the PID file,
// the clipboard). Running test classes in parallel lets one class tear
// down a resource another is using — e.g. registry "key marked for
// deletion". Serialize the whole assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
