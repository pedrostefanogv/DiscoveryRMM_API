using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Commands;

/// <summary>
/// Exclui uma imagem anexada a um artigo da base de conhecimento.
///
/// Por padrão também remove a referência (![alt](kbimage:&lt;id&gt;)) do conteúdo do
/// artigo E das sub-páginas — sem isso o texto continuaria apontando para um
/// anexo removido e o leitor veria "imagem quebrada".
/// </summary>
/// <param name="RemoveReferences">
/// Remove o token (![alt](kbimage:&lt;id&gt;)) do artigo e das sub-páginas.
/// </param>
/// <param name="Permanent">
/// true = exclusão DEFINITIVA (remove o objeto do storage agora + a linha, sem
/// janela de recuperação); false = soft delete (purgado pelo job de retenção).
/// </param>
public sealed record DeleteKnowledgeImageCommand(
    Guid ArticleId,
    Guid AttachmentId,
    bool RemoveReferences = true,
    bool Permanent = false
) : ICommand<Result<VoidResult>>;
