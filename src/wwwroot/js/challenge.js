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
  let drag = null;
  let currentRatio = 0;
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
      showFatal("Verification link is missing.");
      return;
    }

    try {
      const confirmed = await window.bookingApi.postJson("/api/v1/verification/email/confirm", {
        ticket
      });
      verificationSessionId = confirmed.verification_session_id;
      await initChallenge();
    } catch (error) {
      showFatal(readableError(error.message));
    }
  }

  async function initChallenge() {
    loadingView.hidden = false;
    challengeView.hidden = true;
    slotsView.hidden = true;
    setPhase("validate");
    loadingStatus.textContent = "Preparing challenge...";

    try {
      const response = await window.bookingApi.postJson("/api/v1/challenge/init", {
        verification_session_id: verificationSessionId
      });

      challengeId = response.challenge_id;
      challengeConfig = response.render_payload.ui_config;
      telemetryEvents = {
        focus_lost: false,
        visibility_changed: false,
        pointer_cancelled: false
      };

      background.src = `${response.render_payload.background_image_url}?v=${Date.now()}`;
      piece.src = `${response.render_payload.piece_image_url}?v=${Date.now()}`;
      frame.style.aspectRatio = `${challengeConfig.width} / ${challengeConfig.height}`;
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
    drag = {
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startRatio: currentRatio,
      startedAt: performance.now(),
      modality: event.pointerType || "mouse",
      points: []
    };
    addTelemetryPoint(event);
    handle.classList.add("dragging");
  }

  function moveDrag(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }

    const metrics = getMetrics();
    const delta = event.clientX - drag.startClientX;
    const left = clamp(drag.startRatio * metrics.pieceMax + delta, 0, metrics.pieceMax);
    applyPieceLeft(left, metrics);
    addTelemetryPoint(event);
  }

  async function endDrag(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }

    addTelemetryPoint(event);
    handle.classList.remove("dragging");
    handle.releasePointerCapture(event.pointerId);
    const finishedDrag = drag;
    drag = null;
    await verify(finishedDrag);
  }

  function cancelDrag(event) {
    if (drag && event.pointerId === drag.pointerId) {
      telemetryEvents.pointer_cancelled = true;
      handle.classList.remove("dragging");
      drag = null;
      resetSlider();
    }
  }

  async function verify(finishedDrag) {
    const metrics = getMetrics();
    const solutionX = Math.round((currentRatio * metrics.pieceMax * metrics.scale) + pieceOffsetX);
    const timeSpentMs = Math.round(performance.now() - finishedDrag.startedAt);
    challengeStatus.textContent = "Checking...";
    handle.disabled = true;

    try {
      const result = await window.bookingApi.postJson("/api/v1/challenge/verify", {
        challenge_id: challengeId,
        solution: {
          x: solutionX,
          time_spent_ms: timeSpentMs
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

      challengeStatus.textContent = "Try again in a moment.";
      window.setTimeout(initChallenge, (result.cooldown_seconds || 3) * 1000);
    } catch (error) {
      challengeStatus.textContent = readableError(error.message);
      window.setTimeout(initChallenge, 1800);
    }
  }

  async function showSlots() {
    const response = await window.bookingApi.postJson("/api/v1/slots/available", {
      validation_token: validationToken
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
        slot_id: slotId
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

  function resetSlider() {
    currentRatio = 0;
    handle.disabled = false;
    applySliderRatio(0);
  }

  function applySliderRatio(ratio) {
    const metrics = getMetrics();
    applyPieceLeft(ratio * metrics.pieceMax, metrics);
  }

  function applyPieceLeft(left, metrics) {
    const nextLeft = clamp(left, 0, metrics.pieceMax);
    currentRatio = metrics.pieceMax > 0 ? nextLeft / metrics.pieceMax : 0;
    piece.style.width = `${metrics.pieceWidth}px`;
    piece.style.top = `${Math.max(0, (challengeConfig.piece_y - pieceOffsetY) / metrics.scale)}px`;
    piece.style.left = `${nextLeft}px`;
    handle.style.left = `${currentRatio * metrics.handleMax}px`;
    progress.style.width = `${(currentRatio * metrics.handleMax) + (handle.offsetWidth / 2)}px`;
  }

  function addTelemetryPoint(event) {
    if (!drag) {
      return;
    }

    const metrics = getMetrics();
    const frameRect = frame.getBoundingClientRect();
    const x = Math.round((currentRatio * metrics.pieceMax * metrics.scale) + pieceOffsetX);
    const y = Math.round((event.clientY - frameRect.top) * metrics.scale);
    const t = Math.round(performance.now() - drag.startedAt);

    if (drag.points.length < 80) {
      drag.points.push({ x, y, t });
    }
  }

  function getMetrics() {
    const frameWidth = Math.max(1, frame.getBoundingClientRect().width);
    const scale = challengeConfig.width / frameWidth;
    const pieceWidth = (challengeConfig.piece_size + 16) / scale;
    const pieceMax = Math.max(1, frameWidth - pieceWidth);
    const handleMax = Math.max(1, track.getBoundingClientRect().width - handle.offsetWidth);

    return { frameWidth, scale, pieceWidth, pieceMax, handleMax };
  }

  function setPhase(phase) {
    stepper.classList.toggle("validate-phase", phase === "validate");
    stepper.classList.toggle("select-phase", phase === "select");
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
      invalid_ticket: "Verification link is invalid or expired.",
      ticket_expired: "Verification link is invalid or expired.",
      invalid_state: "This request is no longer in the expected state.",
      invalid_verification_session: "Verification session could not be restored.",
      challenge_expired_or_consumed: "Challenge expired.",
      too_many_attempts: "Verification is temporarily unavailable for this request.",
      missing_validation_token: "Slot selection token is missing.",
      invalid_validation_token: "Slot selection token is invalid or expired.",
      validation_token_expired_or_used: "Slot selection token is invalid or expired.",
      slot_unavailable: "This slot is no longer available."
    };

    return map[errorCode] || "The request could not be completed.";
  }

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }
})();
