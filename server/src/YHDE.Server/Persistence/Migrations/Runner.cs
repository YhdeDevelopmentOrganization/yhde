using DbUp;
using DbUp.Engine;

namespace YHDE.Server.Persistence.Migrations;

// Runs embedded SQL migrations on server startup using DbUp.
// Migrations are idempotent: DbUp tracks which scripts have already run.
// See deployment.md and database.md (append-only schema conventions).
public static class Runner
{
    public static void Run(string connectionString, ILogger logger)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(Runner).Assembly,
                s => s.StartsWith("YHDE.Server.Persistence.Migrations.") && s.EndsWith(".sql"))
            .WithTransactionPerScript()
            .LogTo(new DbUpLogger(logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            logger.LogCritical(result.Error, "Database migration failed: cannot start");
            throw new InvalidOperationException("Database migration failed", result.Error);
        }

        logger.LogInformation("Database migrations applied successfully");
    }

    private sealed class DbUpLogger(ILogger inner) : DbUp.Engine.Output.IUpgradeLog
    {
        public void WriteInformation(string format, params object[] args)
            => inner.LogInformation(format, args);
        public void WriteError(string format, params object[] args)
            => inner.LogError(format, args);
        public void WriteWarning(string format, params object[] args)
            => inner.LogWarning(format, args);
    }
}
