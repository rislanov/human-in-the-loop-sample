(() => {
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
  const telemetry = {
    firstInteractionMs: 0,
    keyEventCount: 0,
    pasteCount: 0,
    pointerMoveCount: 0,
    focusChangeCount: 0
  };

  function markFirstInteraction() {
    if (!telemetry.firstInteractionMs) {
      telemetry.firstInteractionMs = Math.round(performance.now() - startedAt);
    }
  }

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

  modal.addEventListener("click", (event) => {
    if (event.target === modal) {
      modal.hidden = true;
    }
  });

  continueBookingButton.addEventListener("click", () => {
    if (!continueBookingUrl) {
      return;
    }

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
