# Design Document: "Human-in-the-Loop" Verification System for Booking Portal

**Goal:** Complete blocking of automated booking systems (VLM agents) that simulate human actions in a web browser.

---

## 1. Architecture Overview

The system combines two-factor intent confirmation (Email) and biometric-like behavioral analysis (Slider Trajectory).

**Core Components:**

1. **Identity Provider (IdP):** Service for managing sessions and temporary tokens (Ticket Service).
2. **Captcha Engine:** Generator for visual puzzles and the verification analyzer.
3. **Telemetry Collector:** Client-side JS module for implicit movement data collection.



---

## 2. User Journey

### Stage 1: Session Reservation

1. The user fills in primary data (Name, Email) on `https://sample-booking-system.com`.
2. The backend creates a record in Redis: pending_session:{email_hash} with the status AWAITING_EMAIL and a strict TTL (e.g., 15 minutes) to prevent memory exhaustion.
3. A **Magic Link** is sent to the provided email address:
`https://sample-booking-system.com/confirm-booking?t=[SIGNED_JWT]`
*Note: The JWT contains the email, timestamp, nonce, and a server signature.*

### Stage 2: Captcha Activation

1. Upon clicking the link, the frontend extracts `t` (the ticket) and makes a call:
`POST /api/v1/captcha/init` (Payload: `{ ticket: "[JWT]" }`).
2. The backend verifies the JWT signature and generates a captcha object:
* **Background Image:** A random photo from the database.
* **X_target:** A random coordinate (hidden from the client).
* **Session_ID:** Cryptographically tied to the ticket.



### Stage 3: Resolution and Telemetry Collection

1. The user is presented with the slider puzzle.
2. At the exact moment of interaction (mousedown/touchstart), a timer starts and `mousemove` events begin recording.
3. Upon releasing the slider (mouseup), the frontend compiles the collected data payload.

---

## 3. Deep Trajectory Analysis (Backend Heuristics)

This is the core defense mechanism against AI agents. The backend receives an array of points:

`Track = [{x, y, t}, {x, y, t}, ...]`

### 3.1. Physical Filters (The Physics Engine)

*Updated to include device-aware routing to prevent mobile users from being falsely flagged as bots.*

For every submission, the backend first determines the **Input Modality** (Mouse vs. Touch) based on the event types (`mousemove` vs. `touchmove`) collected in the telemetry payload. The heuristics are dynamically adjusted based on this modality:

**A. Mouse Profile (Desktop):**

* **Micro-Jitter Metric (Y-Variance):** Computes the standard deviation along the `Y` axis. Human hands experience natural physiological tremor, resulting in a standard deviation > 0.5 pixels. If $\sigma Y \approx 0$, the request is strictly flagged as `BOT`.
* **Fitts's Law Velocity Curve:** Evaluates the movement using the acceleration-peak-deceleration model. Bots typically exhibit constant velocity or mathematically perfect Bezier curves without the final "homing in" deceleration phase.

**B. Touch Profile (Mobile/Tablet):**

* **Jitter Exemption:** The `Y-Variance` check is disabled or severely relaxed for touch events, as mobile operating systems apply aggressive hardware-level smoothing to swipe gestures, natively resulting in $\sigma Y \approx 0$.
* **Swipe Arc & Fitt's Law Adaptation:** Mobile swipes differ from mouse dragging. They typically feature an explosive initial acceleration (flick) followed by a sharp stop, or an arc-like trajectory due to the biomechanics of the thumb. perfectly straight diagonal lines with constant speed are flagged as synthetic.
* **Touch Surface Metrics:** Validates the presence and organic fluctuation of `Touch.radiusX`, `Touch.radiusY`, and `Touch.force` properties across the event array. Headless browsers (like Puppeteer) emulating touch often send static, hardcoded values (e.g., `radiusX = 1`) or omit them entirely.

**C. Global Temporal Metrics:**
* *Time Entropy (t-delta):* The intervals between recorded events are analyzed. Bot browser cycles often yield events with suspiciously consistent intervals (e.g., exactly every 16.6ms). Authentic human intervals demonstrate high temporal entropy.

### 3.2. Headless Browser Traps

* **isTrusted Check:** Verification of the native system event flag.
* **Overlay Trap:** The confirmation button is covered by an invisible transparent layer. A bot will search the DOM and click the button's exact coordinates, while a human will physically click on the transparent layer (triggering the actual event handler).

---

## 4. API Specification (Verification Protocol)

### Method: `POST /api/v1/captcha/verify`

**Request Headers:**

* `X-Captcha-Session-ID`: UUID generated from the Init step.
* `Content-Type`: `application/json`

**Request Body (Payload):**

```json
{
  "ticket": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "solution": {
    "x": 184, 
    "time_spent": 1240
  },
  "telemetry": {
    "modality": "touch", 
    "points": [
      {"x": 0, "y": 5, "t": 0, "rX": 1.2, "rY": 1.2, "f": 0.5}, 
      {"x": 12, "y": 6, "t": 45, "rX": 1.5, "rY": 1.3, "f": 0.6}, 
      {"x": 184, "y": 5, "t": 1240, "rX": 1.1, "rY": 1.1, "f": 0.4}
    ],
    "user_agent": "Mozilla/5.0...",
    "canvas_fingerprint": "hash_string"
  }
}
```

**Response (Success):**

```json
{
  "success": true,
  "validation_token": "SECURE_FINAL_TOKEN_12345",
  "message": "Humanity confirmed"
}

```

---

## 5. Implementation Plan (Roadmap)

1. Deploy the `Identity Service` to generate Magic Links. Configure Rate Limiting for email dispatch (maximum of 1 email per address per 10 minutes).
2. Integrate the Slider widget onto the `confirm-booking` page. Configure server-side puzzle generation (using Canvas API on Node.js or ImageMagick).
3. Implement the trajectory analyzer module. Train a baseline model on "good" data from test users to accurately calibrate the jitter and speed filters.
4. Enable "Shadow Mode" — silently collect data and run heuristics without actively blocking, purely to analyze and mitigate false-positive rates.
5. Full Enforcement.

---

## 6. Infrastructure Security

* **Anti-Replay & Retry Logic:**
*Captcha Session Isolation:* A X-Captcha-Session-ID is strictly single-use. It is purged from Redis immediately after any verification attempt, regardless of whether the result was a "success" or a "failure."

*Failure Handling:* If a user fails to solve the captcha, the frontend must request a fresh session via the /init endpoint. This prevents attackers from brute-forcing a single puzzle or replaying captured telemetry.

*Email Ticket Persistence:* The Email Ticket (JWT) remains valid for multiple captcha attempts until its TTL expires or a successful booking is completed. This ensures that legitimate users can retry the puzzle if they make a mistake without needing a new email.
* **HMAC-Telemetry:** The telemetry payload generated on the frontend must be signed with a temporary session key. This prevents bot developers from maliciously replacing the `points` array via the browser console without holding the valid session key. The client-side Telemetry Collector JS must be heavily obfuscated and mutated periodically to prevent attackers from easily reverse-engineering the HMAC signing logic or hooking into the JS context.
* **API Origin/Referer Lockdown:** While the confirmation HTML page (`/confirm-booking`) must allow direct navigation from external sources (e.g., email clients where the `Referer` is typically null), the captcha API endpoints (`/api/v1/captcha/init` and `/api/v1/captcha/verify`) must exclusively process `Fetch/XHR` requests. The backend server must reject any API request if the HTTP `Origin` or `Referer` headers are missing, or if they do not strictly match `https://sample-booking-system.com`.