using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260303_015)]
public class M015_ScopeDeployTokensToClientSite : Migration
{
    public override void Up()
    {
        if (!Schema.Table("deploy_tokens").Column("client_id").Exists())
        {
            Alter.Table("deploy_tokens").AddColumn("client_id").AsGuid().Nullable();
        }

        if (!Schema.Table("deploy_tokens").Column("site_id").Exists())
        {
            Alter.Table("deploy_tokens").AddColumn("site_id").AsGuid().Nullable();
        }

        if (!Schema.Table("deploy_tokens").Constraint("fk_deploy_tokens_client").Exists())
        {
            Create.ForeignKey("fk_deploy_tokens_client")
                .FromTable("deploy_tokens").ForeignColumn("client_id")
                .ToTable("clients").PrimaryColumn("id");
        }

        if (!Schema.Table("deploy_tokens").Constraint("fk_deploy_tokens_site").Exists())
        {
            Create.ForeignKey("fk_deploy_tokens_site")
                .FromTable("deploy_tokens").ForeignColumn("site_id")
                .ToTable("sites").PrimaryColumn("id");
        }

        if (!Schema.Table("deploy_tokens").Index("ix_deploy_tokens_client_site").Exists())
        {
            Create.Index("ix_deploy_tokens_client_site")
                .OnTable("deploy_tokens")
                .OnColumn("client_id")
                .Ascending()
                .OnColumn("site_id")
                .Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("deploy_tokens").Index("ix_deploy_tokens_client_site").Exists())
        {
            Delete.Index("ix_deploy_tokens_client_site").OnTable("deploy_tokens");
        }

        if (Schema.Table("deploy_tokens").Constraint("fk_deploy_tokens_site").Exists())
        {
            Delete.ForeignKey("fk_deploy_tokens_site").OnTable("deploy_tokens");
        }

        if (Schema.Table("deploy_tokens").Constraint("fk_deploy_tokens_client").Exists())
        {
            Delete.ForeignKey("fk_deploy_tokens_client").OnTable("deploy_tokens");
        }

        if (Schema.Table("deploy_tokens").Column("site_id").Exists())
        {
            Delete.Column("site_id").FromTable("deploy_tokens");
        }

        if (Schema.Table("deploy_tokens").Column("client_id").Exists())
        {
            Delete.Column("client_id").FromTable("deploy_tokens");
        }
    }
}
