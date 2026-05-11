(() => {
  const shell = document.querySelector("[data-page='verification']");
  if (!shell) {
    return;
  }

  const ticket = shell.dataset.ticket;
  const stepper = document.getElementById("stepper");
  const loadingView = document.getElementById("loadingView");
  const loadingStatus = document.getElementById("loadingStatus");
  const challengeView = document.getElementById("challengeView");
  const slotsView = document.getElementById("slotsView");
  const challengeStatus = document.getElementById("challengeStatus");
  const background = document.getElementById("challengeBackground");
  const piece = document.getElementById("challengePiece");
  const frame = document.getElementById("puzzleFrame");
  const track = document.getElementById("sliderTrack");
  const progress = document.getElementById("sliderProgress");
  const handle = document.getElementById("sliderHandle");
  const slotGrid = document.getElementById("slotGrid");
  const bookingStatus = document.getElementById("bookingStatus");

  const pieceOffsetX = 8;
  const pieceOffsetY = 12;
  let verificationSessionId = "";
  let validationToken = "";
  let challengeId = "";
  let challengeConfig = null;
  let activeBackgroundUrl = "";
  let drag = null;
  let currentRatio = 0;
  // The handle has a small vertical lane. This gives the server a second
  // movement plane to score without changing the puzzle's horizontal solution.
  let currentPlaneRatio = 0.5;
  let telemetryEvents = {
    focus_lost: false,
    visibility_changed: false,
    pointer_cancelled: false
  };

  window.addEventListener("blur", () => {
    telemetryEvents.focus_lost = true;
  });

  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState !== "visible") {
      telemetryEvents.visibility_changed = true;
    }
  });

  window.addEventListener("resize", () => {
    if (challengeConfig) {
      applySliderRatio(currentRatio);
    }
  });

  handle.addEventListener("pointerdown", startDrag);
  handle.addEventListener("pointermove", moveDrag);
  handle.addEventListener("pointerup", endDrag);
  handle.addEventListener("pointercancel", cancelDrag);

  boot();

  async function boot() {
    if (!ticket) {
      showFatal("Booking confirmation is missing.");
      return;
    }

    try {
      const confirmed = await window.bookingApi.postJson("/api/v1/verification/email/confirm", {
        ticket,
        browser: window.bookingApi.browserSignals()
      });
      verificationSessionId = confirmed.verification_session_id;
      window.bookingApi.setSessionNonce(confirmed.session_nonce);
      await initChallenge();
    } catch (error) {
      showFatal(readableError(error.message));
    }
  }

  async function initChallenge() {
    loadingView.hidden = false;
    challengeView.hidden = true;
    slotsView.hidden = true;
    setPhase("select");
    loadingStatus.textContent = "Preparing challenge...";

    try {
      const response = await window.bookingApi.postJson("/api/v1/challenge/init", {
        verification_session_id: verificationSessionId,
        browser: window.bookingApi.browserSignals()
      });

      challengeId = response.challenge_id;
      challengeConfig = response.render_payload.ui_config;
      activeBackgroundUrl = "";
      telemetryEvents = {
        focus_lost: false,
        visibility_changed: false,
        pointer_cancelled: false
      };

      background.src = cacheBust(response.render_payload.background_image_url);
      piece.src = cacheBust(response.render_payload.piece_image_url);
      configureChallengeLayout();
      loadingView.hidden = true;
      challengeView.hidden = false;
      challengeStatus.textContent = "";
      window.requestAnimationFrame(resetSlider);
    } catch (error) {
      showFatal(readableError(error.message));
    }
  }

  function startDrag(event) {
    if (!challengeConfig) {
      return;
    }

    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    const startedAt = performance.now();
    drag = {
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startClientY: event.clientY,
      startRatio: currentRatio,
      startPlaneRatio: currentPlaneRatio,
      startedAt,
      activeAt: 0,
      phaseNonce: "",
      interactionPhase: "pre_active",
      lastClientX: event.clientX,
      lastClientY: event.clientY,
      lastMeaningfulMoveAt: startedAt,
      activationTimer: 0,
      lifecyclePromise: null,
      modality: event.pointerType || "mouse",
      points: []
    };
    drag.lifecyclePromise = beginChallengeLifecycle(event.pointerId);
    addTelemetryPoint(event, "down");
    handle.classList.add("dragging");
  }

  async function beginChallengeLifecycle(pointerId) {
    try {
      // The browser asks the server to mark the start of the interaction only
      // after pointerdown. This prevents a verifier from posting a final X value
      // without going through the active visual phase.
      const response = await window.bookingApi.postJson("/api/v1/challenge/start", {
        challenge_id: challengeId,
        browser: window.bookingApi.browserSignals()
      });

      if (!drag || drag.pointerId !== pointerId) {
        return;
      }

      drag.phaseNonce = response.phase_nonce || "";
      activeBackgroundUrl = response.active_background_image_url || "";
      if (response.ui_config) {
        challengeConfig = response.ui_config;
        configureChallengeLayout();
      }

      const delay = Math.max(0, Number(response.activation_delay_ms ?? challengeConfig.activation_delay_ms ?? 0));
      drag.activationTimer = window.setTimeout(() => activateChallengePhase(pointerId), delay);
    } catch (error) {
      if (drag && drag.pointerId === pointerId) {
        telemetryEvents.pointer_cancelled = true;
        challengeStatus.textContent = readableError(error.message);
      }
    }
  }

  function activateChallengePhase(pointerId) {
    if (!drag || drag.pointerId !== pointerId) {
      return;
    }

    drag.activeAt = performance.now();
    drag.interactionPhase = "active";
    track.classList.add("active-phase");

    // The active background can reveal or move the target. A screenshot taken
    // before pointerdown is therefore intentionally insufficient.
    if (challengeConfig.variant === "hold_and_release") {
      track.classList.add("hold-mode");
      challengeStatus.textContent = "Pause briefly, then release.";
    } else {
      track.classList.remove("hold-mode");
      challengeStatus.textContent = "";
    }

    if (activeBackgroundUrl) {
      background.src = cacheBust(activeBackgroundUrl);
    }
  }

  function moveDrag(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }

    const metrics = getMetrics();
    const delta = event.clientX - drag.startClientX;
    const deltaY = event.clientY - drag.startClientY;
    const left = clamp(drag.startRatio * metrics.pieceMax + delta, 0, metrics.pieceMax);
    const top = clamp(drag.startPlaneRatio * metrics.handleTopMax + deltaY, 0, metrics.handleTopMax);
    applyDragPosition(left, top, metrics);
    if (Math.abs(event.clientX - drag.lastClientX) + Math.abs(event.clientY - drag.lastClientY) >= 3) {
      drag.lastMeaningfulMoveAt = performance.now();
      drag.lastClientX = event.clientX;
      drag.lastClientY = event.clientY;
    }
    addTelemetryPoint(event, "move");
  }

  async function endDrag(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }

    const releasedAt = performance.now();
    addTelemetryPoint(event, "up");
    handle.classList.remove("dragging");
    handle.releasePointerCapture(event.pointerId);
    const finishedDrag = drag;
    drag = null;
    if (finishedDrag.activationTimer) {
      window.clearTimeout(finishedDrag.activationTimer);
    }
    await finishedDrag.lifecyclePromise?.catch(() => {});
    await verify(finishedDrag, releasedAt);
  }

  function cancelDrag(event) {
    if (drag && event.pointerId === drag.pointerId) {
      telemetryEvents.pointer_cancelled = true;
      handle.classList.remove("dragging");
      if (drag.activationTimer) {
        window.clearTimeout(drag.activationTimer);
      }
      drag = null;
      resetSlider();
    }
  }

  async function verify(finishedDrag, releasedAt) {
    const metrics = getMetrics();
    const solutionX = Math.round((currentRatio * metrics.pieceMax * metrics.scale) + pieceOffsetX);
    const timeSpentMs = Math.round(releasedAt - finishedDrag.startedAt);
    // These values are not trusted as secrets. They let the server compare the
    // reported lifecycle with the server-owned challenge state and trajectory.
    const activeElapsedMs = finishedDrag.activeAt > 0
      ? Math.max(0, Math.round(releasedAt - finishedDrag.activeAt))
      : 0;
    const holdAnchor = Math.max(finishedDrag.lastMeaningfulMoveAt, finishedDrag.activeAt || finishedDrag.startedAt);
    const holdMs = Math.max(0, Math.round(releasedAt - holdAnchor));
    const lastAdjustmentMs = finishedDrag.activeAt > 0
      ? Math.max(0, Math.round(finishedDrag.lastMeaningfulMoveAt - finishedDrag.activeAt))
      : 0;
    challengeStatus.textContent = "Checking...";
    handle.disabled = true;

    try {
      const result = await window.bookingApi.postJson("/api/v1/challenge/verify", {
        challenge_id: challengeId,
        solution: {
          x: solutionX,
          time_spent_ms: timeSpentMs,
          phase_nonce: finishedDrag.phaseNonce,
          interaction_phase: finishedDrag.interactionPhase,
          active_elapsed_ms: activeElapsedMs,
          hold_ms: holdMs,
          last_adjustment_ms: lastAdjustmentMs
        },
        telemetry: {
          modality: finishedDrag.modality,
          points: finishedDrag.points,
          events: telemetryEvents
        },
        browser: window.bookingApi.browserSignals()
      });

      if (result.decision === "allow") {
        validationToken = result.validation_token;
        challengeStatus.textContent = "";
        await showSlots();
        return;
      }

      if (result.decision === "temporarily_denied") {
        showFatal("Verification is temporarily unavailable for this request.");
        return;
      }

      if (result.decision === "hard_denied") {
        showFatal(readableError("hard_denied"));
        return;
      }

      challengeStatus.textContent = "Try again in a moment.";
      window.setTimeout(initChallenge, (result.cooldown_seconds || 3) * 1000);
    } catch (error) {
      challengeStatus.textContent = readableError(error.message);
      window.setTimeout(initChallenge, 1800);
    }
  }

  async function showSlots() {
    const response = await window.bookingApi.postJson("/api/v1/slots/available", {
      validation_token: validationToken,
      browser: window.bookingApi.browserSignals()
    });

    renderSlots(response.slots || []);
    challengeView.hidden = true;
    loadingView.hidden = true;
    slotsView.hidden = false;
    setPhase("select");
  }

  function renderSlots(slots) {
    slotGrid.replaceChildren();
    const visibleSlots = slots.slice(0, 7);

    visibleSlots.forEach((slot) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "slot-card";
      button.dataset.slotId = slot.id;
      button.innerHTML = `
        <strong>${slot.date}</strong>
        <span>${slot.day_name}</span>
        <b>${slot.time}</b>
        <small>${slot.status}</small>
      `;
      button.addEventListener("click", () => finalizeSlot(button, slot.id));
      slotGrid.appendChild(button);
    });
  }

  async function finalizeSlot(button, slotId) {
    bookingStatus.textContent = "";
    [...slotGrid.querySelectorAll("button")].forEach((candidate) => {
      candidate.disabled = true;
      candidate.classList.toggle("selected", candidate === button);
    });

    try {
      const result = await window.bookingApi.postJson("/api/v1/bookings/finalize", {
        validation_token: validationToken,
        slot_id: slotId,
        browser: window.bookingApi.browserSignals()
      });
      bookingStatus.textContent = `Booking confirmed: ${result.booking_id}`;
      setPhase("confirmation");
    } catch (error) {
      bookingStatus.textContent = readableError(error.message);
      [...slotGrid.querySelectorAll("button")].forEach((candidate) => {
        candidate.disabled = false;
      });
    }
  }

  function configureChallengeLayout() {
    frame.style.aspectRatio = `${challengeConfig.width} / ${challengeConfig.height}`;
    track.style.width = `min(${challengeConfig.track_width || challengeConfig.width}px, 100%)`;
    track.style.setProperty("--stripe-offset", `${challengeConfig.stripe_offset || 0}px`);
    handle.style.width = `${challengeConfig.handle_size || 54}px`;
    handle.style.height = `${challengeConfig.handle_size || 54}px`;
    track.classList.toggle("shift-mode", challengeConfig.variant === "shift_after_start");
    track.classList.toggle("hold-mode", false);
    track.classList.remove("active-phase");
  }

  function resetSlider() {
    currentRatio = 0;
    currentPlaneRatio = 0.5;
    handle.disabled = false;
    track.classList.remove("active-phase");
    track.classList.remove("hold-mode");
    applySliderRatio(currentRatio);
  }

  function applySliderRatio(ratio) {
    const metrics = getMetrics();
    applyDragPosition(ratio * metrics.pieceMax, currentPlaneRatio * metrics.handleTopMax, metrics);
  }

  function applyDragPosition(left, top, metrics) {
    const nextLeft = clamp(left, 0, metrics.pieceMax);
    const nextTop = clamp(top, 0, metrics.handleTopMax);
    currentRatio = metrics.pieceMax > 0 ? nextLeft / metrics.pieceMax : 0;
    currentPlaneRatio = metrics.handleTopMax > 0 ? nextTop / metrics.handleTopMax : 0.5;
    piece.style.width = `${metrics.pieceWidth}px`;
    piece.style.top = `${Math.max(0, (challengeConfig.piece_y - pieceOffsetY) / metrics.scale)}px`;
    piece.style.left = `${nextLeft}px`;
    handle.style.left = `${currentRatio * metrics.handleMax}px`;
    handle.style.top = `${nextTop}px`;
    progress.style.width = `${(currentRatio * metrics.handleMax) + (handle.offsetWidth / 2)}px`;
  }

  function addTelemetryPoint(event, phase) {
    if (!drag) {
      return;
    }

    const metrics = getMetrics();
    const frameRect = frame.getBoundingClientRect();
    const x = Math.round((currentRatio * metrics.pieceMax * metrics.scale) + pieceOffsetX);
    const y = Math.round((event.clientY - frameRect.top) * metrics.scale);
    const t = Math.round(performance.now() - drag.startedAt);
    const state = drag.activeAt > 0 ? "active" : "pre_active";

    if (drag.points.length < 80) {
      drag.points.push({ x, y, t, phase, state });
    }
  }

  function getMetrics() {
    const frameWidth = Math.max(1, frame.getBoundingClientRect().width);
    const scale = challengeConfig.width / frameWidth;
    const pieceWidth = (challengeConfig.piece_size + 16) / scale;
    const pieceMax = Math.max(1, frameWidth - pieceWidth);
    const handleMax = Math.max(1, track.getBoundingClientRect().width - handle.offsetWidth);
    const handleTopMax = Math.max(1, track.getBoundingClientRect().height - handle.offsetHeight);

    return { frameWidth, scale, pieceWidth, pieceMax, handleMax, handleTopMax };
  }

  function setPhase(phase) {
    stepper.classList.toggle("select-phase", phase === "select");
    stepper.classList.toggle("validate-phase", phase === "validate");
    stepper.classList.toggle("confirmation-phase", phase === "confirmation");
  }

  function showFatal(message) {
    loadingView.hidden = false;
    challengeView.hidden = true;
    slotsView.hidden = true;
    loadingStatus.textContent = message;
  }

  function readableError(errorCode) {
    const map = {
      invalid_ticket: "Booking confirmation is invalid or expired.",
      ticket_expired: "Booking confirmation is invalid or expired.",
      invalid_state: "This request is no longer in the expected state.",
      invalid_verification_session: "Verification session could not be restored.",
      missing_challenge: "Challenge could not be started.",
      challenge_not_found: "Challenge expired.",
      challenge_expired_or_consumed: "Challenge expired.",
      invalid_challenge_lifecycle: "Challenge expired.",
      too_many_attempts: "Verification is temporarily unavailable for this request.",
      hard_denied: "This booking request can no longer continue.",
      missing_session_nonce: "Booking session could not be verified.",
      invalid_session_nonce: "Booking session could not be verified.",
      device_mismatch: "Booking session could not be restored on this device.",
      missing_validation_token: "Slot selection token is missing.",
      invalid_validation_token: "Slot selection token is invalid or expired.",
      validation_token_expired_or_used: "Slot selection token is invalid or expired.",
      slot_unavailable: "This slot is no longer available.",
      slot_pressure_cooldown: "This slot group is busy. Try again shortly."
    };

    return map[errorCode] || "The request could not be completed.";
  }

  function cacheBust(url) {
    const separator = url.includes("?") ? "&" : "?";
    return `${url}${separator}v=${Date.now()}`;
  }

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }
})();
