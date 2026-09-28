using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// The RSVP roster rules shared by every path that changes who is in a listing (join, maybe, leave, the owner's edit,
/// /privacy delete). They work on the listing and ALL its participant rows as loaded inside the caller's write transaction
/// (SQLite BEGIN IMMEDIATE), so every decision is serialized with every other one; the caller saves.
/// <para>
/// Invariant of an active listing after every write: <c>Joined ≤ MaxPlayers</c>, and a waitlist exists only while every
/// slot is taken (<c>Waitlist = ∅ ∨ Joined = MaxPlayers</c>). A free slot is therefore never left while someone waits, and
/// a newcomer can never pass anyone already in the queue.
/// </para>
/// </summary>
internal static class LfgRoster
{
    /// <summary>
    /// Fills free slots from the head of the waitlist (first in line first) and sets Open / Full from the Joined count.
    /// Returns the promoted users. A terminal listing (closed, expired, orphaned) is left exactly as it is: nobody is ever
    /// promoted there.
    /// </summary>
    public static IReadOnlyList<ulong> Rebalance(LfgListingEntity listing, IReadOnlyCollection<LfgParticipantEntity> members, DateTimeOffset now)
    {
        if (listing.Status is not (LfgStatus.Open or LfgStatus.Full))
            return [];

        var joined = members.Count(p => p.Response == LfgResponse.Joined);
        var promoted = new List<ulong>();
        foreach (var next in Queue(members).ToList())
        {
            if (joined >= listing.MaxPlayers)
                break;
            next.Response = LfgResponse.Joined;
            next.WaitlistOrder = null;
            next.JoinedAt = now; // joins the team now: listed after the players already in it
            joined++;
            promoted.Add(next.UserId);
        }

        listing.Status = joined >= listing.MaxPlayers ? LfgStatus.Full : LfgStatus.Open;
        return promoted;
    }

    /// <summary>
    /// Puts a member at the end of the waitlist: the highest place still in the queue + 1 (an emptied queue starts again at
    /// 1). Under the write lock, so two concurrent joins get two different places in the order they were written.
    /// </summary>
    public static void Enqueue(LfgParticipantEntity participant, IEnumerable<LfgParticipantEntity> members, DateTimeOffset now)
    {
        var last = members.Where(p => !ReferenceEquals(p, participant) && p.Response == LfgResponse.Waitlisted).Max(p => p.WaitlistOrder);
        participant.Response = LfgResponse.Waitlisted;
        participant.WaitlistOrder = (last ?? 0) + 1;
        participant.JoinedAt = now;
    }

    /// <summary>Leaves the waitlist (to Maybe, or Joined by promotion): the place is dropped, the others keep their order.</summary>
    public static void Dequeue(LfgParticipantEntity participant) => participant.WaitlistOrder = null;

    /// <summary>The waitlist in queue order. The stored place decides; the rest only orders rows that cannot occur.</summary>
    public static IOrderedEnumerable<LfgParticipantEntity> Queue(IEnumerable<LfgParticipantEntity> members) =>
        members.Where(p => p.Response == LfgResponse.Waitlisted).OrderBy(p => p.WaitlistOrder).ThenBy(p => p.JoinedAt).ThenBy(p => p.UserId);

    /// <summary>The live position (1 = next in line), computed from the current queue — never a stored number.</summary>
    public static int Position(LfgParticipantEntity participant, IEnumerable<LfgParticipantEntity> members) =>
        Queue(members).TakeWhile(p => !ReferenceEquals(p, participant)).Count() + 1;
}
