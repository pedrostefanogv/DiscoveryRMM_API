using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Deferral por idade de publicação na política de patches (decisão D09,
/// 2026-10-29).
///
/// `deferral_days` é o número de dias que um update precisa ter desde a data de
/// publicação (`agent_windows_updates.published_at`) antes de poder ser
/// AUTO-aprovado pela política. 0 (default) preserva o comportamento anterior:
/// sem deferral.
///
/// Regra de resolução (ver PatchApprovalResolver): com DeferralDays &gt; 0 o
/// update permanece Unknown/aguardando até PublishedAt + DeferralDays; sem
/// PublishedAt é fail-closed (não auto-aprova). Aprovação manual explícita
/// continua vencendo.
///
/// Coluna NOT NULL com DEFAULT 0 para que políticas existentes fiquem
/// exatamente no comportamento atual. Idempotente (ADD COLUMN IF NOT EXISTS),
/// no mesmo estilo de M245.
/// </summary>
[Migration(20261225_246)]
public class M246_AddDeferralDaysToPatchPolicies : Migration
{
    private const string Table = "patch_policies";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS deferral_days int NOT NULL DEFAULT 0");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("deferral_days").Exists())
            Delete.Column("deferral_days").FromTable(Table);
    }
}
