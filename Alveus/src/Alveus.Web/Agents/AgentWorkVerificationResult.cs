namespace Alveus.Web.Agents;

/// <summary>
/// Résultat d'une vérification de travail (cf. <see cref="IAgentWorkVerificationService"/>, ADR 0020).
/// </summary>
/// <param name="Success">Vrai si le travail est validé.</param>
/// <param name="Output">Détails de la vérification (sortie du script, message d'erreur...),
/// réinjectés dans le prompt de l'agent en cas d'échec pour qu'il corrige.</param>
public sealed record AgentWorkVerificationResult(bool Success, string Output)
{
    /// <summary>Résultat "validé" sans détails — raccourci pour les implémentations no-op.</summary>
    public static AgentWorkVerificationResult Passed { get; } = new(true, string.Empty);
}
