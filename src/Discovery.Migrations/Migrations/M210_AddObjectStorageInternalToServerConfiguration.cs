using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Modo "storage interno" (RustFS do proprio servidor).
///
/// Quando ligado, as credenciais/loopback vem do env (ObjectStorage__*) e o
/// Endpoint/Bucket/Regiao salvos no console passam a definir a URL PUBLICA usada
/// nas URLs pre-assinadas entregues ao agent/navegador.
/// </summary>
[Migration(20260312_210)]
public class M210_AddObjectStorageInternalToServerConfiguration : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("object_storage_internal").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("object_storage_internal").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        if (Schema.Table("server_configurations").Column("object_storage_internal").Exists())
        {
            Delete.Column("object_storage_internal").FromTable("server_configurations");
        }
    }
}
