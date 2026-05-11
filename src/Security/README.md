# Reusable Security Subsystem

This folder contains the parts of the demo that are intended to be portable to a
production booking application:

- `Challenges`: challenge session generation, safe render payloads, dynamic
  slider protocol validation, and demo asset rendering.
- `Risk`: form/challenge scoring, trajectory heuristics, and risk decisions.

The booking sample depends on these modules through interfaces:

- `IChallengeSessionFactory`
- `IChallengeProtocolValidator`
- `IChallengeAssetRenderer`
- `ITrajectoryAnalyzer`
- `IRiskEngine`

The booking-specific state machine lives outside this folder in
`BookingFlowStore`, but it now depends on the reusable modules through these
contracts and on `ITemporarySecurityStateStore` for Redis-shaped temporary state.
That store is intentionally not a generic cache: it exposes the operations the
security flow needs to be atomic in production:

- `SetIfNotExists` for one-time setup and `SET NX EX` style semantics;
- `CompareAndSet` for lifecycle transitions such as `/challenge/start`;
- `TryConsumeOnce` for challenge sessions and validation tokens;
- `IncrementWithExpiry` for rate limits and pressure counters.

The sample implementation is `InMemoryTemporarySecurityStateStore`. Replace it
with Redis before using more than one app replica. Redis implementations should
use Lua scripts, transactions, or native `SET NX EX` where appropriate.

The current renderer is a demo implementation. A production application can
replace it with a vetted open-source CAPTCHA component or an external challenge
service while keeping the same state-machine and risk contracts.

Current challenge variants are:

- `reveal_target`: reveal the target only after pointerdown/start.
- `shift_after_start`: show a preview target first, then shift during active
  phase.
- `hold_and_release`: require a short post-alignment hold before release.
- `follow_up_shift`: require `/challenge/follow-up`, a follow-up nonce, a final
  target image, and movement after that final target appears.

Challenge assets are no longer public by `challengeId` alone. The booking flow
issues short-lived asset tokens bound to challenge id, verification session id,
phase, and the current HttpOnly session cookie. This keeps normal `<img>` usage
working while preventing cheap direct asset scraping from an unrelated session.

Risk enforcement is centralized through `BotDefenseOptions.EnforcementMode`.
`Shadow` is the default and logs the would-have decision while allowing traffic;
`RetryOnly`, `CooldownOnly`, and `Full` progressively apply stronger outcomes.

The modules intentionally produce risk signals rather than final proof of
humanity. The booking flow remains responsible for persistence, rate limits,
single-use tokens, audit logging, and slot finalization.
