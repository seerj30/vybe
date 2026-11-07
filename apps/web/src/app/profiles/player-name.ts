/** The name to show: the display name, or the handle when the player has none (guests). */
export function playerName(player: { handle: string; displayName: string | null }): string {
  return player.displayName ?? player.handle;
}
