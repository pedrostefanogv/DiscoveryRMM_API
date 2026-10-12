using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Campos customizados: visibilidade no portal e destaque no card do detalhe.
///
/// - allow_web_read: o valor aparece no card do detalhe (agente/cliente/site);
/// - allow_web_edit: o suporte pode editar o valor pelo portal (exige allow_web_read);
/// - is_pinned: o campo aparece primeiro no card do detalhe.
///
/// Defaults preservam o comportamento atual: os campos que já existem passam a
/// ser visíveis e editáveis no portal — a permissão da entidade (Agents.Edit /
/// Clients.Edit / Sites.Edit) continua sendo exigida em conjunto com a flag.
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco; ela apenas existe
/// no repositório, pronta para ser executada quando autorizado.
/// </summary>
[Migration(20261201_214)]
public class M214_AddWebVisibilityToCustomFields : Migration
{
    private const string Table = "custom_field_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("allow_web_read").Exists())
        {
            Alter.Table(Table)
                .AddColumn("allow_web_read").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (!Schema.Table(Table).Column("allow_web_edit").Exists())
        {
            Alter.Table(Table)
                .AddColumn("allow_web_edit").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (!Schema.Table(Table).Column("is_pinned").Exists())
        {
            Alter.Table(Table)
                .AddColumn("is_pinned").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("is_pinned").Exists())
            Delete.Column("is_pinned").FromTable(Table);

        if (Schema.Table(Table).Column("allow_web_edit").Exists())
            Delete.Column("allow_web_edit").FromTable(Table);

        if (Schema.Table(Table).Column("allow_web_read").Exists())
            Delete.Column("allow_web_read").FromTable(Table);
    }
}