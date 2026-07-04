namespace Alveus.Web.Activities;

/// <summary>
/// Résultat du vote des 3 participants sur un topic résolu (cf. ADR 0024) :
/// <see cref="Agree"/>/<see cref="Disagree"/> sont les décomptes finaux (3-0 ou 2-1 après
/// correction). Un topic non résolu (toujours 2-1 après correction) ne produit pas de
/// <see cref="MeetingVoteTally"/> — il déclenche directement l'issue "NeedsHelp".
/// </summary>
public sealed record MeetingVoteTally(int Agree, int Disagree);
