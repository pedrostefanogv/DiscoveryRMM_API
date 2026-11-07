namespace Discovery.Core.Enums;

/// <summary>
/// Tipo do sensor. Built-in = o agente coleta nativamente (sem script).
/// Script = execução de script/comando do catálogo ou inline.
/// </summary>
public enum SensorType
{
    DiskSpace = 0,
    Cpu = 1,
    Memory = 2,
    Uptime = 3,
    Service = 4,
    Process = 5,
    Ping = 6,
    TcpPort = 7,
    HttpProbe = 8,
    HttpThroughput = 9,
    DnsLatency = 10,
    Bandwidth = 11,
    DiskIo = 12,
    Script = 13,
    // Fase 5: sensores de log de eventos, saúde de disco (SMART), certificado,
    // antivírus e criptografia de volume.
    EventLogRate = 14,
    SmartStatus = 15,
    CertificateExpiry = 16,
    DefenderStatus = 17,
    BitLocker = 18,
    // F5 — Consistência de Active Directory (replicação, dcdiag, relógio).
    // Só se aplica a controlador de domínio; em cliente/membro o agente
    // devolve coleta vazia sem erro.
    ADConsistency = 19
}
