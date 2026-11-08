using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Alinha report_executions ao modelo EF (DiscoveryDbContext.Reports.cs).
///
/// No modelo EF os campos de object storage sao opcionais (string? / long?)
/// porque a execucao nasce Pending SEM destino de storage: bucket, chave,
/// content-type e tamanho so ficam conhecidos depois do upload bem-sucedido
/// (UpdateResultAsync). A M060, porem, criou as colunas NOT NULL com defaults
/// ("", "application/octet-stream", 0) — e esses defaults NUNCA se aplicam no
/// caminho do EF, que envia todas as propriedades mapeadas no INSERT: uma
/// propriedade null vira NULL explicito e o Postgres rejeita com 23502.
///
/// Sintoma (incidente 2026-11-07, POST /api/v1/reports/run -> HTTP 500):
///   23502: null value in column "storage_bucket" of relation "report_executions"
///   violates not-null constraint
///   em ReportExecutionRepository.CreateAsync <- RunReportNowCommandHandler.Handle.
///
/// Esta migracao apenas relaxa as quatro colunas afetadas para NULLABLE, sem
/// tocar em dados (nenhuma linha perde informacao; colunas nullable aceitam os
/// valores preenchidos pelas execucoes antigas).
/// </summary>
[Migration(20270113_281)]
public class M281_AlterReportExecutionsStorageFieldsNullable : Migration
{
    public override void Up()
    {
        Alter.Table("report_executions")
            .AlterColumn("storage_bucket").AsString(200).Nullable();

        Alter.Table("report_executions")
            .AlterColumn("storage_object_key").AsString(1000).Nullable();

        Alter.Table("report_executions")
            .AlterColumn("storage_content_type").AsString(200).Nullable();

        Alter.Table("report_executions")
            .AlterColumn("storage_size_bytes").AsInt64().Nullable();
    }

    public override void Down()
    {
        // Restaura a restricao da M060: preenche os nulos com os defaults
        // historicos antes de religar o NOT NULL (o ALTER COLUMN SET NOT NULL
        // falharia se qualquer linha tivesse NULL).
        Execute.Sql("UPDATE report_executions SET storage_bucket = '' WHERE storage_bucket IS NULL");
        Execute.Sql("UPDATE report_executions SET storage_object_key = '' WHERE storage_object_key IS NULL");
        Execute.Sql("UPDATE report_executions SET storage_content_type = 'application/octet-stream' WHERE storage_content_type IS NULL");
        Execute.Sql("UPDATE report_executions SET storage_size_bytes = 0 WHERE storage_size_bytes IS NULL");

        Alter.Table("report_executions")
            .AlterColumn("storage_bucket").AsString(200).NotNullable().WithDefaultValue("");

        Alter.Table("report_executions")
            .AlterColumn("storage_object_key").AsString(1000).NotNullable().WithDefaultValue("");

        Alter.Table("report_executions")
            .AlterColumn("storage_content_type").AsString(200).NotNullable().WithDefaultValue("application/octet-stream");

        Alter.Table("report_executions")
            .AlterColumn("storage_size_bytes").AsInt64().NotNullable().WithDefaultValue(0);
    }
}
