# Design Document: Bot-Resistant Booking Verification Flow

## 1. Goal

Build a practical anti-automation layer for a booking portal that increases the cost and reduces the reliability of automated slot booking by:

- AI agents controlling a real Chrome browser;
- Selenium/WebDriver scripts;
- scripted email-link confirmation flows;
- high-volume attempts against scarce booking slots.

The system does **not** claim to prove that a user is human. Instead, it evaluates risk across multiple independent signals and allows, challenges, cools down, or denies requests based on that risk.

Accessibility is intentionally out of scope for this document.

---

## 2. Non-Goals

The system will not attempt to:

- build a proprietary CAPTCHA from scratch;
- fully prevent human solver farms;
- rely on a single browser fingerprint or telemetry signal;
- use client-side obfuscation as a security boundary;
- check mailbox age, mailbox freshness, or mailbox ownership quality;
- reserve or finalize a real booking slot before successful verification.

---

## 3. Threat Model

### 3.1 In Scope

#### AI Agent with Real Chrome

An automated agent can:

- open the site in a real Chrome browser;
- inspect the rendered page visually;
- use screenshots and VLM reasoning;
- move the mouse or perform touch-like actions;
- read the magic-link email through mailbox automation;
- retry challenges and adapt behavior.

#### Selenium / WebDriver Script

A script can:

- automate form filling;
- click links and buttons;
- call browser APIs;
- emulate mouse and keyboard actions;
- attempt to hide WebDriver markers;
- replay or synthesize client telemetry.

### 3.2 Out of Scope

The system does not fully defend against:

- a real human manually completing bookings;
- paid human CAPTCHA-solving services;
- compromised legitimate user mailboxes;
- attackers with full control over residential proxy fleets and large numbers of real devices.

---

## 4. Core Design Principles

1. **Server state is authoritative.**
   Client telemetry is only a signal, never a source of truth.

2. **Use existing open-source CAPTCHA/slider components.**
   Do not build the visual slider engine from scratch unless required for integration or security hardening.

3. **Slider CAPTCHA is not the defense by itself.**
   It is one challenge signal inside a broader risk engine.

4. **Every token is scoped, short-lived, and single-purpose.**
   Email tickets, challenge sessions, and booking validation tokens have different purposes and lifetimes.

5. **Risk-based decisions instead of hard binary rules.**
   Suspicious telemetry increases risk; it should not automatically block legitimate users.

6. **Mobile is a first-class flow.**
   Touch input must be supported without strict assumptions about jitter, force, radius, or event density.

7. **No slot is selected or finalized before successful challenge.**
   Slot selection happens only after verification.

---

## 5. High-Level Architecture

### Components

#### Booking Frontend

Responsible for:

- collecting user form data;
- displaying the email-confirmation state;
- rendering the open-source slider challenge;
- collecting interaction telemetry;
- submitting challenge verification;
- allowing slot selection only after verification success.

#### Booking API

Responsible for:

- creating booking-intent records;
- sending verification emails;
- enforcing state transitions;
- issuing and consuming validation tokens;
- finalizing bookings atomically.

#### Challenge Service

Responsible for:

- integrating with an open-source slider CAPTCHA component;
- generating challenge sessions;
- storing challenge metadata server-side;
- verifying slider solutions;
- passing challenge telemetry to the risk engine.

#### Risk Engine

Responsible for:

- evaluating browser, session, telemetry, velocity, and abuse signals;
- producing a risk score;
- returning one of the allowed decisions:
  - allow;
  - allow with cooldown;
  - require fresh challenge;
  - temporarily deny;
  - hard deny for the current ticket/session.

#### Redis / State Store

Responsible for temporary state:

- email ticket state;
- challenge session state;
- attempt counters;
- rate-limit buckets;
- validation-token state;
- short-lived risk context.

---

## 6. Open-Source Slider CAPTCHA Strategy

### 6.1 Requirement

The slider challenge must use an existing open-source implementation as the base component.

Acceptable examples include libraries in the following categories:

- React/JavaScript slider CAPTCHA components;
- Node/Express-compatible slider CAPTCHA backends;
- mobile-compatible slider CAPTCHA widgets;
- framework-specific components if they support server-side verification.

### 6.2 Selection Criteria

A candidate library must be evaluated against:

- active maintenance or acceptable fork quality;
- license compatibility;
- mobile support;
- ability to verify solutions server-side;
- ability to customize challenge images;
- ability to collect or attach telemetry;
- ability to avoid exposing the correct answer to the client;
- minimal dependency risk;
- compatibility with the existing frontend stack.

### 6.3 Recommended Integration Approach

Use the open-source component for:

- rendering the slider UI;
- handling pointer/touch interaction;
- basic solution submission;
- client-side visual behavior.

Keep the following server-owned:

- challenge id;
- expected solution;
- solution tolerance;
- image/challenge seed;
- attempt count;
- expiry;
- risk context;
- final pass/fail decision.

The client must never receive the raw target coordinate or any reusable secret that allows offline solving.

### 6.4 Important Limitation

Open-source slider CAPTCHA code is public and can be studied by attackers. Slider CAPTCHA solvers also exist. Therefore, the slider is used as a friction and signal source, not as a complete anti-bot mechanism.

---

## 7. Verification State Machine

The flow is modeled as a server-side state machine.

```text
FORM_SUBMITTED
  -> EMAIL_SENT
  -> EMAIL_CONFIRMED
  -> CHALLENGE_STARTED
  -> CHALLENGE_PASSED
  -> SLOT_SELECTION_ALLOWED
  -> BOOKING_FINALIZED
```

Invalid transitions are rejected.

For example:

- a user cannot start a challenge without a valid email ticket;
- a user cannot select a slot before `CHALLENGE_PASSED`;
- a validation token cannot be reused;
- a challenge session cannot be verified twice;
- a stale email ticket cannot issue new challenges.

---

## 8. End-to-End User Flow

### Stage 1: Form Submission

The user submits initial booking data:

```text
POST /api/v1/booking-intents
```

Input:

- name;
- email;
- optional booking metadata that is safe to collect before verification.

Server behavior:

1. Normalize the email address.
2. Create a server-side booking intent.
3. Generate an opaque email ticket token.
4. Store ticket state in Redis or another temporary store.
5. Send a magic link to the email address.

Example Redis record:

```text
booking_intent:{intent_id}
  status = EMAIL_SENT
  email_hash = ...
  created_at = ...
  expires_at = ...
  email_send_count = 1
  challenge_init_count = 0
  challenge_fail_count = 0
  risk_score = initial_score
```

The email link should contain an opaque token:

```text
https://sample-booking-system.com/confirm-booking?t=<opaque_email_ticket>
```

Do not put raw email or sensitive booking data inside the URL.

---

### Stage 2: Email Confirmation

The user opens the magic link.

Frontend calls:

```text
POST /api/v1/verification/email/confirm
```

Payload:

```json
{
  "ticket": "opaque_email_ticket"
}
```

Server behavior:

1. Look up the ticket server-side.
2. Validate TTL and state.
3. Mark the booking intent as `EMAIL_CONFIRMED`.
4. Create a browser verification session.
5. Set a secure, HTTP-only, SameSite cookie for the verification session.
6. Return a short-lived frontend session descriptor.

The email ticket confirms access to the mailbox, but it does not grant booking rights.

---

### Stage 3: Challenge Initialization

Frontend calls:

```text
POST /api/v1/challenge/init
```

Server behavior:

1. Validate the email-confirmed booking intent.
2. Check rate limits and attempt counters.
3. Compute current risk score.
4. Select challenge difficulty or variant.
5. Create a one-time challenge session.
6. Generate slider challenge data using the selected open-source component or its backend.
7. Store the expected answer server-side.
8. Return only render-safe challenge data.

Example response:

```json
{
  "challenge_id": "ch_123",
  "type": "slider",
  "expires_in_seconds": 120,
  "render_payload": {
    "background_image_url": "/api/v1/challenge/assets/bg/ch_123?phase=preview&asset_token=at_...",
    "piece_image_url": "/api/v1/challenge/assets/piece/ch_123?asset_token=at_...",
    "ui_config": {
      "width": 320,
      "height": 160
    }
  }
}
```

The response must not include:

- raw `x_target`;
- solution hash that can be brute-forced locally;
- reusable signing secrets;
- long-lived identifiers.

---

### Stage 4: Challenge Interaction and Telemetry

The frontend renders the open-source slider component and collects lightweight telemetry.

Telemetry may include:

- pointer/touch event sequence;
- relative coordinates;
- relative timestamps;
- event type;
- focus/blur/visibility changes during the challenge;
- challenge start and release timestamps;
- coarse device/browser hints.

Example telemetry:

```json
{
  "modality": "mouse",
  "points": [
    { "x": 0, "y": 2, "t": 0 },
    { "x": 19, "y": 3, "t": 53 },
    { "x": 61, "y": 1, "t": 118 },
    { "x": 180, "y": 4, "t": 920 }
  ],
  "events": {
    "focus_lost": false,
    "visibility_changed": false,
    "pointer_cancelled": false
  }
}
```

Telemetry is not trusted. It is evaluated for consistency and risk only.

---

### Stage 5: Challenge Verification

Frontend calls:

```text
POST /api/v1/challenge/verify
```

Payload:

```json
{
  "challenge_id": "ch_123",
  "solution": {
    "x": 184,
    "time_spent_ms": 1240
  },
  "telemetry": {
    "modality": "mouse",
    "points": []
  }
}
```

Server behavior:

1. Load challenge state.
2. Reject if missing, expired, already consumed, or not bound to the current verification session.
3. Consume the challenge session immediately.
4. Verify the slider solution server-side.
5. Evaluate telemetry and behavioral signals.
6. Update risk score and attempt counters.
7. Decide the next action.

Challenge sessions are single-use regardless of success or failure.

---

### Stage 6: Verification Decision

Possible responses:

#### Allow

```json
{
  "decision": "allow",
  "validation_token": "opaque_validation_token",
  "expires_in_seconds": 120
}
```

#### Require Fresh Challenge

```json
{
  "decision": "retry_challenge",
  "reason_code": "verification_uncertain",
  "cooldown_seconds": 3
}
```

#### Temporary Deny

```json
{
  "decision": "temporarily_denied",
  "reason_code": "too_many_attempts",
  "cooldown_seconds": 600
}
```

Do not expose detailed bot-detection reasons to the client.

---

### Stage 7: Slot Selection and Booking Finalization

Slot selection is available only after `CHALLENGE_PASSED`.

The frontend calls:

```text
POST /api/v1/bookings/finalize
```

Payload:

```json
{
  "validation_token": "opaque_validation_token",
  "slot_id": "slot_456",
  "booking_data": {
    "name": "...",
    "email": "..."
  }
}
```

Server behavior:

1. Validate the token.
2. Ensure token is short-lived and unused.
3. Ensure token is bound to the current booking intent and email hash.
4. Apply slot-level rate limits.
5. Atomically consume the validation token.
6. Atomically finalize the booking if the slot is still available.
7. Mark booking intent as `BOOKING_FINALIZED`.

The validation token must not allow multiple bookings or unrelated actions.

---

## 9. Token Semantics

### 9.1 Email Ticket

Purpose:

- confirm access to the submitted email address.

Properties:

- opaque random token;
- stored server-side;
- TTL: 10–15 minutes;
- not a booking authorization token;
- can start only a limited number of challenge sessions;
- invalidated after successful booking;
- may be invalidated after suspicious activity.

Recommended limits:

```text
max_email_sends_per_email = 1 per 10 minutes
max_email_sends_per_ip = configurable
max_challenge_inits_per_ticket = 3–5
max_failed_challenges_per_ticket = 3
```

### 9.2 Challenge Session

Purpose:

- represent one CAPTCHA attempt.

Properties:

- one-time use;
- bound to booking intent;
- bound to verification browser session;
- TTL: 1–2 minutes;
- consumed on every verify attempt;
- stores expected solution server-side.

### 9.3 Validation Token

Purpose:

- authorize short-lived access to slot selection/finalization.

Properties:

- opaque random token;
- one-time use;
- TTL: 1–3 minutes;
- scoped to booking intent;
- scoped to email hash;
- scoped to allowed action: `booking_finalize`;
- consumed atomically during finalization;
- invalidated after booking success or expiration.

---

## 10. Risk Engine

### 10.1 Inputs

The risk engine evaluates multiple signal categories.

#### Browser and Automation Signals

Examples:

- WebDriver indicators;
- suspicious global variables;
- inconsistent browser APIs;
- abnormal permissions or plugin behavior;
- inconsistent user-agent/client hints;
- suspicious focus and visibility lifecycle.

These signals are never individually decisive.

#### Interaction Lifecycle Signals

Examples:

- time from page load to first interaction;
- form-fill duration;
- key event sequence quality;
- pointer movement before click;
- focus order;
- copy/paste behavior;
- challenge start timing;
- time between email send and magic-link open.

#### Challenge Telemetry Signals

Examples:

- solution accuracy;
- total solve time;
- movement curve;
- acceleration/deceleration profile;
- number of correction movements;
- event interval distribution;
- pointer/touch consistency;
- impossible or contradictory telemetry.

#### Velocity and Abuse Signals

Examples:

- attempts per email hash;
- attempts per IP;
- attempts per subnet/ASN;
- attempts per device fingerprint;
- attempts per booking slot group;
- ratio of challenge failures to successes;
- repeated behavior across many emails.

#### Slot Pressure Signals

Examples:

- scarce/high-demand slot group;
- sudden spike in verified sessions;
- many validation tokens but few finalized bookings;
- many users selecting the same slot immediately after release.

---

### 10.2 Example Scoring Model

```text
risk = 0

risk += browser_automation_score
risk += lifecycle_anomaly_score
risk += challenge_solution_score
risk += telemetry_anomaly_score
risk += velocity_score
risk += slot_pressure_score
risk += fingerprint_instability_score
risk += previous_failure_score
```

Example decision thresholds:

```text
risk < 30       -> allow
30 <= risk < 60 -> retry challenge or small cooldown
60 <= risk < 85 -> temporary deny
risk >= 85      -> hard deny current ticket/session
```

Thresholds must be calibrated in shadow mode using production traffic.

---

## 11. Desktop vs Mobile Handling

### Desktop Mouse / Trackpad

Useful signals:

- movement smoothness;
- micro-corrections;
- acceleration profile;
- overshoot and correction;
- event interval distribution;
- pointer movement before drag.

Avoid hard-blocking on a single movement feature.

### Mobile Touch

Mobile must be handled separately.

Do not hard-block because of:

- low Y-axis variance;
- missing `force`;
- static or missing `radiusX` / `radiusY`;
- short flick-like movements;
- sparse event points;
- OS-level gesture smoothing.

Useful mobile signals:

- touch lifecycle consistency;
- impossible coordinate jumps;
- repeated identical traces;
- suspiciously perfect timing across attempts;
- mismatch between declared modality and actual events;
- high-velocity repeated attempts from the same device/network.

---

## 12. Rate Limits and Abuse Controls

Rate limiting must be applied across several dimensions.

### Email Dispatch

```text
per_email_hash: 1 email per 10 minutes
per_ip: configurable
per_subnet: configurable
per_domain: configurable
per_booking_campaign: configurable
```

### Challenge Init

```text
per_ticket: 3–5 active attempts total
per_session: low single-digit attempts
per_ip/subnet: configurable
per_device: configurable
```

### Challenge Verify

```text
challenge_id: exactly 1 attempt
ticket: limited failed attempts
session: limited failed attempts
ip/subnet: velocity-based limits
```

### Validation Token

```text
token: one-time use
booking_intent: limited successful challenges
email_hash: limited successful challenges per window
ip/device: limited successful challenges per window
slot_group: pressure-based throttling
```

---

## 13. API Specification

### 13.1 Create Booking Intent

```text
POST /api/v1/booking-intents
```

Request:

```json
{
  "name": "Jane Doe",
  "email": "jane@example.com"
}
```

Response:

```json
{
  "status": "email_sent"
}
```

The response must not reveal whether the email was previously used.

---

### 13.2 Confirm Email Ticket

```text
POST /api/v1/verification/email/confirm
```

Request:

```json
{
  "ticket": "opaque_email_ticket"
}
```

Response:

```json
{
  "status": "email_confirmed",
  "session_nonce": "sn_..."
}
```

The verification session id is set only in the HttpOnly
`__booking_verification` cookie. JavaScript receives only the nonce, which must
be sent as `X-Booking-Session-Nonce` on protected API calls.

---

### 13.3 Initialize Challenge

```text
POST /api/v1/challenge/init
```

Request:

```json
{
  "browser": { "web_driver": false }
}
```

Response:

```json
{
  "challenge_id": "ch_123",
  "type": "slider",
  "expires_in_seconds": 120,
  "render_payload": {
    "background_image_url": "/api/v1/challenge/assets/bg/ch_123?phase=preview&asset_token=at_...",
    "piece_image_url": "/api/v1/challenge/assets/piece/ch_123?asset_token=at_...",
    "ui_config": {
      "width": 320,
      "height": 160
    }
  }
}
```

---

### 13.4 Verify Challenge

```text
POST /api/v1/challenge/verify
```

Request:

```json
{
  "challenge_id": "ch_123",
  "solution": {
    "x": 184,
    "time_spent_ms": 1240
  },
  "telemetry": {
    "modality": "mouse",
    "points": [
      { "x": 0, "y": 2, "t": 0 },
      { "x": 40, "y": 3, "t": 90 },
      { "x": 120, "y": 4, "t": 470 },
      { "x": 184, "y": 3, "t": 1240 }
    ],
    "events": {
      "focus_lost": false,
      "visibility_changed": false
    }
  }
}
```

Response:

```json
{
  "decision": "allow",
  "validation_token": "opaque_validation_token",
  "expires_in_seconds": 120
}
```

Alternative response:

```json
{
  "decision": "retry_challenge",
  "cooldown_seconds": 3
}
```

---

### 13.5 Finalize Booking

```text
POST /api/v1/bookings/finalize
```

Request:

```json
{
  "validation_token": "opaque_validation_token",
  "slot_id": "slot_456",
  "booking_data": {
    "name": "Jane Doe",
    "email": "jane@example.com"
  }
}
```

Response:

```json
{
  "status": "booked",
  "booking_id": "bk_789"
}
```

---

## 14. Security Controls

### 14.1 Origin and CSRF Protection

For API endpoints:

- validate `Origin` where available;
- use SameSite cookies;
- use CSRF/session nonces where appropriate;
- reject invalid state transitions;
- do not rely on `Referer` as a primary security boundary.

Origin checks help against simple cross-site requests but do not stop real-browser automation.

### 14.2 Idempotency and Atomicity

Booking finalization must be atomic:

1. validate token;
2. acquire slot lock or use database transaction;
3. consume token;
4. create booking;
5. mark intent finalized;
6. release lock / commit transaction.

The same validation token cannot finalize two bookings.

### 14.3 Logging and Audit

Log security-relevant events:

- booking intent created;
- email sent;
- email confirmed;
- challenge initialized;
- challenge verified;
- challenge failed;
- validation token issued;
- booking finalized;
- rate limit triggered;
- risk decision made.

Logs should include:

- event type;
- timestamp;
- booking intent id;
- email hash;
- challenge id;
- risk score bucket;
- decision;
- IP/subnet/ASN where legally acceptable;
- coarse browser/device information.

Avoid storing raw high-resolution behavioral traces longer than necessary.

---

## 15. Privacy and Data Retention

Telemetry and fingerprint-like data should be minimized.

Recommended retention:

```text
raw telemetry: 7–14 days
aggregated risk features: 30–90 days
security audit logs: according to internal policy
email hash / booking intent: according to booking policy
```

Store hashed or coarse identifiers where possible.

---

## 16. Shadow Mode Rollout

Before enforcement, run the system in shadow mode.

Shadow mode behavior:

- collect risk signals;
- calculate risk score;
- record would-have-blocked decisions;
- allow the user through unless the existing production system would block them.

Metrics to collect:

- challenge pass rate;
- challenge fail rate;
- retry rate;
- mobile vs desktop pass rate;
- browser-specific false positives;
- time to complete verification;
- email confirmation rate;
- booking finalization rate after successful challenge;
- high-risk traffic volume;
- suspected automation success rate.

Enforcement should start gradually:

```text
Phase 1: shadow only
Phase 2: cooldown for high-risk sessions
Phase 3: retry challenge for medium-risk sessions
Phase 4: temporary deny for very high-risk sessions
Phase 5: tune thresholds continuously
```

---

## 17. Operational Playbooks

### 17.1 High Failure Spike

Possible causes:

- broken CAPTCHA asset generation;
- frontend regression;
- mobile browser incompatibility;
- overly strict threshold;
- CDN/cache issue.

Immediate actions:

- disable hard deny;
- fall back to allow-with-logging;
- inspect recent challenge failures;
- compare mobile and desktop metrics;
- roll back challenge library update if needed.

### 17.2 Slot Attack Spike

Signals:

- many email confirmations;
- many challenge starts;
- many validation tokens;
- low booking-finalization ratio;
- concentration on one slot group;
- repeated IP/subnet/ASN/device patterns.

Actions:

- tighten per-slot-group throttling;
- shorten validation-token TTL;
- add cooldown for high-risk sessions;
- reduce max challenge attempts per ticket;
- temporarily raise risk score for affected slot group.

### 17.3 Selenium Pattern Detected

Actions:

- add or adjust browser automation signal weights;
- add decoy/honeypot signal if safe;
- increase cooldown for matching sessions;
- avoid revealing the exact detection reason.

---

## 18. Implementation Plan

### Phase 1: State Machine and Token Hardening

- Replace self-contained magic-link JWT with opaque server-side email ticket.
- Add booking intent state machine.
- Add challenge session state.
- Add one-time validation token.
- Add atomic booking finalization.

### Phase 2: Open-Source Slider Integration

- Select an open-source slider CAPTCHA library.
- Integrate frontend widget.
- Keep answer generation and verification server-side.
- Add mobile support testing.
- Add basic telemetry collection.

### Phase 3: Rate Limits and Counters

- Add email dispatch limits.
- Add challenge init limits.
- Add challenge verify limits.
- Add validation-token limits.
- Add slot-group pressure limits.

### Phase 4: Risk Engine v1

- Implement simple weighted scoring.
- Add browser automation signals.
- Add lifecycle timing signals.
- Add telemetry anomaly signals.
- Add velocity signals.
- Return allow/retry/cooldown/deny decisions.

### Phase 5: Shadow Mode

- Run risk scoring without enforcement.
- Collect metrics.
- Tune desktop/mobile thresholds separately.
- Identify browser-specific false positives.

### Phase 6: Gradual Enforcement

- Enable cooldown for high-risk traffic.
- Enable retry challenge for medium-risk traffic.
- Enable temporary deny only after confidence is high.
- Keep kill switch for all enforcement decisions.

---

## 19. Key Design Decisions

### Use Opaque Tokens Instead of JWT for Email Links

Opaque tokens are easier to revoke, count, expire, and bind to server-side state. They also avoid exposing email or booking data in URLs and logs.

### Treat Slider CAPTCHA as Friction, Not Proof

Slider challenges are solvable by sufficiently advanced automation. The system uses them to increase attack cost and collect interaction signals.

### Separate Mobile and Desktop Scoring

Mobile touch behavior differs significantly from desktop mouse behavior. Strict desktop heuristics should not be applied to mobile.

### Consume Challenge Sessions on Every Attempt

This prevents repeated brute force or telemetry replay against the same challenge.

### Keep Validation Tokens Short-Lived

A successful challenge should grant only a short window to select and finalize a slot.

---

## 20. Open Questions

1. Which frontend framework must the slider component support?
2. What is the acceptable false-positive rate?
3. How many retries should a legitimate user get before cooldown?
4. What are the expected peak booking volumes?
5. Are IP/ASN-based controls legally and operationally acceptable?
6. Should high-demand slot groups use stricter thresholds?
7. What is the maximum acceptable verification completion time on mobile?

---

## 21. Summary

The proposed system protects booking slots through a layered verification flow:

- email confirms basic intent;
- open-source slider CAPTCHA adds friction;
- telemetry contributes risk signals;
- server-side state prevents replay and invalid transitions;
- short-lived validation tokens limit abuse after verification;
- rate limits and slot-pressure controls reduce high-volume attacks;
- risk scoring allows adaptive decisions instead of fragile hard rules.

This design is expected to be effective against basic Selenium scripts and to materially increase the cost of AI agents using real Chrome. It is not expected to fully stop human solver farms or highly resourced attackers, but it should make automated slot booking less reliable and less scalable.

---

## 22. Sample Implementation Status

This section describes the current .NET 10 / Razor sample implementation in
`src/`.

### Implemented

- Initial booking form, development email preview modal, and `Continue Booking`
  flow.
- Opaque email tickets stored server-side.
- Email confirmation exchange for a short-lived browser verification session.
- Cookie-only verification session id: JavaScript receives only the frontend
  session nonce and all protected API calls resolve session state from
  `__booking_verification` + `X-Booking-Session-Nonce`.
- One-time challenge sessions with short TTL.
- Session-bound, short-lived asset tokens for challenge preview, piece, active,
  and follow-up images.
- One-time validation tokens for slot access and booking finalization.
- Central `EnforcementMode` (`Shadow`, `RetryOnly`, `CooldownOnly`, `Full`) with
  default `Shadow` and audit logging of applied vs would-have risk decisions.
- Slot selection only after successful challenge verification.
- Atomic in-memory booking finalization for the demo slot list.
- Multi-dimensional rate limits for email creation, challenge init, challenge
  verify, validation-token issuance, and slot pressure.
- In-memory state store wrapped behind `ITemporarySecurityStateStore`, whose
  contract exposes Redis-safe semantics such as increment-with-expiry,
  compare-and-set, set-if-not-exists, and consume-once. Comments mark where a
  Redis implementation must use Lua scripts, transactions, or `SET NX EX`.
- Slot-group pressure policy that shortens validation-token TTLs, limits slot
  availability reads, logs high pressure, and binds tokens to the first queried
  slot group.
- Security audit events with hashed/coarse identifiers and risk signals.
- Origin check for same-site API POST requests.
- Automated xUnit test project covering session binding, asset binding,
  challenge protocol bypasses, validation token scope, rate limits, enforcement
  modes, and consume-once concurrency.

### Implemented Custom Interactive Challenge

The sample currently implements a custom challenge layer in `src/Security`:

- `IChallengeSessionFactory` generates server-owned challenge sessions.
- `IChallengeProtocolValidator` validates the interaction lifecycle.
- `IChallengeAssetRenderer` renders demo challenge assets.
- `ITrajectoryAnalyzer` evaluates pointer/touch trajectory features.
- `IRiskEngine` combines protocol, trajectory, browser, retry, and lifecycle
  signals into risk decisions.

The challenge protocol includes:

- `/api/v1/challenge/init` for render-safe challenge setup.
- `/api/v1/challenge/start` to mark server-side pointerdown lifecycle start.
- `/api/v1/challenge/verify` for single-use release verification.
- Active visual phase after a randomized delay.
- Challenge variants:
  - `reveal_target`;
  - `shift_after_start`;
  - `hold_and_release`;
  - `follow_up_shift`.
- Server-owned target coordinate, tolerance, variant, phase nonce, and timing.
- `follow_up_shift` adds a second lifecycle endpoint,
  `/api/v1/challenge/follow-up`, a follow-up nonce, a final target, a
  randomized follow-up delay, and mandatory post-follow-up movement.
- Client telemetry with drag phases and `pre_active` / `active` / `follow_up`
  state markers.
- Second-plane slider movement used as a risk signal.
- Coarse trajectory fingerprinting to detect repeated traces without storing raw
  high-resolution telemetry.

This implementation intentionally treats the slider as a friction and signal
source, not as proof of humanity.

### Important Deviations From The Original Design

- The original document recommends using an existing open-source slider CAPTCHA
  component as the visual base. The current sample uses a custom renderer because
  the goal of the prototype shifted toward demonstrating a dynamic custom
  challenge protocol. Production deployment should either evaluate a vetted
  open-source component or keep the custom protocol with a clear maintenance and
  QA budget.
- Risk enforcement is controlled by `BotDefense:EnforcementMode` and defaults to
  `Shadow`, so production hard deny is impossible unless `Full` is explicitly
  configured.

### Not Yet Implemented

- Redis-backed `ITemporarySecurityStateStore`; the in-memory implementation is
  still process-local and exists only for the sample.
- Production database persistence for booking intents, audit logs, and slots.
- Runtime kill switches and an operator UI for staged rollout beyond the
  configuration-based enforcement mode.
- Admin authentication/authorization for audit endpoints.
- ASN / reputation enrichment and production metrics dashboards.
- Dedicated mobile/touch calibration across iOS, Android, and common browsers.
- Progressive slot inventory release, slot holds, or delayed inventory exposure.
- Non-enumerating production response for email rate limiting. The API still
  returns `email_rate_limited` in this demo for development visibility.
- Accessibility work, which remains intentionally outside the scope of this
  sample document.
