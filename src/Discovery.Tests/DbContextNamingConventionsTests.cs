using System.Text.RegularExpressions;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Discovery.Tests;

public class DbContextNamingConventionsTests
{
    private static readonly Regex SnakeCasePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    [Test]
    public void All_mapped_tables_and_columns_should_use_snake_case_names()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"dbctx-naming-{Guid.NewGuid():N}")
            .Options;

        using var db = new DiscoveryDbContext(options);
        var failures = new List<string>();

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                continue;
            }

            if (!SnakeCasePattern.IsMatch(tableName))
            {
                failures.Add($"Table '{entityType.DisplayName()}' mapped to invalid table name '{tableName}'.");
            }

            var tableIdentifier = StoreObjectIdentifier.Table(tableName!, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                if (property.IsShadowProperty())
                {
                    continue;
                }

                var columnName = property.GetColumnName(tableIdentifier);
                if (string.IsNullOrWhiteSpace(columnName))
                {
                    continue;
                }

                if (!SnakeCasePattern.IsMatch(columnName!))
                {
                    failures.Add(
                        $"Column mapping '{entityType.DisplayName()}.{property.Name}' -> '{columnName}' is not snake_case.");
                }
            }
        }

        Assert.That(failures, Is.Empty,
            $"Found non snake_case table/column mappings:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
