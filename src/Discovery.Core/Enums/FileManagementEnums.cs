namespace Discovery.Core.Enums;

/// <summary>Escopo de visibilidade/herança do arquivo gerenciado.</summary>
public enum FileScope
{
    Global = 0,
    Client = 1,
    Site = 2
}

/// <summary>Quem pode visualizar/baixar o arquivo.</summary>
public enum FileVisibility
{
    /// <summary>Visível apenas no console administrativo ou via link compartilhado.</summary>
    Private = 0,

    /// <summary>Visível no console e na aba Arquivos dos agents dentro do escopo.</summary>
    AgentOnly = 1,

    /// <summary>Visível no portal público /download, nos agents e no console.</summary>
    Public = 2,

    /// <summary>Visível apenas no portal público /download e no console (não aparece nos agents).</summary>
    PublicWebOnly = 3
}

/// <summary>Origem do arquivo no catálogo.</summary>
public enum FileSource
{
    AdminUpload = 0,
    ClientUploadLink = 1
}
