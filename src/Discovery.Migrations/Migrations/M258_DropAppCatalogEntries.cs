using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove a tabela do catálogo curado de apps de terceiros (WU-21), criada
/// pela M251.
///
/// Motivo: a curadoria server-side (ingestão de manifestos winget-pkgs, status
/// active/pending/revoked e o gate de auto-aprovação por versão) foi
/// descontinuada. A validação do artefato passou a ser responsabilidade do
/// gerenciador de pacote (choco/winget) e do download direto com SHA-256, e o
/// controle de segurança para apps de terceiros fica no duplo consentimento
/// (D22).
///
/// A tabela era criada pela M251, removida do repositório junto com o recurso
/// (bancos que já a aplicaram mantêm o registro no VersionInfo, órfão e inócuo).
/// Esta migration garante o estado final em qualquer ambiente. Idempotente:
/// DROP ... IF EXISTS.
///
/// ATENÇÃO: não confundir com o catálogo da Loja de Softwares (app_packages,
/// criado pela M250), que continua existindo.
/// </summary>
[Migration(20261231_258)]
public class M258_DropAppCatalogEntries : Migration
{
    public override void Up()
    {
        // DROP TABLE IF EXISTS: não depende do VersionInfo nem de schema.Exists(),
        // então funciona tanto para a tabela ausente quanto para a criada por M251.
        Execute.Sql("DROP TABLE IF EXISTS app_catalog_entries;");
    }

    /// <summary>
    /// Não recria a tabela: o recurso não existe mais no código. Reverter exigiria
    /// restaurar a versão anterior da API, não apenas o schema.
    /// </summary>
    public override void Down()
    {
        // Intencionalmente vazio.
    }
}
