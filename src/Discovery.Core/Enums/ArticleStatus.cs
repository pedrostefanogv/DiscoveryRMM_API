namespace Discovery.Core.Enums;

/// <summary>
/// Status de visibilidade do artigo:
/// Draft = Rascunho — visível apenas para o autor e usuários com permissão Edit
/// Published = Público — visível para todos com permissão View no escopo
/// Internal = Interno — restrito a usuários do mesmo departamento do artigo
/// </summary>
public enum ArticleStatus
{
    Draft = 0,
    Published = 1,
    Internal = 2
}
