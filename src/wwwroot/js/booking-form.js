(() => {
  // The script is loaded on every Razor page, so it exits quietly when the
  // booking form is absent. This keeps the shared layout simple without adding
  // page-specific script bundles.
  const form = document.getElementById("bookingForm");
  if (!form) {
    return;
  }

  const statusLine = document.getElementById("formStatus");
  const modal = document.getElementById("emailModal");
  const closeModal = document.getElementById("closeEmailModal");
  const mailTo = document.getElementById("mailTo");
  const mailSubject = document.getElementById("mailSubject");
  const continueBookingButton = document.getElementById("continueBookingButton");
  const submitButton = document.getElementById("submitBooking");
  let continueBookingUrl = "";
  const startedAt = performance.now();

  // Pre-submit telemetry is deliberately lightweight. It is not a CAPTCHA and
  // it is not trusted as truth; it gives the risk engine context about whether
  // the form was filled in a way that resembles normal browser interaction.
  const telemetry = {
    firstInteractionMs: 0,
    keyEventCount: 0,
    pasteCount: 0,
    pointerMoveCount: 0,
    focusChangeCount: 0
  };

  function markFirstInteraction() {
    // Record the first interaction only once. The server uses this as a weak
    // lifecycle signal: instant submit after page load is suspicious, but slow
    // or missing events should not block legitimate users by themselves.
    if (!telemetry.firstInteractionMs) {
      telemetry.firstInteractionMs = Math.round(performance.now() - startedAt);
    }
  }

  // These listeners intentionally count broad categories instead of storing raw
  // keystrokes or field values. That keeps the sample privacy-conscious while
  // still giving the risk model useful behavioral shape.
  form.addEventListener("input", markFirstInteraction, { passive: true });
  form.addEventListener("focusin", () => {
    telemetry.focusChangeCount += 1;
    markFirstInteraction();
  });
  form.addEventListener("keydown", () => {
    telemetry.keyEventCount += 1;
    markFirstInteraction();
  });
  form.addEventListener("paste", () => {
    telemetry.pasteCount += 1;
    markFirstInteraction();
  });
  form.addEventListener("pointermove", () => {
    telemetry.pointerMoveCount += 1;
  }, { passive: true });

  closeModal.addEventListener("click", () => {
    modal.hidden = true;
  });

  // The email preview is a development replacement for sending real mail. The
  // production flow should send the same opaque ticket URL via an email service.
  modal.addEventListener("click", (event) => {
    if (event.target === modal) {
      modal.hidden = true;
    }
  });

  continueBookingButton.addEventListener("click", () => {
    if (!continueBookingUrl) {
      return;
    }

    // The confirmation flow opens in a new tab to resemble a user clicking a
    // real email link. noopener/opener cleanup prevents the new tab from gaining
    // a scripting reference back to the form page.
    const challengeWindow = window.open(continueBookingUrl, "_blank", "noopener");
    if (challengeWindow) {
      challengeWindow.opener = null;
    }
  });

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    statusLine.textContent = "";

    const data = new FormData(form);
    const email = String(data.get("email") || "").trim();
    const reenteredEmail = String(data.get("reenteredEmail") || "").trim();

    // Browser validation and duplicate-email comparison are UX conveniences.
    // The server repeats all meaningful validation because client checks can be
    // bypassed or modified.
    if (!form.reportValidity()) {
      return;
    }

    if (email.toLowerCase() !== reenteredEmail.toLowerCase()) {
      statusLine.textContent = "Email fields must match.";
      return;
    }

    submitButton.disabled = true;
    submitButton.textContent = "Submitting...";

    try {
      // The payload uses snake_case to match the API JSON policy. Form telemetry
      // is sent with the booking intent so the initial risk score exists before
      // any challenge is created or slot inventory is exposed.
      const response = await window.bookingApi.postJson("/api/v1/booking-intents", {
        name: String(data.get("name") || ""),
        date_of_birth: String(data.get("dateOfBirth") || ""),
        number_of_applicants: Number(data.get("numberOfApplicants") || 1),
        phone_number: String(data.get("phoneNumber") || ""),
        email,
        reentered_email: reenteredEmail,
        passport_number: String(data.get("passportNumber") || ""),
        citizenship: String(data.get("citizenship") || ""),
        residence_permit: String(data.get("residencePermit") || ""),
        telemetry: {
          submit_elapsed_ms: Math.round(performance.now() - startedAt),
          first_interaction_ms: telemetry.firstInteractionMs,
          key_event_count: telemetry.keyEventCount,
          paste_count: telemetry.pasteCount,
          pointer_move_count: telemetry.pointerMoveCount,
          focus_change_count: telemetry.focusChangeCount,
          browser: window.bookingApi.browserSignals()
        }
      });

      // The backend returns an email preview only for the sample. The important
      // value is continue_booking_url: it contains an opaque server-side ticket,
      // not booking state or any challenge answer.
      const preview = response.dev_email_preview;
      mailTo.textContent = preview.to;
      mailSubject.textContent = preview.subject;
      continueBookingUrl = preview.continue_booking_url;
      modal.hidden = false;
      continueBookingButton.focus();
      statusLine.textContent = "";
    } catch (error) {
      statusLine.textContent = readableError(error.message);
    } finally {
      submitButton.disabled = false;
      submitButton.textContent = "Submit";
    }
  });

  function readableError(errorCode) {
    // Keep UI messages intentionally generic. Detailed operational signals are
    // written to audit logs instead of being returned to an attacker-controlled
    // browser.
    const map = {
      missing_required_fields: "Fill in the required fields.",
      email_mismatch: "Email fields must match.",
      invalid_email: "Enter a valid email address.",
      email_rate_limited: "A confirmation was already created recently. Try again later.",
      request_failed: "The request could not be completed."
    };

    return map[errorCode] || "The request could not be completed.";
  }
})();
