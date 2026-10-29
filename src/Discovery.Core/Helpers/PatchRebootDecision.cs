using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Decide o que a política de reboot FAZ com um reinício pendente.
///
/// Decisão D02 (2026-10-29): nunca reiniciar automaticamente. A política é
/// consumida para gerar sinal operacional — antes disso, <c>RebootPolicy</c> e
/// <c>RebootAllowed</c> eram gravados, editáveis na UI e lidos por ninguém.
///
/// Regras:
///  • sem reinício pendente → <see cref="PatchRebootAction.None"/>;
///  • <c>Never</c> → None (o badge de reinício continua aparecendo, mas a
///    política não pede ação);
///  • <c>Notify</c> → notificar o operador;
///  • <c>MaintenanceWindow</c> sem <c>RebootAllowed</c> → apenas notificar
///    (o operador autorizou aviso, não reinício);
///  • <c>MaintenanceWindow</c> com <c>RebootAllowed</c> → notificar indicando a
///    janela (aberta ou a próxima). O reinício em si continua manual.
/// </summary>
public static class PatchRebootDecision
{
    public static (PatchRebootAction Action, string Reason) Evaluate(
        PatchRebootPolicy policy,
        bool rebootAllowed,
        bool rebootRequired,
        bool inMaintenanceWindow)
    {
        if (!rebootRequired)
            return (PatchRebootAction.None, "sem reinício pendente");

        switch (policy)
        {
            case PatchRebootPolicy.Never:
                return (PatchRebootAction.None, "política Never: nenhuma ação automática");

            case PatchRebootPolicy.Notify:
                return (PatchRebootAction.NotifyOperator, "política Notify: avisar o operador");

            case PatchRebootPolicy.MaintenanceWindow:
                if (!rebootAllowed)
                    return (PatchRebootAction.NotifyOperator,
                        "política MaintenanceWindow sem RebootAllowed: apenas avisar");
                return inMaintenanceWindow
                    ? (PatchRebootAction.NotifyWithWindowHint,
                        "janela aberta: reinício pode ser agendado pelo operador")
                    : (PatchRebootAction.NotifyWithWindowHint,
                        "fora da janela: reinício somente na próxima janela, pelo operador");

            default:
                return (PatchRebootAction.None, "política de reboot desconhecida: nenhuma ação");
        }
    }
}
