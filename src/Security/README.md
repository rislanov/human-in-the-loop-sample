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

The current renderer is a demo implementation. A production application can
replace it with a vetted open-source CAPTCHA component or an external challenge
service while keeping the same state-machine and risk contracts.

The modules intentionally produce risk signals rather than final proof of
humanity. The booking flow remains responsible for persistence, rate limits,
single-use tokens, audit logging, and slot finalization.
