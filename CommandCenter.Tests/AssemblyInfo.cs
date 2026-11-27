using Xunit;

// Unit/InMemory tests run in parallel. Collections that share physical resources or ports
// opt out explicitly: PostgresRealCollection (real PostgreSQL databases) and
// FullStackSerialCollection (Kestrel/UI). Per-test WeakReferenceMessenger.Default.UnregisterAll
// cleanup remains as defense in depth for ViewModels using the static bus.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
