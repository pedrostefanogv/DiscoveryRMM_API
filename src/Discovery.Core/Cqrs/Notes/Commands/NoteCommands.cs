using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Notes.Commands;

// ── Request DTOs (bind do corpo) ─────────────────────────────────────
// O autor NÃO é aceito do cliente: é resolvido do usuário autenticado
// (HttpContext.Items["Username"]) nos controllers antes de montar o comando.

/// <summary>Payload de criação — sem Author (definido pelo servidor).</summary>
public sealed record CreateNoteRequest(
    Guid? ClientId, Guid? SiteId, Guid? AgentId,
    string Content, bool IsPinned
);

/// <summary>Payload de edição — sem Author (mantém o autor original).</summary>
public sealed record UpdateNoteRequest(
    string? Content, bool? IsPinned
);

// ── Commands internos (carregam Author já resolvido) ────────────────

public sealed record CreateNoteCommand(
    Guid? ClientId, Guid? SiteId, Guid? AgentId,
    string Content, string? Author, bool IsPinned
) : ICommand<Result<NoteDto>>;

public sealed record UpdateNoteCommand(
    Guid Id, string? Content, bool? IsPinned
) : ICommand<Result<NoteDto>>;

public sealed record DeleteNoteCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record NoteDto(
    Guid Id, Guid? ClientId, Guid? SiteId, Guid? AgentId,
    string Content, string? Author, bool IsPinned,
    DateTime CreatedAt, DateTime UpdatedAt
);
