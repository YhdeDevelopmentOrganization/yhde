using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using YHDE.Server.Persistence;
using YHDE.Server.Persistence.Migrations;

namespace YHDE.Server.Tests;

// The throwaway database named by YHDE_TEST_DATABASE, migrated once per test
// run (running the migrations from several tests at the same moment races on
// DbUp's journal table). Null when the variable is not set: the test skips.
public static class TestDatabase
{
    private static readonly Lazy<Database?> Instance = new(() =>
    {
        var cs = Environment.GetEnvironmentVariable("YHDE_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(cs)) return null;
        Runner.Run(cs, NullLogger.Instance);
        return new Database(new NpgsqlDataSourceBuilder(cs).Build());
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Database? Open() => Instance.Value;
}
