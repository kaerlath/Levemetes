# Levemetes Relay

Cloudflare Worker and Durable Object relay for Levemetes 1.4 multiplayer rooms.

- Durable Objects coordinate public/private rooms and WebSocket membership.
- R2 stores only temporary host deck bundles while a room exists.
- Player IP addresses are never exposed to other players.
- Room passwords and bearer tokens are never written to the public directory.

The production Worker is expected at `https://levemetes-relay.kaerlath.workers.dev`.

## Local verification

Run `pnpm install`, then `pnpm check` from this directory.

## Deployment

The production Cloudflare account needs:

1. A private R2 bucket named `levemetes-room-decks`.
2. An R2 binding named `DECKS` (already represented in `wrangler.jsonc`).
3. The Durable Object bindings and migrations in `wrangler.jsonc`; Wrangler creates these during the first deployment.
4. An R2 lifecycle rule that deletes objects under `rooms/` after one day.

Authenticate Wrangler and run `pnpm deploy`. Do not commit Wrangler credentials or `.dev.vars`. After deployment, verify `GET /health`, then test room creation and joining with two beta clients before publishing Levemetes 1.4 to the production plugin channel.

Deck objects are limited to 100 MB and are deleted when their room closes or expires. Room state expires after six hours. The R2 lifecycle rule is a fallback for interrupted cleanup rather than the primary deletion mechanism.
