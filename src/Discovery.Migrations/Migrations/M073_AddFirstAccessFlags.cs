using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona colunas must_change_password e must_change_profile para
/// suporte ao fluxo de primeiro acesso (first-access).
///
/// O seed do usuário administrador inicial NÃO é feito nesta migration.
/// Utilize o comando de manutenção após executar as migrations:
///   dotnet run --project src/Discovery.Api -- --recover-admin
/// Esse comando gera uma senha aleatória, cria o admin e vincula os grupos/roles.
/// A senha é exibida apenas no console e NÃO é salva em disco.
/// </summary>
[Migration(20260316_073)]
public class M073_AddFirstAccessFlags : Migration
{
    public override void Up()
    {
        Alter.Table("users")
            .AddColumn("must_change_password").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("users")
            .AddColumn("must_change_profile").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("must_change_profile").FromTable("users");
        Delete.Column("must_change_password").FromTable("users");
    }
}
