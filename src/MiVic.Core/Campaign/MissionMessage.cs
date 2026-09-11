namespace MiVic.Core.Campaign;

/// <summary>
/// A line a mission has shown the player, and the tick it was shown on.
/// <para>
/// The message itself is transient on screen — the interface shows the newest ones and lets the
/// older ones go — and this ledger is what the probe and the interface read: "the mission said
/// this, thirty seconds ago" is a question about a running match, and a transcript that only
/// showed the latest line could not answer it.
/// </para>
/// </summary>
/// <param name="Tick">Tick the mission raised it on.</param>
/// <param name="GreekText">What the player was told, in the mission's own words.</param>
public readonly record struct MissionMessage(long Tick, string GreekText);
