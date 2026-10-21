using System;
using System.Threading;
using System.Threading.Tasks;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve o nome de EXIBICAO de um usuario do suporte (ex.: "Pedro Stefano")
/// a partir do id, em vez do username/login ("pedrostefanogv") que chega nos
/// claims do JWT. Usado pelo chat de suporte (nome do tecnico na sessao e
/// autor das mensagens) para o usuario final ver o nome do atendente.
/// </summary>
public interface IChatUserNameResolver
{
    /// <summary>
    /// Devolve o nome de exibicao do usuario; quando o id e nulo, a busca falha
    /// ou o cadastro nao tem nome, devolve <paramref name="fallback"/>.
    /// </summary>
    Task<string> ResolveAsync(Guid? userId, string fallback, CancellationToken cancellationToken = default);
}
