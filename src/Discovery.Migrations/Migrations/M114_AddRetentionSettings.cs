using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adds centralized retention settings JSON column to server_configurations.
/// Allows per-deployment tuning of: logs, notifications, agent commands,
/// sessions, tokens, telemetry, and database maintenance windows.
/// </summary>
[Migration(20260429_114)]
public class M114_AddRetentionSettings : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("retention_settings_json").AsString(int.MaxValue).Nullable();

        Execute.Sql(@"
            UPDATE server_configurations
            SET retention_settings_json = '{
              ""logRetentionDays"": 90,
              ""notificationRetentionDays"": 60,
              ""agentCommandRetentionDays"": 30,
              ""sessionRetentionDays"": 30,
              ""tokenExpiredGraceDays"": 7,
              ""syncPingRetentionDays"": 7,
              ""telemetryRetentionDays"": 7,
              ""automationReportRetentionDays"": 30,
              ""aiChatExpiryDays"": 180,
              ""aiChatGraceDays"": 30,
              ""databaseMaintenance"": {
                ""enabled"": true,
                ""schedule"": ""0 0 3 ? * SUN"",
                ""vacuumFull"": true,
                ""reindex"": true,
                ""analyze"": true,
                ""vacuumAnalyze"": false
              }
            }'
            WHERE retention_settings_json IS NULL
        ");
    }

    public override void Down()
    {
        Delete.Column("retention_settings_json").FromTable("server_configurations");
    }
}
