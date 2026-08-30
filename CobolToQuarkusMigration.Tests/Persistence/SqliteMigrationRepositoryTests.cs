using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Persistence;

public sealed class SqliteMigrationRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"migration-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveBusinessLogicAsync_DuplicateFileNamesWithDifferentPaths_PersistsBothArtifacts()
    {
        var repository = new SqliteMigrationRepository(_databasePath, NullLogger<SqliteMigrationRepository>.Instance);
        await repository.InitializeAsync();
        var runId = await repository.StartRunAsync("source", "output");

        await repository.SaveBusinessLogicAsync(runId,
        [
            new BusinessLogic { FileName = "Program.cs", FilePath = "/src/A/Program.cs", BusinessPurpose = "First application." },
            new BusinessLogic { FileName = "Program.cs", FilePath = "/src/B/Program.cs", BusinessPurpose = "Second application." }
        ]);

        var persisted = await repository.GetBusinessLogicAsync(runId);

        persisted.Should().HaveCount(2);
        persisted.Select(item => item.FilePath).Should().BeEquivalentTo("/src/A/Program.cs", "/src/B/Program.cs");
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }
}
