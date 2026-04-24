using System.ComponentModel;
using Butlr.McpHome.Backends;
using ModelContextProtocol.Server;

namespace Butlr.McpHome.Tools;

[McpServerToolType]
internal sealed class LightTools(IDeviceBackend backend)
{
    [McpServerTool(Name = "turn_on_light"), Description("Allume la lumière dans la pièce spécifiée.")]
    public Task TurnOnLightAsync(
        [Description("Nom de la pièce (ex. salon, cuisine, chambre).")] string room,
        CancellationToken cancellationToken = default)
        => backend.TurnOnLightAsync(room, cancellationToken);

    [McpServerTool(Name = "turn_off_light"), Description("Éteint la lumière dans la pièce spécifiée.")]
    public Task TurnOffLightAsync(
        [Description("Nom de la pièce (ex. salon, cuisine, chambre).")] string room,
        CancellationToken cancellationToken = default)
        => backend.TurnOffLightAsync(room, cancellationToken);

    [McpServerTool(Name = "get_light_states"), Description("Retourne l'état (on/off) de toutes les lumières connues.")]
    public IReadOnlyDictionary<string, bool> GetLightStates()
        => backend.GetLightStates();
}
