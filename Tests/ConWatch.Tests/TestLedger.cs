using ConPersistence;
using ConPersistence.Warnings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

/// <summary>A ModelWarningLedger for tests that only read it (Current is empty): it is
/// wired to a database factory that refuses to open, so it can never write a row.</summary>
internal sealed class TestLedger : IDisposable
{
    private sealed class NoDatabase : IDbContextFactory<ConWatchDbContext>
    {
        public ConWatchDbContext CreateDbContext() => throw new NotSupportedException();
    }

    private readonly ModelWarningRecorder _recorder;

    public TestLedger(TimeProvider time)
    {
        _recorder = new ModelWarningRecorder(new NoDatabase(), NullLogger<ModelWarningRecorder>.Instance);
        Ledger = new ModelWarningLedger(_recorder, new NoDatabase(), time, NullLogger<ModelWarningLedger>.Instance);
    }

    public ModelWarningLedger Ledger { get; }

    public void Dispose() => _recorder.Dispose();
}
