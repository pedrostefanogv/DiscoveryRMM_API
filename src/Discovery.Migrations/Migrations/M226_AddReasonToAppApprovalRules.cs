using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona app_approval_rules.reason: o motivo digitado ao criar/atualizar uma
/// regra de aprovação na Loja de Softwares.
///
/// A propriedade passou a existir no EF Core (AppApprovalRule.Reason, mapeada em
/// DiscoveryDbContext.Automation.cs) e é exposta em
/// AppApprovalRuleResolvedDto.Reason — antes o motivo só existia na auditoria
/// (app_approval_audits.reason), então a listagem de regras nunca o mostrava.
///
/// Nullable: regras antigas ficam sem motivo. Mesmo padrão idempotente das
/// migrações vizinhas (checa Exist() antes de alterar).
/// </summary>
[Migration(20261216_226)]
public class M226_AddReasonToAppApprovalRules : Migration
{
    private const string Table = "app_approval_rules";
    private const string Column = "reason";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column(Column).Exists())
            Alter.Table(Table).AddColumn(Column).AsString(500).Nullable();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
