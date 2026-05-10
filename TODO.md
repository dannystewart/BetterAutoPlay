# BetterAutoPlay TODO

1. Pre-filter by game-native playability before combo ordering (highest priority)
- Use `PlayerModel.CanAffordCard` and current retry-cooldown state before combo sorting.
- Build combo order from cards that can be played now.
- Append deferred cards after playable cards to reduce failed attempts and animation spam.

2. Reduce reflection in hot paths
- Keep strongly-typed calls where possible.
- Keep reflection lookups cached and avoid repeated `Invoke` calls per comparator pass.

3. Separate sorting policy from mechanics
- Split logic into focused units:
- Card facts gathering
- Combo sequence planning
- Fallback ordering
- Retry policy

4. Replace mana inflation cooldown trick with explicit priority penalty
- Keep cooldown behavior out of `GetManaCost`.
- Apply a clear comparator penalty so intent is explicit and less error-prone.

5. Shift state handling to game events when possible
- Prefer event-driven resets over frame polling in `Tick`.
- Use known gameplay transitions (turn/encounter/autoplay state) to reduce races.

6. Reduce per-frame UI/autoplay resume work
- Keep resume checks interval-based and gated by state changes.
- Avoid unnecessary work every frame.
