namespace ToroSquad.Modules.Giveaway.Domain;

/// <summary>
/// Active until it is drawn (<see cref="Finished"/>, also with no valid entrant), cancelled, or its card is gone
/// (<see cref="Orphaned"/>: message or channel deleted, or the card was never confirmed). Every terminal state is final:
/// only an Active giveaway can change, and only once (the write transaction re-checks the stored status).
/// </summary>
public enum GiveawayStatus
{
    Active = 0,
    Finished = 1,
    Cancelled = 2,
    Orphaned = 3,
}
